using System.Globalization;
using NexusApp.Models;

namespace NexusApp.Services;

// App-lifetime session-profit consumer of the shared Game.log tail (issue #39). Mirrors
// HaulTracker's shape (public Ingest for headless tests; a private feed when none is injected).
// Owns the current SessionLedger and the persisted profit history: every applied or voided
// settlement rewrites the CURRENT session's summary, coalesced to one save and one Changed per
// dispatcher batch, so a crash mid-session loses at most the batch still in flight (the replay
// rebuilds it) and a later replay of the same log lands on the same SessionKey and overwrites.
// The session rolls when the LOG does (reset, a re-point to a different file, or a re-point
// whose replay comes here) - never on a shard change: leaving a shard abandons contracts, not
// money already settled at a kiosk.
public sealed class ProfitTracker : IDisposable
{
    private readonly GameLogFeed _feed;
    private readonly bool _ownsFeed;            // true only when nobody handed us a shared feed
    private GameLogSubscription? _sub;
    private readonly string _historyPath;
    private readonly Func<GameChannel> _channel;

    private readonly Action<Action>? _flushScheduler;   // test seam; null = dispatcher when present
    private readonly Func<(string? Label, bool IsArea)> _place;   // last known location at settlement time

    private string? _sessionKey;                // latched from the log's own first-line stamp
    private GameChannel _sessionChannel;
    private DateTime _sessionStartUtc;
    private string? _feedPath;                  // the file the tail reads; a DIFFERENT file rolls
    private bool _flushPending;                 // a save + Changed is queued for the current batch

    public ProfitTracker(GameLogFeed? feed = null, string? historyPath = null,
                         Func<GameChannel>? channel = null, Action<Action>? flushScheduler = null,
                         Func<(string? Label, bool IsArea)>? place = null)
    {
        _feed = feed ?? new GameLogFeed();
        _ownsFeed = feed is null;
        _historyPath = historyPath ?? ProfitHistoryStore.DefaultPath;
        _channel = channel ?? (() => _feed.ActiveChannel);
        _flushScheduler = flushScheduler;
        // The location tracker consumes the same fan-out and is subscribed ahead of this consumer
        // (App constructs Locations before Profit), so at every line, replayed or live, its state
        // is current through that line and the stamp is replay-stable.
        _place = place ?? (() => (App.Player?.Label, App.Player?.LabelIsJurisdiction ?? false));
        _feedPath = string.IsNullOrEmpty(_feed.Path) ? null : _feed.Path;
        History = ProfitHistoryStore.Load(_historyPath, out var reason) ?? new ProfitHistoryState();
        if (reason is not null) Logger.Info($"[LEDGER] starting fresh profit history: {reason}");
        // Replay appetite as HaulTracker: every start of the tail replays the current Game.log
        // from the top, so an app restart mid-session rebuilds the live ledger (Apply dedupes by
        // Key, error responses by their own stamp).
        _sub = _feed.Subscribe(Ingest, includeReplay: true, onLogReset: Reset, onStarted: OnFeedStarted);
    }

    /// <summary>The current log session's ledger. Rolled (never replaced) on a session boundary.</summary>
    public SessionLedger Ledger { get; } = new();

    /// <summary>The persisted per-channel history (profit_history.json), current session included.
    /// Read through ProfitHistory for the derived all-time figures.</summary>
    public ProfitHistoryState History { get; }

    public event Action? Changed;

    // New SC session (Game.log reset). A batch still queued belongs to the outgoing session, so
    // it flushes under the outgoing key first; then only the live ledger rolls - the finished
    // session's summary is already in history, and the new log's replay rebuilds whatever the
    // new session already did.
    public void Reset()
    {
        Flush();
        Ledger.Reset();
        _sessionKey = null;
        Changed?.Invoke();
    }

