using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace NexusApp.Services;

// Builds the report a user Copies/Exports from the Game.log import when some blueprint
// names couldn't be matched, so they can send it to the maintainer to fix the mapping.
// Formatting is a pure function and the build-line extraction takes lines (not a path),
// so both are unit-testable headless. Privacy: versions + build line + raw names only -
// no player handle, no file paths (SC log paths contain the Windows username).
public static class UnrecognizedBlueprintReport
{
    // SC writes a build/branch/changelist line near the top of Game.log. We surface it
    // verbatim (trimmed) because blueprint display names shift between patches, so the
    // build tells the maintainer which patch's names to check. Keywords mirror the
    // GameLogWatcher categorizer. Best-effort: null when nothing recognizable is found.
    private static readonly string[] BuildKeywords = { "changelist", "branch", "build_" };
    private const int MaxBuildLineLength = 240;
    private const int MaxLinesToScanForBuild = 1000;

    /// <summary>First line mentioning a build/branch/changelist, trimmed; null if none.</summary>
    public static string? ExtractBuildLine(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var lower = line.ToLowerInvariant();
            if (!BuildKeywords.Any(k => lower.Contains(k))) continue;
            var trimmed = line.Trim();
            return trimmed.Length > MaxBuildLineLength ? trimmed[..MaxBuildLineLength] : trimmed;
        }
        return null;
    }

    /// <summary>IO wrapper: read the live log (shared, read-only) and return its build line, or null.</summary>
    public static string? TryReadBuildLine(string liveLogPath)
    {
        try
        {
            if (!File.Exists(liveLogPath)) return null;
            using var fs = new FileStream(liveLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            var lines = new List<string>();
            string? line;
            int n = 0;
            while ((line = sr.ReadLine()) != null && n++ < MaxLinesToScanForBuild)
                lines.Add(line);
            return ExtractBuildLine(lines);
        }
        catch { return null; }
    }

    /// <summary>The Copy/Export payload: a short context header followed by the full Game.log line for each unmatched blueprint.</summary>
    public static string Build(
        string appVersion,
        string miningDataVersion,
        string? buildLine,
        int filesScanned,
        int matchedCount,
        IReadOnlyList<string> unmatchedLines,
        bool starStringsDetected,
        DateTime timestamp)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Nexus v{appVersion}  ·  Mining data v{miningDataVersion}  ·  {timestamp:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"Star Citizen build: {(string.IsNullOrWhiteSpace(buildLine) ? "unknown" : buildLine)}");
        sb.AppendLine($"Scanned {filesScanned} log file(s) - matched {matchedCount}, unrecognized {unmatchedLines.Count}");
        sb.AppendLine($"StarStrings mod: {(starStringsDetected ? "detected" : "not detected")}");
        sb.AppendLine();
        sb.AppendLine("Unrecognized blueprints - the full Game.log line for each (samples to fix the mapping):");
        foreach (var line in unmatchedLines) sb.AppendLine(line);
        return sb.ToString();
    }
}
