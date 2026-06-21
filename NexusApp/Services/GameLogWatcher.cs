using System.IO;
using System.Text;

namespace NexusApp.Services;

// BETA / EXPERIMENTAL. Reads the Star Citizen Game.log the client writes to disk.
// NOTE: this reads a game-authored file, which differs from Nexus's usual
// "screen-OCR only, no game files" model — keep it on the beta branch and rework
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
}

// Tails Game.log live WITHOUT locking it: the game keeps the file open for writing,
// so we open it shared (FileShare.ReadWrite) and poll for appended bytes. Handles
// the file being recreated/truncated on a new game session. UI-thread timer, so all
// events are raised on the dispatcher — safe to touch UI from handlers.
public sealed class GameLogWatcher : IDisposable
{
    public const string DefaultLivePath = @"C:\Program Files\Roberts Space Industries\StarCitizen\LIVE\Game.log";
    private const int MaxBytesPerTick = 1_000_000;   // cap a single read so a huge backlog can't freeze the UI

    private readonly System.Windows.Threading.DispatcherTimer _timer;
    private readonly StringBuilder _partial = new();   // carries a trailing partial line between polls
    private string _path = "";
    private long _position;

    public event Action<GameLogEntry>? LineAppended;
    public event Action<string>? StatusChanged;
    public bool IsRunning { get; private set; }
    public string Path => _path;

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
                _position = fromBeginning ? 0 : new FileInfo(_path).Length;
                StatusChanged?.Invoke($"Watching {_path}");
            }
            else
            {
                _position = 0;
                StatusChanged?.Invoke($"Waiting for file to appear: {_path}");
            }
        }
        catch (Exception ex)
        {
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
    }

    private void Poll()
    {
        if (string.IsNullOrEmpty(_path)) return;
        try
        {
            if (!File.Exists(_path)) return;

            long len = new FileInfo(_path).Length;
            if (len < _position)   // file shrank => recreated/truncated (new session)
            {
                _position = 0;
                _partial.Clear();
                StatusChanged?.Invoke($"Log reset (new session) — {_path}");
            }
            if (len <= _position) return;

            int toRead = (int)Math.Min(len - _position, MaxBytesPerTick);
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
                LineAppended?.Invoke(new GameLogEntry { Raw = line, Category = Categorize(line) });
            }
            if (start < text.Length) _partial.Append(text, start, text.Length - start);   // keep partial line
        }
        catch (IOException)
        {
            // transient (file momentarily busy) — try again next tick
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"Error: {ex.Message}");
        }
    }

    // Best-effort tagging. SC's log format shifts between patches, so unknown lines
    // fall to Other — the raw text + the filter box are the real discovery tools.
    public static LogCategory Categorize(string line)
    {
        string l = line.ToLowerInvariant();
        // Blueprint acquisition is the priority signal we're hunting for — tag it first.
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
