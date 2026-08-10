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
// find their way home and SUM rather than each looking like an unrelated first fill.
//
// RESTART (code review fix, 2026-08-09 - the previous version of this comment claimed a restart
// replays Game.log and rebuilds this table; that was false, and every buy after the first one was
// silently dropped on every relaunch). A restart does not replay through this tracker in any way
// that could rebuild _accumulators - and even if it did, RouteMatcher.BestMatch's own rule 1
// refuses to rebind a Loaded route to a BUY on a replay exactly as it would live, so the table
// would come back empty regardless. The constructor instead seeds _accumulators directly from
// every persisted Loaded route's own ActualQty/ActualBuyPer (SeedAccumulatorsFromPersistedRoutes)
// - the one place a route's running total DOES survive a restart - so the continuation path below
// has something to find on the very next buy.
//
// RESET. _accumulators is session-scoped, not haul-scoped: a log reset (SettingsPage's
// fromBeginning re-Start, or a channel auto-follow flip) replays the CURRENT log from the top
// again, and ProfitTracker raises TransactionParsed unconditionally for that replay - outside the
// ledger's own dedupe. Left uncleared, the replay would sum every buy a second time on top of
// whatever this table already held. Subscribing to ProfitTracker.LogWasReset (AutoLoadTracker.cs
// follows the same rule for its own active entries) clears the table before the replay lands.
public sealed class AcceptedRouteTracker : IDisposable
{
    private readonly ProfitTracker _profit;
    private readonly Func<List<AcceptedRoute>> _routes;
    private readonly Action _save;
    private readonly Func<string?, string?, IReadOnlySet<int>> _terminalsForLocation;
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
        Func<string?, string?, IReadOnlySet<int>> terminalsForLocation,
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
        _profit.LogWasReset += Reset;

        SeedAccumulatorsFromPersistedRoutes();
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

    /// <summary>Forgets a route's running accumulator (code review fix, 2026-08-09). Every place a
    /// route is removed (TradePage.UnpinRoute, Cargo Hauling's Delete route button) must call this
    /// alongside RoutePlanner.RemovePin: without it, deleting a route and re-accepting an identical
    /// one (same buy/sell/commodity triple - the identity RouteKey compares) binds the stale
    /// accumulator, makes the fresh route's first fill look like a continuation instead of a first
    /// fill, and the route never moves to Loaded. A no-op when the route never had an accumulator
    /// (still Accepted, or already Sold - ApplySell removes a sold route's accumulator itself).</summary>
    public void Forget(AcceptedRoute route) => _accumulators.Remove(KeyOf(route));

    private void ApplyBuy(CommodityTransaction tx)
    {
        var routes = _routes();
        var txCommodity = _commodityNameForGuid(tx.ResourceGuid);
        if (string.IsNullOrWhiteSpace(txCommodity)) return;   // rule 2: never guess an unresolved GUID
        var terminals = _terminalsForLocation(tx.PlaceLabel, tx.PlaceUexLocation);

        // Two candidate pools compete on the SAME recency rule RouteMatcher.BestMatch's own rule 4
        // uses (most recent PinnedUtc), rather than continuation always winning outright (code
        // review fix, 2026-08-09): a route this tracker already loaded this session, whose buy leg
        // and commodity still match (continuation - RouteMatcher.CommodityAndPlaceMatch, since a
        // Loaded route is invisible to a fresh BestMatch search by rule 1), and whatever BestMatch
        // itself offers fresh (an Accepted route only). Letting continuation win unconditionally
        // meant the FIRST route to load at a given terminal+commodity combo absorbed every later
        // buy there forever, and a second accepted route sharing that terminal and commodity could
        // never load - recency between the two pools is what lets a freshly accepted route win its
        // own buy back.
        AcceptedRoute? continuation = null;
        DateTime continuationPinnedUtc = default;
        foreach (var candidate in routes)
        {
            if (!_accumulators.ContainsKey(KeyOf(candidate))) continue;
            if (!RouteMatcher.CommodityAndPlaceMatch(candidate, isBuy: true, terminals, txCommodity, _commodityNameForId))
                continue;
            if (continuation is null || candidate.PinnedUtc > continuationPinnedUtc)
            {
                continuation = candidate;
                continuationPinnedUtc = candidate.PinnedUtc;
            }
        }

        var freshIdx = RouteMatcher.BestMatch(routes, tx, _terminalsForLocation, _commodityNameForGuid, _commodityNameForId);
        var fresh = freshIdx is { } i ? routes[i] : null;

        AcceptedRoute? route;
        if (fresh is null) route = continuation;
        else if (continuation is null || fresh.PinnedUtc > continuationPinnedUtc) route = fresh;
        else route = continuation;
        if (route is null) return;   // no accepted or continuing route qualifies - stays exactly where it is

        var key = KeyOf(route);
        bool firstFill = !_accumulators.ContainsKey(key);

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

    // Restart continuity (code review fix, 2026-08-09): see the class comment's RESTART section.
    // Only Loaded routes are seeded - an Accepted route has no fill yet to reconstruct, and a Sold
    // route already left the active list (ApplySell removes it and its accumulator together, so
    // there is nothing here to find even if a stale one somehow lingered in settings.json).
    private void SeedAccumulatorsFromPersistedRoutes()
    {
        foreach (var route in _routes())
        {
            if (route.Stage != AcceptedStage.Loaded) continue;
            if (route.ActualQty is not { } qty || qty <= 0) continue;
            var per = route.ActualBuyPer ?? 0m;
            _accumulators[KeyOf(route)] = new Accumulator
            {
                TotalScu = qty,
                TotalAmount = (long)Math.Round(per * qty, MidpointRounding.AwayFromZero),
            };
        }
    }

    // Log reset (code review fix, 2026-08-09): see the class comment's RESET section. Mirrors
    // AutoLoadTracker.Reset's own log-reset handling for its active entries.
    private void Reset()
    {
        if (_accumulators.Count == 0) return;
        _accumulators.Clear();
        Logger.Info("[CARGO] route accumulators cleared by log reset");
    }

    private static RouteKey KeyOf(AcceptedRoute r) => new(r.BuyTerminalId, r.SellTerminalId, r.CommodityId);

    public void Dispose()
    {
        _profit.TransactionParsed -= Apply;
        _profit.LogWasReset -= Reset;
    }
}
