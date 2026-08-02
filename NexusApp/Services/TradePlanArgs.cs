using System.Collections.Generic;
using NexusApp.Views;

namespace NexusApp.Services;

// The setting-interpretation seams shared by the main Trade planner and the overlay planner
// (overlay planner spec, 2026-08-02): every place that turns persisted AppSettings values into
// RoutePlanner.Rank arguments goes through here, so the two surfaces cannot drift. Moved from
// TradePage.Planner.cs verbatim; behavior unchanged.
internal static class TradePlanArgs
{
    // Any stored value that isn't a recognized label (corrupt settings.json, a future rollback)
    // falls back to Profit - the planner's original ordering, same fail-open idiom as
    // ParseDemandFilter.
    internal static RankMode ParseRankMode(string? value) => value switch
    {
        "PROFIT PER SCU" => RankMode.ProfitPerScu,
        "PROFIT PER GM" => RankMode.ProfitPerGm,
        _ => RankMode.Profit,
    };

    // Fail-open: an unrecognized persisted value (corrupt settings.json, a future rollback) falls
    // back to Any. Also accepts the pre-task-10 "COVERS TRIP"/"COVERS 2X" strings, so a
    // settings.json written before this rename still resolves to the same tier instead of silently
    // resetting to Any.
    internal static StockFilter ParseDemandFilter(string? value) => value switch
    {
        "MIN" or "COVERS TRIP" => StockFilter.CoversTrip,
        "2X" or "COVERS 2X" => StockFilter.CoversTwoTrips,
        _ => StockFilter.Any,
    };

    // Sell-leg constraint from a destination terminal NAME ("ANY"/null/"" = unconstrained).
    // A name that does not resolve yields an EMPTY set rather than null: an unresolved
    // constraint restricts to nothing, it never silently widens back out (the same contract
    // the origin set keeps).
    internal static IReadOnlySet<int>? DestTerminalIds(string? destName, IReadOnlyList<MarketTerminal> terminals)
    {
        if (string.IsNullOrEmpty(destName) || destName == "ANY") return null;
        return TradeOriginResolver.TerminalIdForName(destName, terminals) is { } id
            ? new HashSet<int> { id }
            : new HashSet<int>();
    }
}
