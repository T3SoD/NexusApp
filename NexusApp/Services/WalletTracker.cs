using System.Globalization;
using NexusApp.Models;

namespace NexusApp.Services;

// The wallet brain (spec sections 4 and 11). Holds the per-channel anchor (last trusted balance,
// OCR capture or manual entry) and derives the live estimate: anchor plus the profit tracker's
// unvoided settlements after the anchor instant. On every confirmed capture it reconciles: a
// delta the ledger cannot explain becomes an Untracked Income / Untracked Purchase row. Those
// rows are wallet-layer artifacts: they persist in wallet.json (a Game.log replay cannot
// regenerate them) and they NEVER count into any profit figure. Mirrors ProfitTracker's
// lifecycle: shared feed subscription, session latch from the log's own first-line stamp,
// coalesced flush, one save and one Changed per batch.
public sealed class WalletTracker : IDisposable
{
    // A settlement or void this close to the capture window could be the same money the scan
    // saw: skip the row, re-anchor only, let the next scan reconcile (spec 11.3).
    public static readonly TimeSpan RaceGuard = TimeSpan.FromSeconds(6);

    // Anchor age past which the UI shows AGING (spec section 6 item 2).
    public static readonly TimeSpan AgingThreshold = TimeSpan.FromMinutes(30);

    // Recent contract completions kept for income attribution (spec section 12). In-memory
    // only: the current Game.log replays them on app start, and completions from older game
    // sessions cannot be attributed anyway.
    public const int CompletionMemory = 64;

    private readonly ProfitTracker _profit;
    private readonly GameLogFeed _feed;
    private readonly bool _ownsFeed;
    private GameLogSubscription? _sub;
    private readonly string _walletPath;
    private readonly Func<GameChannel> _channel;
    private readonly Action<Action>? _flushScheduler;
    private readonly WalletState _state;
    private string? _feedPath;
    private DateTime? _sessionStartUtc;
    private bool _flushPending;

    // Feed thread writes, the burst task reads at reconcile time; the lock covers both.
    private readonly object _completionsLock = new();
    private readonly List<(DateTime Utc, string Name)> _completions = new();

    public event Action? Changed;

    public WalletTracker(ProfitTracker profit, GameLogFeed? feed = null, string? walletPath = null,
                         Func<GameChannel>? channel = null, Action<Action>? flushScheduler = null)
    {
        _profit = profit;
        _feed = feed ?? new GameLogFeed();
        _ownsFeed = feed is null;
        _walletPath = walletPath ?? WalletStore.DefaultPath;
        _channel = channel ?? (() => _feed.ActiveChannel);
        _flushScheduler = flushScheduler;
        _feedPath = string.IsNullOrEmpty(_feed.Path) ? null : _feed.Path;
        _state = WalletStore.Load(_walletPath, out var reason) ?? new WalletState();
        if (reason is not null) Logger.Info($"[WALLET] starting fresh wallet state: {reason}");
        _sub = _feed.Subscribe(Ingest, includeReplay: true, onLogReset: Reset, onStarted: OnFeedStarted);
        _profit.Changed += OnProfitChanged;
    }

    public bool HasAnchor => Current() is not null;
    public DateTime? AnchorUtc => Current()?.AnchorUtc;
    public string? AnchorSource => Current()?.Source;
    public DateTime? SessionStartUtc => _sessionStartUtc;

    /// <summary>Anchor plus every unvoided post-anchor settlement; null until an anchor exists.
    /// Negative stands: it is the design's own self-check (never clamp).</summary>
    public long? Estimate
    {
        get
        {
            var ch = Current();
            if (ch is null) return null;
            return ch.Anchor + LedgerDeltaBetween(ch.AnchorUtc, DateTime.MaxValue);
        }
    }

