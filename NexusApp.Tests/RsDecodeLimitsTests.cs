using NexusApp.Models;
using Xunit;

namespace NexusApp.Tests;

// SC 4.10 removed the practical bound on rocks per scanned pack: single pings of 43,000+ RS
// are many unchanged rocks summed (per-rock signatures and the DataCore cluster presets are
// byte-identical to 4.9; the growth is procedural spawning the game files cannot predict).
// The issue #34 per-rarity caps no longer bound real readings, so the decoder matches any
// node count and the 0.5% close-match band is the only remaining limit.
public class RsDecodeLimitsTests
{
    private static Resource Ore(string rarity, int baseRs = 3600) => new()
    {
        Name = "TestOre", BaseRs = baseRs, Rarity = rarity, Method = "ship",
    };

    [Fact]
    public void CheckRs_ExactSmallCount_Matches()
    {
        var (matches, nodes, isExact, _) = Ore("rare").CheckRs(4 * 3600);
        Assert.True(matches);
        Assert.True(isExact);
        Assert.Equal(4, nodes);
    }

    [Fact]
    public void CheckRs_ExactBeyondOldClusterCap_Matches()
    {
        // 5 nodes, one past the old rare-tier cap of 4 that issue #34 enforced pre-4.10.
        var (matches, nodes, isExact, _) = Ore("rare").CheckRs(5 * 3600);
        Assert.True(matches);
        Assert.True(isExact);
        Assert.Equal(5, nodes);
    }

    [Fact]
    public void CheckRs_CloseBeyondOldClusterCap_Matches()
    {
        // 18010 / 3600 = 5.003: a 0.06% close match at 5 nodes.
        var (matches, nodes, isExact, _) = Ore("rare").CheckRs(18010);
        Assert.True(matches);
        Assert.False(isExact);
        Assert.Equal(5, nodes);
    }

    [Fact]
    public void CheckRs_LargePackReading_Decodes()
    {
        // The 4.10 field-report shape: a 43,020 ping over a gold pack = 12 x 3585.
        var (matches, nodes, isExact, _) = Ore("rare", baseRs: 3585).CheckRs(43020);
        Assert.True(matches);
        Assert.True(isExact);
        Assert.Equal(12, nodes);
    }

    [Fact]
    public void CheckRs_OutsideCloseBand_StillRefuses()
    {
        // 18200 / 3600 = 5.056: 1.1% off the nearest multiple, past the 0.5% band.
        Assert.False(Ore("rare").CheckRs(18200).Matches);
    }

    [Fact]
    public void CheckRs_UnknownRarity_DecodesUnbounded()
    {
        var (matches, nodes, _, _) = Ore("").CheckRs(50 * 3600);
        Assert.True(matches);
        Assert.Equal(50, nodes);
    }
}
