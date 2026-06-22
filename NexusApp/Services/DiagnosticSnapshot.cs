using System.Collections.Generic;
using System.Text;

namespace NexusApp.Services;

// Builds the diagnostic snapshot a user saves/copies from the App Log Monitor and sends for
// debugging: a short context header (versions + key settings) followed by the full nexus.log.
// Pure formatting so it's unit-testable headless. Local-only until the user chooses to share it;
// contains versions, basic settings, and the app's own log — no other data.
public static class DiagnosticSnapshot
{
    public static string Build(
        string appVersion,
        string gameVersion,
        string miningDataVersion,
        string osVersion,
        IReadOnlyList<(string Key, string Value)> settings,
        string logContents,
        DateTime timestamp)
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
        return sb.ToString();
    }
}
