using System;
using System.Collections.Generic;
using NexusApp.Services;
using NexusApp.Views;
using Xunit;

namespace NexusApp.Tests;

// Task 8 (starmap MAP tab -> TradePage integration): the two pure decisions
// PrefillPlannerOriginFromMap and RebuildPlanner's stale-pin rule reduce to, extracted so they are
// unit tested directly rather than only through the WPF page that calls them. Constructing a real
// TradePage needs a live App/window context - too heavy for a unit test (the same reasoning that
// already moved PriceSort.SortRows out of this page, TradePage.Prices.cs:16-18), so this file only
// covers the pure seams; the rest of the wiring (PinRoute toggling, PrefillPlannerOriginFromMap's
// side effects, ShowPricesForTerminal) is self-reviewed and smoke-tested instead.
public class TradePinnedRouteTests
{
    // ---- OriginNameForTerminal --------------------------------------------------------------

    private static readonly List<MarketTerminal> Terminals = new()
    {
        new(1, "CRU-L4 Shallow Fields", "trading", false, "Stanton", "Crusader"),
        new(2, "Everus Harbor", "trading", true, "Stanton", "Hurston"),
    };

    [Fact]
    public void OriginNameForTerminal_ResolvesTheTerminalsName()
        => Assert.Equal("Everus Harbor", TradeOriginResolver.OriginNameForTerminal(2, Terminals));

    [Fact]
    public void OriginNameForTerminal_UnknownId_ReturnsNull()
        => Assert.Null(TradeOriginResolver.OriginNameForTerminal(999, Terminals));

    // ---- PinSurvivesRefresh (the stale-pin rule) ---------------------------------------------

    private static TradePriceRow Row(int terminalId, int commodityId, double buy = 0, double sell = 0) =>
        new(terminalId, commodityId, buy, sell, 0, 0, buy > 0 ? 1 : 0, sell > 0 ? 1 : 0,
            "1,2,4,8,16,24,32", DateTime.UtcNow, $"Terminal {terminalId}", $"Commodity {commodityId}");

    private static TradeRoute Route(int buyTerminalId, int sellTerminalId, int commodityId, double net = 1000) =>
        new(Row(buyTerminalId, commodityId, buy: 100), Row(sellTerminalId, commodityId, sell: 200),
            TripQty: 50, Gross: net, Net: net, Tier: ProximityTier.SameSystem, TripParts: new[] { "test" });

    [Fact]
    public void PinSurvivesRefresh_SameTriple_Survives()
    {
        var pinned = Route(1, 2, 47);
        var fresh = new List<TradeRoute> { Route(1, 2, 47) };

        Assert.True(RoutePlanner.PinSurvivesRefresh(pinned, fresh));
    }

    [Fact]
    public void PinSurvivesRefresh_SameTripleWithChangedPrices_StillSurvives()
    {
        var pinned = Route(1, 2, 47, net: 1000);
        var fresh = new List<TradeRoute> { Route(1, 2, 47, net: 4200) };   // a fresh snapshot repriced the same route

        Assert.True(RoutePlanner.PinSurvivesRefresh(pinned, fresh));
    }

    [Fact]
    public void PinSurvivesRefresh_MissingTriple_DoesNotSurvive()
    {
        var pinned = Route(1, 2, 47);
        var fresh = new List<TradeRoute> { Route(1, 3, 47) };   // sell leg moved to a different terminal

        Assert.False(RoutePlanner.PinSurvivesRefresh(pinned, fresh));
    }

    [Fact]
    public void PinSurvivesRefresh_DifferentCommodity_SameTerminals_DoesNotSurvive()
    {
        var pinned = Route(1, 2, 47);
        var fresh = new List<TradeRoute> { Route(1, 2, 48) };

        Assert.False(RoutePlanner.PinSurvivesRefresh(pinned, fresh));
    }

    [Fact]
    public void PinSurvivesRefresh_EmptyFreshList_DoesNotSurvive()
    {
        var pinned = Route(1, 2, 47);

        Assert.False(RoutePlanner.PinSurvivesRefresh(pinned, new List<TradeRoute>()));
    }
}
