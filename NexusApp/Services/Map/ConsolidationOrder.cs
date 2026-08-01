using NexusApp.Models;

namespace NexusApp.Services.Map;

/// <summary>
/// Orders consolidated haul stops nearest-first from wherever the player is.
///
/// <para>App review: BuildConsolidation groups legs by location and returns them in dictionary
/// insertion order, which is the order contracts happened to be accepted in. The app has had real
/// coordinates for those places and a live player position the whole time and used neither, so a
/// hauler with six stops got a list whose order meant nothing.</para>
///
/// <para>Stop locations are FREE TEXT (contract OCR, or Game.log leg destinations), not terminal
/// ids, so they are matched by name across every system - the same tier ResolvePlayerLocation uses
/// for a Game.log place name. Plenty will not resolve: "Pickup (TBD)", a system-level destination
/// like "Stanton System", or an OCR misread. Those keep their original relative order and sort
/// LAST, because an unknown distance is not a large one and must never displace a stop we can
/// actually place.</para>
///
/// <para>With no player position, or none of the stops resolving, the original order is returned
/// untouched. Sorting by distance from nowhere would be theatre.</para>
/// </summary>
internal static class ConsolidationOrder
{
    public static List<ConsolidationStop> ByDistanceFrom(
        IEnumerable<ConsolidationStop> stops, MapCatalog map, MapObject? from)
    {
        var list = stops.ToList();
        if (from is null || list.Count < 2) return list;

        // Index by position so unresolvable stops can hold their original relative order rather
        // than being shuffled by an unstable comparison.
        var scored = new List<(ConsolidationStop Stop, int Index, double? Meters)>(list.Count);
        for (int i = 0; i < list.Count; i++)
        {
            var target = map.ResolvePlayerLocation(list[i].Location, rawToken: null);
            scored.Add((list[i], i, map.DistanceMeters(from, target)));
        }

        // Nothing placed: return exactly what came in, rather than a reordering that pretends to
        // mean something.
        if (!scored.Any(s => s.Meters.HasValue)) return list;

        return scored
            .OrderBy(s => s.Meters.HasValue ? 0 : 1)      // placed stops first
            .ThenBy(s => s.Meters ?? 0)                   // then nearest
            .ThenBy(s => s.Index)                         // stable within each group
            .Select(s => s.Stop)
            .ToList();
    }

    /// <summary>The formatted distance to one stop, or null when it does not resolve. Callers append
    /// it and render nothing on null, the same silence rule the price surfaces follow.</summary>
    public static string? DistanceTo(ConsolidationStop stop, MapCatalog map, MapObject? from)
    {
        if (from is null) return null;
        var target = map.ResolvePlayerLocation(stop.Location, rawToken: null);
        return map.DistanceMeters(from, target) is { } m ? MapCatalog.FormatGm(m) : null;
    }
}
