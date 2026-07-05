using NexusApp.Models.Cargo;
using NexusApp.Views;
using Xunit;

namespace NexusApp.Tests.Cargo;

// Characterization tests for CargoWebView.ComputeLayout - the pure grid-frame + origin math that feeds
// the 3D payload. These lock the CURRENT behavior (min-corner normalization + 2dp rounding, which fixed
// the layout-shift bugs, and the Wy extent swap). Do not "fix" these numbers without a matching change
// to the Three.js scene in Web/cargo/index.html.
public class CargoPayloadTests
{
    private static GridDef Grid(int id, int w, int d, int h, double? px, double? py, double? pz, bool wy = false) =>
        new() { Id = id, W = w, D = d, H = h, PosX = px, PosY = py, PosZ = pz, WAlongShipY = wy };

    private static ShipCargoDef Ship(params GridDef[] grids) =>
        new() { Id = "test", DisplayName = "Test", Grids = grids };

    [Fact]
    public void PositionedShip_NormalizesMinCornerToOrigin()
    {
        // Two grids, centers at x=10 and x=20 (w=4). Min corner is x=8; the layout normalizes so that
        // corner sits at the origin and the returned origin carries the shift for the hull anchor.
        var ship = Ship(
            Grid(0, 4, 2, 2, px: 10, py: 5, pz: 3),
            Grid(1, 4, 2, 2, px: 20, py: 5, pz: 3));
        var (frames, origin) = CargoWebView.ComputeLayout(ship);

        Assert.NotNull(origin);
        Assert.Equal(8.0, origin![0], 3);      // minX corner = 10 - 4/2
        Assert.Equal(0.0, frames[0].X, 3);     // normalized to origin
        Assert.Equal(10.0, frames[1].X, 3);    // 18 - 8
        Assert.Equal(0.0, frames[0].Y, 3);     // shared min Y -> 0
        Assert.Equal(0.0, frames[0].Z, 3);     // shared min Z -> 0
    }

    [Fact]
    public void PositionedShip_RoundsFramesAndOriginToTwoDecimals()
    {
        var ship = Ship(Grid(0, 3, 3, 3, px: 1.234, py: 2.345, pz: 0.456));
        var (frames, origin) = CargoWebView.ComputeLayout(ship);

        // Single grid normalizes to the origin; the origin carries the rounded min corner exactly as
        // the implementation computes it (locks the 2dp rounding).
        Assert.Equal(0.0, frames[0].X, 5);
        Assert.Equal(0.0, frames[0].Y, 5);
        Assert.Equal(0.0, frames[0].Z, 5);
        Assert.NotNull(origin);
        Assert.Equal(System.Math.Round(1.234 - 1.5, 2), origin![0]);
        Assert.Equal(System.Math.Round(2.345 - 1.5, 2), origin![1]);
        Assert.Equal(System.Math.Round(0.456 - 1.5, 2), origin![2]);
    }

    [Fact]
    public void WyGrid_SwapsLateralAndDepthExtents()
    {
        // Wy: the W axis runs fore-aft, so the frame's lateral size comes from D and its depth from W.
        var ship = Ship(Grid(0, 6, 2, 2, px: 10, py: 10, pz: 2, wy: true));
        var (frames, _) = CargoWebView.ComputeLayout(ship);
        Assert.Equal(2, frames[0].SizeX);   // = D
        Assert.Equal(6, frames[0].SizeY);   // = W
        Assert.True(frames[0].Wy);
    }

    [Fact]
    public void SchematicShip_UsesSyntheticStripAndNullOrigin()
    {
        // No datamined positions -> side-by-side strip with a 2-cell gap, null origin.
        var ship = Ship(
            Grid(0, 4, 2, 2, px: null, py: null, pz: null),
            Grid(1, 3, 2, 2, px: null, py: null, pz: null));
        var (frames, origin) = CargoWebView.ComputeLayout(ship);
        Assert.Null(origin);
        Assert.Equal(0.0, frames[0].X, 5);
        Assert.Equal(6.0, frames[1].X, 5);   // grid0 width (4) + gap (2)
    }

    [Fact]
    public void NullShip_ReturnsEmpty()
    {
        var (frames, origin) = CargoWebView.ComputeLayout(null);
        Assert.Empty(frames);
        Assert.Null(origin);
    }
}
