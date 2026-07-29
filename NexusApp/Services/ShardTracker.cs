using System.Linq;
using NexusApp.Models;

namespace NexusApp.Services;

// App-lifetime tracker of the current shard + recent shard history, parsed from Game.log <Join PU>
// lines. Mirrors HaulTracker (consumer of the shared GameLogFeed, public Ingest for headless tests).
// Persistence is injected so the rolling list survives app/SC relaunches. No PII (server ip only).
// History is NOT cleared on a new SC session (LogReset) - the point is to remember across sessions.
public sealed class ShardTracker : IDisposable
{
    private const int MaxKeep = 4;   // current + last 3
    private readonly GameLogFeed _feed;
    private readonly bool _ownsFeed;            // true only when nobody handed us a shared feed
    private GameLogSubscription? _sub;
    private readonly List<ShardSession> _history = new();   // newest first; current = [0]
    private readonly Action<IReadOnlyList<ShardSession>> _save;
    private readonly Func<string>? _channelTag;

    public ShardTracker(Func<IEnumerable<ShardSession>> loadPersisted,
                        Action<IReadOnlyList<ShardSession>> savePersisted,
                        GameLogFeed? feed = null,
                        Func<string>? channelTag = null)
    {
        _save = savePersisted;
        _channelTag = channelTag;
        _history.AddRange(loadPersisted() ?? Enumerable.Empty<ShardSession>());
        _feed = feed ?? new GameLogFeed();
        _ownsFeed = feed is null;
        // Always replays the log from the top: the point is to recover this session's joins.
        // History intentionally persists across SC sessions (no history clear on LogReset), but a
        // fresh log means a live game session, so stale-replay suppression lifts.
        _sub = _feed.Subscribe(Ingest, includeReplay: true,
            onLogReset: () => _staleReplay = false, onStarted: OnFeedStarted);
    }

    private bool _onShard;      // true while in the PU: set by a <Join PU>, cleared by a leave marker
    private bool _staleReplay;  // replaying a cold leftover log: record history, never claim on-shard

    // SC 4.8 logs end sessions with <SystemQuit>; older logs used the EAC EndSession line.
    private static bool IsLeaveLine(string raw) =>
        raw.Contains("CDisciplineServiceExternal::EndSession") || raw.Contains("<SystemQuit>");

    /// <summary>True while currently in the PU (between a join and the next leave/EndSession).</summary>
    public bool OnShard => _onShard;
    /// <summary>The shard the player is on right now, or null after they have left (until the next join).</summary>
    public ShardSession? Current => _onShard && _history.Count > 0 ? _history[0] : null;
    /// <summary>Recent shards: when on a shard, the 3 before it; when off, the last 3 (the shard just
    /// left now sits here).</summary>
    public IReadOnlyList<ShardSession> Recent => (_onShard ? _history.Skip(1) : _history).Take(3).ToList();
    public IReadOnlyList<ShardSession> All => _history;

    public event Action? Changed;

    // A log untouched for a few minutes is a leftover from a previous game session: replay it for
    // history, but never claim to be on a shard from it (the chip showed a stale shard as live).
    private static readonly TimeSpan ColdLogAge = TimeSpan.FromMinutes(3);

    // The shared tail was (re)pointed. Re-decide the cold-log question against the file it now reads,
    // before its first replayed line arrives - but ONLY when that replay is actually coming here: a
    // from-the-top re-read routed to another consumer (the advanced monitor) is not a replay of ours
    // to suppress, and must leave the current stale-replay state exactly as it is.
    private void OnFeedStarted(string path, bool willReplayToMe)
    {
        if (!willReplayToMe) return;
        _staleReplay = false;
        try
        {
            if (System.IO.File.Exists(path)
                && DateTime.UtcNow - System.IO.File.GetLastWriteTimeUtc(path) > ColdLogAge)
                BeginStaleReplay();
        }
        catch { _staleReplay = false; }
    }

    /// <summary>Suppress on-shard claims while replaying a stale log (see OnFeedStarted; public for headless tests).</summary>
    public void BeginStaleReplay()
    {
        _staleReplay = true;
        Logger.Info("[SHARD] cold Game.log replay - loading shard history without on-shard state");
    }

    public void Ingest(GameLogEntry e)
    {
        var raw = e.Raw;

        // Left the PU (to menu / quit / disconnect). Clear "current" - the last shard stays in
        // history and slides into RECENT - until the next join.
        if (IsLeaveLine(raw))
        {
            if (_onShard) { _onShard = false; Logger.Info("[SHARD] left the shard"); Changed?.Invoke(); }
            return;
        }

        if (!raw.Contains("<Join PU>")) return;
        var s = ShardLogParser.ParseJoin(raw);
        if (s is null) return;
        s.Channel = _channelTag?.Invoke() ?? "";

        // Re-joined the SAME shard (e.g. after a brief disconnect): just mark us back on it.
        if (_history.Count > 0 && _history[0].ShardId == s.ShardId)
        {
            if (!_onShard && !_staleReplay) { _onShard = true; Changed?.Invoke(); }
            return;
        }

        _history.Insert(0, s);
        while (_history.Count > MaxKeep) _history.RemoveAt(_history.Count - 1);
        _onShard = !_staleReplay;
        _save(_history);
        Logger.Info($"[SHARD] joined {s.Region} shard {s.Instance} ({s.ShardId})"
            + (s.Channel is "" or "LIVE" ? "" : $" [{s.Channel}]"));
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _sub?.Dispose();
        _sub = null;
        if (_ownsFeed) _feed.Dispose();   // a shared feed is the app's to dispose, not ours
    }
}
