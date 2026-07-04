using System.Text.RegularExpressions;
using NexusApp.Models;

namespace NexusApp.Services;

// Pure parsing of the in-game Contracts-panel OCR text. Tuned to the REAL on-screen layout (see the
// captured fixtures in ContractParserTests): there is NO "Reward" or "Contracted By" label on screen.
// The reward sits immediately before the "N/A" contract-deadline value and the contractor org follows
// it: "<glyph> 139,250 N/A Citizens For Prosperity [50/200 Rep] Need a Hauler DETAILS ...". OCR renders
// the thousands separator as ',' or '.'. Returns null when no contract anchor is present (which is also
// how the scanner knows no contract is on screen). No player identity is read.
public static class ContractParser
{
    // Reward = the comma/dot-grouped amount right before the "N/A" deadline. Fallback: first grouped number.
    private static readonly Regex RewardBeforeNa = new(@"(\d{2,3}[.,]\d{3})\s+N\s*/\s*A", RegexOptions.Compiled);
    private static readonly Regex RewardGrouped  = new(@"\d{2,3}[.,]\d{3}", RegexOptions.Compiled);

    // Contractor org = the text after the "N/A" deadline, up to a rep tag, a number, or a section word.
    private static readonly Regex ContractorAfterNa = new(
        @"N\s*/\s*A\s+(?<org>[A-Za-z][A-Za-z .'&\-]+?)\s*" +
        @"(?=\[|\d|DETAILS|PRIMARY|ACCEPT|TRACK|UNTRACK|ABANDON|Deliver|Collect|Share|$)",
        RegexOptions.Compiled);

    // "SC\w+" tolerates OCR rendering "SCU" as "SCIJ". Dropoff/pickup are lazy and stop at a sentence end
    // or the next Collect/Deliver line, so a misread period can't make the dropoff swallow the Collect text.
    private static readonly Regex DeliverRx = new(
        @"Deliver\s+\d+\s*/\s*(?<scu>\d+)\s+SC\w+\s+of\s+(?<commodity>.+?)\s+to\s+(?<dropoff>.+?)(?=\.|\bCollect\b|\bDeliver\b|[\r\n]|$)",
        RegexOptions.Compiled);
    private static readonly Regex CollectRx = new(
        @"Collect\s+(?<commodity>.+?)\s+from\s+(?<pickup>.+?)(?=\.|\bCollect\b|\bDeliver\b|[\r\n]|$)",
        RegexOptions.Compiled);
    private static readonly Regex RepTag = new(@"\[[^\]]*\]", RegexOptions.Compiled);

    // The contract states its max container size in the details prose. "SC\w+" tolerates OCR rendering
    // "SCU" as "SCIJ". Most specific wording first. Never matches a "N SCU of <commodity>" objective line.
    private static readonly Regex[] ContainerCapRx =
    {
        new(@"(\d+)\s*SC\w+\s+(?:cargo\s+)?containers?", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"(\d+)\s*SC\w+\s+or\s+smaller", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"max(?:imum)?\s+(?:container|box)[^0-9]{0,24}(\d+)\s*SC\w+", RegexOptions.Compiled | RegexOptions.IgnoreCase),
    };
    private static readonly int[] StandardBoxSizes = { 1, 2, 4, 8, 16, 24, 32 };

    public static ContractDetails? Parse(string ocrText)
    {
        if (string.IsNullOrWhiteSpace(ocrText)) return null;

        // Pickup sources keyed by commodity (so a Deliver can borrow its Collect's source).
        var pickups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match c in CollectRx.Matches(ocrText))
            pickups[c.Groups["commodity"].Value.Trim()] = Tidy(c.Groups["pickup"].Value);

        var objectives = new List<ContractObjective>();
        foreach (Match m in DeliverRx.Matches(ocrText))
        {
            var commodity = m.Groups["commodity"].Value.Trim();
            objectives.Add(new ContractObjective
            {
                Commodity = commodity,
                Scu = int.TryParse(m.Groups["scu"].Value, out var s) ? s : 0,
                Dropoff = Tidy(m.Groups["dropoff"].Value),
                Pickup = pickups.TryGetValue(commodity, out var p) ? p : "",
            });
        }

        var contractedBy = ContractorAfterNa.Match(ocrText) is { Success: true } cb
            ? Tidy(cb.Groups["org"].Value) : "";

        // Not a contract panel unless we found the contractor or a Deliver objective.
        if (objectives.Count == 0 && contractedBy.Length == 0) return null;

        return new ContractDetails
        {
            Title = ExtractTitle(ocrText),
            Reward = ParseReward(ocrText),
            ContractedBy = contractedBy,
            Objectives = objectives,
            ContainerCap = ParseContainerCap(ocrText),
        };
    }

    // The contract's max container size, snapped to the nearest standard box (1/2/4/8/16/24/32).
    // Null when the panel text did not state a container size (the caller then defaults it).
    public static int? ParseContainerCap(string ocrText)
    {
        foreach (var rx in ContainerCapRx)
        {
            var m = rx.Match(ocrText);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var n) && n > 0)
                return SnapBoxSize(n);
        }
        return null;
    }

    private static int SnapBoxSize(int n)
    {
        int best = StandardBoxSizes[0];
        foreach (var s in StandardBoxSizes)
            if (Math.Abs(s - n) < Math.Abs(best - n)) best = s;
        return best;
    }

    // OCR objective checkbox bullets render as stray single letters ("O"/"o") that cling to the end of a
    // captured field ("Ling Family Hauling O O", "Sunset Mesa on o"). Strip trailing single-char tokens.
    private static string Tidy(string s)
    {
        s = s.Trim();
        while (Regex.IsMatch(s, @"\s[A-Za-z0-9]$")) s = s[..^2].TrimEnd();
        return s;
    }

    private static int ParseReward(string text)
    {
        var token = RewardBeforeNa.Match(text) is { Success: true } r ? r.Groups[1].Value
                  : RewardGrouped.Match(text) is { Success: true } g ? g.Value
                  : "";
        var digits = token.Replace(",", "").Replace(".", "");
        return int.TryParse(digits, out var v) ? v : 0;
    }

    // Best-effort heading for display/dedup only (NOT used for haul matching, which joins on the
    // contractor org). The panel's OCR reading order scrambles the title, so this is intentionally loose.
    private static string ExtractTitle(string text)
    {
        foreach (var anchor in new[] { "DETAILS", "PRIMARY OBJECTIVES", "ACCEPT", "TRACK" })
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
            sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    private static string Collapse(string s) => Regex.Replace(s, @"\s+", " ").Trim();
}
