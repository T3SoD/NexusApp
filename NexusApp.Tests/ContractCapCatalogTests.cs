using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// The datamined contract -> container cap map, keyed by the exact contract token the Game.log records.
public class ContractCapCatalogTests
{
    private static readonly ContractCapCatalog Catalog = ContractCapCatalog.LoadEmbedded();

    [Fact]
    public void Load_HasManyContracts()
    {
        Assert.True(Catalog.Count > 100, $"expected a populated map, got {Catalog.Count}");
    }

    [Theory]
    [InlineData("HaulCargo_AToB_Interstellar_Small_SunB_GolDMed_Intro", 2)]
    [InlineData("HaulCargo_AToB_Interstellar_Bulk_Ammon_PlasFu_Arg_PreIce_CM", 24)]
    [InlineData("RedWind_Pyro_SupplyGrade_RegionA_CFP_StationToTradepost_Carbon_CargoHauling_AtoB_Intro", 8)]
    public void Lookup_ReturnsDataminedCap(string token, int expected)
    {
        Assert.Equal(expected, Catalog.Lookup(token));
    }

    [Fact]
    public void Lookup_ReturnsNullForUnknownOrEmpty()
    {
        Assert.Null(Catalog.Lookup("BountyHuntersGuild_Bounty_Stanton_Easy_2"));
        Assert.Null(Catalog.Lookup(""));
        Assert.Null(Catalog.Lookup(null));
    }
}
