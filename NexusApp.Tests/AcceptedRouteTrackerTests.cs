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
        Func<string?, string?, IReadOnlySet<int>>? terminalsForLocation = null,
        Func<DateTime>? utcNow = null) => new(_profit,
            () => _routes,
            () => _saveCount++,
            terminalsForLocation ?? ((_, _) => new HashSet<int> { 7, 9 }),
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

    // Code review fix, 2026-08-09 (item 1, CRITICAL): a restart does NOT replay Game.log through
    // this tracker in any way that could rebuild _accumulators (the class comment's old claim that
    // it did was false), so the constructor must seed the table from each persisted Loaded route's
    // own ActualQty/ActualBuyPer instead.
    [Fact]
    public void Construction_SeedsAccumulatorFromPersistedLoadedRoute_SoASecondBuyAfterARestartIsNotDropped()
    {
        // Simulates a restart: a route already Loaded from a buy in the PREVIOUS process, persisted
        // with its running totals, and a brand new tracker built over it - a fresh _accumulators
        // table with nothing replayed into it.
        var route = Route(buyTerminalId: 7, sellTerminalId: 9, commodityId: 1, stage: AcceptedStage.Loaded);
        route.ActualQty = 200;
        route.ActualBuyPer = 1000m;   // 200,000 aUEC / 200 SCU
        _routes = new List<AcceptedRoute> { route };
        using var tracker = NewTracker();

        // RouteMatcher.BestMatch's rule 1 refuses to rebind a Loaded route to a fresh BUY, so this
        // can ONLY land if the seeded accumulator lets the continuation path find it.
        tracker.Apply(Tx(TransactionKind.Buy, 200_000, 200m));

        Assert.Equal(400, route.ActualQty);        // the 200 already held plus the 200 just bought
        Assert.Equal(1000m, route.ActualBuyPer);   // same price both times, weighted average unchanged
    }

    // Code review fix, 2026-08-09 (item 2a): a log reset must clear the accumulator table before
    // the replay lands, exactly as AutoLoadTracker.Reset does for its own active entries -
    // otherwise a redelivered buy sums onto whatever the table already held and double-counts.
    [Fact]
    public void LogReset_ClearsAccumulator_SoAReplayOfTheSameBuyDoesNotDoubleCount()
    {
        var route = Route(buyTerminalId: 7, sellTerminalId: 9, commodityId: 1);
        _routes = new List<AcceptedRoute> { route };
        using var tracker = NewTracker();

        var buy = Tx(TransactionKind.Buy, 200_000, 200m);
        tracker.Apply(buy);
        Assert.Equal(200, route.ActualQty);

        _profit.Reset();     // SettingsPage.ApplyGameLogPath's fromBeginning re-Start, or a channel auto-follow flip
        tracker.Apply(buy);  // the replay redelivers the SAME physical buy line

        Assert.Equal(200, route.ActualQty);   // not 400 - a redelivered buy must never double-count
    }

    // Code review fix, 2026-08-09 (item 2b): deleting a route must forget its accumulator, or a
    // freshly re-accepted route for the identical haul (same buy/sell/commodity triple, so the
    // same RouteKey) binds the stale total and its own first fill never sets Stage to Loaded.
    [Fact]
    public void Forget_ClearsAccumulator_SoADeletedAndReacceptedRouteLoadsFresh()
    {
        var deleted = Route(buyTerminalId: 7, sellTerminalId: 9, commodityId: 1);
        _routes = new List<AcceptedRoute> { deleted };
        using var tracker = NewTracker();

        tracker.Apply(Tx(TransactionKind.Buy, 200_000, 200m));
        Assert.Equal(AcceptedStage.Loaded, deleted.Stage);

        // Delete: TradePage.UnpinRoute and Cargo Hauling's Delete route button both call Forget
        // alongside RoutePlanner.RemovePin.
        tracker.Forget(deleted);
        var reaccepted = Route(buyTerminalId: 7, sellTerminalId: 9, commodityId: 1);   // identical identity triple
        _routes = new List<AcceptedRoute> { reaccepted };

        tracker.Apply(Tx(TransactionKind.Buy, 150_000, 150m));

        Assert.Equal(AcceptedStage.Loaded, reaccepted.Stage);   // without Forget this stays Accepted forever
        Assert.Equal(150, reaccepted.ActualQty);                // a fresh total, not summed onto the deleted route's 200
    }

    // Code review fix, 2026-08-09 (item 3, IMPORTANT): continuation must not win unconditionally
    // over a freshly accepted route sharing the same buy terminal and commodity - recency (the
    // same rule RouteMatcher.BestMatch's own rule 4 uses) decides between them.
    [Fact]
    public void TwoRoutesSameTerminalAndCommodity_SecondCanStillLoad_AfterTheFirstAlreadyDid()
    {
        var first = Route(buyTerminalId: 7, sellTerminalId: 9, commodityId: 1);   // Everus -> ArcCorp Scrap
        _routes = new List<AcceptedRoute> { first };
        using var tracker = NewTracker();

        // Only `first` exists yet, so it loads via a fresh BestMatch bind.
        tracker.Apply(Tx(TransactionKind.Buy, 200_000, 200m));
        Assert.Equal(AcceptedStage.Loaded, first.Stage);

        // A second route is accepted afterwards for the SAME buy terminal and commodity (Everus
        // Scrap) but a different sell leg - Everus -> Olisar Scrap.
        var second = Route(buyTerminalId: 7, sellTerminalId: 11, commodityId: 1);
        second.PinnedUtc = T0.AddMinutes(5);
        _routes.Add(second);

        // A further Scrap buy at Everus: continuation-first must not blindly re-absorb this onto
        // `first`'s already-open accumulator just because it still matches commodity+place -
        // `second` is the more recently accepted qualifying route and must win it.
        tracker.Apply(Tx(TransactionKind.Buy, 150_000, 150m));

        Assert.Equal(AcceptedStage.Loaded, second.Stage);   // "the second can never load" - now it can
        Assert.Equal(150, second.ActualQty);
        Assert.Equal(200, first.ActualQty);                 // first's own total is untouched, not stolen either
    }

    // Code review fix, 2026-08-09 (item 5): RoutePlanner.ToSellPin now creates a sell-only route
    // already Loaded (the cargo is already held, there is no buy leg to match), so its own
    // matching sell must be able to close it exactly like a planner route's.
    [Fact]
    public void SellOnlyRoute_MatchingSellClosesIt()
    {
        var route = Route(buyTerminalId: null, sellTerminalId: 9, commodityId: 1, stage: AcceptedStage.Loaded);
        route.ActualQty = 96;
        _routes = new List<AcceptedRoute> { route };
        using var tracker = NewTracker();

        tracker.Apply(Tx(TransactionKind.Sell, 200_000, 96m));

        Assert.Empty(_routes);
        Assert.Equal(AcceptedStage.Sold, route.Stage);
    }

    // ---- RoutesChanged (code review finding, 2026-08-09) --------------------------------------
    // The correction was being persisted and left invisible: _save() wrote the new quantity, but
    // nothing told the overlay CARGO tab - the surface actually in front of the player at the
    // kiosk - to repaint, so it kept showing the accepted figures until an unrelated event
    // rebuilt it.

    [Fact]
    public void MatchedBuy_RaisesRoutesChanged()
    {
        _routes = new List<AcceptedRoute> { Route(buyTerminalId: 7, sellTerminalId: 9, commodityId: 1) };
        using var tracker = NewTracker();
        var changed = 0;
        tracker.RoutesChanged += () => changed++;

        tracker.Apply(Tx(TransactionKind.Buy, 693_600, 680m));

        Assert.Equal(1, changed);
    }

    [Fact]
    public void EveryPartialFill_RaisesRoutesChanged_NotJustTheFirst()
    {
        _routes = new List<AcceptedRoute> { Route(buyTerminalId: 7, sellTerminalId: 9, commodityId: 1) };
        using var tracker = NewTracker();
        var changed = 0;
        tracker.RoutesChanged += () => changed++;

        tracker.Apply(Tx(TransactionKind.Buy, 200_000, 200m));
        tracker.Apply(Tx(TransactionKind.Buy, 200_000, 200m));

        Assert.Equal(2, changed);   // the running total moves on each one, so each one is a repaint
    }

    [Fact]
    public void MatchedSell_RaisesRoutesChanged()
    {
        var route = Route(buyTerminalId: 7, sellTerminalId: 9, commodityId: 1, stage: AcceptedStage.Loaded);
        route.ActualQty = 680;
        _routes = new List<AcceptedRoute> { route };
        using var tracker = NewTracker();
        var changed = 0;
        tracker.RoutesChanged += () => changed++;

        tracker.Apply(Tx(TransactionKind.Sell, 837_760, 680m));

        Assert.Equal(1, changed);
    }

    // Silence is the point: a transaction that matches nothing changed no route, and waking every
    // surface on every unrelated kiosk purchase would repaint the overlay constantly for nothing.
    [Fact]
    public void UnmatchedTransaction_RaisesNothing()
    {
        _routes = new List<AcceptedRoute> { Route(buyTerminalId: 7, sellTerminalId: 9, commodityId: 1) };
        using var tracker = NewTracker(commodityNameForGuid: _ => "Laranite");   // not this route's commodity
        var changed = 0;
        tracker.RoutesChanged += () => changed++;

        tracker.Apply(Tx(TransactionKind.Buy, 693_600, 680m));

        Assert.Equal(0, changed);
    }

    // Source pin: MainWindow is the one subscriber, and it must marshal to the UI thread - this
    // event fires on the Game.log feed's thread, like the TransactionParsed that drives it.
    [Fact]
    public void MainWindow_SubscribesRoutesChanged_AndMarshalsToTheUiThread()
    {
        var src = SourceFiles.ReadAppSource(@"Views\MainWindow.xaml.cs");
        Assert.Contains("RoutesChanged +=", src);
        Assert.Contains("Dispatcher.BeginInvoke", src);
    }

    // Source pin: both push helpers read the routes from SETTINGS, never through the lazy
    // TradePage - going through the page pushed an EMPTY list to the overlay and the map until the
    // user happened to open Trade once (code review finding, 2026-08-09).
    [Fact]
    public void PushHelpers_ReadTheRoutesFromSettings_NotThroughTradePage()
    {
        var src = SourceFiles.ReadAppSource(@"Views\MainWindow.xaml.cs");
        Assert.DoesNotContain("_tradePage?.PinnedRoutes", src);
        Assert.Contains("AcceptedRoutesNow", src);
    }
}
