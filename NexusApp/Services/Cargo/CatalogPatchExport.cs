using System.Text.Json;
using System.Text.Json.Nodes;
using NexusApp.Models.Cargo;

namespace NexusApp.Services.Cargo;

// Exports one ship's effective cargo grids as a JSON object in the exact shape of cargo_ships.json (the
// embedded catalog), so the owner can paste-merge an approved override into the catalog by hand and
// commit it. This is the "promote to datamine" step: it writes a file only, and touches no build
// pipeline or datamine tooling.
public static class CatalogPatchExport
{
    public static string ToPatchJson(ShipCargoDef ship)
    {
        var grids = new JsonArray();
        foreach (var g in ship.Grids.OrderBy(x => x.Id))
        {
            var go = new JsonObject
            {
                ["id"] = g.Id, ["w"] = g.W, ["d"] = g.D, ["h"] = g.H, ["cap"] = g.MaxContainerScu,
            };
            // The file omits "accepts" when the set is just the standard sizes up to the cap; keep it
            // only when the grid rejects a size it is big enough for (a narrowed set).
            if (!DerivedFromCap(g))
                go["accepts"] = new JsonArray(g.AcceptedCaps.OrderBy(a => a).Select(a => (JsonNode)a).ToArray());
            if (g.PosX.HasValue) go["px"] = g.PosX.Value;
            if (g.PosY.HasValue) go["py"] = g.PosY.Value;
            if (g.PosZ.HasValue) go["pz"] = g.PosZ.Value;
            if (g.WAlongShipY) go["wy"] = true;
            grids.Add(go);
        }

        var obj = new JsonObject
        {
            ["id"] = ship.Id,
            ["name"] = ship.DisplayName,
            ["manufacturer"] = ship.Manufacturer,
            ["class"] = ship.Classification,
            ["flyable"] = true,   // every ship in the cargo catalog is flyable
            ["grids"] = grids,
            ["priority"] = ship.Priority,
        };
        return obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    // True when the grid's accepted set is exactly the standard sizes up to its cap, so the patch can use
    // a bare cap and omit the accepts array (matching cargo_ships.json's convention).
    private static bool DerivedFromCap(GridDef g)
    {
        var derived = BoxType.SizesDesc.Where(s => s <= g.MaxContainerScu).OrderBy(s => s).ToList();
        return g.AcceptedCaps.OrderBy(a => a).SequenceEqual(derived);
    }
}
