using NexusApp.Models.Cargo;

namespace NexusApp.Services.Cargo;

// A full plan across one or more trips, plus any boxes no trip of this ship can ever hold (a box
// whose footprint does not fit any grid on any axis). Deterministic for a given (ship, manifest).
public sealed class CargoPlan
{
    public List<PackResult> Trips { get; } = new();
    public List<PackBox> Unplaceable { get; } = new();

    public int TripCount => Trips.Count;
    public bool FitsInOneTrip => Trips.Count == 1 && Unplaceable.Count == 0;
    public int TotalPlacedScu => Trips.Sum(t => t.PlacedScu);
}

// When cargo exceeds a ship, packing is not an error: boxes that do not fit trip one pack into
// trip two, and so on, by real box-level replay (never naive ceil(scu / capacity), which lies when
// the long boxes leave unusable gaps). This planner is also what the ship-fit search calls per ship.
public static class MultiTripPlanner
{
    public static CargoPlan Plan(IReadOnlyList<GridDef> grids, IReadOnlyList<PackBox> boxes,
                                 bool requireSupport = true, int maxTrips = 100)
    {
        var plan = new CargoPlan();
        var remaining = boxes.ToList();

        while (remaining.Count > 0 && plan.Trips.Count < maxTrips)
        {
            var pack = CargoPacker.AutoPack(grids, remaining, requireSupport);
            if (pack.Placed.Count == 0)
            {
                // Nothing in the remaining set fits any grid on this ship: they never will.
                plan.Unplaceable.AddRange(pack.Deferred);
                break;
            }
            plan.Trips.Add(pack);
            remaining = pack.Deferred.ToList();
        }

        // Hit the trip ceiling with cargo still left (pathological input): surface it, do not loop.
        if (plan.Trips.Count >= maxTrips && remaining.Count > 0)
            plan.Unplaceable.AddRange(remaining);

        return plan;
    }
}
