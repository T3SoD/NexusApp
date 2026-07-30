using System.Linq;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Verifies ParseTerminals' new Orbit/PlanetOrMoon fields (Task 3) against real UEX /terminals
// rows, not synthetic ones - the field names (orbit_name/planet_name/moon_name) are confirmed
// present in the real capture, but their combinations (moon_name never appears without
// planet_name; orbit_name can appear alone) are only provable against real data.
public class TerminalsFixtureTests
{
    [Fact]
    public void ParseTerminals_FixtureFile_ParsesExpectedCountsAndSkipsTheBadRow()
    {
        var rows = MarketParse.ParseTerminals(TerminalsFixture.LoadSampleJson(), out var skipped);

        Assert.Equal(8, rows.Count);
        Assert.Equal(1, skipped);
    }

    [Fact]
    public void ParseTerminals_MalformedRow_NeverAppearsInResults()
    {
        var rows = MarketParse.ParseTerminals(TerminalsFixture.LoadSampleJson(), out _);

        Assert.DoesNotContain(rows, r => r.Name.Contains("Malformed Row"));
    }

    // ARC-L1 admin desk and its refinery ore-sales counter (ids 1 and 234, real capture): same
    // orbit ("ArcCorp Lagrange Point 1") and same station, differing only in is_refinery.
    [Fact]
    public void ParseTerminals_ArcL1Pair_SharesOrbitAndStationLocation()
    {
        var rows = MarketParse.ParseTerminals(TerminalsFixture.LoadSampleJson(), out _);

        var admin = rows.Single(r => r.Id == 1);
        var refineryDesk = rows.Single(r => r.Id == 234);

        Assert.Equal("ArcCorp Lagrange Point 1", admin.Orbit);
        Assert.Equal("ArcCorp Lagrange Point 1", refineryDesk.Orbit);
        Assert.Equal("ARC-L1 Wide Forest Station", admin.Location);
        Assert.Equal("ARC-L1 Wide Forest Station", refineryDesk.Location);
        Assert.False(admin.IsRefinery);
        Assert.True(refineryDesk.IsRefinery);
    }

    // ARC-L4 (id 4, real capture): different orbit than ARC-L1 but the same planet (ArcCorp) -
    // the real-data pair that exercises ProximityTiers' SamePlanet tier.
    [Fact]
    public void ParseTerminals_ArcL4_DifferentOrbitSamePlanetAsArcL1()
    {
        var rows = MarketParse.ParseTerminals(TerminalsFixture.LoadSampleJson(), out _);

        var arcL4 = rows.Single(r => r.Id == 4);

        Assert.Equal("ArcCorp Lagrange Point 4", arcL4.Orbit);
        Assert.Equal("ArcCorp", arcL4.PlanetOrMoon);
    }

    // ArcCorp Mining Area 045 (id 6, real capture): orbit_name, planet_name, AND moon_name are
    // all populated together on this row - planet_name ("ArcCorp") wins over moon_name ("Wala")
    // per FirstNonEmptyStr's declared order. Location falls back to outpost_name (no station).
    [Fact]
    public void ParseTerminals_MiningArea_PlanetNameWinsOverMoonNameWhenBothPresent()
    {
        var rows = MarketParse.ParseTerminals(TerminalsFixture.LoadSampleJson(), out _);

        var miningArea = rows.Single(r => r.Id == 6);

        Assert.Equal("ArcCorp", miningArea.Orbit);
        Assert.Equal("ArcCorp", miningArea.PlanetOrMoon);
        Assert.Equal("ArcCorp Mining Area 045", miningArea.Location);
    }

    // Conscientious Objects - Levski (id 109, real capture, Nyx system): orbit_name ("Delamar")
    // is populated with NO planet_name or moon_name - proves orbit can stand alone in real data.
    [Fact]
    public void ParseTerminals_Levski_OrbitPopulatedWithoutPlanetOrMoon()
    {
        var rows = MarketParse.ParseTerminals(TerminalsFixture.LoadSampleJson(), out _);

        var levski = rows.Single(r => r.Id == 109);

        Assert.Equal("Nyx", levski.System);
        Assert.Equal("Delamar", levski.Orbit);
        Assert.Equal("", levski.PlanetOrMoon);
        Assert.Equal("Levski", levski.Location);
    }

    // Admin - UEX Station (id 422, real capture): every hierarchy field AND location field is
    // null - a real "nothing recorded" row, not a hypothetical one.
    [Fact]
    public void ParseTerminals_UexStation_AllHierarchyAndLocationFieldsEmpty()
    {
        var rows = MarketParse.ParseTerminals(TerminalsFixture.LoadSampleJson(), out _);

        var uexStation = rows.Single(r => r.Id == 422);

        Assert.Equal("", uexStation.Orbit);
        Assert.Equal("", uexStation.PlanetOrMoon);
        Assert.Equal("", uexStation.Location);
    }

    // Wikelo Emporium Dasi Station (id 709, real capture): Location is recorded (station name)
    // but the hierarchy fields are all null - the case ProximityTiersTests'
    // Derive_SameStationLocation_ReturnsSameOrbit depends on being real, not invented.
    [Fact]
    public void ParseTerminals_Wikelo_LocationPresentButHierarchyFieldsEmpty()
    {
        var rows = MarketParse.ParseTerminals(TerminalsFixture.LoadSampleJson(), out _);

        var wikelo = rows.Single(r => r.Id == 709);

        Assert.Equal("Wikelo Emporium Dasi Station", wikelo.Location);
        Assert.Equal("", wikelo.Orbit);
        Assert.Equal("", wikelo.PlanetOrMoon);
    }
}
