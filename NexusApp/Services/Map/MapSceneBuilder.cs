using System.Text.Json;

namespace NexusApp.Services.Map;

// Per-object layer membership for the starmap scene, keyed by MapObject.Id. Built by the Task 7
// providers (trade terminals, guide entries, mineable ores, the exec hangar object) and consumed
// here to compute the four per-row booleans (trade/guide/mine/hangar) BuildInit serializes onto
// each catalog row. An object counts as flagged for a layer purely by key presence in the matching
// dictionary (an empty terminal/ore list still flags the object) - the same "present means true"
// contract as MapCatalog's own alias indexing.
public sealed record MapLayerPins(
    IReadOnlyDictionary<int, IReadOnlyList<int>> TradeTerminalsByObject,  // objectId -> UEX terminal ids
    IReadOnlyDictionary<int, string> GuideIdByObject,                     // objectId -> GuideEntry.Id
    IReadOnlyDictionary<int, IReadOnlyList<string>> OresByObject,         // objectId -> ore names
    int? HangarObjectId);

// Pure C# bridge payload builders for the MAP tab's WebView2 scene (Web/map/index.html). Every
// method serializes one of the page's inbound message shapes (init, layerToggle, select,
// focusObject, routeChanged, plannerRoute, measureArm, systemView) via JsonSerializer.Serialize of
// an anonymous object - same pattern as CargoWebView.BuildPayload. Property names are declared
// lowercase in source so the emitted JSON is camelCase by construction, with no naming policy
// needed. No I/O, no WebView2 dependency: the caller (the MAP tab host) owns posting the result and
// resolving state (current system, layer toggles, Motion.Reduced) that this class only carries.
public static class MapSceneBuilder
{
    // Filters the catalog to the active system (case-insensitive, matching MapCatalog's own
    // comparer) and serializes the full init snapshot the page's applyInit expects. Each catalog
    // row carries the four layer booleans precomputed here - the page never derives layer
    // membership itself (see the page's own header comment).
    public static string BuildInit(MapCatalog catalog, string system, MapLayerPins pins,
        bool tradeOn, bool guidesOn, bool miningOn, bool hangarOn, bool asteroidsOn,
        int? selection, IReadOnlyList<int> draft, IReadOnlyList<int> planner, bool reduced,
        int? player = null)
    {
        var rows = catalog.Objects
            .Where(o => string.Equals(o.System, system, StringComparison.OrdinalIgnoreCase))
            .Select(o => new
            {
                id = o.Id,
                name = o.Name,
                type = o.Type,
                x = o.X,
                y = o.Y,
                z = o.Z,
                parent = o.Parent,
                trade = pins.TradeTerminalsByObject.ContainsKey(o.Id),
                guide = pins.GuideIdByObject.ContainsKey(o.Id),
                mine = pins.OresByObject.ContainsKey(o.Id),
                hangar = pins.HangarObjectId.HasValue && pins.HangarObjectId.Value == o.Id,
            })
            .ToList();

        var payload = new
        {
            type = "init",
            system,
            reduced,
            asteroids = asteroidsOn,
            catalog = rows,
            layers = new { trade = tradeOn, guides = guidesOn, mining = miningOn, hangar = hangarOn },
            selection,
            draft,
            planner,
            player,
        };
        return JsonSerializer.Serialize(payload);
    }

    public static string BuildLayerToggle(string layer, bool on) =>
        JsonSerializer.Serialize(new { type = "layerToggle", layer, on });

    public static string BuildSelect(int? id) =>
        JsonSerializer.Serialize(new { type = "select", id });

    public static string BuildFocus(int id) =>
        JsonSerializer.Serialize(new { type = "focusObject", id });

    public static string BuildRoute(IReadOnlyList<int> ids) =>
        JsonSerializer.Serialize(new { type = "routeChanged", ids });

    public static string BuildPlanner(IReadOnlyList<int> ids) =>
        JsonSerializer.Serialize(new { type = "plannerRoute", ids });

    public static string BuildMeasureArm(bool on) =>
        JsonSerializer.Serialize(new { type = "measureArm", on });

    public static string BuildSystemView() =>
        JsonSerializer.Serialize(new { type = "systemView" });

    // Standalone player-marker update (MAP tab: the live Game.log location resolved through
    // MapCatalog.ResolvePlayerLocation), for a change that happens without a full init resend -
    // same "id or null" shape as BuildSelect. A null id clears the marker; an id for an object not
    // in the scene's currently active system is a safe no-op there (the page's own pins lookup
    // handles that, same defensive pattern as focusObject/onSelect - see the page's own header
    // comment), never an error here.
    public static string BuildPlayerMarker(int? id) =>
        JsonSerializer.Serialize(new { type = "playerMarker", id });

    // Pure leg/total distance math for a draft route (MapPage's ROUTE BUILDER zone uses this to
    // paint per-stop leg distances plus the running total): meters between each consecutive pair,
    // in catalog order. A single stop (or an empty draft) has no legs and a zero total. An id that
    // fails to resolve in the catalog - or a same-system pair MapCatalog.DistanceMeters itself
    // refuses - contributes 0 to that leg and to the total rather than throwing: the draft route
    // degrades to an honest zero for that hop instead of crashing the panel. The returned Legs
    // array is always ids.Count-1 long so callers can index it 1:1 against the draft's stop list.
    internal static (double[] Legs, double Total) DraftLegs(IReadOnlyList<int> ids, MapCatalog catalog)
    {
        if (ids.Count < 2) return (Array.Empty<double>(), 0);

        var legs = new double[ids.Count - 1];
        double total = 0;
        for (int i = 1; i < ids.Count; i++)
        {
            var d = catalog.DistanceMeters(catalog.ById(ids[i - 1]), catalog.ById(ids[i])) ?? 0;
            legs[i - 1] = d;
            total += d;
        }
        return (legs, total);
    }
}
