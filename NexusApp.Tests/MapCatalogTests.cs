using NexusApp.Services;
using NexusApp.Services.Map;
using Xunit;

namespace NexusApp.Tests;

public class MapCatalogTests
{
    // Load once per class (pattern: StarmapCatalogTests, CargoShipCatalogTests) - the embedded
    // artifact is immutable within a test run, so 963 objects need not be parsed per test.
    private static readonly MapCatalog Catalog = MapCatalog.LoadEmbedded();

    [Fact]
    public void LoadEmbedded_HasExpectedObjectCount()
    {
        // 4 source placeholder records ("<= UNINITIALIZED =>", Stanton) were filtered out of the
        // extractor's map artifact; ids 419/437/473/515 are gaps, not renumbered.
        Assert.Equal(963, Catalog.Count);
        Assert.Equal(963, Catalog.Objects.Count);
    }

    [Fact]
    public void ByName_Hurston_HasVerbatimPositionAndParentsToStanton()
    {
        // Values copied verbatim from Data/starmap_map.json (object id 1): x=12850457093,
        // parent=0 (the id of the "Stanton" star object itself).
        var hurston = Catalog.ByName("Stanton", "Hurston");
        Assert.NotNull(hurston);
        Assert.Equal(12850457093, hurston!.X);
        Assert.NotNull(hurston.Parent);

        var parent = Catalog.ById(hurston.Parent!.Value);
        Assert.NotNull(parent);
        Assert.Equal("Stanton", parent!.Name);
    }

    [Fact]
    public void ByName_PyroExhangSite_IsFound()
    {
        var obj = Catalog.ByName("Pyro", "PYAM-EXHANG-0-1");
        Assert.NotNull(obj);
    }

    [Fact]
    public void ByName_Magda_NameIsClean()
    {
        // "Clean" here means the plain catalog Name carries no UEX alias decoration - it is just
        // the moon's own name as stored on the object.
        Assert.Equal("Magda", Catalog.ByName("Stanton", "Magda")!.Name);
    }

    [Fact]
    public void ResolveTerminal_ByLocation_ReturnsMatchingObject()
    {
        var terminal = new MarketTerminal(1, "x", "", false, "Stanton", "Everus Harbor");
        var resolved = Catalog.ResolveTerminal(terminal);
        Assert.NotNull(resolved);
        Assert.Equal("Everus Harbor", resolved!.Name);
    }

    [Fact]
    public void ResolveTerminal_FallsThroughToPlanetOrMoon_WhenLocationEmpty()
    {
        var terminal = new MarketTerminal(1, "x", "", false, "Stanton", "", PlanetOrMoon: "ArcCorp");
        var resolved = Catalog.ResolveTerminal(terminal);
        Assert.NotNull(resolved);
        Assert.Equal("ArcCorp", resolved!.Name);
    }

    [Fact]
    public void ResolveTerminal_FallsThroughToOrbit_WhenLocationAndPlanetOrMoonEmpty()
    {
        var terminal = new MarketTerminal(1, "x", "", false, "Stanton", "", Orbit: "ArcCorp");
        var resolved = Catalog.ResolveTerminal(terminal);
        Assert.NotNull(resolved);
        Assert.Equal("ArcCorp", resolved!.Name);
    }

    [Fact]
    public void ResolveTerminal_NonMatchingLocation_FallsThroughToPlanetOrMoon()
    {
        var terminal = new MarketTerminal(1, "x", "", false, "Stanton", "Nowhere Station", PlanetOrMoon: "ArcCorp");
        var resolved = Catalog.ResolveTerminal(terminal);
        Assert.NotNull(resolved);
        Assert.Equal("ArcCorp", resolved!.Name);
    }

    [Fact]
    public void ResolveTerminal_NothingMatches_ReturnsNull()
    {
        var terminal = new MarketTerminal(1, "x", "", false, "Stanton", "Nowhere", Orbit: "Nowhere Either", PlanetOrMoon: "Still Nowhere");
        Assert.Null(Catalog.ResolveTerminal(terminal));
    }

