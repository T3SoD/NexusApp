using NexusApp.Services;

namespace NexusApp.Views;

// Planner-side auto-load fold: dock membership decides the chip, the calibrated flag decides
// whether the min-max range renders at all. Pure so the gate is testable without WPF.
internal static class AutoLoadPlannerParts
{
    internal sealed record Parts(bool ShowDockChip, string? RangeText);

    internal static Parts For(MarketTerminal? buyTerm, string containerSizes, int tripQty,
                              StarmapCatalog starmap, LoadingDockCatalog docks, AutoLoadTimeTable times)
    {
        var id = starmap.StarmapId(buyTerm);
        if (!docks.Contains(id)) return new(false, null);
        if (!times.Calibrated) return new(true, null);
        return new(true, AutoLoadEstimator.FormatRange(times, tripQty, containerSizes));
    }
}
