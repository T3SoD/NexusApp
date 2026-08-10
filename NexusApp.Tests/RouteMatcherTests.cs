using NexusApp.Models;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Task D2 of the trade/cargo fusion spec (2026-08-09, section 1.5): the heuristic buy/sell ->
// accepted-route binding. Every rule can only narrow the candidate list or return null - never
// guess - so most of these tests are shaped around "this one fact is missing/wrong, so null".
public class RouteMatcherTests
{
    private static readonly DateTime T0 = new(2026, 8, 9, 12, 0, 0, DateTimeKind.Utc);

    private static AcceptedRoute Route(int? buyTerminalId, int sellTerminalId, int commodityId,
        AcceptedStage stage = AcceptedStage.Accepted, DateTime? pinnedUtc = null) => new()
    {
        BuyTerminalId = buyTerminalId, SellTerminalId = sellTerminalId, CommodityId = commodityId,
        CommodityName = $"Commodity {commodityId}", BuyTerminalName = "Buy", SellTerminalName = "Sell",
        TripQty = 100, PerScuMargin = 10, Stage = stage, PinnedUtc = pinnedUtc ?? T0,
    };

    private static CommodityTransaction Tx(TransactionKind kind, string guid = "guid-scrap",
        string? placeLabel = "Everus Harbor", string? placeUexLocation = null) => new()
    {
        TimestampUtc = T0, Kind = kind, Amount = 100_000, Scu = 100m, ResourceGuid = guid,
        PlaceLabel = placeLabel, PlaceUexLocation = placeUexLocation,
    };

    // A single terminal id, whatever the location string was - a stand-in for a real
    // TradeOriginResolver.TerminalIdsForLocation call that always resolves to terminal 7.
    private static Func<string, IReadOnlySet<int>> AlwaysResolvesTo(params int[] ids)
        => _ => new HashSet<int>(ids);

    private static Func<string, IReadOnlySet<int>> NeverResolves => _ => new HashSet<int>();

    private static string? ScrapName(string guid) => guid == "guid-scrap" ? "Scrap" : null;
    private static string? NameForId(int id) => id == 1 ? "Scrap" : id == 2 ? "Laranite" : null;

    [Fact]
    public void ExactMatch_BuyBindsTheAcceptedRoute()
    {
        var routes = new[] { Route(buyTerminalId: 7, sellTerminalId: 9, commodityId: 1) };
        var idx = RouteMatcher.BestMatch(routes, Tx(TransactionKind.Buy), AlwaysResolvesTo(7), ScrapName, NameForId);
        Assert.Equal(0, idx);
    }

    [Fact]
    public void CommodityNameMismatch_NeverMatches()
    {
        var routes = new[] { Route(buyTerminalId: 7, sellTerminalId: 9, commodityId: 2) };   // Laranite
        var idx = RouteMatcher.BestMatch(routes, Tx(TransactionKind.Buy), AlwaysResolvesTo(7), ScrapName, NameForId);
        Assert.Null(idx);
    }

    [Fact]
    public void LocationResolvesToASetExcludingTheLeg_NeverMatches()
    {
        var routes = new[] { Route(buyTerminalId: 7, sellTerminalId: 9, commodityId: 1) };
        // The tx's own location resolves to real terminals - just not terminal 7.
        var idx = RouteMatcher.BestMatch(routes, Tx(TransactionKind.Buy), AlwaysResolvesTo(3, 4), ScrapName, NameForId);
        Assert.Null(idx);
    }

    [Fact]
    public void LocationResolvesToNothing_NeverMatches()
    {
        var routes = new[] { Route(buyTerminalId: 7, sellTerminalId: 9, commodityId: 1) };
        var idx = RouteMatcher.BestMatch(routes, Tx(TransactionKind.Buy), NeverResolves, ScrapName, NameForId);
        Assert.Null(idx);
    }

    [Fact]
    public void SellOnlyRoute_NeverMatchesABuy()
    {
        var routes = new[] { Route(buyTerminalId: null, sellTerminalId: 9, commodityId: 1) };
        // Even if the tx's location resolves to the sell terminal itself, a BUY needs a buy leg.
        var idx = RouteMatcher.BestMatch(routes, Tx(TransactionKind.Buy), AlwaysResolvesTo(9), ScrapName, NameForId);
        Assert.Null(idx);
    }

