using System.Linq;

namespace NexusApp.Services;

// One priced leg pair for the route planner: buy this commodity at BuyRow's terminal, haul it,
// sell at SellRow's terminal. Net == Gross in v1 - no fee schedule exists yet (spec: "fee
// provider is zero in v1"); TradeRoute carries only the one number so the UI never has to
// reconcile a fee split that does not exist yet. TripParts narrates the same TripQty this route
// was built from, so the headline number and the expanded-band explanation can never drift apart.
public sealed record TradeRoute(TradePriceRow BuyRow, TradePriceRow SellRow, int TripQty, double Gross,
    double Net, ProximityTier Tier, string[] TripParts);

// Demand-at-destination coverage filter for the route planner (task 5; resemantic task 10). Any =
// no filter (default, the planner's original behavior, byte-preserved). CoversTrip requires the
// SELL leg to carry at least one full trip's worth of demand; CoversTwoTrips requires two trips'
// worth, so the shown route can be run back-to-back without a fresh scan in between. DEMAND ONLY:
// the buy leg's stock is never independently checked here - TradeMath.TripQty already caps
// tripQty at buyStockScu, so the buy side is self-limiting by construction (enum member names kept
// from task 5 to avoid an unrelated cascade; the UI-facing pill labels are what actually renamed).
public enum StockFilter { Any, CoversTrip, CoversTwoTrips }

// Route planner ranking mode (task 7). Profit (default) orders by raw Net descending, byte-
// identical to the planner's original behavior. ProfitPerScu re-orders by Net/TripQty descending
// (ties broken by Net descending), surfacing high-margin small-qty routes that a raw-net sort
// buries under high-net bulk hauls; a zero-TripQty route (nobody can actually run it) sorts to
// the bottom under either mode, since 0 is the lowest per-SCU value any route can have.
public enum RankMode { Profit, ProfitPerScu }

