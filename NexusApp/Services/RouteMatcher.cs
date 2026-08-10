using NexusApp.Models;

namespace NexusApp.Services;

// Heuristic buy/sell -> accepted-route binding (trade/cargo fusion spec 2026-08-09, section 1.5,
// task D2). A logged Game.log transaction carries a commodity GUID, an SCU amount, a total aUEC,
// a kiosk token and a player location label - NO UEX terminal id and NO route affiliation. A
// Scrap buy is byte-identical whether it feeds an accepted route or a hauling contract. So this is
// explicitly a HEURISTIC, never a certain identification, and it is built to fail SILENT rather
// than fail WRONG: every rule below can only narrow the candidate list or return null, never guess
// past an unresolved fact. Returning null is a correct and common outcome - the route the
// transaction actually served may not be an accepted route at all, and the caller must leave every
// route exactly where it is when that happens.
//
// Pure and WPF-free by design (no App.*, no live snapshot): every fact this needs arrives as a
// plain value or a delegate, so the fold is unit testable without a running app.
internal static class RouteMatcher
{
    /// <summary>The index into <paramref name="routes"/> of the route <paramref name="tx"/> most
    /// likely serves, or null when nothing qualifies.
    ///
    /// <para>Rules, in order: (1) stage gate - only an Accepted route is a BUY candidate, only a
    /// Loaded route is a SELL candidate; (2) the commodity must resolve on BOTH sides (the tx's
    /// GUID via <paramref name="commodityNameForGuid"/>, the route's CommodityId via
    /// <paramref name="commodityNameForId"/>) and match case-insensitively after trimming - either
    /// side failing to resolve drops the route as a candidate, never a guess; (3) the tx's stamped
    /// location must resolve (<paramref name="terminalsForLocation"/>) to a terminal-id set that
    /// contains the route's relevant leg (BuyTerminalId for a BUY, SellTerminalId for a SELL) - a
    /// sell-only route (null BuyTerminalId) can therefore never match a BUY; (4) the most recently
    /// accepted route wins when more than one still qualifies.</para></summary>
    internal static int? BestMatch(IReadOnlyList<AcceptedRoute> routes, CommodityTransaction tx,
        Func<string, IReadOnlySet<int>> terminalsForLocation,
        Func<string, string?> commodityNameForGuid,
        Func<int, string?> commodityNameForId)
    {
        // Rule 2, tx side: an unresolvable GUID means every route in the list fails the commodity
        // test below anyway, so this short-circuits the whole search rather than resolving it once
        // per candidate for nothing.
        var txCommodity = commodityNameForGuid(tx.ResourceGuid);
        if (string.IsNullOrWhiteSpace(txCommodity)) return null;

        // The tx's stamped location, resolved once. PlaceUexLocation (task D1) is the precise UEX
        // Location string when the tracker resolved one; PlaceLabel is the display fallback -
        // TradeOriginResolver.TerminalIdsForLocation's own uexLocation-first pass is what actually
        // benefits from PlaceUexLocation reaching it, so either field feeding the SAME single
        // string argument here is correct.
        var terminals = terminalsForLocation(tx.PlaceUexLocation ?? tx.PlaceLabel ?? "");

        bool isBuy = tx.Kind == TransactionKind.Buy;
        int? bestIndex = null;
        DateTime bestAcceptedUtc = default;
        for (int i = 0; i < routes.Count; i++)
        {
            var r = routes[i];

            // Rule 1: stage gate.
            if (isBuy && r.Stage != AcceptedStage.Accepted) continue;
            if (!isBuy && r.Stage != AcceptedStage.Loaded) continue;

            if (!CommodityAndPlaceMatch(r, isBuy, terminals, txCommodity, commodityNameForId)) continue;

            // Rule 4: PinnedUtc is when the route was ACCEPTED (never updated afterwards - see its
            // own doc comment) and is the true "most recently accepted" ordering; UpdatedUtc moves
            // on every display refresh instead and would answer the wrong question here.
            if (bestIndex is null || r.PinnedUtc > bestAcceptedUtc) { bestIndex = i; bestAcceptedUtc = r.PinnedUtc; }
        }
        return bestIndex;
    }

    // Rules 2 and 3, factored out so AcceptedRouteTracker can re-run the SAME commodity+place test
    // against a route it already bound earlier this session (spec 1.3: a second and third partial
    // buy must SUM onto the route the first buy opened). That route is Loaded, not Accepted, by
    // the time the second buy lands - BestMatch's rule 1 above intentionally never lets a fresh
    // search re-open a Loaded route to a BUY, so the accumulator's own continuation check calls
    // this directly instead of BestMatch.
    internal static bool CommodityAndPlaceMatch(AcceptedRoute route, bool isBuy,
        IReadOnlySet<int> terminalsAtLocation, string txCommodity, Func<int, string?> commodityNameForId)
    {
        int? leg = isBuy ? route.BuyTerminalId : route.SellTerminalId;
        if (leg is null || !terminalsAtLocation.Contains(leg.Value)) return false;

        var routeCommodity = commodityNameForId(route.CommodityId);
        if (string.IsNullOrWhiteSpace(routeCommodity)) return false;   // never guess an unresolved id either

        return string.Equals(txCommodity.Trim(), routeCommodity.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