    [Fact]
    public void SoldRoute_NeverMatchesEitherKind()
    {
        var routes = new[] { Route(buyTerminalId: 7, sellTerminalId: 9, commodityId: 1, stage: AcceptedStage.Sold) };
        Assert.Null(RouteMatcher.BestMatch(routes, Tx(TransactionKind.Buy), AlwaysResolvesTo(7), ScrapName, NameForId));
        Assert.Null(RouteMatcher.BestMatch(routes, Tx(TransactionKind.Sell), AlwaysResolvesTo(9), ScrapName, NameForId));
    }

    [Fact]
    public void SellMatchesOnlyALoadedRoute_NotAnAcceptedOne()
    {
        var accepted = new[] { Route(buyTerminalId: 7, sellTerminalId: 9, commodityId: 1) };
        Assert.Null(RouteMatcher.BestMatch(accepted, Tx(TransactionKind.Sell), AlwaysResolvesTo(9), ScrapName, NameForId));

        var loaded = new[] { Route(buyTerminalId: 7, sellTerminalId: 9, commodityId: 1, stage: AcceptedStage.Loaded) };
        Assert.Equal(0, RouteMatcher.BestMatch(loaded, Tx(TransactionKind.Sell), AlwaysResolvesTo(9), ScrapName, NameForId));
    }

    [Fact]
    public void TwoCandidates_PicksTheMostRecentlyAccepted()
    {
        var older = Route(buyTerminalId: 7, sellTerminalId: 9, commodityId: 1, pinnedUtc: T0);
        var newer = Route(buyTerminalId: 7, sellTerminalId: 11, commodityId: 1, pinnedUtc: T0.AddMinutes(5));
        var routes = new[] { older, newer };
        var idx = RouteMatcher.BestMatch(routes, Tx(TransactionKind.Buy), AlwaysResolvesTo(7), ScrapName, NameForId);
        Assert.Equal(1, idx);   // the newer route, index 1
    }

    [Fact]
    public void UnresolvableGuid_ReturnsNull()
    {
        var routes = new[] { Route(buyTerminalId: 7, sellTerminalId: 9, commodityId: 1) };
        var idx = RouteMatcher.BestMatch(routes, Tx(TransactionKind.Buy, guid: "unknown-guid"),
            AlwaysResolvesTo(7), ScrapName, NameForId);
        Assert.Null(idx);
    }

    [Fact]
    public void UnresolvableRouteCommodityId_NeverMatches()
    {
        var routes = new[] { Route(buyTerminalId: 7, sellTerminalId: 9, commodityId: 999) };   // NameForId -> null
        var idx = RouteMatcher.BestMatch(routes, Tx(TransactionKind.Buy), AlwaysResolvesTo(7), ScrapName, NameForId);
        Assert.Null(idx);
    }

    [Fact]
    public void EmptyRouteList_ReturnsNull()
    {
        var idx = RouteMatcher.BestMatch(Array.Empty<AcceptedRoute>(), Tx(TransactionKind.Buy),
            AlwaysResolvesTo(7), ScrapName, NameForId);
        Assert.Null(idx);
    }

    // PlaceUexLocation (task D1) feeds the SAME lookup as PlaceLabel, whichever is present - the
    // matcher does not need to know which one carried the winning value.
    [Fact]
    public void PlaceUexLocation_FeedsTheSameLocationLookup()
    {
        var routes = new[] { Route(buyTerminalId: 7, sellTerminalId: 9, commodityId: 1) };
        var tx = Tx(TransactionKind.Buy, placeLabel: "some display label that resolves nowhere",
            placeUexLocation: "Real UEX Location");
        string? seen = null;
        IReadOnlySet<int> Capture(string loc) { seen = loc; return new HashSet<int> { 7 }; }
        var idx = RouteMatcher.BestMatch(routes, tx, Capture, ScrapName, NameForId);
        Assert.Equal(0, idx);
        Assert.Equal("Real UEX Location", seen);
    }
}
