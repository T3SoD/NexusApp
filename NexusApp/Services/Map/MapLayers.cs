using NexusApp.Models;

namespace NexusApp.Services.Map;

// Pure C# providers of the four MAP tab layer pin dictionaries that feed MapLayerPins (Task 6,
// MapSceneBuilder.cs): trade terminals, mission guide contested-zone sites, mineable ore
// locations, and the Exec Hangar object. HARD CONTRACT shared by every dictionary-returning
// method here: a key is only ever added once at least one value has been pushed onto its list,
// so no entry ever carries an empty list. MapSceneBuilder.BuildInit derives its per-object layer
// booleans purely from key presence (an empty-list entry would falsely flag an object as pinned).
public static class MapLayers
{
    // Groups terminals by resolved catalog object via catalog.ResolveTerminal (location ->
    // planetOrMoon -> orbit fallback, MapCatalog.cs). A terminal that misses every level is
    // dropped; the drop count is logged once per call, only when at least one terminal was
    // dropped (n == 0 never logs).
    public static IReadOnlyDictionary<int, IReadOnlyList<int>> BuildTrade(
        IReadOnlyList<MarketTerminal> terminals, MapCatalog catalog)
    {
        var byObject = new Dictionary<int, List<int>>();
        var unmatched = 0;

        foreach (var terminal in terminals)
        {
            var obj = catalog.ResolveTerminal(terminal);
            if (obj is null)
            {
                unmatched++;
                continue;
            }

            if (!byObject.TryGetValue(obj.Id, out var list))
                byObject[obj.Id] = list = new List<int>();
            list.Add(terminal.Id);
        }

        if (unmatched > 0)
            Logger.Info($"[UI] map: {unmatched} terminals unmatched");

        return byObject.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<int>)kv.Value);
    }

    // Fixed table, spec adaptation 3: the mission guide contested-zone sites the MAP tab pins.
    // GuideId values are GuideCatalog.All ids verbatim - MapLayersTests cross-checks every row
    // both ways (resolves in the embedded MapCatalog, and its GuideId exists in GuideCatalog).
    // "supervisor" covers two distinct asteroid objects (PYAM-SUPVISR-3-4 and -3-5) and so
    // appears twice with the same GuideId.
    public static readonly IReadOnlyList<(string GuideId, string System, string Place)> GuideSites =
    [
        ("checkmate", "Pyro", "Checkmate"),
        ("exchange", "Pyro", "PYAM-EXHANG-0-1"),
        ("orbituary", "Pyro", "Orbituary"),
        ("ruin", "Pyro", "Ruin Station"),
        ("supervisor", "Pyro", "PYAM-SUPVISR-3-4"),
        ("supervisor", "Pyro", "PYAM-SUPVISR-3-5"),
    ];

    // Every row in GuideSites is locked by MapLayersTests to resolve against the embedded
    // catalog, so this is the drift guard for a future starmap patch renaming a site. A row that
    // still fails to resolve at runtime is skipped rather than thrown, so a stale table entry
    // degrades the MAP tab's guide layer instead of crashing it.
    public static IReadOnlyDictionary<int, string> BuildGuides(MapCatalog catalog)
    {
        var byObject = new Dictionary<int, string>();
        foreach (var (guideId, system, place) in GuideSites)
        {
            var obj = catalog.ByName(system, place);
            if (obj is not null)
                byObject[obj.Id] = guideId;
        }
        return byObject;
    }

    // For each resource, for each location name: catalog match by name, tried across every
    // system present in the catalog (a resource's Locations entries are plain community strings
    // with no system tag of their own). A location name that misses in every system is skipped
    // silently - community location strings do not all exist as map objects.
    public static IReadOnlyDictionary<int, IReadOnlyList<string>> BuildMining(
        IReadOnlyList<Resource> resources, MapCatalog catalog)
    {
        var systems = catalog.Objects.Select(o => o.System).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var byObject = new Dictionary<int, List<string>>();

        foreach (var resource in resources)
        {
            foreach (var locationName in resource.Locations)
            {
                MapObject? obj = null;
                foreach (var system in systems)
                {
                    obj = catalog.ByName(system, locationName);
                    if (obj is not null) break;
                }
                if (obj is null) continue;

                if (!byObject.TryGetValue(obj.Id, out var list))
                    byObject[obj.Id] = list = new List<string>();
                if (!list.Contains(resource.Name))
                    list.Add(resource.Name);
            }
        }

        return byObject.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value);
    }

    public static int? HangarObject(MapCatalog catalog) => catalog.ByName("Pyro", "PYAM-EXHANG-0-1")?.Id;

    // ── Layers built from LIVE app state (app review G11) ───────────────────────────────────────
    // Every layer above is static reference data - the same pins for every player, every session.
    // The app has been holding two sets of places that are specific to THIS player and THIS session
    // (the stops their accepted contracts require, and the refineries their work orders are sitting
    // in) and the map showed neither, which is what made it a reference chart rather than a tool.
    //
    // Both resolve FREE TEXT, not ids, and both are honest about it. Contract stop names come from
    // OCR or a Game.log Deliver line; refinery names come from a picker with a free-text fallback.
    // Plenty will never resolve ("Pickup (TBD)", a system-level destination, an OCR misread), and an
    // unresolved place is silently absent from the layer rather than pinned somewhere plausible.

    /// <summary>Objects that an active haul needs the player to visit, mapped to the labels shown
    /// on their pins. Pickups and dropoffs are one layer: what matters on a map is that the place
    /// is on the run, and the label says which kind it is.</summary>
    public static IReadOnlyDictionary<int, IReadOnlyList<string>> BuildHauls(
        IReadOnlyList<Haul> hauls, MapCatalog catalog)
    {
        var byObject = new Dictionary<int, List<string>>();

        foreach (var haul in hauls)
        {
            if (!haul.IsActive) continue;   // a finished contract is not somewhere you still have to go
            foreach (var leg in haul.Legs)
            {
                if (leg.Completed) continue;
                var place = leg.Role == HaulRole.Dropoff ? leg.Destination : haul.PickupName;
                if (string.IsNullOrWhiteSpace(place)) continue;

                // Same resolver ConsolidationOrder uses for these exact strings, so a stop that
                // gets a distance in the hauling list is the stop that gets a pin here.
                var obj = catalog.ResolvePlayerLocation(place, rawToken: null);
                if (obj is null) continue;

                var label = leg.Role == HaulRole.Dropoff && !string.IsNullOrWhiteSpace(leg.Commodity)
                    ? $"Drop {leg.Commodity}"
                    : leg.Role == HaulRole.Dropoff ? "Dropoff" : "Pickup";

                if (!byObject.TryGetValue(obj.Id, out var list))
                    byObject[obj.Id] = list = new List<string>();
                if (!list.Contains(label)) list.Add(label);
            }
        }

        return byObject.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value);
    }

    /// <summary>Refineries with a work order still in them, mapped to their order labels. Complete
    /// orders are excluded: the point of the layer is where the player still has to go.</summary>
    public static IReadOnlyDictionary<int, IReadOnlyList<string>> BuildOrders(
        IEnumerable<WorkOrder> orders, MapCatalog catalog)
    {
        var systems = catalog.Objects.Select(o => o.System).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var byObject = new Dictionary<int, List<string>>();

        foreach (var order in orders)
        {
            if (order.Status == WorkOrderStatus.Complete) continue;
            if (string.IsNullOrWhiteSpace(order.Refinery)) continue;

            // A work order records a refinery NAME and no system, so this searches every system for
            // it - the same shape BuildMining uses for community location strings. The name is
            // written in UEX vocabulary by the picker ("Stanton Gateway (Pyro)"), so it goes through
            // the same parenthetical strip the refinery yield rows use.
            var station = RefineryPlaces.BaseName(order.Refinery);
            MapObject? obj = null;
            foreach (var system in systems)
            {
                obj = catalog.ByName(system, station);
                if (obj is not null) break;
            }
            if (obj is null) continue;

            var label = string.IsNullOrWhiteSpace(order.Label) ? order.StatusLabel : order.Label;
            if (!byObject.TryGetValue(obj.Id, out var list))
                byObject[obj.Id] = list = new List<string>();
            if (!list.Contains(label)) list.Add(label);
        }

        return byObject.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value);
    }
}
