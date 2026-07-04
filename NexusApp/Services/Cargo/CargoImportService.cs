using NexusApp.Models;
using NexusApp.Models.Cargo;

namespace NexusApp.Services.Cargo;

// Turns the active in-game hauls into cargo-planner manifest lines. Commodity and SCU come from the
// haul's dropoff legs (Game.log); the container cap comes from the contract OCR if it was read, else
// unknown and resolved to the default at pack time. Reads only commodity / SCU / cap, never identity.
public static class CargoImportService
{
    public static List<ManifestItem> FromActiveHauls(IReadOnlyList<Haul> hauls)
    {
        // Merge same commodity + same cap across all hauls into one line (the cap changes the box
        // split, so different caps stay separate). Preserve first-seen order for a stable list.
        var order = new List<(string Commodity, int? Cap)>();
        var merged = new Dictionary<(string, int?), ManifestItem>();

        foreach (var h in hauls)
        {
            int? cap = h.ContainerCap;
            var capSource = cap.HasValue ? CapSource.Ocr : CapSource.Default;

            foreach (var (commodity, scu) in SumDropoffScuByCommodity(h))
            {
                var key = (commodity.ToLowerInvariant(), cap);
                if (merged.TryGetValue(key, out var existing))
                {
                    existing.Scu += scu;
                }
                else
                {
                    order.Add((commodity, cap));
                    merged[key] = new ManifestItem
                    {
                        Label = commodity,
                        Scu = scu,
                        Cap = cap,
                        CapSource = capSource,
                        Source = ManifestSource.Imported,
                        GameLogMissionId = h.MissionId,
                    };
                }
            }
        }

        var items = order.Select(k => merged[(k.Commodity.ToLowerInvariant(), k.Cap)]).ToList();
        for (int i = 0; i < items.Count; i++) items[i].ColorId = i;
        return items;
    }

    private static IEnumerable<(string Commodity, int Scu)> SumDropoffScuByCommodity(Haul h)
    {
        var byCommodity = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var seen = new List<string>();
        foreach (var leg in h.Legs)
        {
            if (leg.Role != HaulRole.Dropoff) continue;
            var commodity = leg.Commodity?.Trim() ?? "";
            if (commodity.Length == 0 || leg.TargetScu <= 0) continue;
            if (!byCommodity.ContainsKey(commodity)) seen.Add(commodity);
            byCommodity.TryGetValue(commodity, out var cur);
            byCommodity[commodity] = cur + leg.TargetScu;
        }
        foreach (var commodity in seen)
            yield return (commodity, byCommodity[commodity]);
    }
}
