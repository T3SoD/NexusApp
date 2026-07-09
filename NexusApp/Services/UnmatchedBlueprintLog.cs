using System.IO;

namespace NexusApp.Services;

// Dedicated diagnostics file for blueprint receipts that failed EVERY resolution step - the
// exact data needed to debug a "not recognized" report (issue #17) without a back-and-forth:
// one self-contained line per distinct unmatched name per app run, carrying the source (live
// tail / import scan), app version, localization-map state, the raw name, and the full Game.log
// line. Sits next to nexus.log (so the test env override isolates it too) and rides along in the
// diagnostic snapshot. Local-only until the user chooses to share it; game-item text and game
// log lines only - no paths, no handles.
public static class UnmatchedBlueprintLog
{
    /// <summary>Sibling of nexus.log: %AppData%\NexusApp\logs\unmatched_blueprints.log.</summary>
    public static string LogPath => Path.Combine(
        Path.GetDirectoryName(Logger.LogPath) ?? ".", "unmatched_blueprints.log");

    private static readonly object _lock = new();
    private static readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);   // per app run
    private const long MaxBytes = 512_000;   // runaway cap: restart the file with a notice line

    /// <summary>Append one unmatched receipt with its resolution context. Dedupes per app run by
    /// raw name (notification lifecycle repeats and rescans would flood the file). Never throws.
    /// Returns true when a line was written.</summary>
    public static bool Record(string source, string rawName, string fullLine, int? localizationEntries)
    {
        try
        {
            lock (_lock)
            {
                if (string.IsNullOrWhiteSpace(rawName) || !_seen.Add(rawName)) return false;

                var path = LogPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                {
                    File.Delete(path);
                    File.AppendAllText(path, $"{Stamp()} | (older entries dropped at the size cap){Environment.NewLine}");
                }

                var map = localizationEntries.HasValue ? $"map {localizationEntries.Value} entries" : "map not loaded";
                File.AppendAllText(path,
                    $"{Stamp()} | {source} | app {NexusApp.AppInfo.Version} | {map} | {rawName} | {fullLine.Trim()}{Environment.NewLine}");
                return true;
            }
        }
        catch { return false; }   // diagnostics must never break the feature they observe
    }

    private static string Stamp() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
}
