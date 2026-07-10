using System;
using System.Linq;
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
        Assert.Equal(-88, segs[0].StartDeg, 6);
        Assert.Equal(356, segs[0].SweepDeg, 6);            // one segment, one gap
    }

    [Fact]
    public void ComputeRing_Empty_ReturnsEmpty()
    {
        Assert.Empty(HologramGeometry.ComputeRing(Array.Empty<double>()));
    }

    [Fact]
    public void For_WireframeData_MatchesFrozenMockValues()
    {
        // Full-value pin: the vertex/edge sets are frozen from the approved codex-motion mock;
        // any drift here is a silent break of the mock contract, so assert every number.
        var metal = HologramGeometry.For("Metal");
        Assert.Equal(new[] { new Point3D(0,1.3,0), new Point3D(0,-1.3,0), new Point3D(0.8,0,0),
            new Point3D(-0.8,0,0), new Point3D(0,0,0.8), new Point3D(0,0,-0.8) }, metal.Vertices);
        Assert.Equal(new[] { (0,2),(0,3),(0,4),(0,5),(1,2),(1,3),(1,4),(1,5),(2,4),(4,3),(3,5),(5,2) },
            metal.Edges.Select(e => (e.A, e.B)).ToArray());

        var gem = HologramGeometry.For("Gem");
        Assert.Equal(new[] { new Point3D(0.75,0,0), new Point3D(0.375,0,0.6495), new Point3D(-0.375,0,0.6495),
            new Point3D(-0.75,0,0), new Point3D(-0.375,0,-0.6495), new Point3D(0.375,0,-0.6495),
            new Point3D(0,1.15,0), new Point3D(0,-1.15,0) }, gem.Vertices);
        Assert.Equal(new[] { (0,1),(1,2),(2,3),(3,4),(4,5),(5,0),(6,0),(6,1),(6,2),(6,3),(6,4),(6,5),
            (7,0),(7,1),(7,2),(7,3),(7,4),(7,5) }, gem.Edges.Select(e => (e.A, e.B)).ToArray());

        var mineral = HologramGeometry.For("Mineral");
        Assert.Equal(new[] { new Point3D(0,1.1,0), new Point3D(0,-1.1,0), new Point3D(0.7,0,0),
            new Point3D(-0.7,0,0), new Point3D(0,0,0.7), new Point3D(0,0,-0.7),
            new Point3D(0.6,0.1,0.25), new Point3D(0.6,-0.8,0.25), new Point3D(1.0,-0.35,0.25),
            new Point3D(0.2,-0.35,0.25), new Point3D(0.6,-0.35,0.65), new Point3D(0.6,-0.35,-0.15) },
            mineral.Vertices);
        Assert.Equal(new[] { (0,2),(0,3),(0,4),(0,5),(1,2),(1,3),(1,4),(1,5),(2,4),(4,3),(3,5),(5,2),
            (6,8),(6,9),(6,10),(6,11),(7,8),(7,9),(7,10),(7,11),(8,10),(10,9),(9,11),(11,8) },
            mineral.Edges.Select(e => (e.A, e.B)).ToArray());
    }

    [Fact]
    public void Project_YawAndTiltTogether_AppliesCrossTerm()
    {
        // yaw 90deg sends x onto z1=-1; tilt then feeds z1 into y2 = y*cos(t) - z1*sin(t).
        double t = Math.PI / 6;
        var pts = HologramGeometry.Project(
            new[] { new Point3D(1, 0, 0) }, Math.PI / 2, t, 50, new Point(0, 0));
        Assert.Equal(0, pts[0].X, 6);
        Assert.Equal(-Math.Sin(t) * 50, pts[0].Y, 6);   // y2 = 0 - (-1)*sin(t) = sin(t); screen inverts
    }
}
