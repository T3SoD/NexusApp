using NexusApp.Models.Cargo;
using NexusApp.Services.Cargo;
using Xunit;

namespace NexusApp.Tests.Cargo;

public class GridFingerprintTests
{
    private static GridDef Grid(int id, int w, int d, int h, IReadOnlyList<int>? accepts = null,
        double? px = null, double? py = null, double? pz = null, bool wy = false) =>
        new()
        {
            Id = id, W = w, D = d, H = h, AcceptedCaps = accepts ?? new[] { 1, 2, 4, 8 },
            PosX = px, PosY = py, PosZ = pz, WAlongShipY = wy,
        };

    [Fact]
    public void SameGrids_SameHash()
    {
        var a = new[] { Grid(0, 4, 2, 2), Grid(1, 3, 3, 3) };
        var b = new[] { Grid(0, 4, 2, 2), Grid(1, 3, 3, 3) };
        Assert.Equal(GridFingerprint.Of(a), GridFingerprint.Of(b));
    }

    [Fact]
    public void GridOrder_DoesNotChangeHash()
    {
        var a = new[] { Grid(0, 4, 2, 2), Grid(1, 3, 3, 3) };
        var b = new[] { Grid(1, 3, 3, 3), Grid(0, 4, 2, 2) };
        Assert.Equal(GridFingerprint.Of(a), GridFingerprint.Of(b));
    }

    [Fact]
    public void ChangedDimension_ChangesHash()
    {
        Assert.NotEqual(GridFingerprint.Of(new[] { Grid(0, 4, 2, 2) }),
                        GridFingerprint.Of(new[] { Grid(0, 4, 2, 3) }));
    }

    [Fact]
    public void ChangedPosition_ChangesHash()
    {
        Assert.NotEqual(GridFingerprint.Of(new[] { Grid(0, 4, 2, 2, px: 1, py: 2, pz: 3) }),
                        GridFingerprint.Of(new[] { Grid(0, 4, 2, 2, px: 1, py: 2, pz: 4) }));
    }

    [Fact]
    public void NullVsZeroPosition_ChangesHash()
    {
        Assert.NotEqual(GridFingerprint.Of(new[] { Grid(0, 4, 2, 2, px: null, py: null, pz: null) }),
                        GridFingerprint.Of(new[] { Grid(0, 4, 2, 2, px: 0, py: 0, pz: 0) }));
    }

    [Fact]
    public void ChangedAccepts_ChangesHash()
    {
        Assert.NotEqual(GridFingerprint.Of(new[] { Grid(0, 4, 2, 2, accepts: new[] { 1, 2, 4 }) }),
                        GridFingerprint.Of(new[] { Grid(0, 4, 2, 2, accepts: new[] { 1, 2, 4, 8 }) }));
    }
}
