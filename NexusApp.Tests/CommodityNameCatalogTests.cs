using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// The embedded GUID-to-display-name table (D10a). The two fixture GUIDs are the spec section 7
// verbatim transactions: the Pyro Rund buy resolves to Distilled Spirits and the GrimHex sell
// to Corundum, pinned against the extraction so a regenerated table cannot silently rename
// what the ledger already showed.
public class CommodityNameCatalogTests
{
    [Fact]
    public void Embedded_ResolvesTheSpecFixtureGuids()
    {
        var cat = CommodityNameCatalog.LoadEmbedded();
        Assert.Equal("Distilled Spirits", cat.Resolve("e938ab24-2af8-48b5-9af7-8e82fb26dcb3"));
        Assert.Equal("Corundum", cat.Resolve("4236c16b-c47f-4083-9e26-4313733f2326"));
    }

    [Fact]
    public void Resolve_IsCaseInsensitive()
    {
        var cat = CommodityNameCatalog.LoadEmbedded();
        Assert.Equal("Distilled Spirits", cat.Resolve("E938AB24-2AF8-48B5-9AF7-8E82FB26DCB3"));
    }

    [Fact]
    public void Resolve_UnknownOrEmptyGuid_ReturnsNull()
    {
        var cat = CommodityNameCatalog.LoadEmbedded();
        Assert.Null(cat.Resolve("00000000-0000-0000-0000-000000000000"));
        Assert.Null(cat.Resolve(""));
        Assert.Null(cat.Resolve(null));
    }

    [Fact]
    public void Embedded_CarriesTheFullResourceTypeSet()
        => Assert.True(CommodityNameCatalog.LoadEmbedded().Count >= 200);
}
