namespace NexusApp.Services.Map;

/// <summary>
/// Puts Mission Guides on the map (app review G7b). Every guide in the catalog is a map of a real
/// place, and the MAP tab has been drawing those places as its GUIDES layer since it shipped - but
/// the guide cards themselves never said where they were, so the two features knew about each other
/// in exactly one direction.
///
/// <para>MapLayers.GuideSites is the existing table and stays the single source: this only reads it
/// the other way round, guide id first instead of map object first, so there is no second list of
/// which guide is where to drift out of step.</para>
///
/// <para>Two of the eight guides (the Tactical Strike Groups pair) have no site and never will -
/// they document a formation, not a location. Those return nothing, and their cards must look
/// exactly as they do today rather than gaining an empty row.</para>
/// </summary>
public static class GuidePlaces
{
    /// <summary>The places one guide covers, in table order. Empty for a guide with no site.</summary>
    public static IReadOnlyList<(string System, string Place)> SitesFor(string? guideId)
        => string.IsNullOrEmpty(guideId)
            ? []
            : MapLayers.GuideSites
                .Where(s => string.Equals(s.GuideId, guideId, StringComparison.OrdinalIgnoreCase))
                .Select(s => (s.System, s.Place))
                .ToList();

    /// <summary>The first of a guide's sites that the catalog can actually place, or null. First
    /// rather than nearest: the table's order is the order the guide itself presents them, and a
    /// card that reordered its own subject by where the player stood would be disorienting.</summary>
    public static MapObject? Resolve(MapCatalog map, string? guideId)
    {
        foreach (var (system, place) in SitesFor(guideId))
            if (map.ByName(system, place) is { } obj) return obj;
        return null;
    }

    /// <summary>What to print on a guide card: the place name, then where it is in the same words
    /// every other surface uses. "Checkmate - Pyro, 12.4 Gm". Null when the guide has no site or
    /// nothing resolves, and the caller then renders no row at all.</summary>
    public static string? Describe(MapCatalog map, string? guideId, MapObject? playerAt)
    {
        var obj = Resolve(map, guideId);
        if (obj is null) return null;

        var where = PlaceLabel.Describe(obj.System, obj, map, playerAt);
        var extra = SitesFor(guideId).Count - 1;
        // A guide covering two sites says so rather than quietly naming one of them - the
        // Supervisor guide is one map of two adjacent facilities, and calling it just "3-4" would
        // be wrong on the card of a guide whose own title is "PYAM-SUPVISR-3-4/5".
        var more = extra > 0 ? $" +{extra}" : "";
        return where is null ? obj.Name + more : $"{obj.Name}{more} - {where}";
    }
}
