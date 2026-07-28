namespace NexusApp.Services;

/// <summary>One consumer's attachment to the shared Game.log tail. Dispose to detach; the tail keeps
/// running for everyone else.</summary>
public sealed class GameLogSubscription : IDisposable
{
    private readonly Action<GameLogSubscription> _detach;

    internal GameLogSubscription(Action<GameLogEntry> onLine, bool includeReplay, Action? onLogReset,
                                 Action<string>? onStatus, Action<GameLogSubscription> detach)
    {
        OnLine = onLine;
        IncludeReplay = includeReplay;
        OnLogReset = onLogReset;
        OnStatus = onStatus;
        _detach = detach;
    }

    internal Action<GameLogEntry> OnLine { get; }
    internal Action? OnLogReset { get; }
    internal Action<string>? OnStatus { get; }

    /// <summary>True when this consumer also wants the history a from-the-top (re)start replays, not
    /// just the lines the game writes from now on. Settable: the blueprint session joins live at
    /// startup but replays the file after the user re-points the Game.log path.</summary>
    public bool IncludeReplay { get; set; }

    public void Dispose() => _detach(this);
}

/// <summary>Subscriber bookkeeping and the replay routing decision for the shared tail. Deliberately
/// pure - no file, no timer, no WPF - so the fan-out is exercised headless (GameLogFeedTests) even
/// though the watcher that drives it is DispatcherTimer-bound.</summary>
public sealed class GameLogFanOut
{
    private readonly List<GameLogSubscription> _subs = new();
    // Dispatch walks a snapshot, so a handler that detaches itself mid-line (the monitor window's
    // Stop) can never disturb the walk, and the per-line path allocates nothing.
    private GameLogSubscription[] _snapshot = Array.Empty<GameLogSubscription>();

    public int Count => _subs.Count;

    /// <summary>True when at least one consumer wants replayed history. This is what decides whether
    /// a (re)start rewinds the single tail to byte 0 at all - with no such consumer there is nothing
    /// to replay to, so the tail just starts at the end of the file.</summary>
    public bool AnyWantsReplay => _subs.Exists(static s => s.IncludeReplay);

    public GameLogSubscription Add(Action<GameLogEntry> onLine, bool includeReplay,
                                   Action? onLogReset = null, Action<string>? onStatus = null)
    {
        var sub = new GameLogSubscription(onLine, includeReplay, onLogReset, onStatus, Remove);
        _subs.Add(sub);
        _snapshot = _subs.ToArray();
        return sub;
    }

    public void Remove(GameLogSubscription sub)
    {
        if (!_subs.Remove(sub)) return;
        _snapshot = _subs.ToArray();
    }

    /// <summary>Fan one tailed line out. Replayed history reaches only the consumers that asked for
    /// it; live lines reach everyone.</summary>
    public void Line(GameLogEntry e)
    {
        foreach (var s in _snapshot)
            if (!e.IsReplay || s.IncludeReplay) s.OnLine(e);
    }

    /// <summary>The log was truncated/recreated - Star Citizen started a new session.</summary>
    public void LogReset()
    {
        foreach (var s in _snapshot) s.OnLogReset?.Invoke();
    }

    public void Status(string text)
    {
        foreach (var s in _snapshot) s.OnStatus?.Invoke(text);
    }
}

/// <summary>The app's single Game.log tail. ONE GameLogWatcher - one 500ms DispatcherTimer, one file
/// position, one FileStream cycle, one process-presence probe - fanned out to every consumer: the
/// blueprint session (GameLogSession), the haul tracker and the shard tracker. Before this existed
/// each of the three owned a watcher and independently re-read and re-parsed the same file forever.
///
/// Consumers attach with Subscribe and detach by disposing the handle, so one of them stopping (the
/// advanced monitor's Stop button) never stops the tail the others depend on. The tail itself is
/// (re)pointed with Start - once at startup and again whenever the Game.log path changes.
///
/// Events are raised from the watcher's DispatcherTimer, i.e. on the UI thread: handlers may touch
/// WPF directly and need no Dispatcher marshaling.</summary>
public sealed class GameLogFeed : IDisposable
{
    private readonly GameLogWatcher _watcher = new();
    private readonly GameLogFanOut _subs = new();

    public GameLogFeed()
    {
        _watcher.LineAppended  += _subs.Line;
        _watcher.LogReset      += _subs.LogReset;
        _watcher.StatusChanged += _subs.Status;
        _watcher.SessionLiveChanged += live => SessionLiveChanged?.Invoke(live);
    }

    /// <summary>The user's saved Game.log path (injected from settings); honored over the install
    /// probe so a custom location survives restarts, even if the file isn't present yet. "" = none.</summary>
    public string PreferredPath { get; set; } = "";

    public string Path => _watcher.Path;
    public bool IsRunning => _watcher.IsRunning;
    /// <summary>True while the Star Citizen process is running (independent of window focus). Probed
    /// once here for all consumers - never with a handle to the game process.</summary>
    public bool IsSessionLive => _watcher.IsSessionLive;

    /// <summary>Path to start watching: the active one if it exists, then the user's saved path,
    /// else a best-effort probe of common installs.</summary>
    public string StartPath()
    {
        if (!string.IsNullOrEmpty(Path) && System.IO.File.Exists(Path)) return Path;
        if (!string.IsNullOrEmpty(PreferredPath)) return PreferredPath;
        return GameLogWatcher.FindGameLog();
    }

    /// <summary>The tail was (re)pointed: (path, replayedFromTheTop). Raised before any line of the
    /// new window is pumped, so consumers can prepare state that depends on the source file.</summary>
    public event Action<string, bool>? Started;
    /// <summary>Star Citizen's running state changed (its process appeared / exited).</summary>
    public event Action<bool>? SessionLiveChanged;

    public GameLogSubscription Subscribe(Action<GameLogEntry> onLine, bool includeReplay,
                                         Action? onLogReset = null, Action<string>? onStatus = null)
        => _subs.Add(onLine, includeReplay, onLogReset, onStatus);

    /// <summary>(Re)point the one tail at <paramref name="path"/>. It rewinds to the top of the file
    /// whenever a consumer wants the replay (hauls and shards always do, so they rebuild their state
    /// from the whole log); consumers that only want live lines are filtered, not re-read.</summary>
    public void Start(string path)
    {
        bool fromBeginning = _subs.AnyWantsReplay;
        _watcher.Start(path, fromBeginning);
        Started?.Invoke(_watcher.Path, fromBeginning);
    }

    public void Stop() => _watcher.Stop();

    public void Dispose() => _watcher.Dispose();
}