    /// <summary>Untracked rows discovered since the current log session started (display scope);
    /// the full capped list lives in wallet.json.</summary>
    public IReadOnlyList<UntrackedEntry> SessionUntracked
    {
        get
        {
            if (_sessionStartUtc is not { } start) return Array.Empty<UntrackedEntry>();
            var ch = Current();
            if (ch is null) return Array.Empty<UntrackedEntry>();
            return ch.Untracked.Where(u => u.Utc >= start).ToList();
        }
    }

    // A typed balance is a correction, never activity: no row, ever (ruling 3, spec 11.1). The
    // typed figure came off the game screen, which already reflects every settled trade, so the
    // anchor instant is the newest settlement (or the session start) - re-subtracting a trade
    // the player already sees in their balance would double count it.
    public void SetManualBalance(long balance)
    {
        var txs = _profit.Ledger.Transactions;
        var anchorUtc = txs.Count > 0 ? txs[^1].TimestampUtc : _sessionStartUtc ?? DateTime.UtcNow;
        var ch = CurrentOrCreate();
        ch.Anchor = balance;
        ch.AnchorUtc = anchorUtc;
        ch.Source = "Manual";
        Logger.Info($"[WALLET] manual balance set: {balance}");
        QueueFlush();
    }

    // The reconciliation entry point (spec 11.2); WalletCapture calls it on a confirmed burst.
    public void OnBalanceCaptured(long balance, DateTime triggerUtc, DateTime captureUtc)
    {
        var ch = Current();
        if (ch is null)
        {
            ch = CurrentOrCreate();
            ch.Anchor = balance;
            ch.AnchorUtc = captureUtc;
            ch.Source = "Ocr";
            Logger.Info("[WALLET] reconcile first anchor");
            QueueFlush();
            return;
        }

        var estimateAtCapture = ch.Anchor + LedgerDeltaBetween(ch.AnchorUtc, captureUtc);
        var unexplained = balance - estimateAtCapture;

        // Partial-read guard (live failure 2026-08-06 16:44): a value whose digits are a strict
        // prefix of the estimate's digits is an animation-truncated read, not money. A real
        // balance landing exactly on a proper digit prefix of itself is effectively impossible;
        // rejecting changes nothing and the next burst carries the true value.
        if (IsStrictDigitPrefix(balance, estimateAtCapture))
        {
            Logger.Info("[WALLET] reconcile rejected partial read");
            return;
        }

        if (TradeRacesTheCapture(triggerUtc, captureUtc))
        {
            Logger.Info("[WALLET] reconcile skipped-race");
        }
        else if (unexplained != 0)
        {
            // Completions pay in, never out: only income asks the log for a name. The window is
            // the span the unexplained money accumulated over, previous anchor to this capture.
            var label = unexplained > 0 ? CompletionLabel(ch.AnchorUtc, captureUtc) : null;
            ch.Untracked.Add(new UntrackedEntry { Utc = captureUtc, Amount = unexplained, Label = label });
            while (ch.Untracked.Count > WalletStore.UntrackedCap) ch.Untracked.RemoveAt(0);
            Logger.Info($"[WALLET] reconcile recorded {(unexplained > 0 ? "income" : "purchase")} {unexplained}");
            if (label is not null) Logger.Info($"[WALLET] income attributed: \"{label}\"");
        }
        else
        {
            Logger.Info("[WALLET] reconcile none");
        }

        ch.Anchor = balance;
        ch.AnchorUtc = captureUtc;
        ch.Source = "Ocr";
        QueueFlush();
    }

    private static bool IsStrictDigitPrefix(long value, long reference)
    {
        var v = Math.Abs(value).ToString(CultureInfo.InvariantCulture);
        var r = Math.Abs(reference).ToString(CultureInfo.InvariantCulture);
        return v.Length < r.Length && r.StartsWith(v, StringComparison.Ordinal);
    }

