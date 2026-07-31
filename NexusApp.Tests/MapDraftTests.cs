using NexusApp.Services.Map;
using Xunit;

namespace NexusApp.Tests;

// Draft-math seam for Task 9's MapPage ROUTE BUILDER zone: MapSceneBuilder.DraftLegs is a pure
// function (no WebView2, no WPF) so the leg/total distance math is unit-testable on its own.
public class MapDraftTests
{
    private static readonly MapCatalog Catalog = MapCatalog.LoadEmbedded();

    [Fact]
    public void DraftLegs_ThreeStopRoute_LegsAndTotalWithinOnePercent()
    {
        var everusHarbor = Catalog.ByName("Stanton", "Everus Harbor")!.Id;
        var arcL1WideForest = Catalog.ByName("Stanton", "ARC-L1 Wide Forest Station")!.Id;
        var portTressler = Catalog.ByName("Stanton", "Port Tressler")!.Id;

        var (legs, total) = MapSceneBuilder.DraftLegs(
            new[] { everusHarbor, arcL1WideForest, portTressler }, Catalog);

        Assert.Equal(2, legs.Length);
        AssertGmWithinOnePercent(20.3, legs[0]);
        AssertGmWithinOnePercent(57.4, legs[1]);
        AssertGmWithinOnePercent(77.7, total);
    }

    [Fact]
    public void DraftLegs_SingleStop_EmptyLegsAndZeroTotal()
    {
        var everusHarbor = Catalog.ByName("Stanton", "Everus Harbor")!.Id;

        var (legs, total) = MapSceneBuilder.DraftLegs(new[] { everusHarbor }, Catalog);

        Assert.Empty(legs);
        Assert.Equal(0, total);
    }

    [Fact]
    public void DraftLegs_EmptyDraft_EmptyLegsAndZeroTotal()
    {
        var (legs, total) = MapSceneBuilder.DraftLegs(System.Array.Empty<int>(), Catalog);

        Assert.Empty(legs);
        Assert.Equal(0, total);
    }

    [Fact]
    public void DraftLegs_UnknownId_NullSafeSkipContributesZero()
    {
        var everusHarbor = Catalog.ByName("Stanton", "Everus Harbor")!.Id;
        const int unknownId = -999999;

        var (legs, total) = MapSceneBuilder.DraftLegs(new[] { everusHarbor, unknownId }, Catalog);

        Assert.Single(legs);
        Assert.Equal(0, legs[0]);
        Assert.Equal(0, total);
    }

    // Legs/total from DraftLegs are raw meters; convert to Gm before comparing against the
    // brief's Gm-scale expectations.
    private static void AssertGmWithinOnePercent(double expectedGm, double actualMeters)
    {
        var actualGm = actualMeters / 1_000_000_000.0;
        var tolerance = expectedGm * 0.01;
        Assert.InRange(actualGm, expectedGm - tolerance, expectedGm + tolerance);
    }
}