// Pairs a Buy>0 row with a Sell>0 row of the same commodity, at two DIFFERENT terminals (a
// same-terminal pair is not a haul), ranks by net profit per trip. Anchor mode (FROM HERE vs
// ANYWHERE) only restricts which terminals the BUY leg may come from; the sell leg and the
// ranking math are identical either way (spec: "Same math either way; FROM HERE only restricts
// the buy-terminal set"). originTerminalIds is null ONLY for ANYWHERE (every terminal's buy legs
// are eligible); FROM HERE always passes a non-null set, EMPTY when the origin could not be
// resolved to any terminal. An empty set restricts the buy leg to NOTHING, not to "unrestricted" -
// treating an unresolved origin as ANYWHERE would silently show cross-map routes while FROM HERE
// stays lit (spec Decision 6: "every listed route is purchasable where the player stands").
//
// destTerminalIds (task 6) is the exact same contract, mirrored onto the SELL leg: null = ANY (no
// constraint, the planner's original behavior, default parameter so every pre-existing call site
// is untouched), non-null restricts sell legs to that set, non-null EMPTY means the DESTINATION
// picker's name could not be resolved to a terminal and must yield zero sell legs rather than
// silently falling back to ANY - same reasoning as the origin's empty-set case above.
//
// rankMode (task 7) only changes the final ordering, never which pairs qualify as routes - it is
// applied after every filter above (scope, box fit, stock coverage, origin/destination) has
// already run, same as the pre-existing Net-descending sort it replaces for ProfitPerScu. Default
// parameter (Profit) so every pre-existing call site's ordering is untouched.
public static class RoutePlanner
{
    public static IReadOnlyList<TradeRoute> Rank(IReadOnlyList<TradePriceRow> rows,
        IReadOnlyDictionary<int, MarketTerminal> terminals, int shipScu, int shipMaxBox, double? budget,
        IReadOnlySet<int>? originTerminalIds, string scope, int take, StockFilter stockFilter = StockFilter.Any,
        IReadOnlySet<int>? destTerminalIds = null, RankMode rankMode = RankMode.Profit)
    {
        var result = new List<TradeRoute>();
        if (rows is null || rows.Count == 0 || take <= 0) return result;

        var byCommodity = new Dictionary<int, (List<TradePriceRow> Buys, List<TradePriceRow> Sells)>();
        foreach (var row in rows)
        {
            if (!terminals.TryGetValue(row.TerminalId, out var terminal)) continue;   // unresolvable: no tier, no pairing
            if (!InScope(terminal, scope)) continue;

            if (!byCommodity.TryGetValue(row.CommodityId, out var lists))
            {
                lists = (new List<TradePriceRow>(), new List<TradePriceRow>());
                byCommodity[row.CommodityId] = lists;
            }
            // null = ANYWHERE (unrestricted). Non-null EMPTY = FROM HERE with an unresolved
            // origin - Contains() on an empty set is always false, so no terminal qualifies as a
            // buy leg rather than silently falling back to every terminal's.
            if (row.Buy > 0 && (originTerminalIds is null || originTerminalIds.Contains(row.TerminalId)))
                lists.Buys.Add(row);
            // Same rule, sell leg: null = ANY destination, non-null EMPTY = an unresolved
            // DESTINATION pick that must exclude every sell leg, not fall back to ANY.
            if (row.Sell > 0 && (destTerminalIds is null || destTerminalIds.Contains(row.TerminalId)))
                lists.Sells.Add(row);
        }

        foreach (var (buys, sells) in byCommodity.Values)
        {
            foreach (var buyRow in buys)
            {
                if (!TradeMath.BoxFits(buyRow.ContainerSizes, shipMaxBox)) continue;
                var buyTerminal = terminals[buyRow.TerminalId];

                foreach (var sellRow in sells)
                {
                    if (sellRow.TerminalId == buyRow.TerminalId) continue;   // not a haul
                    if (!TradeMath.BoxFits(sellRow.ContainerSizes, shipMaxBox)) continue;

                    var sellTerminal = terminals[sellRow.TerminalId];
                    var tripQty = TradeMath.TripQty(shipScu, buyRow.BuyStockScu, budget, buyRow.Buy);
                    if (!PassesStockFilter(stockFilter, tripQty, sellRow.SellDemandScu)) continue;
                    var gross = tripQty * (sellRow.Sell - buyRow.Buy);
                    result.Add(new TradeRoute(buyRow, sellRow, tripQty, gross, gross,
                        ProximityTiers.Derive(buyTerminal, sellTerminal),
                        TradeMath.TripParts(shipScu, buyRow.BuyStockScu, budget, buyRow.Buy)));
                }
            }
        }

        return (rankMode == RankMode.ProfitPerScu
                ? result.OrderByDescending(r => r.TripQty > 0 ? r.Net / r.TripQty : 0).ThenByDescending(r => r.Net)
                : result.OrderByDescending(r => r.Net))
            .Take(take).ToList();
    }

    // Applied per pair, inside the pairing loop, before the take cutoff - a route that fails
    // coverage is skipped outright rather than merely ranked lower and then trimmed away by
    // Take(). tripQty == 0 never passes CoversTrip/CoversTwoTrips: a route nobody can actually run
    // once (ship capacity, stock, or budget already zeroed the trip) can't be said to "cover"
    // anything, so it only ever surfaces under Any.
    //
    // DEMAND ONLY (task 10 resemantic): buyStockScu is deliberately NOT checked here anymore.
    // tripQty is derived from buyStockScu via TradeMath.TripQty (tripQty = min(shipScu,
    // buyStockScu, ...)), so tripQty can never exceed buyStockScu - the buy side is already
    // self-limiting. That made the old CoversTrip check (buyStockScu >= tripQty) a tautology, but
    // the old CoversTwoTrips check (buyStockScu >= 2*tripQty) was NOT: whenever stock was the
    // binding constraint (the common case), tripQty == buyStockScu, so buyStockScu >= 2*tripQty
    // reduced to buyStockScu >= 2*buyStockScu - only ever true for buyStockScu <= 0. CoversTwoTrips
    // was therefore nearly unsatisfiable for any low-stock route under the old code; this is now a
    // straight demand comparison instead.
    private static bool PassesStockFilter(StockFilter filter, int tripQty, int sellDemandScu) =>
        filter switch
        {
            StockFilter.CoversTrip => tripQty > 0 && sellDemandScu >= tripQty,
            StockFilter.CoversTwoTrips => tripQty > 0 && sellDemandScu >= 2 * tripQty,
            _ => true,
        };

