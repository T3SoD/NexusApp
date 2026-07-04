using NexusApp.Models.Cargo;

namespace NexusApp.Services.Cargo;

public sealed record GridDiffResult(bool HasChanges, IReadOnlyList<string> Lines);

// Compares the owner's current grid set to an incoming submission, matched by grid Id, and
// produces human-readable lines describing added, removed, and changed grids. Whole-ship
// keep/discard uses this only to inform the decision.
public static class GridDiff
{
    public static GridDiffResult Compute(IReadOnlyList<GridOverride> current, IReadOnlyList<GridOverride> incoming)
    {
        var lines = new List<string>();
        var cur = current.ToDictionary(g => g.Id);
        var inc = incoming.ToDictionary(g => g.Id);

        foreach (var id in cur.Keys.Where(id => !inc.ContainsKey(id)).OrderBy(i => i))
            lines.Add($"Grid {id + 1}: removed");
        foreach (var id in inc.Keys.Where(id => !cur.ContainsKey(id)).OrderBy(i => i))
            lines.Add($"Grid {id + 1}: added ({Describe(inc[id])})");
        foreach (var id in cur.Keys.Where(inc.ContainsKey).OrderBy(i => i))
        {
            var diffs = FieldDiffs(cur[id], inc[id]);
            if (diffs.Count > 0) lines.Add($"Grid {id + 1}: {string.Join(", ", diffs)}");
        }

        bool has = lines.Count > 0;
        if (!has) lines.Add("No differences.");
        return new GridDiffResult(has, lines);
    }

    private static string Describe(GridOverride g) => $"{g.W}x{g.D}x{g.H}, cap {g.Cap}";

    private static List<string> FieldDiffs(GridOverride a, GridOverride b)
    {
        var d = new List<string>();
        if (a.W != b.W || a.D != b.D || a.H != b.H) d.Add($"size {a.W}x{a.D}x{a.H} -> {b.W}x{b.D}x{b.H}");
        if (a.Cap != b.Cap) d.Add($"cap {a.Cap} -> {b.Cap}");
        if (!AcceptsEqual(a.Accepts, b.Accepts)) d.Add($"accepts {Fmt(a.Accepts)} -> {Fmt(b.Accepts)}");
        if (a.Px != b.Px || a.Py != b.Py || a.Pz != b.Pz)
            d.Add($"pos ({a.Px:0.##},{a.Py:0.##},{a.Pz:0.##}) -> ({b.Px:0.##},{b.Py:0.##},{b.Pz:0.##})");
        if (a.Wy != b.Wy) d.Add($"wy {a.Wy} -> {b.Wy}");
        return d;
    }

    private static bool AcceptsEqual(List<int>? a, List<int>? b) =>
        (a ?? new List<int>()).OrderBy(x => x).SequenceEqual((b ?? new List<int>()).OrderBy(x => x));

    private static string Fmt(List<int>? a) =>
        a == null || a.Count == 0 ? "-" : string.Join("/", a.OrderBy(x => x));
}
