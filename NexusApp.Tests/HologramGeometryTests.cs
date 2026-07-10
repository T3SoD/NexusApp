using System;
using System.Windows;
using System.Windows.Media.Media3D;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Pure math behind the Codex hologram: per-class wireframes, orthographic projection
// (Y-yaw then X-tilt, mirrors the approved design-lab mock), and composition ring segments.
public class HologramGeometryTests
{
    [Theory]
    [InlineData("Metal", 6, 12)]
    [InlineData("gem", 8, 18)]
    [InlineData("MINERAL", 12, 24)]
    [InlineData("unknown-class", 6, 12)]   // falls back to Metal
    public void For_ReturnsClassWireframe_CaseInsensitive(string cls, int verts, int edges)
    {
        var w = HologramGeometry.For(cls);
        Assert.Equal(verts, w.Vertices.Length);
        Assert.Equal(edges, w.Edges.Length);
    }

    [Fact]
    public void Project_YawZeroTiltZero_MapsXToScreenX()
    {
        var pts = HologramGeometry.Project(
            new[] { new Point3D(1, 0, 0) }, 0, 0, 50, new Point(100, 100));
        Assert.Equal(150, pts[0].X, 6);
        Assert.Equal(100, pts[0].Y, 6);
    }

    [Fact]
    public void Project_QuarterYaw_MovesXBehindCamera()
    {
        // yaw 90deg: x-axis rotates onto -z; screen x returns to center, y unchanged.
        var pts = HologramGeometry.Project(
            new[] { new Point3D(1, 0, 0) }, Math.PI / 2, 0, 50, new Point(100, 100));
        Assert.Equal(100, pts[0].X, 6);
        Assert.Equal(100, pts[0].Y, 6);
    }

    [Fact]
    public void Project_Tilt_LiftsYByCosine_AndScreenYIsInverted()
    {
        // tilt rotates about X: y' = y*cos(t) - z*sin(t); screen y = cy - y'*scale.
        var pts = HologramGeometry.Project(
            new[] { new Point3D(0, 1, 0) }, 0, Math.PI / 6, 50, new Point(0, 0));
        Assert.Equal(0, pts[0].X, 6);
        Assert.Equal(-Math.Cos(Math.PI / 6) * 50, pts[0].Y, 6);
    }

    [Fact]
    public void ComputeRing_TwoParts_SweepsShareArcMinusGaps()
    {
        var segs = HologramGeometry.ComputeRing(new[] { 60.0, 40.0 }, gapDeg: 4);
        Assert.Equal(2, segs.Length);
        Assert.Equal(-88, segs[0].StartDeg, 6);            // -90 + gap/2
        Assert.Equal(0.6 * 352, segs[0].SweepDeg, 6);      // 211.2
        Assert.Equal(segs[0].StartDeg + segs[0].SweepDeg + 4, segs[1].StartDeg, 6);
        Assert.Equal(0.4 * 352, segs[1].SweepDeg, 6);      // 140.8
    }

    [Fact]
    public void ComputeRing_SkipsNonPositive_AndNormalizes()
    {
        var segs = HologramGeometry.ComputeRing(new[] { 0.0, -5.0, 30.0 }, gapDeg: 4);
        Assert.Single(segs);
        Assert.Equal(356, segs[0].SweepDeg, 6);            // one segment, one gap
    }

    [Fact]
    public void ComputeRing_Empty_ReturnsEmpty()
    {
        Assert.Empty(HologramGeometry.ComputeRing(Array.Empty<double>()));
    }
}
