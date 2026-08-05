using NexusApp.Models;
using Xunit;

namespace NexusApp.Tests;

// Issue #34: the decoder used to report any node multiple (Gold x7) even though the game's
// cluster presets cap rocks per cluster by rarity tier. Caps datamined from the DataCore
// HarvestableClusterPreset records (clusteringpresets/shipmineables, one per tier); every
// ship ore's provider preset pairs it with exactly its rarity-tier preset.
public class RsDecodeLimitsTests
{
    private static Resource Ore(string rarity, int baseRs = 3600) => new()
    {
        Name = "TestOre", BaseRs = baseRs, Rarity = rarity, Method = "ship",
    };

    [Theory]
    [InlineData("common", 6)]
    [InlineData("uncommon", 5)]
    [InlineData("rare", 4)]
    [InlineData("epic", 3)]
    [InlineData("legendary", 2)]
    public void MaxNodes_MatchesDataminedTierCaps(string rarity, int expected)
    {
        Assert.Equal(expected, ClusterLimits.MaxNodes(rarity));
    }

    [Fact]
    public void MaxNodes_UnknownRarity_HasNoCap()
    {
        Assert.Null(ClusterLimits.MaxNodes(""));
        Assert.Null(ClusterLimits.MaxNodes("mythic"));
    }

    [Fact]
    public void CheckRs_ExactWithinCap_StillMatches()
    {
        var (matches, nodes, isExact, _) = Ore("rare").CheckRs(4 * 3600);
        Assert.True(matches);
        Assert.True(isExact);
        Assert.Equal(4, nodes);
    }

    [Fact]
    public void CheckRs_ExactBeyondCap_IsDropped()
    {
        var (matches, _, _, _) = Ore("rare").CheckRs(5 * 3600);
        Assert.False(matches);
    }

    [Fact]
    public void CheckRs_CloseBeyondCap_IsDropped()
    {
        // 18010 / 3600 = 5.003: a 0.06% close match at 5 nodes, one past the rare-tier cap.
        var (matches, _, _, _) = Ore("rare").CheckRs(18010);
        Assert.False(matches);
    }

    [Fact]
    public void CheckRs_CommonSixNodes_Matches_SevenDropped()
    {
        Assert.True(Ore("common").CheckRs(6 * 3600).Matches);
        Assert.False(Ore("common").CheckRs(7 * 3600).Matches);
    }

    [Fact]
    public void CheckRs_CloseMatchAtTheCap_StillMatches()
    {
        // 14458 / 3600 = 4.016: a 0.4% close match at exactly the rare-tier cap of 4. Kills a
        // mutation that compares the raw ratio against the cap instead of the rounded count.
        var (matches, nodes, isExact, _) = Ore("rare").CheckRs(14458);
        Assert.True(matches);
        Assert.False(isExact);
        Assert.Equal(4, nodes);
    }

    [Fact]
    public void CheckRs_UnknownRarity_FailsOpen()
    {
        // No cap data must never silently hide results: an unknown tier decodes unbounded.
        var (matches, nodes, _, _) = Ore("").CheckRs(50 * 3600);
        Assert.True(matches);
        Assert.Equal(50, nodes);
    }

    // Data guard: the cap map keys on the seed's rarity strings. A ship ore whose rarity
    // ever falls outside the five known tiers would silently decode uncapped, so pin the
    // seed here rather than discover it in-game.
    [Fact]
    public void Seed_EveryShipOre_HasACappedRarity()
    {
        using var seed = SeedTestFixture.LoadSeed();
        var shipOres = 0;
        foreach (var r in seed.RootElement.GetProperty("resources").EnumerateArray())
        {
            if (r.GetProperty("method").GetString() != "ship") continue;
            shipOres++;
            var rarity = r.GetProperty("rarity").GetString();
            Assert.NotNull(ClusterLimits.MaxNodes(rarity!));
        }
        // The seed carries 26 ship ores today. A floor keeps this guard from passing vacuously
        // if the method value is ever renamed (the loop body would simply never run).
        Assert.True(shipOres >= 20, $"only {shipOres} ship ores found; the method filter may be stale");
    }
}
