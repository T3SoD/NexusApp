namespace NexusApp.Services;

/// <summary>
/// Pure, headless bucketing logic for the SCMDB import (issue #3 design point 3): takes the
/// blueprint names an export listed as completed, resolves each through a caller-supplied
/// delegate, and sorts the result into three buckets. The delegate stands in for the real
/// official-name + localization pipeline GameLogBlueprintImporter uses (Task 2 plugs that in;
/// tests use a plain dictionary lookup). ADD-ONLY by construction: there is no bucket and no
/// code path that ever un-owns a blueprint, and a name already owned but absent from the export
/// is never even considered - the plan only ever hands back names to mark owned.
/// </summary>
public static class ScmdbImportPlan
{
    /// <param name="ToImport">Resolved canonical names not already owned - hand these to
    /// Settings.SetBlueprintsOwned (or equivalent) to apply the import.</param>
    /// <param name="AlreadyOwned">Resolved canonical names that were already owned - no action taken.</param>
    /// <param name="Unrecognized">Raw parsed names the resolver could not map to a known blueprint.</param>
    public sealed record Result(
        IReadOnlyList<string> ToImport,
        IReadOnlyList<string> AlreadyOwned,
        IReadOnlyList<string> Unrecognized);

    /// <summary>
    /// Builds the import plan. <paramref name="resolveName"/> maps a raw SCMDB blueprint name to
    /// its canonical Nexus name, or returns null if unrecognized. <paramref name="ownedNames"/> is
    /// matched case-insensitively (mirrors GameLogBlueprintImporter's own name lookups). Two raw
    /// names resolving to the same canonical blueprint are counted once, in first-seen order.
    /// </summary>
    public static Result Build(IEnumerable<string> parsedNames, IReadOnlyCollection<string> ownedNames,
        Func<string, string?> resolveName)
    {
        var owned = new HashSet<string>(ownedNames, StringComparer.OrdinalIgnoreCase);
        var toImport = new List<string>();
        var alreadyOwned = new List<string>();
        var unrecognized = new List<string>();

        var seenResolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenUnrecognized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in parsedNames)
        {
            var resolved = resolveName(raw);
            if (resolved is null)
            {
                if (seenUnrecognized.Add(raw)) unrecognized.Add(raw);
                continue;
            }
            if (!seenResolved.Add(resolved)) continue; // de-dupe: two raw names -> same canonical blueprint
            (owned.Contains(resolved) ? alreadyOwned : toImport).Add(resolved);
        }

        return new Result(toImport, alreadyOwned, unrecognized);
    }
}
