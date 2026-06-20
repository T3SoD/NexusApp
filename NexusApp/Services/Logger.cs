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
                // Cheap rotation: if the log grows past ~1 MB, start fresh.
                if (File.Exists(_path) && new FileInfo(_path).Length > 1_000_000)
                    File.WriteAllText(_path, "");
                File.AppendAllText(_path, line + Environment.NewLine);
            }
        }
        catch { /* logging must never throw */ }
    }
}
