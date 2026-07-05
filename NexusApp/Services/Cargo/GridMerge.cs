using NexusApp.Models.Cargo;

namespace NexusApp.Services.Cargo;

// Which aspects of an imported submission to bring in, per matching grid Id. GridSet controls the
// grid roster (add grids the submission has, drop ones it omits); the other flags control which
// fields of a matched grid come from the submission versus stay as the owner's current version.
[Flags]
public enum GridMergeAspect
{
    None = 0,
    Positions = 1,   // px/py/pz placement
    Sizes = 2,       // w/d/h dimensions and the wy orientation flag
    Caps = 4,        // cap and accepted container sizes
    GridSet = 8,     // grids the submission adds or removes vs the current roster
    All = Positions | Sizes | Caps | GridSet,
}

// Merge a contributor submission onto the owner's current grids, taking only the selected aspects.
// Grids are matched by Id. A grid only in the submission is brought in whole when GridSet is set; a
// grid only in the current set is kept (GridSet off) or dropped (GridSet on and the submission
// omits it). For a matched grid, each selected flag pulls that field group from the submission.
public static class GridMerge
{
    public static List<GridOverride> Apply(
        IReadOnlyList<GridOverride> current, IReadOnlyList<GridOverride> incoming, GridMergeAspect aspects)
    {
        var cur = current.ToDictionary(g => g.Id);
        var inc = incoming.ToDictionary(g => g.Id);
        bool gridSet = aspects.HasFlag(GridMergeAspect.GridSet);
        bool pos = aspects.HasFlag(GridMergeAspect.Positions);
        bool size = aspects.HasFlag(GridMergeAspect.Sizes);
        bool caps = aspects.HasFlag(GridMergeAspect.Caps);

        var ids = (gridSet ? inc.Keys : cur.Keys).OrderBy(i => i);
        var result = new List<GridOverride>();
        foreach (var id in ids)
        {
            cur.TryGetValue(id, out var c);
            inc.TryGetValue(id, out var i);
            if (c == null) { if (i != null) result.Add(i); continue; }   // added grid: bring it in whole
            if (i == null) { result.Add(c); continue; }                  // no submission match: keep current
            result.Add(c with
            {
                Px = pos ? i.Px : c.Px, Py = pos ? i.Py : c.Py, Pz = pos ? i.Pz : c.Pz,
                W = size ? i.W : c.W, D = size ? i.D : c.D, H = size ? i.H : c.H, Wy = size ? i.Wy : c.Wy,
                Cap = caps ? i.Cap : c.Cap, Accepts = caps ? i.Accepts : c.Accepts,
            });
        }
        return result;
    }
}
