using System;
using System.Collections.Generic;
using System.Linq;
using NexusApp.Models;
using NexusApp.Services;
using NexusApp.Views;
using Xunit;

namespace NexusApp.Tests;

// The pure decisions behind route pinning. Constructing a real TradePage needs a live App/window
// context - too heavy for a unit test (the same reasoning that moved PriceSort.SortRows out of that
// page) - so this covers the seams; the WPF wiring is smoke-tested instead.
//
// Pins became PERSISTED on 2026-08-01 (the owner: "the same as refinery orders"), which changed what the
// rules have to guarantee. The stale-pin DROP is gone and RefreshPins took its place; the reasoning
// is in RoutePlanner.RefreshPins and is asserted here.
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

    // ---- fixtures ----------------------------------------------------------------------------

    private static readonly DateTime T0 = new(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);

    private static TradePriceRow Row(int terminalId, int commodityId, double buy = 0, double sell = 0) =>
        new(terminalId, commodityId, buy, sell, 0, 0, buy > 0 ? 1 : 0, sell > 0 ? 1 : 0,
            "1,2,4,8,16,24,32", T0, $"Terminal {terminalId}", $"Commodity {commodityId}");

    private static TradeRoute Route(int buyTerminalId, int sellTerminalId, int commodityId,
                                    double net = 1000, int tripQty = 50, double sellPrice = 200) =>
        new(Row(buyTerminalId, commodityId, buy: 100), Row(sellTerminalId, commodityId, sell: sellPrice),
            TripQty: tripQty, Gross: net, Net: net, Tier: ProximityTier.SameSystem, TripParts: new[] { "test" });

    private static IReadOnlyList<PinnedRoute> Pin(params TradeRoute[] routes)
    {
        IReadOnlyList<PinnedRoute> pins = Array.Empty<PinnedRoute>();
        foreach (var r in routes) pins = RoutePlanner.TogglePin(pins, r, T0);
        return pins;
    }

    // ---- PinSurvivesRefresh / SameHaul --------------------------------------------------------

    [Fact]
    public void PinSurvivesRefresh_SameTripleWithChangedPrices_StillMatches()
    {
        // A fresh snapshot reprices the same haul. Identity is the triple, never the numbers.
        Assert.True(RoutePlanner.PinSurvivesRefresh(Route(1, 2, 47, net: 1000),
                                                    new List<TradeRoute> { Route(1, 2, 47, net: 4200) }));
    }

    [Fact]
    public void PinSurvivesRefresh_MissingTriple_DoesNotMatch()
        => Assert.False(RoutePlanner.PinSurvivesRefresh(Route(1, 2, 47), new List<TradeRoute> { Route(1, 3, 47) }));

    [Fact]
    public void PinSurvivesRefresh_DifferentCommodity_SameTerminals_DoesNotMatch()
        => Assert.False(RoutePlanner.PinSurvivesRefresh(Route(1, 2, 47), new List<TradeRoute> { Route(1, 2, 48) }));

    // ---- ToPin -------------------------------------------------------------------------------

    [Fact]
    public void ToPin_CapturesIdentityAndTheDisplayFacts()
    {
        var pin = RoutePlanner.ToPin(Route(1, 2, 47, tripQty: 64, sellPrice: 250), T0);

        Assert.Equal(1, pin.BuyTerminalId);
        Assert.Equal(2, pin.SellTerminalId);
        Assert.Equal(47, pin.CommodityId);
        Assert.Equal("Commodity 47", pin.CommodityName);
        Assert.Equal("Terminal 1", pin.BuyTerminalName);
        Assert.Equal("Terminal 2", pin.SellTerminalName);
        Assert.Equal(64, pin.TripQty);
        Assert.Equal(150, pin.PerScuMargin);   // sell 250 - buy 100
        Assert.Equal(T0, pin.PinnedUtc);
        Assert.Equal(T0, pin.UpdatedUtc);
    }

    // ---- TogglePin ---------------------------------------------------------------------------

    [Fact]
    public void TogglePin_AppendsToTheEnd_SoTheOverlayListsPinsInPinOrder()
    {
        var pins = Pin(Route(1, 2, 47), Route(3, 4, 48));

        Assert.Equal(2, pins.Count);
        Assert.Equal(1, pins[0].BuyTerminalId);
        Assert.Equal(3, pins[1].BuyTerminalId);
    }

    [Fact]
    public void TogglePin_SameHaulAgain_Unpins()
    {
        var route = Route(1, 2, 47);
        Assert.Empty(RoutePlanner.TogglePin(Pin(route), route, T0));
    }

    [Fact]
    public void TogglePin_RepricedSameHaul_Unpins_RatherThanStackingADuplicate()
    {
        // A rebuild hands the chip a brand new TradeRoute for the same haul. If identity were object
        // identity, clicking PIN after any market tick would stack a second card for one run.
        Assert.Empty(RoutePlanner.TogglePin(Pin(Route(1, 2, 47, net: 1000)), Route(1, 2, 47, net: 9999), T0));
    }

    [Fact]
    public void TogglePin_AtTheCap_DropsTheOldest_AndKeepsTheNewPin()
    {
        IReadOnlyList<PinnedRoute> pins = Array.Empty<PinnedRoute>();
        for (int i = 0; i < RoutePlanner.MaxPins; i++)
            pins = RoutePlanner.TogglePin(pins, Route(i + 1, 100, 47), T0);
        var oldest = pins[0];

        pins = RoutePlanner.TogglePin(pins, Route(999, 100, 47), T0);

        Assert.Equal(RoutePlanner.MaxPins, pins.Count);
        Assert.DoesNotContain(pins, p => p.SameHaulAs(oldest));
        Assert.Equal(999, pins[^1].BuyTerminalId);   // the click always pins; it never silently refuses
    }

    [Fact]
    public void TogglePin_NeverMutatesTheListItWasGiven()
    {
        var pins = Pin(Route(1, 2, 47));
        RoutePlanner.TogglePin(pins, Route(3, 4, 48), T0);
        Assert.Single(pins);
    }

    // ---- RefreshPins -------------------------------------------------------------------------
    // The rule that had to change when pins started outliving their session.

    [Fact]
    public void RefreshPins_UpdatesTheDisplayFactsOfAPinTheRankingContains()
    {
        var pins = Pin(Route(1, 2, 47, tripQty: 50, sellPrice: 200));
        var later = T0.AddHours(3);

        var fresh = new List<TradeRoute> { Route(1, 2, 47, tripQty: 96, sellPrice: 350) };
        var refreshed = RoutePlanner.RefreshPins(pins, fresh, later);

        Assert.Equal(96, refreshed[0].TripQty);
        Assert.Equal(250, refreshed[0].PerScuMargin);
        Assert.Equal(later, refreshed[0].UpdatedUtc);
    }

    // THE behavioural reversal. Under the old stale-pin rule this pin was deleted. A ranking is the
    // best 25 routes for the ship, budget and scope selected right now - so dropping on absence
    // would have erased a user's pins the first time they switched ships after a restart.
    [Fact]
    public void RefreshPins_KeepsAPinTheRankingDoesNotContain()
    {
        var pins = Pin(Route(1, 2, 47));

        var refreshed = RoutePlanner.RefreshPins(pins, new List<TradeRoute> { Route(9, 9, 99) }, T0.AddHours(1));

        Assert.Single(refreshed);
        Assert.Equal(47, refreshed[0].CommodityId);
    }

    [Fact]
    public void RefreshPins_AnUnmatchedPinKeepsItsOldFiguresAndItsOldTimestamp()
    {
        // Its UpdatedUtc must NOT move: that stamp is what lets the card say how old its margin is,
        // and bumping it on a refresh that found nothing would make stale numbers look current.
        var pins = Pin(Route(1, 2, 47, tripQty: 50, sellPrice: 200));

        var refreshed = RoutePlanner.RefreshPins(pins, new List<TradeRoute>(), T0.AddDays(2));

        Assert.Equal(50, refreshed[0].TripQty);
        Assert.Equal(100, refreshed[0].PerScuMargin);
        Assert.Equal(T0, refreshed[0].UpdatedUtc);
    }

    [Fact]
    public void RefreshPins_EmptyRanking_KeepsEveryPin()
    {
        // Market data off, or no snapshot yet. Neither is evidence that a route stopped existing.
        var pins = Pin(Route(1, 2, 47), Route(3, 4, 48));
        Assert.Equal(2, RoutePlanner.RefreshPins(pins, Array.Empty<TradeRoute>(), T0).Count);
    }

    [Fact]
    public void RefreshPins_NeverMovesPinnedUtc_EvenWhenItRefreshes()
    {
        var pins = Pin(Route(1, 2, 47));
        var refreshed = RoutePlanner.RefreshPins(pins, new List<TradeRoute> { Route(1, 2, 47) }, T0.AddDays(5));
        Assert.Equal(T0, refreshed[0].PinnedUtc);
    }

    [Fact]
    public void RefreshPins_PreservesPinOrder_MatchedAndUnmatchedAlike()
    {
        var pins = Pin(Route(1, 2, 47), Route(3, 4, 48), Route(5, 6, 49));

        var refreshed = RoutePlanner.RefreshPins(pins, new List<TradeRoute> { Route(3, 4, 48) }, T0.AddHours(1));

        Assert.Equal(new[] { 47, 48, 49 }, refreshed.Select(p => p.CommodityId).ToArray());
    }

    [Fact]
    public void RefreshPins_TakesNamesFromTheFreshRow()
    {
        // A terminal or commodity renamed upstream should reach a pinned card rather than leave it
        // quoting a name that no longer exists anywhere.
        var pins = Pin(Route(1, 2, 47));
        var renamed = new TradeRoute(
            new TradePriceRow(1, 47, 100, 0, 0, 0, 1, 0, "1,2", T0, "Renamed Buy", "Renamed Commodity"),
            new TradePriceRow(2, 47, 0, 200, 0, 0, 0, 1, "1,2", T0, "Renamed Sell", "Renamed Commodity"),
            50, 1000, 1000, ProximityTier.SameSystem, new[] { "test" });

        var refreshed = RoutePlanner.RefreshPins(pins, new List<TradeRoute> { renamed }, T0.AddHours(1));

        Assert.Equal("Renamed Commodity", refreshed[0].CommodityName);
        Assert.Equal("Renamed Buy", refreshed[0].BuyTerminalName);
        Assert.Equal("Renamed Sell", refreshed[0].SellTerminalName);
    }

    [Fact]
    public void RefreshPins_NeverMutatesTheListItWasGiven()
    {
        var pins = Pin(Route(1, 2, 47, tripQty: 50));
        RoutePlanner.RefreshPins(pins, new List<TradeRoute> { Route(1, 2, 47, tripQty: 999) }, T0.AddHours(1));
        Assert.Equal(50, pins[0].TripQty);
    }

    // ---- SameHaulAs (the persisted form's own comparison) -------------------------------------

    [Fact]
    public void SameHaulAs_MatchesOnTheTripleOnly()
    {
        var a = RoutePlanner.ToPin(Route(1, 2, 47, tripQty: 10, sellPrice: 150), T0);
        var b = RoutePlanner.ToPin(Route(1, 2, 47, tripQty: 999, sellPrice: 900), T0.AddDays(9));
        Assert.True(a.SameHaulAs(b));
    }

    [Theory]
    [InlineData(9, 2, 47)]
    [InlineData(1, 9, 47)]
    [InlineData(1, 2, 99)]
    public void SameHaulAs_AnyDifferingLeg_IsADifferentHaul(int buy, int sell, int commodity)
    {
        var a = RoutePlanner.ToPin(Route(1, 2, 47), T0);
        var b = RoutePlanner.ToPin(Route(buy, sell, commodity), T0);
        Assert.False(a.SameHaulAs(b));
    }
}
