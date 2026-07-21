using System.Collections.Generic;
using System.Text;

namespace NexusApp.Services;

// Builds the diagnostic snapshot a user saves/copies from the App Log Monitor and sends for
// debugging: a short context header (versions + key settings) followed by the full nexus.log.
// Pure formatting so it's unit-testable headless. Local-only until the user chooses to share it;
// contains versions, basic settings, and the app's own log - no other data.
public static class DiagnosticSnapshot
{
    public static string Build(
        string appVersion,
        string gameVersion,
        string miningDataVersion,
        string osVersion,
        IReadOnlyList<(string Key, string Value)> settings,
        string logContents,
        DateTime timestamp,
        string? unmatchedLogContents = null,
        string? previousLogContents = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== Nexus diagnostic snapshot ===");
        sb.AppendLine($"Taken: {timestamp:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"App version: {appVersion}");
        sb.AppendLine($"Star Citizen game data: {gameVersion}");
        sb.AppendLine($"Mining data: {miningDataVersion}");
        sb.AppendLine($"OS: {osVersion}");
        sb.AppendLine();
        sb.AppendLine("--- Settings ---");
        foreach (var (key, value) in settings) sb.AppendLine($"{key}: {value}");
        sb.AppendLine();
        sb.AppendLine("--- nexus.log ---");
        sb.AppendLine(string.IsNullOrWhiteSpace(logContents) ? "(log is empty)" : logContents.TrimEnd());
        // Unmatched-blueprint diagnostics ride along when present, so one snapshot carries
        // everything needed to debug a "blueprint not recognized" report (issue #17).
        if (!string.IsNullOrWhiteSpace(unmatchedLogContents))
        {
            sb.AppendLine();
            sb.AppendLine("--- unmatched_blueprints.log ---");
            sb.AppendLine(unmatchedLogContents.TrimEnd());
        }
        // The one kept pre-rotation generation, when present: a crash older than the 72h
        // window lives here, so the snapshot still carries the evidence.
        if (!string.IsNullOrWhiteSpace(previousLogContents))
        {
            sb.AppendLine();
            sb.AppendLine("--- nexus.log.1 (previous) ---");
            sb.AppendLine(previousLogContents.TrimEnd());
        }
        return sb.ToString();
    }

    // Diagnostic snapshots get shared with the maintainer; Star Citizen log paths live under the
    // Windows user-profile folder, so replace that prefix with %USERPROFILE% to avoid leaking the
    // OS username (which can be a real name). Paths outside the profile are returned unchanged.
    public static string RedactUserProfile(string? path, string? home)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(home)) return path ?? "";
        return path.StartsWith(home, StringComparison.OrdinalIgnoreCase)
            ? "%USERPROFILE%" + path.Substring(home.Length)
            : path;
    }
}
