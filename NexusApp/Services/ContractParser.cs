using System.Globalization;
using System.Text.RegularExpressions;
using NexusApp.Models;

namespace NexusApp.Services;

// Pure parsing of the in-game Contracts-panel OCR text. Tolerant of OCR noise; anchored on
// Reward / Contracted By / Deliver / Collect. Returns null when no contract anchor is present
// (which is also how the scanner knows no contract is on screen). No player identity is read.
public static class ContractParser
{
    private static readonly Regex RewardRx = new(@"Reward[^\d]{0,8}([\d][\d,]{2,})", RegexOptions.Compiled);
    private static readonly Regex ContractedByRx = new(@"Contracted By\s+([^\r\n]+)", RegexOptions.Compiled);
    private static readonly Regex DeliverRx = new(
        @"Deliver\s+\d+\s*/\s*(?<scu>\d+)\s+SCU of\s+(?<commodity>.+?)\s+to\s+(?<dropoff>[^.\r\n]+)",
        RegexOptions.Compiled);
    private static readonly Regex CollectRx = new(
        @"Collect\s+(?<commodity>.+?)\s+from\s+(?<pickup>[^.\r\n]+)", RegexOptions.Compiled);
    private static readonly Regex RepTag = new(@"\[[^\]]*\]", RegexOptions.Compiled);

    public static ContractDetails? Parse(string ocrText)
    {
        if (string.IsNullOrWhiteSpace(ocrText)) return null;

        // Pickup sources keyed by commodity (so a Deliver can borrow its Collect's source).
        var pickups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match c in CollectRx.Matches(ocrText))
            pickups[c.Groups["commodity"].Value.Trim()] = c.Groups["pickup"].Value.Trim();

        var objectives = new List<ContractObjective>();
        foreach (Match m in DeliverRx.Matches(ocrText))
        {
            var commodity = m.Groups["commodity"].Value.Trim();
            objectives.Add(new ContractObjective
            {
                Commodity = commodity,
                Scu = int.TryParse(m.Groups["scu"].Value, out var s) ? s : 0,
                Dropoff = m.Groups["dropoff"].Value.Trim(),
                Pickup = pickups.TryGetValue(commodity, out var p) ? p : "",
            });
        }

        var contractedBy = ContractedByRx.Match(ocrText) is { Success: true } cb
            ? cb.Groups[1].Value.Trim() : "";

        // Not a contract panel unless we found at least the contractor or a Deliver objective.
        if (objectives.Count == 0 && contractedBy.Length == 0) return null;

        int reward = 0;
        if (RewardRx.Match(ocrText) is { Success: true } r &&
            int.TryParse(r.Groups[1].Value.Replace(",", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rv))
            reward = rv;

        return new ContractDetails
        {
            Title = ExtractTitle(ocrText),
            Reward = reward,
            ContractedBy = contractedBy,
            Objectives = objectives,
        };
    }

    // The heading: text before the first known anchor line.
    private static string ExtractTitle(string text)
    {
        foreach (var anchor in new[] { "Reward", "Contract Deadline", "Contracted By", "DETAILS", "PRIMARY OBJECTIVES" })
        {
            var i = text.IndexOf(anchor, StringComparison.Ordinal);
            if (i > 0) return Collapse(text[..i]);
        }
        return Collapse(text);
    }

    public static string NormalizeTitle(string title)
    {
        var noTag = RepTag.Replace(title, " ");
        var lower = noTag.ToLowerInvariant();
        var sb = new System.Text.StringBuilder(lower.Length);
        foreach (var ch in lower)
            sb.Append(char.IsLetterOrDigit(ch) ? ch : (ch == ' ' || ch == '\n' || ch == '\r' || ch == '\t' ? ' ' : ' '));
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    private static string Collapse(string s) => Regex.Replace(s, @"\s+", " ").Trim();
}
