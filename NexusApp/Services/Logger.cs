using System.IO;

namespace NexusApp.Services;

/// <summary>
/// Minimal append-only file logger at %AppData%\NexusApp\logs\nexus.log. Local only —
/// nothing is ever sent anywhere. Logging must never throw, so every path is guarded.
/// </summary>
public static class Logger
{
    private static readonly object _lock = new();

    private static readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NexusApp", "logs", "nexus.log");

    /// <summary>Absolute path of the app log file (for the in-app log monitor / diagnostic snapshot).</summary>
    public static string LogPath => _path;

    // Footprint control: start the log fresh once it is older than 72h, with a runaway safety cap.
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(72);
    private const long MaxBytes = 5_000_000;

    public static void Info(string message) => Write("INFO", message, null);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        // WriteTo does the real work; Write owns the "logging must never throw" guarantee.
        try { WriteTo(_path, level, message, ex, DateTime.UtcNow); }
        catch { /* logging must never throw */ }
    }

    // Testable core. <paramref name="nowUtc"/> drives BOTH the rotation age check and the
    // post-rotation creation-time stamp, so tests can force the 72h path deterministically.
    // Production callers pass DateTime.UtcNow (see Write).
    internal static void WriteTo(string path, string level, string message, Exception? ex, DateTime nowUtc)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}";
        if (ex != null) line += Environment.NewLine + ex;

        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Rotation: start fresh when the log is older than 72h (footprint control), or at the
            // ~5 MB runaway cap.
            bool rotated = false;
            if (File.Exists(path))
            {
                var fi = new FileInfo(path);
                if (nowUtc - fi.CreationTimeUtc > MaxAge || fi.Length > MaxBytes)
                {
                    File.Delete(path);
                    rotated = true;
                }
            }
            File.AppendAllText(path, line + Environment.NewLine);
            // NTFS file-system tunneling re-applies a deleted file's ORIGINAL creation time when a
            // same-named file is recreated in the same folder within ~15s. Without overriding it, the
            // 72h age check stays permanently true after the first rotation — deleting and rewriting a
            // single line on every call, which pins the log to one ever-overwritten line. Stamp the
            // real creation time so rotation genuinely restarts the window (across app restarts too).
            if (rotated)
            {
                try { File.SetCreationTimeUtc(path, nowUtc); } catch { /* best-effort */ }
            }
        }
    }
}
