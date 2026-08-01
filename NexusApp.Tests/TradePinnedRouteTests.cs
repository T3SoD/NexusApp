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

    // ---- TogglePin (multi-pin, 2026-08-01) ---------------------------------------------------
    // the owner asked to pin several routes at once and close them individually from the overlay. The
    // list's ORDER is load-bearing (the overlay lists cards in it, and the cap drops the oldest),
    // so these assert order, not just membership.

    [Fact]
    public void TogglePin_AppendsToTheEnd_SoTheOverlayListsPinsInPinOrder()
    {
        var first = Route(1, 2, 47);
        var second = Route(3, 4, 48);

        var pins = RoutePlanner.TogglePin(RoutePlanner.TogglePin(Array.Empty<TradeRoute>(), first), second);

        Assert.Equal(2, pins.Count);
        Assert.True(RoutePlanner.SameHaul(first, pins[0]));
        Assert.True(RoutePlanner.SameHaul(second, pins[1]));
    }

    [Fact]
    public void TogglePin_SameHaulAgain_Unpins()
    {
        var route = Route(1, 2, 47);
        var pins = RoutePlanner.TogglePin(Array.Empty<TradeRoute>(), route);

        Assert.Empty(RoutePlanner.TogglePin(pins, route));
    }

    [Fact]
    public void TogglePin_RepricedSameHaul_Unpins_RatherThanPinningADuplicate()
    {
        // A rebuild hands the chip a brand new TradeRoute instance for the same haul. If identity
        // were the rule, clicking PIN on an already-pinned row after any market tick would stack a
        // second card for the same run instead of unpinning it.
        var pins = RoutePlanner.TogglePin(Array.Empty<TradeRoute>(), Route(1, 2, 47, net: 1000));

        Assert.Empty(RoutePlanner.TogglePin(pins, Route(1, 2, 47, net: 9999)));
    }

    [Fact]
    public void TogglePin_AtTheCap_DropsTheOldest_AndKeepsTheNewPin()
    {
        IReadOnlyList<TradeRoute> pins = Array.Empty<TradeRoute>();
        for (int i = 0; i < RoutePlanner.MaxPins; i++)
            pins = RoutePlanner.TogglePin(pins, Route(i + 1, 100, 47));
        var oldest = pins[0];

        pins = RoutePlanner.TogglePin(pins, Route(999, 100, 47));

        Assert.Equal(RoutePlanner.MaxPins, pins.Count);
        Assert.DoesNotContain(pins, p => RoutePlanner.SameHaul(p, oldest));
        Assert.Equal(999, pins[^1].BuyRow.TerminalId);   // the click always pins, it never silently refuses
    }

    [Fact]
    public void TogglePin_NeverMutatesTheListItWasGiven()
    {
        var pins = RoutePlanner.TogglePin(Array.Empty<TradeRoute>(), Route(1, 2, 47));

        RoutePlanner.TogglePin(pins, Route(3, 4, 48));

        Assert.Single(pins);
    }

    // ---- SurvivingPins -----------------------------------------------------------------------

    [Fact]
    public void SurvivingPins_KeepsSurvivorsInOrder_AndDropsOnlyTheStaleOne()
    {
        var keepA = Route(1, 2, 47);
        var stale = Route(5, 6, 47);
        var keepB = Route(3, 4, 48);
        var pinned = new[] { keepA, stale, keepB };
        var fresh = new List<TradeRoute> { Route(3, 4, 48), Route(1, 2, 47) };   // fresh ranking reordered

        var survivors = RoutePlanner.SurvivingPins(pinned, fresh);

        Assert.Equal(2, survivors.Count);
        Assert.True(RoutePlanner.SameHaul(keepA, survivors[0]));
        Assert.True(RoutePlanner.SameHaul(keepB, survivors[1]));
    }

    [Fact]
    public void SurvivingPins_EmptyRanking_DropsEverything()
        => Assert.Empty(RoutePlanner.SurvivingPins(new[] { Route(1, 2, 47) }, Array.Empty<TradeRoute>()));
}
