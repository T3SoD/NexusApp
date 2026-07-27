using System.Linq;
using System.Text.RegularExpressions;

namespace NexusApp.Services;

// Seed resource name -> UEX raw commodity display name. Embedded and exhaustive: every seed
// resource is either mapped or listed in KnownUnmapped with a reason. Authored by hand against
// the 39 resource rows in NexusApp/Data/seed_data.json and the 44-name UEX raw commodity list
// (task-4-brief.md). Mind the spelling divergences between the seed's in-game names and UEX's
// display names (for example Quantanium vs Quantainium); when a match was not certain enough to
// make honestly, the name went to KnownUnmapped instead of being guessed.
internal static class MarketNameMap
{
    public static readonly IReadOnlyDictionary<string, string> SeedToUexRaw =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Agricium"] = "Agricium (Ore)",
            // Seed uses the British spelling "Aluminium"; UEX lists the American "Aluminum (Ore)".
            // Same element, same in-game mineral, different transliteration.
            ["Aluminium"] = "Aluminum (Ore)",
            ["Aphorite"] = "Aphorite",
            ["Aslarite"] = "Aslarite (Raw)",
            ["Beradom"] = "Beradom",
            ["Beryl"] = "Beryl (Raw)",
            ["Bexalite"] = "Bexalite (Raw)",
            ["Borase"] = "Borase (Ore)",
            ["Copper"] = "Copper (Ore)",
            ["Corundum"] = "Corundum (Raw)",
            ["Dolivine"] = "Dolivine",
            ["Feynmaline"] = "Feynmaline",
            ["Glacosite"] = "Glacosite",
            ["Gold"] = "Gold (Ore)",
            ["Hadanite"] = "Hadanite",
            // Seed spells it "Hephestanite" (missing the second "a" from "Hephaestus"); UEX's
            // "Hephaestanite (Raw)" is the correctly spelled form of the same mineral name.
            ["Hephestanite"] = "Hephaestanite (Raw)",
            ["Ice"] = "Ice (Raw)",
            ["Iron"] = "Iron (Ore)",
            ["Jaclium"] = "Jaclium (Ore)",
            ["Janalite"] = "Janalite",
            ["Laranite"] = "Laranite (Raw)",
            ["Lindinium"] = "Lindinium (Ore)",
            ["Ouratite"] = "Ouratite (Raw)",
            // Known spelling divergence: seed "Quantanium" vs UEX "Quantainium (Raw)".
            ["Quantanium"] = "Quantainium (Raw)",
            ["Quartz"] = "Quartz (Raw)",
            ["Riccite"] = "Riccite (Ore)",
            ["Sadaryx"] = "Sadaryx (Raw)",
            ["Savrilium"] = "Savrilium (Ore)",
            ["Silicon"] = "Silicon (Raw)",
            ["Stileron"] = "Stileron (Raw)",
            ["Taranite"] = "Taranite (Raw)",
            ["Tin"] = "Tin (Ore)",
            ["Titanium"] = "Titanium (Ore)",
            ["Torite"] = "Torite (Ore)",
            ["Tungsten"] = "Tungsten (Ore)",
        };

    public static readonly IReadOnlyDictionary<string, string> KnownUnmapped =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Carinite"] = "No confident UEX match. UEX lists \"Caranite (Raw)\", one letter off, " +
                "but that is not enough to call it the same mineral without confirmation; left " +
                "unmapped rather than guessed.",
            ["Carinite (Pure)"] = "Pure-tier variant of Carinite; no \"(Pure)\" suffix exists in the " +
                "UEX raw commodity list and the base name Carinite is itself unmapped.",
            ["Pressurized Ice"] = "Processed form of Ice (seed method: refined, baseRs 0), not a " +
                "distinct entry in the UEX raw commodity list.",
            ["Saldynium"] = "No matching name in the 44-name UEX raw commodity list; appears to be " +
                "hand-mined only with no UEX raw market price.",
        };

    // Case-insensitive. Null when unmapped.
    public static string? UexRawNameFor(string seedResourceName)
    {
        if (string.IsNullOrEmpty(seedResourceName)) return null;
        return SeedToUexRaw.TryGetValue(seedResourceName, out var uexName) ? uexName : null;
    }

    // Resolve a raw commodity to its refined counterpart id within a commodity list:
    // idParent link first; when IdParent == 0, fall back to stripping " (Raw)"/" (Ore)" from
    // the raw name and matching a commodity with IsRefined and that exact name.
    public static MarketCommodity? RefinedFor(MarketCommodity raw, IReadOnlyList<MarketCommodity> all)
    {
        if (raw is null || all is null) return null;

        if (raw.IdParent != 0)
        {
            foreach (var candidate in all)
            {
                if (candidate.Id == raw.IdParent) return candidate;
            }
            return null;
        }

        var strippedName = StripRawOreSuffix(raw.Name);
        foreach (var candidate in all)
        {
            if (candidate.IsRefined && string.Equals(candidate.Name, strippedName, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }
        return null;
    }

    private static string StripRawOreSuffix(string name)
    {
        const string rawSuffix = " (Raw)";
        const string oreSuffix = " (Ore)";
        if (name.EndsWith(rawSuffix, StringComparison.OrdinalIgnoreCase))
            return name[..^rawSuffix.Length];
        if (name.EndsWith(oreSuffix, StringComparison.OrdinalIgnoreCase))
            return name[..^oreSuffix.Length];
        return name;
    }

    // Splits free text on , + / ; and whitespace runs.
    private static readonly Regex TokenSeparators = new(@"[,+/;\s]+", RegexOptions.Compiled);

    // Tokenize free text like "Bexalite, Gold + Copper" on , + / ; and whitespace runs,
    // match tokens (and adjacent token pairs, for two-word names) case-insensitively against
    // SeedToUexRaw keys, dedupe preserving first occurrence.
    public static List<string> RecognizeSeedNames(string freeText)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(freeText)) return result;

        var tokens = TokenSeparators.Split(freeText.Trim())
            .Where(t => t.Length > 0)
            .ToList();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < tokens.Count; i++)
        {
            if (i + 1 < tokens.Count)
            {
                var pair = $"{tokens[i]} {tokens[i + 1]}";
                if (SeedToUexRaw.ContainsKey(pair))
                {
                    if (seen.Add(pair)) result.Add(pair);
                    i++; // consume both tokens of the matched pair
                    continue;
                }
            }

            if (SeedToUexRaw.ContainsKey(tokens[i]))
            {
                if (seen.Add(tokens[i])) result.Add(tokens[i]);
            }
        }

        return result;
    }
}
