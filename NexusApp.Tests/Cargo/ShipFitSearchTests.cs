using NexusApp.Models.Cargo;
using NexusApp.Services.Cargo;
using Xunit;

namespace NexusApp.Tests.Cargo;

public class ShipFitSearchTests
{
    private static GridDef Grid(int id, int w, int d, int h, int cap) =>
        new() { Id = id, W = w, D = d, H = h, AcceptedCaps = BoxType.SizesDesc.Where(s => s <= cap).ToArray(), Name = $"g{id}" };

    private static ShipCargoDef Ship(string id, params GridDef[] grids) =>
        new() { Id = id, DisplayName = id, Grids = grids };

    private static List<ManifestItem> Manifest(int scu, int cap) =>
        new() { new ManifestItem { Label = "Ore", Scu = scu, Cap = cap } };

    [Fact]
    public void Rank_PutsSmallestOneTripShipFirst()
    {
        var small = Ship("small", Grid(0, 2, 2, 2, 8));    // 8 SCU
        var big = Ship("big", Grid(0, 4, 4, 4, 32));       // 64 SCU
        var ranked = ShipFitSearch.Rank(new[] { big, small }, Manifest(8, 8));

        Assert.Equal("small", ranked[0].Ship.Id);
        Assert.True(ranked[0].FitsInOneTrip);
    }

    [Fact]
    public void Rank_PutsShapeFailuresLast()
    {
        var fits = Ship("fits", Grid(0, 6, 2, 2, 32));     // holds the 6x2x2 box
        var shapeFail = Ship("shapefail", Grid(0, 4, 4, 4, 32)); // SCU headroom but no 6-long axis
        var ranked = ShipFitSearch.Rank(new[] { shapeFail, fits }, Manifest(24, 24));

        Assert.Equal("fits", ranked[0].Ship.Id);
        Assert.Equal("shapefail", ranked[^1].Ship.Id);
        Assert.True(ranked[^1].ShapeFail);
    }

    [Fact]
    public void Rank_FewerTripsBeatsSmallerShip()
    {
        var oneTrip = Ship("onetrip", Grid(0, 2, 2, 4, 8));  // 16 SCU, both cubes in one trip
        var twoTrip = Ship("twotrip", Grid(0, 2, 2, 2, 8));  // 8 SCU, needs two trips
        var ranked = ShipFitSearch.Rank(new[] { twoTrip, oneTrip }, Manifest(16, 8));

        Assert.Equal("onetrip", ranked[0].Ship.Id);
        Assert.Equal(1, ranked[0].Trips);
    }
}
