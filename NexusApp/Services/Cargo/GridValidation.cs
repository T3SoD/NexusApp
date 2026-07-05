using NexusApp.Models.Cargo;

namespace NexusApp.Services.Cargo;

// Physical-consistency check for a single grid: the largest container it accepts must actually fit
// its cell dimensions in some orientation. BuildGrid enforces positive/bounded dimensions and
// standard cap sizes, but not that the accepted size can physically fit, so a caps-only import merge
// (submission caps kept over the owner's smaller dimensions) could otherwise persist a grid that
// accepts a container it can never hold. Test Fill enforces this interactively; this is the same
// rule for the non-interactive apply/save paths.
public static class GridValidation
{
    // Returns null when the grid is physically consistent, otherwise a human-readable reason.
    public static string? Problem(GridDef grid)
    {
        int scu = grid.MaxContainerScu;
        if (scu <= 0) return null;
        var box = BoxType.Of(scu);
        if (box.Orientations.Any(o => o.W <= grid.W && o.D <= grid.D && o.H <= grid.H)) return null;
        var f = box.Size;
        return $"Grid {grid.Id + 1} accepts {scu} SCU ({f.W}x{f.D}x{f.H}) but is only {grid.W}x{grid.D}x{grid.H} cells";
    }

    // The first problem across a ship's grids, or null if all are consistent.
    public static string? FirstProblem(IReadOnlyList<GridDef> grids)
    {
        foreach (var g in grids)
        {
            var p = Problem(g);
            if (p != null) return p;
        }
        return null;
    }
}
