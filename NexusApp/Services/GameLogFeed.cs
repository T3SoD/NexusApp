namespace NexusApp.Services;

/// <summary>One consumer's attachment to the shared Game.log tail. Dispose to detach; the tail keeps
/// running for everyone else.</summary>
public sealed class GameLogSubscription : IDisposable
{
    private readonly Action<GameLogSubscription> _detach;

    internal GameLogSubscription(Action<GameLogEntry> onLine, bool includeReplay, Action? onLogReset,
                                 Action<string>? onStatus, Action<string, bool>? onStarted,
                                 Action<GameLogSubscription> detach)
    {
        OnLine = onLine;
        IncludeReplay = includeReplay;
        OnLogReset = onLogReset;
        OnStatus = onStatus;
        OnStarted = onStarted;
        _detach = detach;
    }

    internal Action<GameLogEntry> OnLine { get; }
    internal Action? OnLogReset { get; }
    internal Action<string>? OnStatus { get; }
    /// <summary>(path, willReplayToMe): the tail was (re)pointed, and whether THIS consumer is one of
    /// the ones the replayed history is routed to. Per-subscriber on purpose - a replay routed to a
    /// single consumer must not read as a from-the-top start to the others.</summary>
    internal Action<string, bool>? OnStarted { get; }

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
    private GameLogSubscription? _replayTarget;   // null = replayed history goes to all who want it

    public int Count => _subs.Count;

    /// <summary>True when at least one consumer wants replayed history. This is what decides whether
    /// a (re)start rewinds the single tail to byte 0 at all - with no such consumer there is nothing
    /// to replay to, so the tail just starts at the end of the file.</summary>
    public bool AnyWantsReplay => WouldReplay(null);

    /// <summary>Would a (re)start routed at <paramref name="target"/> replay anything? With no target
    /// that is "does anyone want history"; with one it is only that consumer's own appetite.</summary>
    public bool WouldReplay(GameLogSubscription? target) =>
        target is null ? _subs.Exists(static s => s.IncludeReplay)
                       : target.IncludeReplay && _subs.Contains(target);

    /// <summary>Does this replayed line belong to this subscriber? An untargeted replay follows each
    /// consumer's own appetite; a targeted one (the advanced monitor's from-the-top re-read) goes to
    /// that consumer alone, so the trackers are never made to re-process a log they already read.</summary>
    private static bool Receives(GameLogSubscription s, GameLogSubscription? target) =>
        target is null ? s.IncludeReplay : ReferenceEquals(s, target);

    public GameLogSubscription Add(Action<GameLogEntry> onLine, bool includeReplay,
                                   Action? onLogReset = null, Action<string>? onStatus = null,
                                   Action<string, bool>? onStarted = null)
    {
        var sub = new GameLogSubscription(onLine, includeReplay, onLogReset, onStatus, onStarted, Remove);
        _subs.Add(sub);
        _snapshot = _subs.ToArray();
        return sub;
    }

    public void Remove(GameLogSubscription sub)
    {
        if (!_subs.Remove(sub)) return;
        // The routing target is deliberately NOT cleared here: if the consumer that asked for the
        // replay leaves mid-window (Stop during a from-the-top re-read), the rest of that history
        // belongs to nobody. Handing it to the trackers would give them a partial replay, which is
        // exactly what routing it away from them prevented. Live lines are unaffected.
        _snapshot = _subs.ToArray();
    }

    /// <summary>The tail was (re)pointed: latch the replay routing for the window it opens, and tell
    /// each consumer whether that history is coming its way.</summary>
    public void Started(string path, bool fromBeginning, GameLogSubscription? target)
    {
        _replayTarget = fromBeginning ? target : null;
        foreach (var s in _snapshot) s.OnStarted?.Invoke(path, fromBeginning && Receives(s, target));
    }

    /// <summary>Fan one tailed line out. Replayed history reaches only the consumers it is routed to;
    /// live lines reach everyone.</summary>
    public void Line(GameLogEntry e)
    {
        foreach (var s in _snapshot)
            if (!e.IsReplay || Receives(s, _replayTarget)) s.OnLine(e);
    }

