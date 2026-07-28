using System.Linq;
using System.Text.Json;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Guards the seed-to-UEX name map (Task 4 of feature/uex-market-data): every embedded seed
// resource must be either mapped to a UEX raw commodity display name or explicitly listed in
// KnownUnmapped with a reason, so a future seed refresh that adds a new resource fails loudly
// here instead of silently dropping market data. Reads the embedded seed the same way
// SeedHygieneTests / DataService do at runtime.
public class MarketNameMapTests
{
    // The 44-name UEX raw commodity list from the task brief, embedded verbatim so test (c)
    // catches a typo'd value in SeedToUexRaw (a value that doesn't exist on UEX at all).
    private static readonly string[] UexRawCommodityNames =
    {
        "Agricium (Ore)", "Aluminum (Ore)", "Aphorite", "Aslarite (Raw)", "Beradom",
        "Beryl (Raw)", "Bexalite (Raw)", "Borase (Ore)", "Caranite (Raw)", "Cobalt (Raw)",
        "Construction Material Pebbles", "Construction Material Rubble",
        "Construction Material Salvage", "Copper (Ore)", "Corundum (Raw)", "Decari Pod",
        "Diamond (Raw)", "Dolivine", "Feynmaline", "Glacosite", "Gold (Ore)", "Hadanite",
        "Hephaestanite (Raw)", "Ice (Raw)", "Inert Materials", "Iron (Ore)", "Jaclium (Ore)",
        "Janalite", "Laranite (Raw)", "Lindinium (Ore)", "Ouratite (Raw)", "Quantainium (Raw)",
        "Quartz (Raw)", "Riccite (Ore)", "Sadaryx (Raw)", "Savrilium (Ore)", "Silicon (Raw)",
        "Stileron (Raw)", "Taranite (Raw)", "Tin (Ore)", "Titanium (Ore)", "Torite (Ore)",
        "Tungsten (Ore)", "Wuotan Seed",
    };

    private static IEnumerable<string> SeedResourceNames(JsonDocument doc) =>
        doc.RootElement.GetProperty("resources").EnumerateArray()
            .Select(r => r.GetProperty("name").GetString()!);

    // --- (a) exhaustiveness ---------------------------------------------------

    [Fact]
    public void EverySeedResource_IsMappedOrKnownUnmapped()
    {
        using var doc = SeedTestFixture.LoadSeed();
        foreach (var name in SeedResourceNames(doc))
        {
            Assert.True(
                MarketNameMap.SeedToUexRaw.ContainsKey(name) || MarketNameMap.KnownUnmapped.ContainsKey(name),
                $"seed resource neither mapped nor listed in KnownUnmapped: {name}");
        }
    }

    // --- (b) no overlap between the two dictionaries ---------------------------

    [Fact]
    public void NoNameAppearsInBothDictionaries()
    {
        foreach (var name in MarketNameMap.SeedToUexRaw.Keys)
        {
            Assert.False(MarketNameMap.KnownUnmapped.ContainsKey(name),
                $"name present in both SeedToUexRaw and KnownUnmapped: {name}");
        }
    }

    // --- (c) every mapped value is a real UEX raw commodity name (typo guard) ---

    [Fact]
    public void EveryMappedValue_IsAKnownUexRawCommodityName()
    {
        var uexNames = new HashSet<string>(UexRawCommodityNames, StringComparer.Ordinal);
        foreach (var kvp in MarketNameMap.SeedToUexRaw)
        {
            Assert.True(uexNames.Contains(kvp.Value),
                $"mapped value is not in the 44-name UEX raw commodity list: {kvp.Key} -> {kvp.Value}");
        }
    }

    // --- (d) UexRawNameFor is case-insensitive ----------------------------------

    [Fact]
    public void UexRawNameFor_IsCaseInsensitive()
    {
        Assert.Equal("Quantainium (Raw)", MarketNameMap.UexRawNameFor("quantanium"));
        Assert.Equal("Quantainium (Raw)", MarketNameMap.UexRawNameFor("QUANTANIUM"));
    }

    [Fact]
    public void UexRawNameFor_UnknownName_ReturnsNull()
    {
        Assert.Null(MarketNameMap.UexRawNameFor("NotARealResource"));
    }

    // --- (e) RefinedFor ----------------------------------------------------------

