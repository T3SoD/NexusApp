using System.Globalization;
using System.Linq;
using NexusApp.Models;

namespace NexusApp.Services;

// One priced leg pair for the route planner: buy this commodity at BuyRow's terminal, haul it,
// sell at SellRow's terminal. Net == Gross in v1 - no fee schedule exists yet (spec: "fee
// provider is zero in v1"); TradeRoute carries only the one number so the UI never has to
// reconcile a fee split that does not exist yet. TripQty is what can actually be BOUGHT: capacity,
// stock and budget first, then snapped down to the fullest load the buy terminal's container menu
// can supply (issue #31). PlannedQty is that same figure BEFORE the snap, carried so the card can
// name the gap ("6 SCU short of the 46 planned") beside a profit that no longer pretends the gap
// was hauled. Rank is the only production constructor and always sets it; the 0 default exists
// only to keep the parameter optional for callers that do not compute one, which in practice means
// direct test constructions (the pin-rename test among them) that predate PlannedQty and have no
// use for it - 0 is not itself a meaningful trip quantity. TripParts narrates the same TripQty this
// route was built from, so the headline number and the expanded-band explanation can never drift
// apart.
public sealed record TradeRoute(TradePriceRow BuyRow, TradePriceRow SellRow, int TripQty, double Gross,
    double Net, ProximityTier Tier, string[] TripParts, int PlannedQty = 0);

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
public enum RankMode { Profit, ProfitPerScu, ProfitPerGm, Roi }

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
        Func<MarketTerminal?, MarketTerminal?, double?>? distanceMeters = null,
        // Commodity filter (issue #41, planner half): null = ANY (no constraint, the planner's
        // original behavior, default parameter so every pre-existing call site is untouched); a
        // non-null name keeps only rows whose CommodityName matches it case-insensitively. A name
        // matching nothing yields zero routes rather than silently widening back to ANY - the same
        // no-silent-fallback contract the origin/destination sets above already keep.
        string? commodityName = null)
    {
        var result = new List<TradeRoute>();
        if (rows is null || rows.Count == 0 || take <= 0) return result;

        var byCommodity = new Dictionary<int, (List<TradePriceRow> Buys, List<TradePriceRow> Sells)>();
        foreach (var row in rows)
        {
            if (!terminals.TryGetValue(row.TerminalId, out var terminal)) continue;   // unresolvable: no tier, no pairing
            if (!InScope(terminal, scope)) continue;
            if (commodityName is not null
                && !string.Equals(row.CommodityName, commodityName, StringComparison.OrdinalIgnoreCase)) continue;

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

        // Memo for the container snap below. The snapped quantity depends on the BUY ROW alone
        // (this hull, this row's stock, this budget, this row's price, this row's menu) and never on
        // the sell leg, so it is computed once per buy row rather than once per pair. Keying on the
        // menu string and the pre-snap quantity collapses that further: a whole snapshot holds only
        // a few dozen distinct menus, and every row a full hold can fill shares one quantity.
        // ContainerPlanner.BuyableScu also takes shipMaxBox as a third input, deliberately left out
        // of this key: shipMaxBox is a Rank parameter, never reassigned once this call starts, and
        // this dictionary is local to the call, so a single Rank invocation can only ever see one
        // value of it. A static or cross-call cache would not have that guarantee and would need
        // shipMaxBox folded back into the key.
        var buyable = new Dictionary<(string Sizes, int Planned), int>();

        foreach (var (buys, sells) in byCommodity.Values)
        {
            // The per-buy-row work below (a TripQty computation, a full ContainerPlanner DP) is
            // only worth paying for when there is something to pair it against. A picked
            // DESTINATION routinely empties this commodity's sell list entirely; before this work
            // was hoisted up from the inner sells loop, an empty sells list simply meant that loop
            // did nothing, so this guard restores the same no-op for every buy row in that case
            // instead of paying for the DP on each one.
            if (sells.Count == 0) continue;   // no eligible sell leg: nothing this buy row can pair with

            foreach (var buyRow in buys)
            {
                if (!TradeMath.BoxFits(buyRow.ContainerSizes, shipMaxBox)) continue;
                var buyTerminal = terminals[buyRow.TerminalId];

                // What capacity, stock and budget allow, before the kiosk gets a say.
                var plannedQty = TradeMath.TripQty(shipScu, buyRow.BuyStockScu, budget, buyRow.Buy);

                // What can actually be carried out of this kiosk. A terminal selling only 8s and
                // bigger cannot fill a 46 SCU hull, and pricing the missing 6 was ranking routes on
                // cargo nobody can buy (issue #31).
                var key = (buyRow.ContainerSizes, plannedQty);
                if (!buyable.TryGetValue(key, out var tripQty))
                {
                    tripQty = ContainerPlanner.BuyableScu(buyRow.ContainerSizes, shipMaxBox, plannedQty);
                    buyable[key] = tripQty;
                }

                var tripParts = TradeMath.TripParts(shipScu, buyRow.BuyStockScu, budget, buyRow.Buy);
                // Appended here rather than inside TradeMath.TripParts: that function is pure
                // capacity math with no container inputs, and growing its signature for one caller
                // would be churn. Only said when the menu actually bit, so the narration never
                // lists a limit that did not apply.
                if (tripQty < plannedQty)
                    tripParts = [.. tripParts, $"containers reach {tripQty.ToString("N0", CultureInfo.InvariantCulture)}"];

                foreach (var sellRow in sells)
                {
                    if (sellRow.TerminalId == buyRow.TerminalId) continue;   // not a haul
                    if (!TradeMath.BoxFits(sellRow.ContainerSizes, shipMaxBox)) continue;

                    var sellTerminal = terminals[sellRow.TerminalId];
                    if (!PassesStockFilter(stockFilter, tripQty, sellRow.SellDemandScu)) continue;
                    var gross = tripQty * (sellRow.Sell - buyRow.Buy);
                    result.Add(new TradeRoute(buyRow, sellRow, tripQty, gross, gross,
                        ProximityTiers.Derive(buyTerminal, sellTerminal), tripParts, plannedQty));
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

            // Capital efficiency (owner, 2026-08-04): net over the buy-side capital the trip ties
            // up, the same derivation the card rail's TradeFinancials fold renders, so the sorted-by
            // figure is the RETURN ON INVESTMENT every card already shows. A route with no capital
            // tied up sorts LAST, the rail's own silence convention: an unpriceable ratio is
            // absence, not a bargain. Ties break on raw net, matching every other ratio mode.
            RankMode.Roi =>
                result.OrderByDescending(r => r.BuyRow.Buy > 0 && r.TripQty > 0
                          ? r.Net / (r.BuyRow.Buy * r.TripQty)
                          : double.NegativeInfinity)
                      .ThenByDescending(r => r.Net).Take(take).ToList(),

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
    // tripQty is derived from buyStockScu via TradeMath.TripQty (min(shipScu, buyStockScu, ...))
    // and then snapped DOWN to what the buy terminal's containers can supply, so tripQty can never
    // exceed buyStockScu - the buy side is already self-limiting, and the snap only tightens it.
    // That made the old CoversTrip check (buyStockScu >= tripQty) a tautology, but
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

    // A3 (owner live-use finding): the scope pill and the START/DESTINATION pickers are independent
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

    /// <summary>How many routes may be pinned at once (owner's call, 2026-08-01, when pinning went
    /// from one route to several). Chosen against the overlay: five Manifest Strip cards fit the
    /// 320x480 panel without scrolling, and nobody flies more than five runs in a session.</summary>
    internal const int MaxPins = 5;

    /// <summary>True when a persisted pin and a live route name the same haul. A sell-only pin
    /// (null buy terminal) can never match a planner route - the int? comparison is false for
    /// null - which is load-bearing: RefreshPins leaves sell pins untouched for free.</summary>
    internal static bool SameHaul(PinnedRoute pin, TradeRoute r) =>
        pin.BuyTerminalId == r.BuyRow.TerminalId
        && pin.SellTerminalId == r.SellRow.TerminalId
        && pin.CommodityId == r.BuyRow.CommodityId;

    /// <summary>The sell-only counterpart: a pin names this Sell-tab buyer when it has NO buy leg
    /// and shares the buyer's terminal and commodity. A planner pin that happens to sell the same
    /// commodity at the same terminal is a DIFFERENT pin - it carries a buy leg this row says
    /// nothing about - so the two coexist rather than toggling each other.</summary>
    internal static bool SameSellHaul(PinnedRoute pin, int sellTerminalId, int commodityId) =>
        pin.BuyTerminalId is null
        && pin.SellTerminalId == sellTerminalId
        && pin.CommodityId == commodityId;

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

    /// <summary>Captures a Sell-tab buyer as a sell-only pin (owner, 2026-08-01: "add a pin to
    /// overlay button for the results in the sell tab"). No buy leg: the identity is (null,
    /// terminal, commodity), PerScuMargin holds the SELL PRICE, and TripQty is the quantity the
    /// user had typed - their own cargo, not a computed trip.</summary>
    internal static PinnedRoute ToSellPin(TradePriceRow row, int qty, DateTime nowUtc) => new()
    {
        BuyTerminalId = null,
        SellTerminalId = row.TerminalId,
        CommodityId = row.CommodityId,
        CommodityName = row.CommodityName,
        BuyTerminalName = "",
        SellTerminalName = row.TerminalName,
        TripQty = qty,
        PerScuMargin = row.Sell,
        UpdatedUtc = nowUtc,
        PinnedUtc = nowUtc,
    };

    /// <summary>TogglePin's sell-only twin: same toggle-off rule, same append, same shared cap -
    /// planner pins and sell pins live in ONE list because the overlay panel they feed is one
    /// surface with one five-card budget.</summary>
    internal static IReadOnlyList<PinnedRoute> ToggleSellPin(
        IReadOnlyList<PinnedRoute> pinned, TradePriceRow row, int qty, DateTime nowUtc, int cap = MaxPins)
    {
        var kept = pinned.Where(p => !SameSellHaul(p, row.TerminalId, row.CommodityId)).ToList();
        if (kept.Count != pinned.Count) return kept;   // it was pinned: this click unpinned it

        kept.Add(ToSellPin(row, qty, nowUtc));
        while (kept.Count > cap) kept.RemoveAt(0);
        return kept;
    }

    /// <summary>RefreshPins' sell-only twin, fed by a Sell-tab ranking of ONE commodity: any
    /// sell-only pin matching a ranked row gets fresh names and a fresh sell price; every other
    /// pin - planner pins, and sell pins for other commodities - is left untouched. TripQty is
    /// deliberately NOT refreshed: it was the user's own cargo entry at pin time, and the box's
    /// current value belongs to whatever they are ranking now, not to the pin.</summary>
    internal static IReadOnlyList<PinnedRoute> RefreshSellPins(
        IReadOnlyList<PinnedRoute> pinned, IReadOnlyList<TradePriceRow> rows, DateTime nowUtc)
    {
        var result = new List<PinnedRoute>(pinned.Count);
        foreach (var pin in pinned)
        {
            var live = pin.BuyTerminalId is null
                ? rows.FirstOrDefault(r => SameSellHaul(pin, r.TerminalId, r.CommodityId))
                : null;
            if (live is null) { result.Add(pin); continue; }

            result.Add(new PinnedRoute
            {
                BuyTerminalId = null,
                SellTerminalId = pin.SellTerminalId,
                CommodityId = pin.CommodityId,
                CommodityName = live.CommodityName,
                BuyTerminalName = "",
                SellTerminalName = live.TerminalName,
                TripQty = pin.TripQty,
                PerScuMargin = live.Sell,
                UpdatedUtc = nowUtc,
                PinnedUtc = pin.PinnedUtc,
            });
        }
        return result;
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
