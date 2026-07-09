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
// sync across both surfaces. Reads a game-authored file - see [[nexus-gamelog-ownership]]
// and rework the EAC-safety wording before this ever ships in a release.
//
// Ownership and the blueprint name list are injected (not read from App directly) so
// the session logic is unit-testable headless - see GameLogSessionTests.
public sealed class GameLogSession : IDisposable
{
    private readonly GameLogWatcher _watcher = new();
    private readonly Func<IEnumerable<string>> _seedNames;
    private readonly Func<string, bool> _isOwned;
    private readonly Action<string, bool> _setOwned;
    // Given a Game.log path, returns the user's customDisplay -> official localization map (or null).
    // Injected so the session stays headlessly testable; built from their global.ini in App.
    private readonly Func<string, IReadOnlyDictionary<string, string>?>? _localizationMapFor;
    private readonly List<BlueprintMark> _marks = new();
    private GameLogBlueprintImporter? _importer;
    private string _lastHandle = "";   // dedupe the HandleDetected event
    private IReadOnlyDictionary<string, string>? _liveLocalizationMap;   // cached for the live tail
    private bool _liveLocalizationBuilt;                                 // rebuilt on a new SC session
    // Unresolved receipt names already logged this session - one nexus.log line per distinct name,
    // not one per notification-lifecycle repeat (issue #17 diagnostics).
    private readonly HashSet<string> _unresolvedLogged = new(StringComparer.OrdinalIgnoreCase);

    public GameLogSession(
        Func<IEnumerable<string>> seedNames,
        Func<string, bool> isOwned,
        Action<string, bool> setOwned,
        Func<string, IReadOnlyDictionary<string, string>?>? localizationMapFor = null)
    {
        _seedNames = seedNames;
        _isOwned   = isOwned;
        _setOwned  = setOwned;
        _localizationMapFor = localizationMapFor;
        _watcher.LineAppended  += Ingest;
        _watcher.StatusChanged += s => StatusChanged?.Invoke(s);
        _watcher.LogReset      += Reset;   // a new SC session starts a fresh tally
        _watcher.SessionLiveChanged += OnSessionLiveChanged;
    }

    /// <summary>The user's saved Game.log path (injected from settings); honored over the install
    /// probe so a custom location survives restarts, even if the file isn't present yet. "" = none.</summary>
    public string PreferredPath { get; set; } = "";

    /// <summary>Path to start watching: the active one if it exists, then the user's saved path,
    /// else a best-effort probe of common installs.</summary>
    public string StartPath()
    {
        if (!string.IsNullOrEmpty(Path) && File.Exists(Path)) return Path;
        if (!string.IsNullOrEmpty(PreferredPath)) return PreferredPath;
        return GameLogWatcher.FindGameLog();
    }

    /// <summary>Clears the session tally (blueprints collected). Fires on a new SC session or on demand.</summary>
    public void Reset()
    {
        _marks.Clear();
        _liveLocalizationMap = null;   // a new SC session may follow a localization change - rebuild lazily
        _liveLocalizationBuilt = false;
        _unresolvedLogged.Clear();     // still-unresolved receipts are worth one fresh line per session
        SessionReset?.Invoke();
    }

    // The user's localization map for the live tail, built once (lazily, off the active log path) and
    // reused per line; rebuilt after a session reset. Null when no builder is wired or no file is found.
    private IReadOnlyDictionary<string, string>? LiveLocalizationMap()
    {
        if (!_liveLocalizationBuilt)
        {
            _liveLocalizationMap = _localizationMapFor?.Invoke(StartPath());
            _liveLocalizationBuilt = true;
        }
        return _liveLocalizationMap;
    }

    /// <summary>Drop the cached live-tail localization map so the next line rebuilds it fresh.
    /// Called when the global.ini setting changes - waiting for a new SC session is too late.</summary>
    public void InvalidateLocalizationMap()
    {
        _liveLocalizationMap = null;
        _liveLocalizationBuilt = false;
    }

    // Built lazily: the blueprint name list isn't ready until seed data loads. The bundled official
    // component names power the importer's structural name fallback (issue #17).
    private GameLogBlueprintImporter Importer => _importer ??=
        new GameLogBlueprintImporter(_seedNames(), ComponentStringReference.KeyToOfficialName.Values);

    /// <summary>Blueprints auto-marked owned so far this session, oldest first.</summary>
    public IReadOnlyList<BlueprintMark> Marks => _marks;
    public int Count => _marks.Count;

    public bool IsRunning => _watcher.IsRunning;
    /// <summary>Star Citizen is running (its process is alive), independent of window focus. False once it exits.</summary>
    public bool IsSessionLive => _watcher.IsSessionLive;
    public bool AutoMark { get; private set; }
    public string Path => _watcher.Path;
    public static string DefaultPath => GameLogWatcher.DefaultLivePath;

    /// <summary>A blueprint was just auto-marked owned (raised once per distinct new blueprint).</summary>
    public event Action<BlueprintMark>? Marked;
    /// <summary>Running / Auto-mark changed - bound UIs resync their Start-Stop + toggle.</summary>
    public event Action? StateChanged;
    /// <summary>SC running-state changed (Game.log went live or stale); bound session/blueprint pills flip on/off.</summary>
    public event Action<bool>? SessionLiveChanged;
    /// <summary>Raw tailed line, for the standalone window's raw-log view.</summary>
    public event Action<GameLogEntry>? LineAppended;
    public event Action<string>? StatusChanged;
    /// <summary>A bulk ownership change happened outside the live feed (the past-logs import).</summary>
    public event Action? BulkOwnershipChanged;
    /// <summary>The session tally was cleared (new SC session, or a manual reset) - bound UIs reset their counts.</summary>
    public event Action? SessionReset;
    /// <summary>The local player's RSI handle was detected in Game.log (read-only). Fires once per distinct handle.</summary>
    public event Action<string>? HandleDetected;

