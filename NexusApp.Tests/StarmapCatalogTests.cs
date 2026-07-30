using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

public class StarmapCatalogTests
{
    private static MarketTerminal Terminal(string system, string location = "", string orbit = "", string planetOrMoon = "")
        => new(1, "Test Terminal", "trading", false, system, location, orbit, planetOrMoon);

    [Fact]
    public void LoadEmbedded_HasExpectedPlaceCount()
    {
        var catalog = StarmapCatalog.LoadEmbedded();
        Assert.Equal(215, catalog.PlaceCount);
    }

    [Fact]
    public void LoadEmbedded_SpotCheckKnownEntry()
    {
        // ARC-L1 Wide Forest Station, Stanton, kind=location, from the source extractor's own
        // output (out/starmap_dive/starmap_locations.json) - values copied verbatim from the file.
        var catalog = StarmapCatalog.LoadEmbedded();
        var pos = catalog.Resolve(Terminal("Stanton", location: "ARC-L1 Wide Forest Station"));
        Assert.NotNull(pos);
        Assert.Equal(16729220034.957521, pos!.Value.X, 3);
        Assert.Equal(-19942048185.734924, pos.Value.Y, 3);
        Assert.Equal(2239765.2149551306, pos.Value.Z, 3);
    }

    [Fact]
    public void Resolve_LocationBeatsPlanetOrMoonBeatsOrbit_WhenAllThreeResolve()
    {
        // ArcCorp: a "location" (ARC-L1 Wide Forest Station), a "planetOrMoon" (ArcCorp) and an
        // "orbit" (ArcCorp Lagrange Point 1) all exist in Stanton with distinct positions - a
        // terminal that carries all three hierarchy fields must resolve to the LOCATION position.
        var catalog = StarmapCatalog.LoadEmbedded();
        var t = Terminal("Stanton", location: "ARC-L1 Wide Forest Station", planetOrMoon: "ArcCorp", orbit: "ArcCorp Lagrange Point 1");
        var pos = catalog.Resolve(t);
        Assert.NotNull(pos);
        Assert.Equal(16729220034.957521, pos!.Value.X, 3);   // the location's own X, not the planet's or the orbit's
    }

    [Fact]
    public void Resolve_FallsThroughToPlanetOrMoon_WhenLocationEmpty()
    {
        var catalog = StarmapCatalog.LoadEmbedded();
        var t = Terminal("Stanton", location: "", planetOrMoon: "ArcCorp", orbit: "ArcCorp Lagrange Point 1");
        var pos = catalog.Resolve(t);
        Assert.NotNull(pos);
        Assert.Equal(18587664739.85602, pos!.Value.X, 3);   // ArcCorp the planet's own X
    }

    [Fact]
    public void Resolve_FallsThroughToOrbit_WhenLocationAndPlanetOrMoonEmpty()
    {
        var catalog = StarmapCatalog.LoadEmbedded();
        var t = Terminal("Stanton", location: "", planetOrMoon: "", orbit: "ArcCorp Lagrange Point 1");
        var pos = catalog.Resolve(t);
        Assert.NotNull(pos);
        Assert.Equal(16729134637.384644, pos!.Value.X, 3);   // the orbit point's own X
    }

    [Fact]
    public void Resolve_NonMatchingLocation_FallsThroughToPlanetOrMoon()
    {
        // A non-empty Location that simply has no match in the catalog is not a hard failure -
        // it falls through to the next level, same as an empty one would.
        var catalog = StarmapCatalog.LoadEmbedded();
        var t = Terminal("Stanton", location: "Nowhere Station That Does Not Exist", planetOrMoon: "ArcCorp");
        var pos = catalog.Resolve(t);
        Assert.NotNull(pos);
        Assert.Equal(18587664739.85602, pos!.Value.X, 3);
    }

    [Fact]
    public void Resolve_NothingMatches_ReturnsNull()
    {
        var catalog = StarmapCatalog.LoadEmbedded();
        var t = Terminal("Stanton", location: "Nowhere", planetOrMoon: "Nowhere Either", orbit: "Still Nowhere");
        Assert.Null(catalog.Resolve(t));
    }

    [Fact]
    public void Resolve_AllHierarchyFieldsEmpty_ReturnsNull()
    {
        var catalog = StarmapCatalog.LoadEmbedded();
        Assert.Null(catalog.Resolve(Terminal("Stanton")));
    }

    [Fact]
    public void Resolve_NullTerminal_ReturnsNull()
    {
        var catalog = StarmapCatalog.LoadEmbedded();
        Assert.Null(catalog.Resolve(null));
    }

    [Fact]
    public void Resolve_IsOrdinalIgnoreCase_OnSystemAndName()
    {
        var catalog = StarmapCatalog.LoadEmbedded();
        var t = Terminal("STANTON", location: "arc-l1 wide forest station");
        var pos = catalog.Resolve(t);
        Assert.NotNull(pos);
        Assert.Equal(16729220034.957521, pos!.Value.X, 3);
    }

    [Fact]
    public void DistanceMeters_CrossSystem_ReturnsNull()
    {
        var catalog = StarmapCatalog.LoadEmbedded();
        var a = Terminal("Stanton", location: "Everus Harbor");
        var b = Terminal("Pyro", location: "Everus Harbor");   // same name, different system on purpose
        Assert.Null(catalog.DistanceMeters(a, b));
    }

    [Fact]
    public void DistanceMeters_EitherSideUnresolved_ReturnsNull()
    {
        var catalog = StarmapCatalog.LoadEmbedded();
        var a = Terminal("Stanton", location: "Everus Harbor");
        var b = Terminal("Stanton", location: "Nowhere At All");
        Assert.Null(catalog.DistanceMeters(a, b));
    }

    [Fact]
    public void DistanceMeters_NullTerminal_ReturnsNull()
    {
        var catalog = StarmapCatalog.LoadEmbedded();
        var a = Terminal("Stanton", location: "Everus Harbor");
        Assert.Null(catalog.DistanceMeters(a, null));
        Assert.Null(catalog.DistanceMeters(null, a));
    }

    [Fact]
    public void DistanceMeters_PinnedRealDistance_EverusHarborToCruL1AmbitiousDream()
    {
        // Pinned real-world figure (the extractor's own verified output): 30,014,696 km between
        // Everus Harbor and CRU-L1 Ambitious Dream Station, both in Stanton. Asserted here in Gm,
        // within 0.01 Gm tolerance of the extractor's rounded 30.015 Gm figure.
        var catalog = StarmapCatalog.LoadEmbedded();
        var a = Terminal("Stanton", location: "CRU-L1 Ambitious Dream Station");
        var b = Terminal("Stanton", location: "Everus Harbor");
        var meters = catalog.DistanceMeters(a, b);
        Assert.NotNull(meters);
        double gm = meters!.Value / 1_000_000_000.0;
        Assert.True(Math.Abs(gm - 30.015) <= 0.01, $"Expected ~30.015 Gm within 0.01, got {gm} Gm");
    }

    [Fact]
    public void Load_MalformedStream_ReturnsEmptyCatalog_NoThrow()
    {
        using var bad = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("{ not json"));
        var catalog = StarmapCatalog.Load(bad);
        Assert.Equal(0, catalog.PlaceCount);
        Assert.Null(catalog.Resolve(Terminal("Stanton", location: "Everus Harbor")));
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
        => Assert.Equal(expected, StarmapCatalog.FormatGm(meters));
}
