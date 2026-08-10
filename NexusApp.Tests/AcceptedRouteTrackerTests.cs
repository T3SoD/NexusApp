using NexusApp.Models;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Task D3 of the trade/cargo fusion spec (2026-08-09, sections 1.4/1.5). AcceptedRouteTracker
// applies RouteMatcher's heuristic to a live transaction stream (Apply is public, like
// AutoLoadTracker's, so these tests drive it one transaction at a time without a real Game.log
// feed) and rewrites a route's realised quantity/cost from what actually happened.
public class AcceptedRouteTrackerTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 8, 9, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _histPath = Path.Combine(Path.GetTempPath(), $"art_hist_{Guid.NewGuid():N}.json");
    private readonly ProfitTracker _profit;
    private List<AcceptedRoute> _routes = new();
    private int _saveCount;

    public AcceptedRouteTrackerTests() => _profit = new ProfitTracker(historyPath: _histPath);

    public void Dispose()
    {
        _profit.Dispose();
        try { File.Delete(_histPath); } catch { }
    }

    private static AcceptedRoute Route(int? buyTerminalId, int sellTerminalId, int commodityId,
        AcceptedStage stage = AcceptedStage.Accepted, int tripQty = 750, double perScuMargin = 10) => new()
    {
        BuyTerminalId = buyTerminalId, SellTerminalId = sellTerminalId, CommodityId = commodityId,
        CommodityName = "Scrap", BuyTerminalName = "Everus Harbor", SellTerminalName = "Baijini Point",
        TripQty = tripQty, PerScuMargin = perScuMargin, Stage = stage, PinnedUtc = T0,
    };

    private static CommodityTransaction Tx(TransactionKind kind, long amount, decimal scu,
        string guid = "guid-scrap", string? place = "Everus Harbor") => new()
    {
        TimestampUtc = T0, Kind = kind, Amount = amount, Scu = scu, ResourceGuid = guid, PlaceLabel = place,
    };

    // Terminals 7 and 9 always resolve by default - matching Route()'s own buy/sell legs above -
    // so most tests need only override the commodity resolvers.
    private AcceptedRouteTracker NewTracker(
        Func<string, string?>? commodityNameForGuid = null,
        Func<int, string?>? commodityNameForId = null,
        Func<string, IReadOnlySet<int>>? terminalsForLocation = null,
        Func<DateTime>? utcNow = null) => new(_profit,
            () => _routes,
            () => _saveCount++,
            terminalsForLocation ?? (_ => new HashSet<int> { 7, 9 }),
            commodityNameForGuid ?? (g => g == "guid-scrap" ? "Scrap" : null),
            commodityNameForId ?? (id => id == 1 ? "Scrap" : null),
            utcNow ?? (() => T0));

    [Fact]
    public void SingleBuy_SetsActualQtyAndMovesToLoaded()
    {
        var route = Route(buyTerminalId: 7, sellTerminalId: 9, commodityId: 1);
        _routes = new List<AcceptedRoute> { route };
        using var tracker = NewTracker();

        tracker.Apply(Tx(TransactionKind.Buy, 693_600, 680m));

        Assert.Equal(AcceptedStage.Loaded, route.Stage);
        Assert.Equal(680, route.ActualQty);
        Assert.Equal(1020m, route.ActualBuyPer);   // 693,600 / 680
        Assert.Equal(T0, route.LoadedUtc);
        Assert.Equal(1, _saveCount);
    }

    [Fact]
    public void ThreePartialBuys_SumQuantityAndWeightAverageThePrice()
    {
        var route = Route(buyTerminalId: 7, sellTerminalId: 9, commodityId: 1);
        _routes = new List<AcceptedRoute> { route };
        using var tracker = NewTracker();

        tracker.Apply(Tx(TransactionKind.Buy, 200_000, 200m));   // 1,000 aUEC/SCU
        tracker.Apply(Tx(TransactionKind.Buy, 200_000, 200m));   // 1,000 aUEC/SCU
        tracker.Apply(Tx(TransactionKind.Buy, 313_600, 280m));   // 1,120 aUEC/SCU

        Assert.Equal(AcceptedStage.Loaded, route.Stage);
        Assert.Equal(680, route.ActualQty);                  // 200 + 200 + 280
        Assert.Equal(713_600m / 680m, route.ActualBuyPer);   // weighted average, not a flat mean
        Assert.Equal(T0, route.LoadedUtc);                   // set once, on the FIRST fill only
        Assert.Equal(3, _saveCount);
    }

    [Fact]
    public void SellMatchedToLoadedRoute_RemovesItFromTheActiveList()
    {
        var route = Route(buyTerminalId: 7, sellTerminalId: 9, commodityId: 1, stage: AcceptedStage.Loaded);
        route.ActualQty = 680;
        route.ActualBuyPer = 1020m;
        _routes = new List<AcceptedRoute> { route };
        using var tracker = NewTracker();

        tracker.Apply(Tx(TransactionKind.Sell, 837_760, 680m));

        Assert.Empty(_routes);
        Assert.Equal(AcceptedStage.Sold, route.Stage);
        Assert.Equal(T0, route.SoldUtc);
        Assert.Equal(1, _saveCount);
    }

    [Fact]
    public void UnmatchedTransaction_ChangesNothing()
    {
        var route = Route(buyTerminalId: 7, sellTerminalId: 9, commodityId: 1);
        _routes = new List<AcceptedRoute> { route };
        using var tracker = NewTracker();

        // A different, unresolvable commodity - never guessed onto the only accepted route.
        tracker.Apply(Tx(TransactionKind.Buy, 100_000, 100m, guid: "guid-unknown"));

        Assert.Equal(AcceptedStage.Accepted, route.Stage);
        Assert.Null(route.ActualQty);
        Assert.Single(_routes);
        Assert.Equal(0, _saveCount);
    }

    [Fact]
    public void SellBeforeAnyBuy_NeverMatchesAnAcceptedRoute()
    {
        var route = Route(buyTerminalId: 7, sellTerminalId: 9, commodityId: 1);   // still Accepted
        _routes = new List<AcceptedRoute> { route };
        using var tracker = NewTracker();

        tracker.Apply(Tx(TransactionKind.Sell, 500_000, 500m));

        Assert.Equal(AcceptedStage.Accepted, route.Stage);
        Assert.Single(_routes);
        Assert.Equal(0, _saveCount);
    }

    // "Never throw into the log pipeline" (task D3): a resolver fault must not escape Apply.
    [Fact]
    public void MatcherFault_NeverThrows()
    {
        _routes = new List<AcceptedRoute> { Route(buyTerminalId: 7, sellTerminalId: 9, commodityId: 1) };
        using var tracker = NewTracker(commodityNameForGuid: _ => throw new InvalidOperationException("boom"));

        var ex = Record.Exception(() => tracker.Apply(Tx(TransactionKind.Buy, 100_000, 100m)));
        Assert.Null(ex);
        Assert.Equal(0, _saveCount);
    }

    // Accumulator identity (spec 1.3, "keyed on route, commodity GUID, place, session"): two routes
    // sharing a buy terminal must never cross-contaminate each other's running totals.
    [Fact]
    public void TwoRoutesAtTheSameTerminal_KeepSeparateAccumulators()
    {
        var scrap = Route(buyTerminalId: 7, sellTerminalId: 9, commodityId: 1);
        var laranite = Route(buyTerminalId: 7, sellTerminalId: 9, commodityId: 2);
        _routes = new List<AcceptedRoute> { scrap, laranite };
        using var tracker = NewTracker(
            commodityNameForGuid: g => g == "guid-scrap" ? "Scrap" : g == "guid-laranite" ? "Laranite" : null,
            commodityNameForId: id => id == 1 ? "Scrap" : id == 2 ? "Laranite" : null);

        tracker.Apply(Tx(TransactionKind.Buy, 100_000, 100m, guid: "guid-scrap"));
        tracker.Apply(Tx(TransactionKind.Buy, 200_000, 100m, guid: "guid-laranite"));

        Assert.Equal(AcceptedStage.Loaded, scrap.Stage);
        Assert.Equal(100, scrap.ActualQty);
        Assert.Equal(1000m, scrap.ActualBuyPer);
        Assert.Equal(AcceptedStage.Loaded, laranite.Stage);
        Assert.Equal(100, laranite.ActualQty);
        Assert.Equal(2000m, laranite.ActualBuyPer);
    }
}
