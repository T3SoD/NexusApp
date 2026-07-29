using System.IO;
using System.Text;

namespace NexusApp.Services;

// BETA / EXPERIMENTAL. Reads the Star Citizen Game.log the client writes to disk.
// NOTE: this reads a game-authored file, which differs from Nexus's usual
// "screen-OCR only, no game files" model - keep it on the beta branch and rework
// the EAC-safety wording before it ever ships in a release.

public enum LogCategory
{
    Other, Blueprint, Kill, VehicleDestruction, Location, Spawn, Login, Version, Quantum, Connection, Mission, Economy,
}

public sealed class GameLogEntry
{
    public DateTime ReceivedAt { get; init; } = DateTime.Now;
    public string Raw { get; init; } = "";
    public LogCategory Category { get; init; }
    /// <summary>True for lines that were already in the file when the tail (re)started from the top -
    /// history being replayed, not a line the game just wrote. GameLogFeed routes on this so one tail
    /// can serve consumers that rebuild their state from the whole log AND consumers that only want
    /// what happens from now on.</summary>
    public bool IsReplay { get; init; }
}

// Tails Game.log live WITHOUT locking it: the game keeps the file open for writing,
// so we open it shared (FileShare.ReadWrite) and poll for appended bytes. Handles
// the file being recreated/truncated on a new game session. UI-thread timer, so all
// events are raised on the dispatcher - safe to touch UI from handlers.
//
// For Game.log there is exactly ONE of these app-wide, owned by GameLogFeed, which fans
// it out to the blueprint session, the haul tracker and the shard tracker. Do not add a
// second Game.log watcher: that is three file reads, three parse passes and three process
// probes for one file. (AppLogMonitorWindow reuses the class for Nexus's own nexus.log,
// which is a different file.)
public sealed class GameLogWatcher : IDisposable
{
    public const string DefaultLivePath = @"C:\Program Files\Roberts Space Industries\StarCitizen\LIVE\Game.log";
    private const int MaxBytesPerTick = 1_000_000;   // cap a single read so a huge backlog can't freeze the UI

    private readonly System.Windows.Threading.DispatcherTimer _timer;
    private readonly StringBuilder _partial = new();   // carries a trailing partial line between polls
    private string _path = "";
    private long _position;
    private long _replayEnd;   // bytes before this offset were already in the file at start = replay
    private DateTime _creationTimeUtc;

    public event Action<GameLogEntry>? LineAppended;
    public event Action<string>? StatusChanged;
    /// <summary>The log file was truncated/recreated - Star Citizen started a new session.</summary>
    public event Action? LogReset;
    /// <summary>Star Citizen's running state changed: true when the game process is running (independent of
    /// window focus), false once it is closed / exited / shut down.</summary>
    public event Action<bool>? SessionLiveChanged;
    public bool IsRunning { get; private set; }
    /// <summary>True while the Star Citizen process is running (independent of window focus); false when it exits.</summary>
    public bool IsSessionLive { get; private set; }
    public string Path => _path;

    // Best-effort search for an existing Game.log across common RSI install locations and
    // channels, so the overlay (which has no path field) can start without manual setup.
    // Falls back to the LIVE default path if nothing is found.
    public static string FindGameLog()
    {
        string[] channels = GameChannels.KnownFolders;   // includes HOTFIX (issue #28)
        string[] roots =
        {
            @"C:\Program Files\Roberts Space Industries\StarCitizen",
            @"C:\Program Files (x86)\Roberts Space Industries\StarCitizen",
            @"D:\Roberts Space Industries\StarCitizen",
            @"D:\Program Files\Roberts Space Industries\StarCitizen",
            @"E:\Roberts Space Industries\StarCitizen",
        };
        try
        {
            foreach (var root in roots)
                foreach (var ch in channels)
                {
                    var p = System.IO.Path.Combine(root, ch, "Game.log");
                    if (File.Exists(p)) return p;
                }
        }
        catch { /* ignore probe errors */ }
        return DefaultLivePath;
    }

    public GameLogWatcher()
    {
        _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => Poll();
    }

    public void Start(string path, bool fromBeginning)
    {
        Stop();
        _path = path ?? "";
        _partial.Clear();
        try
        {
            if (File.Exists(_path))
            {
                long len = new FileInfo(_path).Length;
                // Everything already on disk is history: from the top it is replayed (and flagged as
                // such), otherwise it is skipped outright and only new bytes are read.
                _position = fromBeginning ? 0 : len;
                _replayEnd = fromBeginning ? len : 0;
                _creationTimeUtc = File.GetCreationTimeUtc(_path);
                StatusChanged?.Invoke($"Watching {_path}");
            }
            else
            {
                _position = 0;
                _replayEnd = 0;
                _creationTimeUtc = default;
                StatusChanged?.Invoke($"Waiting for file to appear: {_path}");
            }
        }
        catch (Exception ex)
        {
            _replayEnd = 0;
            StatusChanged?.Invoke($"Error opening: {ex.Message}");
        }
        IsRunning = true;
        _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
        if (IsRunning) StatusChanged?.Invoke("Stopped");
        IsRunning = false;
        if (IsSessionLive) { IsSessionLive = false; SessionLiveChanged?.Invoke(false); }
    }

