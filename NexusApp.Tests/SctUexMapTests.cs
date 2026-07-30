using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

public class SctUexMapTests
{
    [Fact]
    public void LoadEmbedded_HasExpectedTerminalCounts()
    {
        var map = SctUexMap.LoadEmbedded();
        Assert.Equal(158, map.Terminals.Count);
        Assert.Equal(150, map.Terminals.Values.Count(t => t.CommodityId.HasValue));
    }

    [Fact]
    public void LoadEmbedded_RawIdRouting_ResolvesBothRoles()
    {
        // Nyx > Levski: general-trade terminal 778, refinery-ore-sales terminal 786 - the
        // role-split structural insight the mapping doc calls out (Deliverable 1).
        var map = SctUexMap.LoadEmbedded();
        var levski = map.Terminals["Nyx > Levski"];
        Assert.Equal(778, levski.CommodityId);
        Assert.Equal(786, levski.RawId);
    }

    [Fact]
    public void LoadEmbedded_NullCommodityId_IsSctOnlyLocation()
    {
        var map = SctUexMap.LoadEmbedded();
        var sctOnly = map.Terminals["Pyro > Bloom > Frigid Knot"];
        Assert.Null(sctOnly.CommodityId);
        Assert.Null(sctOnly.RawId);
        Assert.Contains("SCT-only", sctOnly.Note);
    }

    [Fact]
    public void LoadEmbedded_TerminalLookup_IsCaseInsensitive()
    {
        // SCT's own listings carry locations lowercased ("nyx > levski"); the map's keys are
        // "System > Body > Shop" - the join (Task 7) must not have to re-case either side.
        var map = SctUexMap.LoadEmbedded();
        Assert.True(map.Terminals.ContainsKey("nyx > levski"));
        Assert.Equal(778, map.Terminals["NYX > LEVSKI"].CommodityId);
    }

    [Fact]
    public void LoadEmbedded_HasExpectedCommodityCounts()
    {
        var map = SctUexMap.LoadEmbedded();
        Assert.Equal(170, map.Commodities.Count);
        var sctOnlyItem = map.Commodities["mobyGlass Personal Computers"];
        Assert.Null(sctOnlyItem.UexId);
    }

    [Fact]
    public void Load_MalformedStream_ReturnsEmptyMap_NoThrow()
    {
        using var bad = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("{ not json"));
        var map = SctUexMap.Load(bad);
        Assert.Empty(map.Terminals);
        Assert.Empty(map.Commodities);
    }
}
