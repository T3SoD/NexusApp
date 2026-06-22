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
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}";
            if (ex != null) line += Environment.NewLine + ex;

            lock (_lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                // Rotation: start fresh when the log is older than 72h (footprint control), or at the
                // ~5 MB runaway cap. Delete (not truncate) so the recreated file's creation time
                // restarts the 72h window cleanly, including across app restarts.
                if (File.Exists(_path))
                {
                    var fi = new FileInfo(_path);
                    if (DateTime.UtcNow - fi.CreationTimeUtc > MaxAge || fi.Length > MaxBytes)
                        File.Delete(_path);
                }
                File.AppendAllText(_path, line + Environment.NewLine);
            }
        }
        catch { /* logging must never throw */ }
    }
}
