using NexusApp.Models.Cargo;

namespace NexusApp.Services.Cargo;

// How one ship handles the current manifest. ShapeFail means some box's footprint fits no grid on
// any axis, so a plain "SCU fits" would be a false green; the UI shows that honestly.
public sealed class ShipFitResult
{
    public ShipCargoDef Ship { get; init; } = null!;
    public int Trips { get; init; }
    public bool ShapeFail { get; init; }
    public double BestTripUtilization { get; init; }   // fullest single trip, 0..1 of ship capacity

    public bool FitsInOneTrip => Trips == 1 && !ShapeFail;
}

// Rank every embedded ship against a manifest by running the real footprint pack, not by comparing
// totals, so the smallest ship that actually does it in one trip floats to the top. Runs on demand
// and fully offline: 93 ships x a few hundred boxes x a sub-millisecond pack is milliseconds.
public static class ShipFitSearch
{
    public static List<ShipFitResult> Rank(IReadOnlyList<ShipCargoDef> ships, IReadOnlyList<ManifestItem> manifest)
    {
        var results = new List<ShipFitResult>(ships.Count);
        foreach (var ship in ships)
        {
            var boxes = CargoPacker.BuildBoxes(manifest, ship.MaxContainerScu);
            var plan = MultiTripPlanner.Plan(ship.Grids, boxes);
            double util = plan.Trips.Count == 0 || ship.TotalScu == 0
                ? 0
                : plan.Trips.Max(t => (double)t.PlacedScu / ship.TotalScu);
            results.Add(new ShipFitResult
            {
                Ship = ship,
                Trips = plan.TripCount,
                ShapeFail = plan.Unplaceable.Count > 0,
                BestTripUtilization = util,
            });
        }

        // Shape failures last; then fewest trips; then the smallest ship (a hauler does not want to
        // fly a Hull C to move 40 SCU); then tighter utilization; then name for a stable order.
        return results
            .OrderBy(r => r.ShapeFail)
            .ThenBy(r => r.Trips == 0 ? int.MaxValue : r.Trips)
            .ThenBy(r => r.Ship.TotalScu)
            .ThenByDescending(r => r.BestTripUtilization)
            .ThenBy(r => r.Ship.DisplayName, StringComparer.Ordinal)
            .ToList();
    }
}
