using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using NexusApp.Models;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Sell-only pins (2026-08-01): the Sell tab's results carry their own PIN TO OVERLAY button that
// behaves exactly like the planner's. A sell pin has NO buy leg - identity is
// (null, sell terminal, commodity) - and shares the planner pins' single list and five-pin cap.
// These pin the pure rules; the WPF wiring (the Sell tab chip, the overlay card variant) follows
// the same untestable-window reasoning as TradePinnedRouteTests.
public class TradeSellPinTests
{
    private static readonly DateTime T0 = new(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);

    private static TradePriceRow Row(int terminalId, int commodityId, double sell = 200) =>
        new(terminalId, commodityId, 0, sell, 0, 0, 0, sell > 0 ? 1 : 0,
            "1,2,4,8,16,24,32", T0, $"Terminal {terminalId}", $"Commodity {commodityId}");

    private static TradeRoute Route(int buyTerminalId, int sellTerminalId, int commodityId) =>
        new(new TradePriceRow(buyTerminalId, commodityId, 100, 0, 0, 0, 1, 0, "1", T0, $"Terminal {buyTerminalId}", $"Commodity {commodityId}"),
            Row(sellTerminalId, commodityId),
            TripQty: 50, Gross: 1000, Net: 1000, Tier: ProximityTier.SameSystem, TripParts: new[] { "test" });

    // ---- ToSellPin -----------------------------------------------------------------------------

    [Fact]
    public void ToSellPin_HasNoBuyLeg_AndCarriesThePriceAndQty()
    {
        var pin = RoutePlanner.ToSellPin(Row(7, 3, sell: 421), qty: 96, T0);
        Assert.Null(pin.BuyTerminalId);
        Assert.Equal(7, pin.SellTerminalId);
        Assert.Equal(3, pin.CommodityId);
        Assert.Equal("Terminal 7", pin.SellTerminalName);
        Assert.Equal("", pin.BuyTerminalName);
        Assert.Equal(96, pin.TripQty);
        Assert.Equal(421, pin.PerScuMargin);   // the SELL PRICE - there is no buy side to subtract
        Assert.Equal(T0, pin.PinnedUtc);
    }

    // ---- ToggleSellPin -------------------------------------------------------------------------

    [Fact]
    public void ToggleSellPin_SecondClickUnpins()
    {
        var once = RoutePlanner.ToggleSellPin(Array.Empty<PinnedRoute>(), Row(7, 3), 50, T0);
        var twice = RoutePlanner.ToggleSellPin(once, Row(7, 3), 50, T0);
        Assert.Single(once);
        Assert.Empty(twice);
    }

    [Fact]
    public void ToggleSellPin_SharesTheCapWithPlannerPins_OldestDrops()
    {
        IReadOnlyList<PinnedRoute> pins = Array.Empty<PinnedRoute>();
        for (int i = 1; i <= RoutePlanner.MaxPins; i++)
            pins = RoutePlanner.TogglePin(pins, Route(i, i + 100, i), T0);

        pins = RoutePlanner.ToggleSellPin(pins, Row(7, 3), 50, T0);

        Assert.Equal(RoutePlanner.MaxPins, pins.Count);
        Assert.Null(pins[^1].BuyTerminalId);            // the sell pin landed
        Assert.Equal(2, pins[0].BuyTerminalId);         // planner pin 1 (the oldest) was evicted
    }

    [Fact]
    public void SellPin_AndPlannerPin_AtTheSameTerminalAndCommodity_Coexist()
    {
        // A planner route that sells commodity 3 at terminal 7, and a sell pin for the same pair:
        // different pins (one carries a buy leg the other says nothing about), so neither click
        // toggles the other off.
        var pins = RoutePlanner.TogglePin(Array.Empty<PinnedRoute>(), Route(1, 7, 3), T0);
        pins = RoutePlanner.ToggleSellPin(pins, Row(7, 3), 50, T0);
        Assert.Equal(2, pins.Count);
    }

    // ---- refresh isolation ----------------------------------------------------------------------

    [Fact]
    public void RefreshPins_LeavesSellPinsUntouched()
    {
        // The planner's RefreshPins runs on every rebuild with planner routes; a sell pin must
        // pass through byte-for-byte (SameHaul's null buy comparison is what guarantees it).
        var sell = RoutePlanner.ToSellPin(Row(7, 3, sell: 421), 96, T0);
        var fresh = new List<TradeRoute> { Route(1, 7, 3) };
        var result = RoutePlanner.RefreshPins(new[] { sell }, fresh, T0.AddHours(1));
        Assert.Same(sell, result.Single());
    }

    [Fact]
    public void RefreshSellPins_UpdatesPriceAndNames_KeepsQtyAndPinnedUtc()
    {
        var pin = RoutePlanner.ToSellPin(Row(7, 3, sell: 421), 96, T0);
        var fresh = new List<TradePriceRow> { Row(7, 3, sell: 500) };
        var updated = RoutePlanner.RefreshSellPins(new[] { pin }, fresh, T0.AddHours(2)).Single();

        Assert.Equal(500, updated.PerScuMargin);
        Assert.Equal(96, updated.TripQty);              // the user's own cargo entry: never refreshed
        Assert.Equal(T0, updated.PinnedUtc);            // never moves
        Assert.Equal(T0.AddHours(2), updated.UpdatedUtc);
        Assert.Null(updated.BuyTerminalId);
    }

    [Fact]
    public void RefreshSellPins_LeavesPlannerPins_AndOtherCommodities_Alone()
    {
        var planner = RoutePlanner.ToPin(Route(1, 7, 3), T0);
        var otherSell = RoutePlanner.ToSellPin(Row(9, 4, sell: 50), 10, T0);
        var fresh = new List<TradePriceRow> { Row(7, 3, sell: 500) };

        var result = RoutePlanner.RefreshSellPins(new[] { planner, otherSell }, fresh, T0.AddHours(1));

        Assert.Same(planner, result[0]);
        Assert.Same(otherSell, result[1]);
    }

    // ---- persistence round-trip -----------------------------------------------------------------

    [Fact]
    public void SellPin_JsonRoundTrip_KeepsTheNullBuyLeg()
    {
        var pin = RoutePlanner.ToSellPin(Row(7, 3, sell: 421), 96, T0);
        var back = JsonSerializer.Deserialize<PinnedRoute>(JsonSerializer.Serialize(pin))!;
        Assert.Null(back.BuyTerminalId);
        Assert.True(back.SameHaulAs(pin));
    }

    [Fact]
    public void PlannerPin_JsonWrittenBeforeNullableBuyLeg_StillDeserializes()
    {
        // Pins persisted before BuyTerminalId became nullable carry a plain number; they must load
        // unchanged (the ship-switch survival that motivated persistence depends on it).
        const string legacy = "{\"BuyTerminalId\":1,\"SellTerminalId\":7,\"CommodityId\":3," +
            "\"CommodityName\":\"Titanium\",\"BuyTerminalName\":\"A\",\"SellTerminalName\":\"B\"," +
            "\"TripQty\":50,\"PerScuMargin\":12.5," +
            "\"UpdatedUtc\":\"2026-08-01T12:00:00Z\",\"PinnedUtc\":\"2026-08-01T12:00:00Z\"}";
        var pin = JsonSerializer.Deserialize<PinnedRoute>(legacy)!;
        Assert.Equal(1, pin.BuyTerminalId);
        Assert.Equal(7, pin.SellTerminalId);
    }
}
