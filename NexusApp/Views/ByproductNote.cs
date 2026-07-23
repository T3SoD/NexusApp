using System.Globalization;
using NexusApp.Models;

namespace NexusApp.Views;

/// <summary>
/// Pure, UI-free helpers for the Blueprint Library "BYPRODUCT SOURCING" block (issue #12).
/// Turns the datamined found-in rows for one ingredient into band-bar data (Section C of the
/// approved mock) and resolves which host rocks actually spawn at a ranked location for the
/// "via &lt;host&gt;" chips (Section B). No WPF or DB types live here, so the grouping, scaling,
/// and presence rules stay unit-testable; the view owns colours, tracks, and chip borders.
/// </summary>
public static class ByproductNote
{
    /// <summary>One composition band shared by one or more host ores. Hosts keep their found-in
    /// order; Min/Max are the % band this resource occupies inside those host rocks.</summary>
    public readonly record struct BandGroup(IReadOnlyList<string> Hosts, double Min, double Max);

    /// <summary>Which of an ore's host rocks actually spawn at one location. Present is the hosts
    /// to chip, capped for display in found-in order; Overflow is how many present hosts were
    /// folded past the cap (the mock's "+N"). HasHosts is false only when the ore has no found-in
    /// rows at all (the view then adds nothing); true with an empty Present means the view shows
    /// the dim "no host here" chip.</summary>
    public readonly record struct HostChips(IReadOnlyList<string> Present, int Overflow, bool HasHosts);

    /// <summary>Group the found-in rows by identical (Min, Max) band. Groups run ascending by Min
    /// then Max; hosts keep their input order within a group. Empty when there are no sources.
    /// This is the data the band-bar renderer consumes (it draws them richest band first).</summary>
    public static IReadOnlyList<BandGroup> Groups(IReadOnlyList<FoundInSource> sources)
    {
        if (sources is null || sources.Count == 0) return System.Array.Empty<BandGroup>();

        var groups = new List<(double Min, double Max, List<string> Hosts)>();
        foreach (var s in sources)
        {
            int idx = groups.FindIndex(g => g.Min == s.MinPct && g.Max == s.MaxPct);
            if (idx < 0) groups.Add((s.MinPct, s.MaxPct, new List<string> { s.Ore }));
            else groups[idx].Hosts.Add(s.Ore);
        }

        groups.Sort((a, b) => a.Min != b.Min ? a.Min.CompareTo(b.Min) : a.Max.CompareTo(b.Max));
        return groups.Select(g => new BandGroup(g.Hosts, g.Min, g.Max)).ToList();
    }

    /// <summary>Fraction of the bar track a band fills: its Max relative to the ore's richest band
    /// (the richest band fills the whole track). A zero or absent ore max maps to 0. Example: a 5%
    /// band with a 50% ore max renders at 5/50 = 0.1 of the track.</summary>
    public static double BarFraction(double bandMax, double oreMax) =>
        oreMax <= 0 ? 0 : bandMax / oreMax;

    /// <summary>A band's upper bound as a label, trailing zeros trimmed and a percent sign:
    /// "50%", "5%", "2.5%".</summary>
    public static string Percent(double value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture) + "%";

    /// <summary>Which host ores from an ingredient's found-in rows actually spawn at
    /// <paramref name="location"/>. Distinct hosts are taken in found-in order; each is kept only
    /// when its own Locations (supplied by the caller via <paramref name="hostLocations"/>) include
    /// the location. Present is capped at <paramref name="cap"/> and the remainder counted as
    /// Overflow, giving the mock's "two chips then +N" rule. Case-insensitive on both host name and
    /// location. No DB or WPF access: the caller passes each host's locations in.</summary>
    public static HostChips HostsPresentAt(
        IReadOnlyList<FoundInSource>? sources,
        IReadOnlyDictionary<string, IReadOnlyList<string>> hostLocations,
        string location,
        int cap = 2)
    {
        var distinct = new List<string>();
        var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        if (sources != null)
            foreach (var s in sources)
                if (seen.Add(s.Ore)) distinct.Add(s.Ore);

        var present = new List<string>();
        foreach (var host in distinct)
            if (hostLocations != null
                && hostLocations.TryGetValue(host, out var locs)
                && locs != null
                && locs.Any(l => string.Equals(l, location, System.StringComparison.OrdinalIgnoreCase)))
                present.Add(host);

        int keep = cap < 0 ? 0 : cap;
        int overflow = present.Count > keep ? present.Count - keep : 0;
        IReadOnlyList<string> shown = overflow > 0 ? present.Take(keep).ToList() : present;

        return new HostChips(shown, overflow, distinct.Count > 0);
    }
}
