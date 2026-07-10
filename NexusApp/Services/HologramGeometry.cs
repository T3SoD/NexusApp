using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media.Media3D;

namespace NexusApp.Services;

// Pure math for the Mining Codex hologram (NexusHologram control): per-class wireframes,
// orthographic projection (Y-yaw then fixed X-tilt), and composition ring segments.
// The vertex sets and projection mirror the approved design-lab mock (codex-motion) exactly.
public static class HologramGeometry
{
    public readonly record struct Edge(int A, int B);
    public sealed record Wireframe(Point3D[] Vertices, Edge[] Edges);
    public readonly record struct RingSegment(double StartDeg, double SweepDeg, int SourceIndex);

    // Metal: elongated octahedron. Vertex/edge data frozen from the codex-motion mock.
    private static readonly Wireframe Metal = new(
        new[] { new Point3D(0,1.3,0), new Point3D(0,-1.3,0), new Point3D(0.8,0,0),
                new Point3D(-0.8,0,0), new Point3D(0,0,0.8), new Point3D(0,0,-0.8) },
        new Edge[] { new(0,2), new(0,3), new(0,4), new(0,5), new(1,2), new(1,3), new(1,4), new(1,5),
                     new(2,4), new(4,3), new(3,5), new(5,2) });

    // Gem: hexagonal bipyramid.
    private static readonly Wireframe Gem = new(
        new[] { new Point3D(0.75,0,0), new Point3D(0.375,0,0.6495), new Point3D(-0.375,0,0.6495),
                new Point3D(-0.75,0,0), new Point3D(-0.375,0,-0.6495), new Point3D(0.375,0,-0.6495),
                new Point3D(0,1.15,0), new Point3D(0,-1.15,0) },
        new Edge[] { new(0,1), new(1,2), new(2,3), new(3,4), new(4,5), new(5,0),
                     new(6,0), new(6,1), new(6,2), new(6,3), new(6,4), new(6,5),
                     new(7,0), new(7,1), new(7,2), new(7,3), new(7,4), new(7,5) });

    // Mineral: faceted cluster - primary shard plus a small offset shard.
    private static readonly Wireframe Mineral = new(
        new[] { new Point3D(0,1.1,0), new Point3D(0,-1.1,0), new Point3D(0.7,0,0),
                new Point3D(-0.7,0,0), new Point3D(0,0,0.7), new Point3D(0,0,-0.7),
                new Point3D(0.6,0.1,0.25), new Point3D(0.6,-0.8,0.25), new Point3D(1.0,-0.35,0.25),
                new Point3D(0.2,-0.35,0.25), new Point3D(0.6,-0.35,0.65), new Point3D(0.6,-0.35,-0.15) },
        new Edge[] { new(0,2), new(0,3), new(0,4), new(0,5), new(1,2), new(1,3), new(1,4), new(1,5),
                     new(2,4), new(4,3), new(3,5), new(5,2),
                     new(6,8), new(6,9), new(6,10), new(6,11), new(7,8), new(7,9), new(7,10), new(7,11),
                     new(8,10), new(10,9), new(9,11), new(11,8) });

    /// <summary>Wireframe for an ore class ("Metal"/"Mineral"/"Gem", case-insensitive);
    /// unknown classes fall back to Metal.</summary>
    public static Wireframe For(string oreClass) => oreClass?.Trim().ToLowerInvariant() switch
    {
        "gem" => Gem,
        "mineral" => Mineral,
        _ => Metal,
    };

    /// <summary>Orthographic projection: yaw about Y, then fixed tilt about X, then scale and
    /// center. Screen Y grows downward, so world +Y maps to screen -Y.</summary>
    public static Point[] Project(Point3D[] vertices, double yawRad, double tiltRad, double scale, Point center)
    {
        var result = new Point[vertices.Length];
        double cy = System.Math.Cos(yawRad), sy = System.Math.Sin(yawRad);
        double ct = System.Math.Cos(tiltRad), st = System.Math.Sin(tiltRad);
        for (int i = 0; i < vertices.Length; i++)
        {
            var v = vertices[i];
            double x1 = v.X * cy + v.Z * sy;
            double z1 = -v.X * sy + v.Z * cy;
            double y2 = v.Y * ct - z1 * st;
            result[i] = new Point(center.X + x1 * scale, center.Y - y2 * scale);
        }
        return result;
    }

    /// <summary>Composition ring: one arc segment per positive percentage, clockwise from
    /// 12 o'clock, values normalized so sweeps total 360 minus one gap per segment.</summary>
    public static RingSegment[] ComputeRing(IReadOnlyList<double> percentages, double gapDeg = 4.0)
    {
        // Carry the ORIGINAL index through the filter so a caller can identify the primary
        // segment by source index rather than by position in the (filtered) result array -
        // a zero-or-negative primary percentage must not silently promote a byproduct into
        // its place.
        var parts = percentages.Select((p, i) => (p, i)).Where(t => t.p > 0).ToList();
        if (parts.Count == 0) return System.Array.Empty<RingSegment>();
        double sum = parts.Sum(t => t.p);
        double arc = 360.0 - parts.Count * gapDeg;
        var segs = new RingSegment[parts.Count];
        double cursor = -90.0 + gapDeg / 2.0;
        for (int i = 0; i < parts.Count; i++)
        {
            double sweep = parts[i].p / sum * arc;
            segs[i] = new RingSegment(cursor, sweep, parts[i].i);
            cursor += sweep + gapDeg;
        }
        return segs;
    }
}