    // Unvoided settlements with AnchorUtc < t <= endUtc, signed (sell in, buy out).
    private long LedgerDeltaBetween(DateTime afterUtc, DateTime endUtc)
    {
        long sum = 0;
        foreach (var tx in _profit.Ledger.Transactions)
        {
            if (tx.Voided is not null) continue;
            if (tx.TimestampUtc <= afterUtc || tx.TimestampUtc > endUtc) continue;
            sum += tx.Kind == TransactionKind.Sell ? tx.Amount : -tx.Amount;
        }
        return sum;
    }

    // True when the scanned pixels and the ledger could straddle the same kiosk event. A voided
    // trade reaches further back: its void response landed up to a void-window after the request.
    private bool TradeRacesTheCapture(DateTime triggerUtc, DateTime captureUtc)
    {
        var plainFloor = triggerUtc - RaceGuard;
        var voidedFloor = plainFloor - SessionLedger.VoidWindow;
        foreach (var tx in _profit.Ledger.Transactions)
        {
            if (tx.TimestampUtc > captureUtc) continue;
            var floor = tx.Voided is null ? plainFloor : voidedFloor;
            if (tx.TimestampUtc >= floor) return true;
        }
        return false;
    }

    public void Ingest(GameLogEntry e)
    {
        if (_sessionStartUtc is null) TryLatchSession(e.Raw);
        if (WalletOcrTrigger.TryParseContractComplete(e.Raw, out var name, out var utc))
        {
            lock (_completionsLock)
            {
                _completions.Add((utc, name));
                while (_completions.Count > CompletionMemory) _completions.RemoveAt(0);
            }
        }
    }

    // A completion the anchored balance already contained explains nothing, so the window opens
    // strictly after the anchor. One hit names the row; several only count (naming one of them
    // would be a guess); none stays the plain untracked row.
    private string? CompletionLabel(DateTime anchorUtc, DateTime captureUtc)
    {
        List<string> hits;
        lock (_completionsLock)
        {
            hits = _completions.Where(c => c.Utc > anchorUtc && c.Utc <= captureUtc)
                               .Select(c => c.Name).ToList();
        }
        return hits.Count switch
        {
            0 => null,
            1 => hits[0],
            _ => $"{hits.Count} contracts completed",
        };
    }

    // New SC session (Game.log reset): the anchor survives (money does not reset with the log),
    // only the display-scope latch rolls.
    public void Reset()
    {
        Flush();
        _sessionStartUtc = null;
        Changed?.Invoke();
    }

    internal void OnFeedStarted(string path, bool willReplayToMe)
    {
        bool newFile = !string.Equals(path, _feedPath, StringComparison.OrdinalIgnoreCase);
        _feedPath = path;
        if (willReplayToMe || newFile) Reset();
    }

    private void OnProfitChanged() => Changed?.Invoke();

    private WalletChannelState? Current()
    {
        var channel = _channel();
        return _state.Channels.FirstOrDefault(c => c.Channel == channel);
    }

    private WalletChannelState CurrentOrCreate()
    {
        var existing = Current();
        if (existing is not null) return existing;
        var created = new WalletChannelState { Channel = _channel() };
        _state.Channels.Add(created);
        return created;
    }

    private void TryLatchSession(string raw)
    {
        if (raw.Length < 26 || raw[0] != '<' || raw[25] != '>') return;
        if (!DateTimeOffset.TryParseExact(raw.Substring(1, 24), "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
            return;
        _sessionStartUtc = dto.UtcDateTime;
    }

    private void QueueFlush()
    {
        if (_flushPending) return;
        _flushPending = true;
        if (_flushScheduler is not null) { _flushScheduler(Flush); return; }
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) { Flush(); return; }
        dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, (Action)Flush);
    }

    private void Flush()
    {
        if (!_flushPending) return;
        _flushPending = false;
        WalletStore.Save(_walletPath, _state);
        Changed?.Invoke();
    }

    public void Dispose()
    {
        Flush();
        _profit.Changed -= OnProfitChanged;
        _sub?.Dispose();
        _sub = null;
        if (_ownsFeed) _feed.Dispose();
    }
}