    public void Start(string path, bool fromBeginning = false)
    {
        // Re-pointing the watcher moves the DERIVED global.ini path with it - drop the cached
        // live map so the next line rebuilds it against the new location (covers the Settings
        // Game.log path change and the monitor's path box in one place).
        InvalidateLocalizationMap();
        _watcher.Start(path, fromBeginning);
        StateChanged?.Invoke();
    }

    public void Stop()
    {
        _watcher.Stop();
        AutoMark = false;   // stopping the watch turns off auto-tracking - nothing to mark from
        StateChanged?.Invoke();
    }

    public void SetAutoMark(bool on)
    {
        if (AutoMark == on) return;
        AutoMark = on;
        // "Auto-Track Blueprints" implies watching - you can't mark from a log you're not reading.
        if (on && !_watcher.IsRunning)
            _watcher.Start(StartPath(), false);
        StateChanged?.Invoke();
    }

    /// <summary>Resolve a raw line to a canonical blueprint name without marking it (raw-log aid).</summary>
    public string? Resolve(string rawLine) => Importer.ResolveLine(rawLine, LiveLocalizationMap());

    public GameLogBlueprintImporter.HistoryScan ScanHistory(string liveLogPath, Action<int>? progress = null)
    {
        var scan = Importer.ScanHistory(liveLogPath, progress, _localizationMapFor?.Invoke(liveLogPath));
        // Every unmatched receipt lands in the dedicated diagnostics file with its map context, so
        // a user report can carry exactly the data needed to fix the mapping (issue #17).
        foreach (var line in scan.UnmatchedLines)
        {
            var raw = GameLogBlueprintImporter.ExtractRawName(line);
            if (raw is not null) UnmatchedBlueprintLog.Record("import scan", raw, line, scan.LocalizationEntries);
        }
        return scan;
    }

    public void NotifyBulkOwnershipChanged() => BulkOwnershipChanged?.Invoke();

    /// <summary>
    /// Process one tailed line: surface it raw, and - when Auto-mark is on - mark a newly
    /// received blueprint owned exactly once, recording it in this session's tally.
    /// Public so the session logic can be exercised directly in tests; the watcher calls it.
    /// </summary>
    public void Ingest(GameLogEntry e)
    {
        LineAppended?.Invoke(e);

        // Capture the player's RSI handle from login lines as they stream past (read-only), so
        // export can pre-fill it. Independent of Auto-mark / the blueprint handling below.
        if (RsiHandleParser.TryExtract(e.Raw, out var liveHandle)) PublishHandle(liveHandle);

        if (!AutoMark || e.Category != LogCategory.Blueprint) return;

        var canon = Importer.ResolveLine(e.Raw, LiveLocalizationMap(), out bool viaFallback);
        if (canon is null)
        {
            // Surface the failure in nexus.log AND the dedicated unmatched-blueprints file (issue
            // #17: these used to vanish silently). The raw name is game-item display text - same
            // data the import dialog already shows; no paths.
            var raw = GameLogBlueprintImporter.ExtractRawName(e.Raw);
            if (raw is not null && _unresolvedLogged.Add(raw))
            {
                Logger.Info($"[GameLog] blueprint receipt unrecognized: {raw}");
                UnmatchedBlueprintLog.Record("live", raw, e.Raw, LiveLocalizationMap()?.Count);
            }
            return;
        }
        if (_isOwned(canon)) return;

        _setOwned(canon, true);
        var mark = new BlueprintMark { Name = canon };
        _marks.Add(mark);
        Logger.Info(viaFallback
            ? $"[GameLog] auto-marked blueprint owned: {canon} (via name fallback)"
            : $"[GameLog] auto-marked blueprint owned: {canon}");
        Marked?.Invoke(mark);
    }

    /// <summary>One-shot scan of the current Game.log for the player's RSI handle (written at login,
    /// near the top). Lets export pre-fill the handle even when the live tail started after login.
    /// Reads with shared access since the game keeps the file open for writing.</summary>
    public void DetectHandleFromCurrentFile()
    {
        var handle = RsiHandleParser.ScanFile(StartPath());
        if (!string.IsNullOrWhiteSpace(handle)) PublishHandle(handle!);
    }

    private void PublishHandle(string handle)
    {
        if (string.IsNullOrWhiteSpace(handle) || string.Equals(handle, _lastHandle, StringComparison.OrdinalIgnoreCase)) return;
        _lastHandle = handle;
        Logger.Info("[NET] local RSI handle detected from Game.log");
        HandleDetected?.Invoke(handle);
    }

    // The game opened or closed (Game.log went fresh or stale). Re-raise it for pill listeners, and refresh
    // the generic StateChanged so the running / auto-mark bound UIs (session + blueprint pills) recompute.
    private void OnSessionLiveChanged(bool live)
    {
        SessionLiveChanged?.Invoke(live);
        StateChanged?.Invoke();
    }

    public void Dispose() => _watcher.Dispose();
}