    // Game-running signal: Star Citizen is "live" while its process exists, regardless of window focus or
    // how often it writes Game.log (the game throttles logging when backgrounded). So this stays on while
    // the game runs minimized / unfocused and flips off only when it is closed / exited. The probe enumerates
    // processes, so it is throttled; checking every ~2s is plenty for open / close detection.
    private DateTime _lastLiveCheckUtc;
    private void UpdateSessionLive()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastLiveCheckUtc).TotalSeconds < 2) return;
        _lastLiveCheckUtc = now;
        bool live;
        try
        {
            var procs = System.Diagnostics.Process.GetProcessesByName("StarCitizen");
            live = procs.Length > 0;
            foreach (var p in procs) p.Dispose();
        }
        catch { live = false; }
        if (live == IsSessionLive) return;
        IsSessionLive = live;
        SessionLiveChanged?.Invoke(live);
    }

    private void Poll()
    {
        if (string.IsNullOrEmpty(_path)) return;
        UpdateSessionLive();   // re-evaluate game-is-running from log freshness every tick (even with no new bytes)
        try
        {
            if (!File.Exists(_path)) return;

            long len = new FileInfo(_path).Length;
            // New-session detection: SC starts a fresh Game.log each launch. Catch both a
            // recreated file (creation time changes) and an in-place truncation (length drops
            // below where we were). The creation-time check also covers the case where the new
            // session grows past our old offset between polls - which a length check alone would
            // miss if the previous session was short.
            DateTime creation;
            try { creation = File.GetCreationTimeUtc(_path); } catch { creation = _creationTimeUtc; }
            bool recreated = _creationTimeUtc != default && creation != _creationTimeUtc;
            _creationTimeUtc = creation;
            if (recreated || len < _position)   // recreated, or shrank => new session
            {
                _position = 0;
                _replayEnd = 0;   // a fresh session's lines are live, never replayed history
                _partial.Clear();
                StatusChanged?.Invoke($"Log reset (new session) - {_path}");
                LogReset?.Invoke();
            }

            // Belt and braces for a recreate BOTH detectors miss (same creation time, and already
            // grown past our offset): a boundary beyond the file's end would leave every consumer
            // that skips replayed history deaf until the next reset, so clamp it to what exists.
            if (len < _replayEnd) _replayEnd = len;

            int toRead = ReadSize(_position, len, _replayEnd, MaxBytesPerTick);
            if (toRead <= 0) return;
            bool replay = _position < _replayEnd;   // whole chunk, by construction of ReadSize
            byte[] buffer = new byte[toRead];
            int read;
            using (var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                fs.Seek(_position, SeekOrigin.Begin);
                read = fs.Read(buffer, 0, toRead);
            }
            if (read <= 0) return;
            _position += read;

            _partial.Append(Encoding.UTF8.GetString(buffer, 0, read));
            string text = _partial.ToString();
            _partial.Clear();

            int start = 0, nl;
            while ((nl = text.IndexOf('\n', start)) >= 0)
            {
                string line = text.Substring(start, nl - start).TrimEnd('\r');
                start = nl + 1;
                if (line.Length == 0) continue;
                LineAppended?.Invoke(new GameLogEntry { Raw = line, Category = Categorize(line), IsReplay = replay });
            }
            if (start < text.Length) _partial.Append(text, start, text.Length - start);   // keep partial line
        }
        catch (IOException)
        {
            // transient (file momentarily busy) - try again next tick
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"Error: {ex.Message}");
        }
    }

    /// <summary>Bytes to read on one tick: capped so a huge backlog can't freeze the UI, and never
    /// straddling the replay boundary - so every chunk is entirely replayed history or entirely live
    /// and each line's IsReplay flag needs no per-byte accounting. 0 when there is nothing to read.
    /// Pure; exercised headless in GameLogFeedTests.</summary>
    internal static int ReadSize(long position, long length, long replayEnd, int maxPerTick)
    {
        long available = length - position;
        if (available <= 0) return 0;
        if (position < replayEnd) available = Math.Min(available, replayEnd - position);
        return (int)Math.Min(available, maxPerTick);
    }

    // Best-effort tagging. SC's log format shifts between patches, so unknown lines
    // fall to Other - the raw text + the filter box are the real discovery tools.
    public static LogCategory Categorize(string line)
    {
        string l = line.ToLowerInvariant();
        // Blueprint acquisition is the priority signal we're hunting for - tag it first.
        // Keywords are a guess until we see the real lines; widen once we know the format.
        if (l.Contains("blueprint") || l.Contains("recipe") || l.Contains("schematic")) return LogCategory.Blueprint;
        if (l.Contains("actor death") || l.Contains("<kill>") || l.Contains("killed")) return LogCategory.Kill;
        if (l.Contains("vehicle destruction") || l.Contains("<vehicle")) return LogCategory.VehicleDestruction;
        if (l.Contains("quantum")) return LogCategory.Quantum;
        if (l.Contains("loading screen") || l.Contains("requesting transition") || l.Contains(" zone") || l.Contains("location")) return LogCategory.Location;
        if (l.Contains("spawn")) return LogCategory.Spawn;
        if (l.Contains("login") || l.Contains("character:") || l.Contains("account")) return LogCategory.Login;
        if (l.Contains("changelist") || l.Contains("branch") || l.Contains("build_")) return LogCategory.Version;
        if (l.Contains("connect") || l.Contains("server")) return LogCategory.Connection;
        if (l.Contains("mission") || l.Contains("contract")) return LogCategory.Mission;
        if (l.Contains("refinery") || l.Contains("mining") || l.Contains("commodity") || l.Contains("shop") || l.Contains("price")) return LogCategory.Economy;
        return LogCategory.Other;
    }

    public void Dispose() => Stop();
}
