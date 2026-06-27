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

    public ShardSession? Current => _history.Count > 0 ? _history[0] : null;
    public IReadOnlyList<ShardSession> Recent => _history.Skip(1).Take(3).ToList();
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
        if (!e.Raw.Contains("<Join PU>")) return;
        var s = ShardLogParser.ParseJoin(e.Raw);
        if (s is null) return;
        if (_history.Count > 0 && _history[0].ShardId == s.ShardId) return;   // dedupe consecutive re-joins
        _history.Insert(0, s);
        while (_history.Count > MaxKeep) _history.RemoveAt(_history.Count - 1);
        _save(_history);
        Logger.Info($"[SHARD] joined {s.Region} shard {s.Instance} ({s.ShardId})");
        Changed?.Invoke();
    }

    public void Dispose() => _watcher.Dispose();
}