    [Fact]
    public void RefinedFor_IdParentLink_Resolves()
    {
        var refined = new MarketCommodity(200, "Gold", "GOLD", false, true, 0);
        var raw = new MarketCommodity(100, "Gold (Ore)", "GOLD-ORE", true, false, 200);
        var all = new List<MarketCommodity> { raw, refined };

        var result = MarketNameMap.RefinedFor(raw, all);

        Assert.Same(refined, result);
    }

    [Fact]
    public void RefinedFor_IdParentZero_FallsBackToStrippedRawSuffix()
    {
        var raw = new MarketCommodity(101, "Taranite (Raw)", "TARANITE-RAW", true, false, 0);
        var refined = new MarketCommodity(201, "Taranite", "TARANITE", false, true, 0);
        var all = new List<MarketCommodity> { raw, refined };

        var result = MarketNameMap.RefinedFor(raw, all);

        Assert.Same(refined, result);
    }

    [Fact]
    public void RefinedFor_IdParentZero_OreSuffixAlsoFallsBack()
    {
        var raw = new MarketCommodity(102, "Gold (Ore)", "GOLD-ORE", true, false, 0);
        var refined = new MarketCommodity(202, "Gold", "GOLD", false, true, 0);
        var all = new List<MarketCommodity> { raw, refined };

        var result = MarketNameMap.RefinedFor(raw, all);

        Assert.Same(refined, result);
    }

    [Fact]
    public void RefinedFor_NoMatch_ReturnsNull()
    {
        var raw = new MarketCommodity(103, "Aphorite", "APHORITE", true, false, 0);
        var all = new List<MarketCommodity> { raw };

        var result = MarketNameMap.RefinedFor(raw, all);

        Assert.Null(result);
    }

    // --- (f) RecognizeSeedNames tokenizer -----------------------------------------

    [Fact]
    public void RecognizeSeedNames_CommaSeparated_FindsBoth()
    {
        var result = MarketNameMap.RecognizeSeedNames("Bexalite, Gold");
        Assert.Equal(new[] { "Bexalite", "Gold" }, result);
    }

    [Fact]
    public void RecognizeSeedNames_PlusSeparated_DedupesRepeat()
    {
        var result = MarketNameMap.RecognizeSeedNames("bexalite + bexalite");
        Assert.Single(result);
    }

    [Fact]
    public void RecognizeSeedNames_SlashAndSemicolon_IgnoresJunkToken()
    {
        var result = MarketNameMap.RecognizeSeedNames("Corundum/Quartz; junk");
        Assert.Equal(new[] { "Corundum", "Quartz" }, result);
    }

    [Fact]
    public void RecognizeSeedNames_EmptyString_ReturnsEmpty()
    {
        var result = MarketNameMap.RecognizeSeedNames("");
        Assert.Empty(result);
    }

    [Fact]
    public void RecognizeSeedNames_NoSeedResourceHasATwoWordMappedName()
    {
        // Documents the current state of the map rather than asserting tokenizer behavior:
        // as of this seed, every SeedToUexRaw key is a single word, so the tokenizer's
        // adjacent-token-pair matching path (required by the interface contract for future
        // two-word names) is not exercised by any real key today. The next test exercises the
        // pair-matching / single-word-fallback distinction directly with unmapped multi-word
        // seed text instead.
        Assert.All(MarketNameMap.SeedToUexRaw.Keys, key => Assert.DoesNotContain(' ', key));
    }

    [Fact]
    public void RecognizeSeedNames_WithinAnUnmappedTwoWordPhrase_StillFindsTheMappedWord()
    {
        // "Pressurized Ice" itself is not a SeedToUexRaw key (it is KnownUnmapped), so the
        // adjacent-pair check must miss, but the single-word fallback must still recognize
        // "Ice" as a token within the phrase.
        var result = MarketNameMap.RecognizeSeedNames("Pressurized Ice");
        Assert.Equal(new[] { "Ice" }, result);
    }

    // --- KnownUnmapped sanity: every reason is non-empty, for reviewer auditing ---

    [Fact]
    public void EveryKnownUnmappedReason_IsNonEmpty()
    {
        foreach (var kvp in MarketNameMap.KnownUnmapped)
        {
            Assert.False(string.IsNullOrWhiteSpace(kvp.Value), $"empty reason for: {kvp.Key}");
        }
    }
}
