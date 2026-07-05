using System.Collections.Generic;
using System.IO;
using System.Linq;
using NexusApp.Models.Cargo;
using NexusApp.Services.Cargo;
using NexusApp.Views;
using Xunit;

namespace NexusApp.Tests.Cargo;

// Covers per-grid tilt (the Railen's canted racks): the payload-space AABB math, the embedded
// catalog wiring, ComputeLayout for a tilted ship, and rotation validation + edit round-trip.
// The quaternions here are the datamine-verified values (solver proved they reproduce the game's
// oriented boxes corner-for-corner); do not change them without re-verifying against the datamine.
public class GridRotationTests
{
    [Fact]
    public void RotatedAabb_IdentityQuaternion_EqualsFootprint()
    {
        // Identity: box (w along x, h up, d fore-aft) -> payload extent (x=w, y=d, z=h).
        var (x, y, z) = CargoWebView.RotatedAabb(new double[] { 0, 0, 0, 1 }, w: 8, h: 4, d: 6);
        Assert.Equal(8.0, x, 6);
        Assert.Equal(6.0, y, 6);
        Assert.Equal(4.0, z, 6);
    }

    [Fact]
    public void RotatedAabb_TiltedTopGrid_MatchesDatamineAabb()
    {
        // Railen grid 2 (left top): a ~40 deg tilt about ship X. The datamine solver computed the
        // enclosing payload AABB as [4.0000, 8.6995, 8.2065] cells; the C# math must agree.
        var q = new double[] { -0.664462, -0.241846, 0.664462, 0.241846 };
        var (x, y, z) = CargoWebView.RotatedAabb(q, w: 8, h: 4, d: 4);
        Assert.Equal(4.00, x, 2);
        Assert.Equal(8.70, y, 2);
        Assert.Equal(8.21, z, 2);
    }

    [Fact]
    public void EmbeddedCatalog_Railen_IsFullyPositionedWithTilts()
    {
        var railen = CargoShipCatalog.LoadEmbedded().ById("gama-railen");
        Assert.NotNull(railen);
        Assert.Equal(6, railen!.Grids.Count);
        Assert.All(railen.Grids, g => Assert.True(g.HasPos));   // every grid positioned -> layout + hologram

        // grids 0-3 are the tilted side/top racks; grids 4-5 are axis-aligned bottoms (Wy, no rot).
        Assert.All(railen.Grids.Take(4), g => Assert.True(g.HasRot));
        Assert.All(railen.Grids.Skip(4), g => { Assert.False(g.HasRot); Assert.True(g.WAlongShipY); });
        Assert.All(railen.Grids.Where(g => g.HasRot), g => Assert.Equal(4, g.Rot!.Count));

        Assert.Equal(640, railen.TotalScu);   // dims/caps unchanged by the tilt work
    }

    [Fact]
    public void ComputeLayout_Railen_AllGridsFramedWithOriginAndTiltPreserved()
    {
        var railen = CargoShipCatalog.LoadEmbedded().ById("gama-railen")!;
        var (frames, origin) = CargoWebView.ComputeLayout(railen);

        Assert.Equal(6, frames.Count);
        Assert.NotNull(origin);   // fully positioned -> real layout, not the synthetic strip
        Assert.NotNull(frames[0].Rot);   // tilted grid carries its quaternion into the frame
        Assert.Null(frames[4].Rot);      // axis-aligned bottom does not

        // Tilted grid 0's AABB extent is wider than its raw footprint (the tilt enlarges the envelope).
        Assert.True(frames[0].Ax > 0 && frames[0].Ay > 0 && frames[0].Az > 0);
        Assert.True(frames[0].Az > frames[0].H);   // vertical envelope exceeds the 2-cell rack height
    }

    private static List<GridOverride> OneGrid(List<double>? rot) =>
        new() { new GridOverride { Id = 0, W = 8, D = 4, H = 4, Cap = 32, Px = 0, Py = 0, Pz = 0, Rot = rot } };

    [Fact]
    public void BuildPreview_ValidUnitQuaternion_RoundTrips()
    {
        var cat = CargoShipCatalog.LoadEmbedded();
        var built = cat.BuildPreview("gama-railen", OneGrid(new List<double> { 0, 0, 0, 1 }));
        Assert.NotNull(built);
        Assert.True(built!.Grids[0].HasRot);
        Assert.Equal(new double[] { 0, 0, 0, 1 }, built.Grids[0].Rot!);
    }

    [Fact]
    public void BuildPreview_RejectsNonUnitQuaternion()
    {
        var cat = CargoShipCatalog.LoadEmbedded();
        Assert.Throws<InvalidDataException>(() =>
            cat.BuildPreview("gama-railen", OneGrid(new List<double> { 1, 1, 1, 1 })));   // norm 2, not unit
    }

    [Fact]
    public void BuildPreview_RejectsWrongLengthQuaternion()
    {
        var cat = CargoShipCatalog.LoadEmbedded();
        Assert.Throws<InvalidDataException>(() =>
            cat.BuildPreview("gama-railen", OneGrid(new List<double> { 0, 0, 1 })));       // 3 components
    }

    [Fact]
    public void BuildPreview_NullRotation_IsAxisAligned()
    {
        var cat = CargoShipCatalog.LoadEmbedded();
        var built = cat.BuildPreview("gama-railen", OneGrid(null));
        Assert.False(built!.Grids[0].HasRot);
    }

    [Fact]
    public void ToOverrides_PreservesTilt()
    {
        // Regression: the GridDef -> GridOverride converter must keep Rot, or export / import / the
        // import-review baseline silently un-tilt the ship.
        var railen = CargoShipCatalog.LoadEmbedded().ById("gama-railen")!;
        var overrides = GridShareService.ToOverrides(railen.Grids);
        Assert.NotNull(overrides[0].Rot);
        Assert.Equal(4, overrides[0].Rot!.Count);
        Assert.Null(overrides[4].Rot);   // axis-aligned bottom stays null
    }

    [Fact]
    public void ExportImport_RoundTripsTilt()
    {
        // Full .nexusgrid round-trip: export the Railen, re-import, rebuild - the tilt quaternion must
        // survive the wire and land back on the built grids identical to the embedded source.
        var railen = CargoShipCatalog.LoadEmbedded().ById("gama-railen")!;
        var pkg = new GridSharePackage
        {
            ShipId = "gama-railen", ShipName = "Railen",
            Grids = GridShareService.ToOverrides(railen.Grids),
        };
        var path = Path.GetTempFileName();
        try
        {
            GridShareService.Export(pkg, path);
            var imported = GridShareService.Import(path);
            var built = CargoShipCatalog.LoadEmbedded().BuildPreview("gama-railen", imported.Grids)!;
            Assert.True(built.Grids[0].HasRot);
            Assert.Equal(railen.Grids[0].Rot!, built.Grids[0].Rot!);
            Assert.False(built.Grids[4].HasRot);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
