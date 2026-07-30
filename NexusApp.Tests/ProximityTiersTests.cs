using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

public class ProximityTiersTests
{
    private static MarketTerminal T(int id, string system = "", string location = "", string orbit = "", string planetOrMoon = "") =>
        new(id, $"Terminal {id}", "commodity", false, system, location, orbit, planetOrMoon);

    [Fact]
    public void Derive_SameOrbitName_ReturnsSameOrbit()
    {
        var a = T(1, system: "Stanton", orbit: "ArcCorp");
        var b = T(2, system: "Stanton", orbit: "ArcCorp");
        Assert.Equal(ProximityTier.SameOrbit, ProximityTiers.Derive(a, b));
    }

    [Fact]
    public void Derive_SameStationLocation_ReturnsSameOrbit()
    {
        // Two terminals at the same station but with no orbit_name recorded on either -
        // Location still confirms co-location.
        var a = T(1, system: "Stanton", location: "Everus Harbor");
        var b = T(2, system: "Stanton", location: "Everus Harbor");
        Assert.Equal(ProximityTier.SameOrbit, ProximityTiers.Derive(a, b));
    }

    [Fact]
    public void Derive_DifferentOrbitSamePlanetOrMoon_ReturnsSamePlanet()
    {
        var a = T(1, system: "Stanton", orbit: "MIC-L2", planetOrMoon: "MicroTech");
        var b = T(2, system: "Stanton", orbit: "MIC-L5", planetOrMoon: "MicroTech");
        Assert.Equal(ProximityTier.SamePlanet, ProximityTiers.Derive(a, b));
    }

    [Fact]
    public void Derive_DifferentPlanetSameSystem_ReturnsSameSystem()
    {
        var a = T(1, system: "Stanton", planetOrMoon: "ArcCorp");
        var b = T(2, system: "Stanton", planetOrMoon: "MicroTech");
        Assert.Equal(ProximityTier.SameSystem, ProximityTiers.Derive(a, b));
    }

    [Fact]
    public void Derive_DifferentSystem_ReturnsCrossSystem()
    {
        var a = T(1, system: "Stanton");
        var b = T(2, system: "Pyro");
        Assert.Equal(ProximityTier.CrossSystem, ProximityTiers.Derive(a, b));
    }

    // Empty fields never assert a tighter tier - they fall through to the next wider check.
    [Fact]
    public void Derive_BothOrbitAndLocationEmpty_FallsThroughToPlanetCheck()
    {
        var a = T(1, system: "Stanton", planetOrMoon: "Hurston");
        var b = T(2, system: "Stanton", planetOrMoon: "Hurston");
        Assert.Equal(ProximityTier.SamePlanet, ProximityTiers.Derive(a, b));
    }

    [Fact]
    public void Derive_OnlyOneSideHasOrbit_DoesNotCountAsSameOrbit()
    {
        var a = T(1, system: "Stanton", orbit: "ArcCorp", planetOrMoon: "ArcCorp");
        var b = T(2, system: "Stanton", planetOrMoon: "ArcCorp");   // no orbit recorded
        Assert.Equal(ProximityTier.SamePlanet, ProximityTiers.Derive(a, b));
    }

    [Fact]
    public void Derive_OnlyOneSideHasPlanetOrMoon_FallsThroughToSystemCheck()
    {
        var a = T(1, system: "Stanton", planetOrMoon: "ArcCorp");
        var b = T(2, system: "Stanton");   // no planet/moon recorded
        Assert.Equal(ProximityTier.SameSystem, ProximityTiers.Derive(a, b));
    }

    [Fact]
    public void Derive_BothSystemsEmpty_ReturnsCrossSystem()
    {
        var a = T(1);
        var b = T(2);
        Assert.Equal(ProximityTier.CrossSystem, ProximityTiers.Derive(a, b));
    }

    [Fact]
    public void Derive_SystemComparison_IsCaseInsensitive()
    {
        var a = T(1, system: "STANTON");
        var b = T(2, system: "stanton");
        Assert.Equal(ProximityTier.SameSystem, ProximityTiers.Derive(a, b));
    }

    [Theory]
    [InlineData(ProximityTier.SameOrbit, "SAME ORBIT")]
    [InlineData(ProximityTier.SamePlanet, "SAME PLANET")]
    [InlineData(ProximityTier.SameSystem, "SAME SYSTEM")]
    [InlineData(ProximityTier.CrossSystem, "CROSS-SYSTEM")]
    public void Label_MapsEveryTier(ProximityTier tier, string expected) =>
        Assert.Equal(expected, ProximityTiers.Label(tier));
}