    /// <summary>The log was truncated/recreated - Star Citizen started a new session.</summary>
    public void LogReset()
    {
        _replayTarget = null;   // a fresh session's lines are live, and belong to every consumer
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
/// (re)pointed with Start - once at startup, again whenever the Game.log path changes, and again
/// when one consumer asks to re-read the file from the top (which is routed to it alone).
///
/// Events are raised from the watcher's DispatcherTimer, i.e. on the UI thread: handlers may touch
/// WPF directly and need no Dispatcher marshaling.</summary>
public sealed class GameLogFeed : IDisposable
{
    private readonly GameLogWatcher _watcher = new();
    private readonly GameLogFanOut _subs = new();

    // Issue #28 auto-follow: every ~2s (matching the process-probe cadence) stat the sibling
    // channel Game.logs and re-point the ONE tail if another channel took over. Stats only -
    // never a second watcher, never a process handle.
    private readonly System.Windows.Threading.DispatcherTimer _probeTimer;

    /// <summary>Launcher channel of the file the tail currently watches. Derived from the path on
    /// every (re)point; Custom for unrecognized folders (single-file mode, no auto-follow).</summary>
    public GameChannel ActiveChannel { get; private set; } = GameChannel.Live;
    /// <summary>The watched channel changed (auto-follow or a manual path change).</summary>
    public event Action<GameChannel>? ChannelChanged;

    public GameLogFeed()
    {
        _watcher.LineAppended  += _subs.Line;
        _watcher.LogReset      += _subs.LogReset;
        _watcher.StatusChanged += _subs.Status;
        _watcher.SessionLiveChanged += live => SessionLiveChanged?.Invoke(live);

        _probeTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _probeTimer.Tick += (_, _) => AutoFollowTick();
        _probeTimer.Start();
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
        var anchor = !string.IsNullOrEmpty(Path) && System.IO.File.Exists(Path) ? Path
            : !string.IsNullOrEmpty(PreferredPath) ? PreferredPath
            : GameLogWatcher.FindGameLog();
        var candidates = GameChannelProbe.Candidates(anchor);
        if (candidates.Count == 0) return anchor;   // custom layout: unchanged single-file behavior
        // One surviving candidate still runs selection: it wins over an anchor whose own file is
        // gone (a persisted path to an uninstalled channel), and a healthy single-channel install
        // no-ops because selection returns that same path.
        return GameChannelProbe.SelectActive(StatAll(candidates), anchor, DateTime.UtcNow, IsSessionLive) ?? anchor;
    }

    // Stat every candidate, dropping any that vanished or refused between the probe and the stat
    // (a channel folder can be removed mid-tick). Shared by StartPath and AutoFollowTick so both
    // sites keep the same swallow-and-continue behavior.
    private static List<GameChannelProbe.LogStat> StatAll(IReadOnlyList<string> candidates)
    {
        var stats = new List<GameChannelProbe.LogStat>(candidates.Count);
        foreach (var c in candidates) { try { stats.Add(GameChannelProbe.Stat(c)); } catch { } }
        return stats;
    }

    /// <summary>The tail was (re)pointed at this path. Feed-level and deliberately replay-agnostic:
    /// for "is that history coming to ME", a consumer uses its subscription's onStarted callback.</summary>
    public event Action<string>? Started;
    /// <summary>Star Citizen's running state changed (its process appeared / exited).</summary>
    public event Action<bool>? SessionLiveChanged;

    public GameLogSubscription Subscribe(Action<GameLogEntry> onLine, bool includeReplay,
                                         Action? onLogReset = null, Action<string>? onStatus = null,
                                         Action<string, bool>? onStarted = null)
        => _subs.Add(onLine, includeReplay, onLogReset, onStatus, onStarted);

    /// <summary>(Re)point the one tail at <paramref name="path"/>. It rewinds to the top of the file
    /// only when the replay would actually reach someone; consumers that want live lines only are
    /// filtered, not re-read. <paramref name="replayOnlyTo"/> routes the replayed history to a single
    /// consumer - the advanced monitor's "From start of file", which must not make the haul and shard
    /// trackers re-process a log they already read. Null replays to everyone who wants it, which is
    /// what a Game.log path change does.</summary>
    public void Start(string path, GameLogSubscription? replayOnlyTo = null)
    {
        bool fromBeginning = _subs.WouldReplay(replayOnlyTo);
        _watcher.Start(path, fromBeginning);
        var route = !fromBeginning ? "none" : replayOnlyTo is null ? "all consumers" : "requesting consumer only";
        var channel = GameChannels.FromLogPath(_watcher.Path);
        bool channelFlip = channel != ActiveChannel;
        ActiveChannel = channel;
        Logger.Info($"[GameLog] tail (re)pointed: {Redact(_watcher.Path)}; replay={route}; channel={GameChannels.FolderName(channel)}");
        _subs.Started(_watcher.Path, fromBeginning, replayOnlyTo);
        Started?.Invoke(_watcher.Path);
        if (channelFlip) ChannelChanged?.Invoke(channel);
    }

    public void Stop() => _watcher.Stop();

    private void AutoFollowTick()
    {
        if (!IsRunning) return;
        try
        {
            var candidates = GameChannelProbe.Candidates(Path);
            if (candidates.Count == 0) return;   // custom layout: nothing to follow
            // A single candidate is still evaluated: when the watched file itself is gone (its
            // channel was uninstalled while the path stayed persisted), the survivor recovers the
            // tail. An intact single-channel install selects itself and falls through the guard below.
            var next = GameChannelProbe.SelectActive(StatAll(candidates), Path, DateTime.UtcNow, IsSessionLive);
            if (next is null || string.Equals(next, Path, StringComparison.OrdinalIgnoreCase)) return;
            Logger.Info($"[GameLog] channel switch: {GameChannels.FolderName(ActiveChannel)} -> " +
                        $"{GameChannels.FolderName(GameChannels.FromLogPath(next))} (auto-follow)");
            Start(next);   // normal re-point: replay routing and consumer notifications as a manual change
        }
        catch { /* probe hiccup: try again next tick */ }
    }

    public void Dispose()
    {
        _probeTimer.Stop();
        _watcher.Dispose();
    }

    // The path can sit under the user's profile (a free-text Settings override or an auto-detected
    // install path), so redact at the source - the same helper App's startup line and the diagnostic
    // snapshot use - and it never lands unredacted in nexus.log in the first place.
    private static string Redact(string path) => string.IsNullOrEmpty(path)
        ? "<no log found yet>"
        : DiagnosticSnapshot.RedactUserProfile(path, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
}
