using NexusApp.Models;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Task D4 of the trade/cargo fusion spec (2026-08-09, sections 1.4/4.3/4.4): the EXPECTED
// segment's arithmetic, kept out of CargoValue on purpose (see CargoValueTests'
// CargoValue_NeedsNoRouteOrTerminalContext - that fold must never learn about routes).
public class AcceptedRouteMoneyTests
{
    private static AcceptedRoute Route(AcceptedStage stage, int tripQty, int? actualQty,
        double perScuMargin) => new()
    {
        BuyTerminalId = 7, SellTerminalId = 9, CommodityId = 1, CommodityName = "Scrap",
        BuyTerminalName = "Everus Harbor", SellTerminalName = "Baijini Point",
        TripQty = tripQty, ActualQty = actualQty, PerScuMargin = perScuMargin, Stage = stage,
    };

    [Fact]
    public void NoRoutes_IsNull()
    {
        Assert.Null(AcceptedRouteMoney.ExpectedMargin(Array.Empty<AcceptedRoute>()));
    }

    [Fact]
    public void AcceptedRoute_ContributesNothing_NoBuyMatchedYet()
    {
        var routes = new[] { Route(AcceptedStage.Accepted, tripQty: 750, actualQty: null, perScuMargin: 10) };
        Assert.Null(AcceptedRouteMoney.ExpectedMargin(routes));
    }

    [Fact]
    public void LoadedRoute_UsesActualQtyOverTripQty()
    {
        // 750 planned, 680 actually bought - the corrected figure must drive the money, not the plan.
        var routes = new[] { Route(AcceptedStage.Loaded, tripQty: 750, actualQty: 680, perScuMargin: 212) };
        Assert.Equal(680 * 212L, AcceptedRouteMoney.ExpectedMargin(routes));
    }

    [Fact]
    public void LoadedRouteWithNoActualQtyYet_FallsBackToTripQty()
    {
        var routes = new[] { Route(AcceptedStage.Loaded, tripQty: 100, actualQty: null, perScuMargin: 20) };
        Assert.Equal(2000, AcceptedRouteMoney.ExpectedMargin(routes));
    }

    [Fact]
    public void MultipleLoadedRoutes_Sum()
    {
        var routes = new[]
        {
            Route(AcceptedStage.Loaded, tripQty: 100, actualQty: 100, perScuMargin: 10),
            Route(AcceptedStage.Loaded, tripQty: 200, actualQty: 200, perScuMargin: 5),
        };
        Assert.Equal(1000 + 1000, AcceptedRouteMoney.ExpectedMargin(routes));
    }

    // A Sold route has already left the active list by the time this runs in the real app, but the
    // fold itself must not double count it either, on principle: it already realised its margin.
    [Fact]
    public void SoldRoute_ContributesNothing()
    {
        var routes = new[] { Route(AcceptedStage.Sold, tripQty: 680, actualQty: 680, perScuMargin: 212) };
        Assert.Null(AcceptedRouteMoney.ExpectedMargin(routes));
    }

    [Fact]
    public void MixedStages_OnlyLoadedCounts()
    {
        var routes = new[]
        {
            Route(AcceptedStage.Accepted, tripQty: 50, actualQty: null, perScuMargin: 100),
            Route(AcceptedStage.Loaded, tripQty: 100, actualQty: 90, perScuMargin: 50),
            Route(AcceptedStage.Sold, tripQty: 60, actualQty: 60, perScuMargin: 30),
        };
        Assert.Equal(90 * 50, AcceptedRouteMoney.ExpectedMargin(routes));
    }
}