    // The tail was (re)pointed. A session boundary exactly like a log reset whenever the tail now
    // reads a DIFFERENT file - even a targeted re-point (the advanced monitor tailing another
    // channel's log) whose replay never comes here, because the new file's LIVE lines still fan
    // out to everyone and must not book into the old file's session - or whenever the same file
    // is about to replay here from the top. A targeted from-the-top re-read of the SAME file
    // rolls nothing: the current key still names that file, and rolling would split the session
    // in two entries, the second double counting once a full replay rebuilt the first. Internal
    // for the headless tests (InternalsVisibleTo), which drive it in place of a real feed.
    internal void OnFeedStarted(string path, bool willReplayToMe)
    {
        bool newFile = !string.Equals(path, _feedPath, StringComparison.OrdinalIgnoreCase);
        _feedPath = path;
        if (willReplayToMe || newFile) Reset();
    }

    public void Ingest(GameLogEntry e)
    {
        var raw = e.Raw;
        if (_sessionKey is null) TryLatchSession(raw);
        if (!CommodityLogParser.LooksCommodityRelevant(raw)) return;

        if (CommodityLogParser.ParseBuy(raw) is { } buy) { Stamp(buy); Apply(buy); return; }
        if (CommodityLogParser.ParseSell(raw) is { } sell) { Stamp(sell); Apply(sell); return; }
        if (CommodityLogParser.ParseTransactionError(raw) is { } err && Ledger.ApplyError(err))
            QueueFlush();
    }

    // Where the player was when this settlement logged. The shop token cannot say (kiosk
    // shopNames are shared templates, not places); the location tracker's state at this line can.
    private void Stamp(CommodityTransaction tx)
    {
        var (label, area) = _place();
        tx.PlaceLabel = label;
        tx.PlaceIsArea = area;
    }

    private void Apply(CommodityTransaction tx)
    {
        if (!Ledger.Apply(tx)) return;   // replayed line, already counted
        QueueFlush();
    }

    // One save and one Changed per dispatcher batch: a startup replay settles a whole log inside
    // one watcher tick, and writing profit_history.json (plus rebuilding the bound surfaces) once
    // per settlement hammered both. The first trigger in a batch posts the flush at Background
    // priority; the rest ride along. Headless (no WPF Application, no injected scheduler: the
    // test context) flushes synchronously, keeping Ingest's seam dispatcher-free.
    private void QueueFlush()
    {
        if (_flushPending) return;
        _flushPending = true;
        if (_flushScheduler is not null) { _flushScheduler(Flush); return; }
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) { Flush(); return; }
        dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, (Action)Flush);
    }

    // Session rolls and Dispose call this directly so a queued batch is never lost; the posted
    // callback then finds nothing pending and no-ops.
    private void Flush()
    {
        if (!_flushPending) return;
        _flushPending = false;
        PersistCurrentSession();
        Changed?.Invoke();
    }

    // Rewrite the current session's summary on every settlement (crash-safe). A session voided
    // back to zero transactions retracts its own entry inside Upsert.
    private void PersistCurrentSession()
    {
        if (_sessionKey is null) return;   // defensive: the settlement's own line latches the key
        ProfitHistory.Upsert(History, Ledger.BuildSummary(_sessionKey, _sessionChannel, _sessionStartUtc));
        ProfitHistoryStore.Save(_historyPath, History);
    }

    // SessionKey is the channel plus the log's own first-line stamp (SessionSummary.MakeKey), so
    // every replay of the same file lands on the same key. The first line this consumer sees IS
    // the file's first line (replay-from-top subscription; a reset reads the new file from byte
    // zero); lines without a parseable stamp are skipped until one arrives.
    private void TryLatchSession(string raw)
    {
        if (raw.Length < 26 || raw[0] != '<' || raw[25] != '>') return;
        if (!DateTimeOffset.TryParseExact(raw.Substring(1, 24), "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
            return;
        _sessionChannel = _channel();
        _sessionStartUtc = dto.UtcDateTime;
        _sessionKey = SessionSummary.MakeKey(_sessionChannel, _sessionStartUtc);
    }

    public void Dispose()
    {
        Flush();   // a batch still queued on the dispatcher must not die with the subscription
        _sub?.Dispose();
        _sub = null;
        if (_ownsFeed) _feed.Dispose();   // a shared feed is the app's to dispose, not ours
    }
}
