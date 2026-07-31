using System.Linq;

namespace NexusApp.Services;

// One priced leg pair for the route planner: buy this commodity at BuyRow's terminal, haul it,
// sell at SellRow's terminal. Net == Gross in v1 - no fee schedule exists yet (spec: "fee
// provider is zero in v1"); TradeRoute carries only the one number so the UI never has to
// reconcile a fee split that does not exist yet. TripParts narrates the same TripQty this route
// was built from, so the headline number and the expanded-band explanation can never drift apart.
public sealed record TradeRoute(TradePriceRow BuyRow, TradePriceRow SellRow, int TripQty, double Gross,
    double Net, ProximityTier Tier, string[] TripParts);

// Stock/demand coverage filter for the route planner (task 5). Any = no filter (default, the
// planner's original behavior, byte-preserved). CoversTrip requires both legs to carry at least
// one full trip's worth of SCU; CoversTwoTrips requires two trips' worth, so the shown route can
// be run back-to-back without a fresh scan in between.
public enum StockFilter { Any, CoversTrip, CoversTwoTrips }

// Pairs a Buy>0 row with a Sell>0 row of the same commodity, at two DIFFERENT terminals (a
// same-terminal pair is not a haul), ranks by net profit per trip. Anchor mode (FROM HERE vs
// ANYWHERE) only restricts which terminals the BUY leg may come from; the sell leg and the
// ranking math are identical either way (spec: "Same math either way; FROM HERE only restricts
// the buy-terminal set"). originTerminalIds is null ONLY for ANYWHERE (every terminal's buy legs
// are eligible); FROM HERE always passes a non-null set, EMPTY when the origin could not be
// resolved to any terminal. An empty set restricts the buy leg to NOTHING, not to "unrestricted" -
// treating an unresolved origin as ANYWHERE would silently show cross-map routes while FROM HERE
// stays lit (spec Decision 6: "every listed route is purchasable where the player stands").
public static class RoutePlanner
{
    public static IReadOnlyList<TradeRoute> Rank(IReadOnlyList<TradePriceRow> rows,
        IReadOnlyDictionary<int, MarketTerminal> terminals, int shipScu, int shipMaxBox, double? budget,
        IReadOnlySet<int>? originTerminalIds, string scope, int take, StockFilter stockFilter = StockFilter.Any)
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
            if (row.Sell > 0) lists.Sells.Add(row);
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
                    if (!PassesStockFilter(stockFilter, tripQty, buyRow.BuyStockScu, sellRow.SellDemandScu)) continue;
                    var gross = tripQty * (sellRow.Sell - buyRow.Buy);
                    result.Add(new TradeRoute(buyRow, sellRow, tripQty, gross, gross,
                        ProximityTiers.Derive(buyTerminal, sellTerminal),
                        TradeMath.TripParts(shipScu, buyRow.BuyStockScu, budget, buyRow.Buy)));
                }
            }
        }

        return result.OrderByDescending(r => r.Net).Take(take).ToList();
    }

    // Applied per pair, inside the pairing loop, before the take cutoff - a route that fails
    // coverage is skipped outright rather than merely ranked lower and then trimmed away by
    // Take(). tripQty == 0 never passes CoversTrip/CoversTwoTrips: a route nobody can actually run
    // once (ship capacity, stock, or budget already zeroed the trip) can't be said to "cover"
    // anything, so it only ever surfaces under Any.
    private static bool PassesStockFilter(StockFilter filter, int tripQty, int buyStockScu, int sellDemandScu) =>
        filter switch
        {
            StockFilter.CoversTrip => tripQty > 0 && buyStockScu >= tripQty && sellDemandScu >= tripQty,
            StockFilter.CoversTwoTrips => tripQty > 0 && buyStockScu >= 2 * tripQty && sellDemandScu >= 2 * tripQty,
            _ => true,
        };

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
}