    // A3 (the owner live-use finding): the scope pill and the START/DESTINATION pickers are independent
    // controls that can be set to two different systems, and nothing stops that. Scope STANTON with
    // a Pyro destination filters every sell leg away, so the planner returns nothing and falls
    // through to "No routes match the current scope and budget" - indistinguishable from a genuine
    // dry spell. The user then goes off changing ship, budget and demand filter trying to fix a
    // contradiction none of those controls can express.
    //
    // Returns the system the chosen terminals actually sit in when NOT ONE of them is inside the
    // active scope, else null. Callers use a non-null result to name the conflict instead of
    // reporting an absence of routes.
    //
    // Null for a null or empty set on purpose: "no constraint" (ANY) and "the picker's name could
    // not be resolved" are different states that already own their own messages, and neither is a
    // scope conflict. Also null when every chosen terminal has no recorded system - InScope treats
    // those as out of scope, but there is no system to name, so the generic message is the honest
    // one. Pure so the planner's empty-state ladder is testable without a WPF tree.
    public static string? ChosenSystemOutsideScope(IReadOnlySet<int>? terminalIds,
        IReadOnlyDictionary<int, MarketTerminal> terminals, string scope)
    {
        if (terminalIds is null || terminalIds.Count == 0) return null;
        if (string.IsNullOrEmpty(scope) || string.Equals(scope, "ALL", StringComparison.OrdinalIgnoreCase))
            return null;

        string? outside = null;
        foreach (var id in terminalIds)
        {
            if (!terminals.TryGetValue(id, out var terminal)) continue;
            if (InScope(terminal, scope)) return null;   // one reachable choice is enough: no conflict
            if (outside is null && !string.IsNullOrEmpty(terminal.System)) outside = terminal.System;
        }
        return outside;
    }

    // "ALL" (case-insensitive) passes every terminal; anything else must match the terminal's
    // star system by name. A terminal with no recorded system is excluded once a specific scope
    // is chosen (an unknown system can never honestly be said to match one).
    private static bool InScope(MarketTerminal terminal, string scope)
    {
        if (string.IsNullOrEmpty(scope) || string.Equals(scope, "ALL", StringComparison.OrdinalIgnoreCase))
            return true;
        return !string.IsNullOrEmpty(terminal.System)
            && string.Equals(terminal.System, scope, StringComparison.OrdinalIgnoreCase);
    }

    // The stale-pin rule (task-8, MAP tab route pinning): a pinned TradeRoute is identified by
    // WHICH haul it names (buy terminal, sell terminal, commodity), not by object identity or its
    // priced fields - Rank rebuilds a brand new TradeRoute instance, with a brand new TripParts
    // array, on every call, even when nothing about the underlying haul actually changed. Pure and
    // unit tested directly (task-8 brief): TradePage.RebuildPlanner calls this once per rebuild to
    // decide whether a session-pinned route survives a fresh snapshot, and the row's own PIN chip
    // reuses it (fresh = the single row's route) to decide its own active/inactive paint - one
    // rule, one place, instead of two copies of the same three-field comparison drifting apart.
    internal static bool PinSurvivesRefresh(TradeRoute pinned, IReadOnlyList<TradeRoute> fresh) =>
        fresh.Any(r => r.BuyRow.TerminalId == pinned.BuyRow.TerminalId
            && r.SellRow.TerminalId == pinned.SellRow.TerminalId
            && r.BuyRow.CommodityId == pinned.BuyRow.CommodityId);
}
