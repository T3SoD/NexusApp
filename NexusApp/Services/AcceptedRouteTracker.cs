using NexusApp.Models;

namespace NexusApp.Services;

// Applies RouteMatcher's heuristic to the live transaction feed, task D3 of the trade/cargo fusion
// spec (2026-08-09, sections 1.4/1.5). Mirrors AutoLoadTracker's shape: ProfitTracker pushes every
// parsed transaction in through Apply, which stays public so a test can drive it directly without
// a live Game.log feed.
//
// ACCUMULATION (spec 1.3: "keyed on route, commodity GUID, place, session"). RouteMatcher's own
// stage gate only ever lets a BUY bind an Accepted route, and the FIRST matched buy moves that
// route to Loaded (spec 1.2) - so a second and third partial buy of the same haul (players buy in
// chunks: 200, then 200, then 280) can never win a FRESH RouteMatcher.BestMatch search, because by
// then the route is Loaded, not Accepted. This tracker keeps its own small in-memory table of
// "which route did I already bind this GUID+buy-terminal to, this session" so repeated buys still
// find their way home and SUM rather than each looking like an unrelated first fill. In-memory
// only, never persisted: a restart replays the whole Game.log from the top (ProfitTracker's own
// replay contract), which re-plays every buy through Apply again and rebuilds this table exactly
// as it was - the same reasoning AutoLoadTracker's own active-entry restore already leans on for
// its OWN persisted state, just simpler here because there is nothing this table alone needs to
// survive a process restart for: the route's own persisted ActualQty/ActualBuyPer already carry
// the running total forward, and a restart's replay rebuilds this table's copy to match.
public sealed class AcceptedRouteTracker : IDisposable
{
    private readonly ProfitTracker _profit;
    private readonly Func<List<AcceptedRoute>> _routes;
    private readonly Action _save;
    private readonly Func<string, IReadOnlySet<int>> _terminalsForLocation;
    private readonly Func<string, string?> _commodityNameForGuid;
    private readonly Func<int, string?> _commodityNameForId;
    private readonly Func<DateTime> _utcNow;

    // A route's identity triple (the same three fields AcceptedRoute.SameHaulAs compares), stable
    // for the route's whole lifetime and the accumulator's own key.
    private readonly record struct RouteKey(int? BuyTerminalId, int SellTerminalId, int CommodityId);
    private sealed class Accumulator
    {
        public decimal TotalScu;
        public long TotalAmount;
    }
    private readonly Dictionary<RouteKey, Accumulator> _accumulators = new();

    public AcceptedRouteTracker(ProfitTracker profit,
        Func<List<AcceptedRoute>> routes, Action save,
        Func<string, IReadOnlySet<int>> terminalsForLocation,
        Func<string, string?> commodityNameForGuid,
        Func<int, string?> commodityNameForId,
        Func<DateTime>? utcNow = null)
    {
        _profit = profit;
        _routes = routes;
        _save = save;
        _terminalsForLocation = terminalsForLocation;
        _commodityNameForGuid = commodityNameForGuid;
        _commodityNameForId = commodityNameForId;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _profit.TransactionParsed += Apply;
    }

    /// <summary>Public (like AutoLoadTracker.Apply) so a test can drive one transaction at a time
    /// without a live feed. A matcher fault must never break transaction parsing - the same
    /// discipline AutoLoadSampleStore's own write path follows: wrap, log under [CARGO], move on.</summary>
    public void Apply(CommodityTransaction tx)
    {
        try
        {
            if (tx.Kind == TransactionKind.Buy) ApplyBuy(tx);
            else ApplySell(tx);
        }
        catch (Exception ex)
        {
            Logger.Info($"[CARGO] route match failed: {ex.Message}");
        }
    }

    private void ApplyBuy(CommodityTransaction tx)
    {
        var routes = _routes();
        var txCommodity = _commodityNameForGuid(tx.ResourceGuid);
        if (string.IsNullOrWhiteSpace(txCommodity)) return;   // rule 2: never guess an unresolved GUID
        var terminals = _terminalsForLocation(tx.PlaceUexLocation ?? tx.PlaceLabel ?? "");

        // Continuation first: a route this tracker already loaded this session, whose buy leg and
        // commodity still match, absorbs this as a further partial fill - the same commodity+place
        // test RouteMatcher itself runs (RouteMatcher.CommodityAndPlaceMatch), just without rule
        // 1's stage gate, since this route is Loaded on purpose and a fresh BestMatch search would
        // never reconsider it for a BUY.
        AcceptedRoute? route = null;
        var key = default(RouteKey);
        foreach (var candidate in routes)
        {
            var candidateKey = KeyOf(candidate);
            if (!_accumulators.ContainsKey(candidateKey)) continue;
            if (!RouteMatcher.CommodityAndPlaceMatch(candidate, isBuy: true, terminals, txCommodity, _commodityNameForId))
                continue;
            route = candidate;
            key = candidateKey;
            break;
        }

        bool firstFill = route is null;
        if (route is null)
        {
            var idx = RouteMatcher.BestMatch(routes, tx, _terminalsForLocation, _commodityNameForGuid, _commodityNameForId);
            if (idx is null) return;   // no accepted route qualifies - stays exactly where it is
            route = routes[idx.Value];
            key = KeyOf(route);
        }

        if (!_accumulators.TryGetValue(key, out var acc)) _accumulators[key] = acc = new Accumulator();
        acc.TotalScu += tx.Scu;
        acc.TotalAmount += tx.Amount;
        if (acc.TotalScu <= 0) return;   // defensive: never record a non-positive fill or divide by zero

        route.ActualQty = (int)Math.Round(acc.TotalScu, MidpointRounding.AwayFromZero);
        route.ActualBuyPer = acc.TotalAmount / acc.TotalScu;
        if (firstFill)
        {
            route.Stage = AcceptedStage.Loaded;
            route.LoadedUtc = _utcNow();
        }
        _save();
        Logger.Info($"[CARGO] route loaded {route.CommodityName} {route.ActualQty} SCU at {route.ActualBuyPer:0.##} aUEC/SCU");
    }

    private void ApplySell(CommodityTransaction tx)
    {
        var routes = _routes();
        var idx = RouteMatcher.BestMatch(routes, tx, _terminalsForLocation, _commodityNameForGuid, _commodityNameForId);
        if (idx is null) return;   // no loaded route qualifies - stays exactly where it is
        var route = routes[idx.Value];

        route.Stage = AcceptedStage.Sold;
        route.SoldUtc = _utcNow();
        _accumulators.Remove(KeyOf(route));
        routes.Remove(route);   // sold cargo is finished work (spec 1.2): it leaves the active list
        _save();
        Logger.Info($"[CARGO] route sold {route.CommodityName}");
    }

    private static RouteKey KeyOf(AcceptedRoute r) => new(r.BuyTerminalId, r.SellTerminalId, r.CommodityId);

    public void Dispose() => _profit.TransactionParsed -= Apply;
}
