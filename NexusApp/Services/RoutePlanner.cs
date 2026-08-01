using System.Linq;
using NexusApp.Models;

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
// ProfitPerGm (app review 2026-08-01) ranks by profit per Gm of straight-line travel. The planner
// has always COMPUTED and DISPLAYED that distance as a dim decoration on every row while ranking
// purely on money: from the shipped positions, Area 18 to ARC-L1 is 2.89 Gm and Area 18 to ARC-L3 is
// 56.96 Gm, so a route paying 3% more at ARC-L3 outranked the near one and a hauler lost the
// difference twenty times over. Deliberately a measured-distance RATIO and not credits-per-hour or
// an ETA: turning it into time would need a speed model the app does not have, and would contradict
// the ProximityTiers spec line "no invented ETAs".
public enum RankMode { Profit, ProfitPerScu, ProfitPerGm }

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
        IReadOnlySet<int>? destTerminalIds = null, RankMode rankMode = RankMode.Profit,
        // Distance source for RankMode.ProfitPerGm, injected as a delegate rather than a catalog so
        // this stays a pure ranking layer with no geometry dependency and tests can feed synthetic
        // distances. Null under any other mode, and a null-returning call is a first-class "cannot
        // measure" (cross-system, unresolved terminal) rather than a failure.
        Func<MarketTerminal?, MarketTerminal?, double?>? distanceMeters = null)
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

        return rankMode switch
        {
            RankMode.ProfitPerScu =>
                result.OrderByDescending(r => r.TripQty > 0 ? r.Net / r.TripQty : 0)
                      .ThenByDescending(r => r.Net).Take(take).ToList(),

            // Rows whose distance cannot be measured (cross-system pairs, unresolved terminals) sort
            // LAST and are never dropped - the same absence convention the per-row distance tag
            // already follows. Dropping them would silently hide every cross-system route the moment
            // this mode is picked, which is the opposite of what a hauler comparing runs wants.
            RankMode.ProfitPerGm when distanceMeters is not null =>
                result.Select(r => (Route: r, Gm: RouteGm(r, terminals, distanceMeters)))
                      .OrderBy(x => x.Gm.HasValue ? 0 : 1)
                      .ThenByDescending(x => x.Gm switch
                      {
                          // Two terminals that resolve to the SAME map object (a station's admin
                          // office and its cargo deck, say) are a genuinely free move, so they are
                          // the best possible ratio rather than a divide-by-zero.
                          null => 0,
                          0 => double.PositiveInfinity,
                          var gm => x.Route.Net / gm,
                      })
                      .ThenByDescending(x => x.Route.Net)
                      .Select(x => x.Route).Take(take).ToList(),

            // ProfitPerGm with no distance source available falls back to plain profit rather than
            // returning an arbitrary order. The UI only offers the pill when a catalog is wired, so
            // this is a guard, not a user-reachable state.
            _ => result.OrderByDescending(r => r.Net).Take(take).ToList(),
        };
    }

    private static double? RouteGm(TradeRoute r, IReadOnlyDictionary<int, MarketTerminal> terminals,
                                   Func<MarketTerminal?, MarketTerminal?, double?> distanceMeters)
    {
        terminals.TryGetValue(r.BuyRow.TerminalId, out var buy);
        terminals.TryGetValue(r.SellRow.TerminalId, out var sell);
        return distanceMeters(buy, sell) is { } meters ? meters / 1_000_000_000.0 : null;
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
        fresh.Any(r => SameHaul(r, pinned));

    /// <summary>The one triple rule every pin decision below is built from: two routes name the
    /// same haul when they share a buy terminal, a sell terminal and a commodity. Prices, trip
    /// quantity and tier are all free to differ - a fresh snapshot reprices the same haul.</summary>
    internal static bool SameHaul(TradeRoute a, TradeRoute b) =>
        a.BuyRow.TerminalId == b.BuyRow.TerminalId
        && a.SellRow.TerminalId == b.SellRow.TerminalId
        && a.BuyRow.CommodityId == b.BuyRow.CommodityId;

    /// <summary>How many routes may be pinned at once (the owner's call, 2026-08-01, when pinning went
    /// from one route to several). Chosen against the overlay: five Manifest Strip cards fit the
    /// 320x480 panel without scrolling, and nobody flies more than five runs in a session.</summary>
    internal const int MaxPins = 5;

    /// <summary>True when a persisted pin and a live route name the same haul.</summary>
    internal static bool SameHaul(PinnedRoute pin, TradeRoute r) =>
        pin.BuyTerminalId == r.BuyRow.TerminalId
        && pin.SellTerminalId == r.SellRow.TerminalId
        && pin.CommodityId == r.BuyRow.CommodityId;

    /// <summary>Captures a live route as a persistable pin. Display facts only - see
    /// PinnedRoute for why no price is among them.</summary>
    internal static PinnedRoute ToPin(TradeRoute r, DateTime nowUtc) => new()
    {
        BuyTerminalId = r.BuyRow.TerminalId,
        SellTerminalId = r.SellRow.TerminalId,
        CommodityId = r.BuyRow.CommodityId,
        CommodityName = r.BuyRow.CommodityName,
        BuyTerminalName = r.BuyRow.TerminalName,
        SellTerminalName = r.SellRow.TerminalName,
        TripQty = r.TripQty,
        PerScuMargin = r.SellRow.Sell - r.BuyRow.Buy,
        UpdatedUtc = nowUtc,
        PinnedUtc = nowUtc,
    };

    /// <summary>Pure pin toggle. Pinning a haul that is already pinned UNPINS it (the chip stays a
    /// toggle, exactly as it behaved when only one pin existed); otherwise the route is appended,
    /// and once the list is at <paramref name="cap"/> the OLDEST pin is dropped to make room.
    /// Dropping the oldest rather than refusing the new pin keeps the chip's promise: a click on
    /// PIN always pins. Returns a new list; never mutates the one passed in.</summary>
    internal static IReadOnlyList<PinnedRoute> TogglePin(
        IReadOnlyList<PinnedRoute> pinned, TradeRoute route, DateTime nowUtc, int cap = MaxPins)
    {
        var kept = pinned.Where(p => !SameHaul(p, route)).ToList();
        if (kept.Count != pinned.Count) return kept;   // it was pinned: this click unpinned it

        kept.Add(ToPin(route, nowUtc));
        while (kept.Count > cap) kept.RemoveAt(0);
        return kept;
    }

    /// <summary>
    /// Refreshes every pin whose haul appears in a fresh ranking, and LEAVES THE REST ALONE.
    ///
    /// <para>This replaced a stale-pin rule that DROPPED any pin missing from the ranking. That was
    /// right while pins lasted only as long as the session that made them, and wrong the moment they
    /// began surviving a restart: a ranking is the best 25 routes for the ship, budget and scope
    /// selected right now, so falling out of one means "not currently top-25", not "no longer
    /// exists". Under the old rule, changing ship and reopening the planner would have silently
    /// erased the user's pins - indistinguishable from a bug, and exactly what persistence was asked
    /// for to prevent.</para>
    ///
    /// <para>Returns a new list in the original pin order; never mutates the inputs.</para>
    /// </summary>
    internal static IReadOnlyList<PinnedRoute> RefreshPins(
        IReadOnlyList<PinnedRoute> pinned, IReadOnlyList<TradeRoute> fresh, DateTime nowUtc)
    {
        var result = new List<PinnedRoute>(pinned.Count);
        foreach (var pin in pinned)
        {
            var live = fresh.FirstOrDefault(r => SameHaul(pin, r));
            if (live is null) { result.Add(pin); continue; }

            result.Add(new PinnedRoute
            {
                BuyTerminalId = pin.BuyTerminalId,
                SellTerminalId = pin.SellTerminalId,
                CommodityId = pin.CommodityId,
                // Names come from the FRESH row: a terminal or commodity rename upstream should
                // reach a pinned card rather than leave it quoting a name that no longer exists.
                CommodityName = live.BuyRow.CommodityName,
                BuyTerminalName = live.BuyRow.TerminalName,
                SellTerminalName = live.SellRow.TerminalName,
                TripQty = live.TripQty,
                PerScuMargin = live.SellRow.Sell - live.BuyRow.Buy,
                UpdatedUtc = nowUtc,
                PinnedUtc = pin.PinnedUtc,   // never moves: it answers "how long have I meant to run this"
            });
        }
        return result;
    }
}
