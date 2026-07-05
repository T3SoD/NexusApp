using System.IO;
using NexusApp.Models.Cargo;
using NexusApp.Services.Cargo;
using Xunit;

namespace NexusApp.Tests.Cargo;

public class CargoShipCatalogPreviewTests
{
    private static readonly CargoShipCatalog Catalog = CargoShipCatalog.LoadEmbedded();
    private static GridOverride G(int cap = 32) => new() { Id = 0, W = 4, D = 2, H = 2, Cap = cap, Px = 0, Py = 0, Pz = 0 };

    [Fact]
    public void BuildPreview_ValidGrids_BuildsShip()
    {
        var ship = Catalog.BuildPreview("crus-c1-spirit", new List<GridOverride> { G() });
        Assert.NotNull(ship);
        Assert.Single(ship!.Grids);
        Assert.Equal(4 * 2 * 2, ship.Grids[0].Capacity);
    }

    [Fact]
    public void BuildPreview_UnknownShip_ReturnsNull() =>
        Assert.Null(Catalog.BuildPreview("nope-ship", new List<GridOverride> { G() }));

    [Fact]
    public void BuildPreview_BadDimension_Throws() =>
        Assert.Throws<InvalidDataException>(() =>
            Catalog.BuildPreview("crus-c1-spirit",
                new List<GridOverride> { new() { Id = 0, W = 0, D = 2, H = 2, Cap = 8 } }));

    [Fact]
    public void BuildPreview_NonStandardCap_Throws() =>
        Assert.Throws<InvalidDataException>(() =>
            Catalog.BuildPreview("crus-c1-spirit", new List<GridOverride> { G(cap: 7) }));

    [Fact]
    public void BuildPreview_OversizeDimension_Throws() =>
        Assert.Throws<InvalidDataException>(() =>
            Catalog.BuildPreview("crus-c1-spirit",
                new List<GridOverride> { new() { Id = 0, W = 9999, D = 2, H = 2, Cap = 8, Px = 0, Py = 0, Pz = 0 } }));

    [Fact]
    public void BuildPreview_HugeVolume_Throws() =>
        Assert.Throws<InvalidDataException>(() =>
            Catalog.BuildPreview("crus-c1-spirit",
                new List<GridOverride> { new() { Id = 0, W = 100, D = 100, H = 100, Cap = 8, Px = 0, Py = 0, Pz = 0 } }));

    [Fact]
    public void BuildPreview_NonFinitePosition_Throws() =>
        Assert.Throws<InvalidDataException>(() =>
            Catalog.BuildPreview("crus-c1-spirit",
                new List<GridOverride> { new() { Id = 0, W = 4, D = 2, H = 2, Cap = 8, Px = double.NaN, Py = 0, Pz = 0 } }));

    [Fact]
    public void BuildPreview_ExtremePosition_Throws() =>
        Assert.Throws<InvalidDataException>(() =>
            Catalog.BuildPreview("crus-c1-spirit",
                new List<GridOverride> { new() { Id = 0, W = 4, D = 2, H = 2, Cap = 8, Px = 1e9, Py = 0, Pz = 0 } }));

    [Fact]
    public void BuildPreview_NullPositions_BuildsUnpositionedGrid()
    {
        var ship = Catalog.BuildPreview("crus-c1-spirit",
            new List<GridOverride> { new() { Id = 0, W = 4, D = 2, H = 2, Cap = 8, Px = null, Py = null, Pz = null } });
        Assert.NotNull(ship);
        Assert.False(ship!.Grids[0].HasPos);
    }
}