    [Fact]
    public void ResolveTerminal_NullTerminal_ReturnsNull()
    {
        Assert.Null(Catalog.ResolveTerminal(null));
    }

    [Fact]
    public void ResolveTerminal_IsOrdinalIgnoreCase()
    {
        var terminal = new MarketTerminal(1, "x", "", false, "STANTON", "everus harbor");
        var resolved = Catalog.ResolveTerminal(terminal);
        Assert.NotNull(resolved);
        Assert.Equal("Everus Harbor", resolved!.Name);
    }

    [Fact]
    public void DistanceMeters_HurstonToArcCorp_WithinOnePercentOfPinnedFigure()
    {
        var hurston = Catalog.ByName("Stanton", "Hurston");
        var arcCorp = Catalog.ByName("Stanton", "ArcCorp");
        var meters = Catalog.DistanceMeters(hurston, arcCorp);
        Assert.NotNull(meters);

        const double expected = 2.2882e10;
        double pctError = Math.Abs(meters!.Value - expected) / expected;
        Assert.True(pctError <= 0.01, $"Expected ~{expected} within 1%, got {meters.Value}");
    }

    [Fact]
    public void DistanceMeters_CrossSystem_ReturnsNull()
    {
        var hurston = Catalog.ByName("Stanton", "Hurston");
        var pyroExhang = Catalog.ByName("Pyro", "PYAM-EXHANG-0-1");
        Assert.Null(Catalog.DistanceMeters(hurston, pyroExhang));
    }

    [Fact]
    public void DistanceMeters_EitherSideNull_ReturnsNull()
    {
        var hurston = Catalog.ByName("Stanton", "Hurston");
        Assert.Null(Catalog.DistanceMeters(hurston, null));
        Assert.Null(Catalog.DistanceMeters(null, hurston));
        Assert.Null(Catalog.DistanceMeters(null, null));
    }

    [Fact]
    public void Load_MalformedStream_ReturnsEmptyCatalog_NoThrow()
    {
        using var bad = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("{ not json"));
        var catalog = MapCatalog.Load(bad);
        Assert.Equal(0, catalog.Count);
        Assert.Empty(catalog.Objects);
        Assert.Null(catalog.ById(1));
        Assert.Null(catalog.ByName("Stanton", "Hurston"));
        Assert.Null(catalog.ResolveTerminal(new MarketTerminal(1, "x", "", false, "Stanton", "Everus Harbor")));
    }

    [Fact]
    public void ById_UnknownId_ReturnsNull()
    {
        Assert.Null(Catalog.ById(-1));
    }

    [Fact]
    public void ByName_UnknownName_ReturnsNull()
    {
        Assert.Null(Catalog.ByName("Stanton", "Nowhere At All"));
    }

    [Theory]
    [InlineData(22_900_000_000.0, "22.9 Gm")]     // >= 10 Gm: one decimal
    [InlineData(10_000_000_000.0, "10.0 Gm")]     // exact boundary counts as the >= 10 branch
    [InlineData(1_850_000_000.0, "1.85 Gm")]      // >= 1 Gm: two decimals
    [InlineData(1_000_000_000.0, "1.00 Gm")]      // exact boundary counts as the >= 1 branch
    [InlineData(460_000_000.0, "0.46 Gm")]        // >= 0.1 Gm: two decimals
    [InlineData(100_000_000.0, "0.10 Gm")]        // exact boundary counts as the >= 0.1 branch
    [InlineData(99_000_000.0, "<0.1 Gm")]         // just under 0.1 Gm: floor literal
    [InlineData(1_000.0, "<0.1 Gm")]              // tiny: floor literal
    [InlineData(0.0, "<0.1 Gm")]                  // zero: floor literal
    public void FormatGm_MatchesSpecBuckets(double meters, string expected)
        => Assert.Equal(expected, MapCatalog.FormatGm(meters));
}
