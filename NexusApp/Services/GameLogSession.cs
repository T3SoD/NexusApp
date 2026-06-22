using System.IO;

namespace NexusApp.Services;

// One blueprint that was auto-marked owned during the current app session.
public sealed class BlueprintMark
{
    public DateTime At { get; init; } = DateTime.Now;
    public string Name { get; init; } = "";
}

// BETA / EXPERIMENTAL. App-lifetime owner of the Game.log watcher, the blueprint
// auto-mark, and the running "this session" tally. Both the standalone Game.log
// monitor window and the overlay's STATS tab bind to this single instance, so there
// is exactly one watcher and the count / feed / Start-Stop / Auto-mark state stay in
// sync across both surfaces. Reads a game-authored file — see [[nexus-gamelog-ownership]]
// and rework the EAC-safety wording before this ever ships in a release.
//
// Ownership and the blueprint name list are injected (not read from App directly) so
// the session logic is unit-testable headless — see GameLogSessionTests.
public sealed class GameLogSession : IDisposable
{
    private readonly GameLogWatcher _watcher = new();
    private readonly Func<IEnumerable<string>> _seedNames;
    private readonly Func<string, bool> _isOwned;
    private readonly Action<string, bool> _setOwned;
    private readonly List<BlueprintMark> _marks = new();
    private GameLogBlueprintImporter? _importer;

    public GameLogSession(
        Func<IEnumerable<string>> seedNames,
        Func<string, bool> isOwned,
        Action<string, bool> setOwned)
    {
        _seedNames = seedNames;
        _isOwned   = isOwned;
        _setOwned  = setOwned;
        _watcher.LineAppended  += Ingest;
        _watcher.StatusChanged += s => StatusChanged?.Invoke(s);
        _watcher.LogReset      += Reset;   // a new SC session starts a fresh tally
    }

    /// <summary>Path to start watching: the current one if it exists, else a best-effort probe.</summary>
    public string StartPath() =>
        !string.IsNullOrEmpty(Path) && File.Exists(Path) ? Path : GameLogWatcher.FindGameLog();

    /// <summary>Clears the session tally (blueprints collected). Fires on a new SC session or on demand.</summary>
    public void Reset()
    {
        _marks.Clear();
        SessionReset?.Invoke();
    }

    // Built lazily: the blueprint name list isn't ready until seed data loads.
    private GameLogBlueprintImporter Importer => _importer ??= new GameLogBlueprintImporter(_seedNames());

    /// <summary>Blueprints auto-marked owned so far this session, oldest first.</summary>
    public IReadOnlyList<BlueprintMark> Marks => _marks;
    public int Count => _marks.Count;

    public bool IsRunning => _watcher.IsRunning;
    public bool AutoMark { get; private set; }
    public string Path => _watcher.Path;
    public static string DefaultPath => GameLogWatcher.DefaultLivePath;

    /// <summary>A blueprint was just auto-marked owned (raised once per distinct new blueprint).</summary>
    public event Action<BlueprintMark>? Marked;
    /// <summary>Running / Auto-mark changed — bound UIs resync their Start-Stop + toggle.</summary>
    public event Action? StateChanged;
    /// <summary>Raw tailed line, for the standalone window's raw-log view.</summary>
    public event Action<GameLogEntry>? LineAppended;
    public event Action<string>? StatusChanged;
    /// <summary>A bulk ownership change happened outside the live feed (the past-logs import).</summary>
    public event Action? BulkOwnershipChanged;
    /// <summary>The session tally was cleared (new SC session, or a manual reset) — bound UIs reset their counts.</summary>
    public event Action? SessionReset;

    public void Start(string path, bool fromBeginning = false)
    {
        _watcher.Start(path, fromBeginning);
        StateChanged?.Invoke();
    }

    public void Stop()
    {
        _watcher.Stop();
        AutoMark = false;   // stopping the watch turns off auto-tracking — nothing to mark from
        StateChanged?.Invoke();
    }

    public void SetAutoMark(bool on)
    {
        if (AutoMark == on) return;
        AutoMark = on;
        // "Auto-Track Blueprints" implies watching — you can't mark from a log you're not reading.
        if (on && !_watcher.IsRunning)
            _watcher.Start(StartPath(), false);
        StateChanged?.Invoke();
    }

    /// <summary>Resolve a raw line to a canonical blueprint name without marking it (raw-log aid).</summary>
    public string? Resolve(string rawLine) => Importer.ResolveLine(rawLine);

    public GameLogBlueprintImporter.HistoryScan ScanHistory(string liveLogPath, Action<int>? progress = null)
        => Importer.ScanHistory(liveLogPath, progress);

    public void NotifyBulkOwnershipChanged() => BulkOwnershipChanged?.Invoke();

    /// <summary>
    /// Process one tailed line: surface it raw, and — when Auto-mark is on — mark a newly
    /// received blueprint owned exactly once, recording it in this session's tally.
    /// Public so the session logic can be exercised directly in tests; the watcher calls it.
    /// </summary>
    public void Ingest(GameLogEntry e)
    {
        LineAppended?.Invoke(e);
        if (!AutoMark || e.Category != LogCategory.Blueprint) return;

        var canon = Importer.ResolveLine(e.Raw);
        if (canon is null || _isOwned(canon)) return;

        _setOwned(canon, true);
        var mark = new BlueprintMark { Name = canon };
        _marks.Add(mark);
        Logger.Info($"[GameLog] auto-marked blueprint owned: {canon}");
        Marked?.Invoke(mark);
    }

    public void Dispose() => _watcher.Dispose();
}
