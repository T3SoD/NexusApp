namespace NexusApp.Models.Cargo;

// An integer size in cell space. One cell is a 1.25m cube and holds 1 SCU of volume.
public readonly record struct CellSize(int W, int D, int H)
{
    public int Volume => W * D * H;
}

// One of the seven standard Star Citizen cargo containers. Footprint is in cells with the
// convention W = long horizontal axis, D = other horizontal axis, H = vertical (gravity).
// Verified two ways (SC wiki + FleetYards maxContainerSize dimensions across 93 ships):
// W*D*H equals the SCU rating exactly for every size. Containers rest upright and rotate only
// in 90-degree yaw (swap W and D); they never tip onto a side, so H is fixed per box.
public sealed class BoxType
{
    public int Scu { get; }
    public CellSize Size { get; }                       // canonical footprint
    public bool IsCube { get; }
    public IReadOnlyList<CellSize> Orientations { get; } // upright yaw variants, fixed order

    private BoxType(int scu, int w, int d, int h)
    {
        Scu = scu;
        Size = new CellSize(w, d, h);
        IsCube = w == d && d == h;
        Orientations = w == d
            ? new[] { new CellSize(w, d, h) }
            : new[] { new CellSize(w, d, h), new CellSize(d, w, h) };
    }

    // Largest first: the greedy split and the placement order both depend on this ordering.
    public static readonly IReadOnlyList<int> SizesDesc = new[] { 32, 24, 16, 8, 4, 2, 1 };

    public static readonly IReadOnlyList<BoxType> All = new[]
    {
        new BoxType(1, 1, 1, 1),
        new BoxType(2, 2, 1, 1),
        new BoxType(4, 2, 2, 1),
        new BoxType(8, 2, 2, 2),
        new BoxType(16, 4, 2, 2),
        new BoxType(24, 6, 2, 2),
        new BoxType(32, 8, 2, 2),
    };

    private static readonly Dictionary<int, BoxType> ByScu = All.ToDictionary(b => b.Scu);

    public static BoxType Of(int scu) => ByScu[scu];
    public static bool IsStandard(int scu) => ByScu.ContainsKey(scu);
}
