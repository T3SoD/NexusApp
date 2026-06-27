using System.Linq;
using NexusApp.Models;

namespace NexusApp.Services;

// App-lifetime tracker of the current shard + recent shard history, parsed from Game.log <Join PU>
// lines. Mirrors HaulTracker (own GameLogWatcher, public Ingest for headless tests). Persistence is
// injected so the rolling list survives app/SC relaunches. No PII (server ip only). History is NOT
// cleared on a new SC session (LogReset) - the point is to remember across sessions.
public sealed class ShardTracker : IDisposable
{
    private const int MaxKeep = 4;   // current + last 3
    private readonly GameLogWatcher _watcher = new();
    private readonly List<ShardSession> _history = new();   // newest first; current = [0]
    private readonly Action<IReadOnlyList<ShardSession>> _save;

    public ShardTracker(Func<IEnumerable<ShardSession>> loadPersisted, Action<IReadOnlyList<ShardSession>> savePersisted)
    {
        _save = savePersisted;
        _history.AddRange(loadPersisted() ?? Enumerable.Empty<ShardSession>());
        _watcher.LineAppended += Ingest;
        // intentionally NOT subscribing LogReset: shard history persists across SC sessions.
    }

    private bool _onShard;   // true while in the PU: set by a <Join PU>, cleared by the EAC EndSession

    /// <summary>True while currently in the PU (between a join and the next leave/EndSession).</summary>
    public bool OnShard => _onShard;
    /// <summary>The shard the player is on right now, or null after they have left (until the next join).</summary>
    public ShardSession? Current => _onShard && _history.Count > 0 ? _history[0] : null;
    /// <summary>Recent shards: when on a shard, the 3 before it; when off, the last 3 (the shard just
    /// left now sits here).</summary>
    public IReadOnlyList<ShardSession> Recent => (_onShard ? _history.Skip(1) : _history).Take(3).ToList();
    public IReadOnlyList<ShardSession> All => _history;

    public event Action? Changed;

    public string PreferredPath { get; set; } = "";
    public bool IsRunning => _watcher.IsRunning;
    public string Path => _watcher.Path;

    public string StartPath()
    {
        if (!string.IsNullOrEmpty(Path) && System.IO.File.Exists(Path)) return Path;
        if (!string.IsNullOrEmpty(PreferredPath)) return PreferredPath;
        return GameLogWatcher.FindGameLog();
    }

    public void Start(string path, bool fromBeginning = true) => _watcher.Start(path, fromBeginning);
    public void Stop() => _watcher.Stop();

    public void Ingest(GameLogEntry e)
    {
        var raw = e.Raw;

        // Left the PU (to menu / quit / disconnect): EAC ends the session. Clear "current" - the last
        // shard stays in history and slides into RECENT - until the next join.
        if (raw.Contains("CDisciplineServiceExternal::EndSession"))
        {
            if (_onShard) { _onShard = false; Logger.Info("[SHARD] left the shard"); Changed?.Invoke(); }
            return;
        }

        if (!raw.Contains("<Join PU>")) return;
        var s = ShardLogParser.ParseJoin(raw);
        if (s is null) return;

        // Re-joined the SAME shard (e.g. after a brief disconnect): just mark us back on it.
        if (_history.Count > 0 && _history[0].ShardId == s.ShardId)
        {
            if (!_onShard) { _onShard = true; Changed?.Invoke(); }
            return;
        }

        _history.Insert(0, s);
        while (_history.Count > MaxKeep) _history.RemoveAt(_history.Count - 1);
        _onShard = true;
        _save(_history);
        Logger.Info($"[SHARD] joined {s.Region} shard {s.Instance} ({s.ShardId})");
        Changed?.Invoke();
    }

    public void Dispose() => _watcher.Dispose();
}
