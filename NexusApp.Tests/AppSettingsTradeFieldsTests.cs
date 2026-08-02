using System;
using System.Collections.Generic;
using System.Text.Json;
using NexusApp.Models;
using Xunit;

namespace NexusApp.Tests;

public class AppSettingsTradeFieldsTests
{
    [Fact]
    public void TradeFields_Defaults_MatchTheTradingTabSpec()
    {
        var s = new AppSettings();

        Assert.Equal("planner", s.TradeActiveFlow);
        Assert.Equal("", s.TradeShipId);
        Assert.Equal("", s.TradeOriginManual);
        Assert.Equal("LIVE", s.TradeStartManual);   // task 10: preserves the old FROM HERE default behavior
        Assert.Null(s.TradeCommodityFilter);        // issue #41: null = ANY, no commodity constraint
        Assert.Equal("ALL", s.TradeScope);
        Assert.True(s.TradeAnchorFromHere);          // dormant since task 10, kept for compatibility
        Assert.False(s.SctDataEnabled);
        Assert.Empty(s.PinnedRoutes);   // pinning is opt-in; a fresh profile has none
    }

    // Pinned routes became PERSISTED on 2026-08-01 (the owner: "the same as refinery orders"). The whole
    // point is that they survive a restart, and that is a serialization question - nothing else in
    // the suite would notice if PinnedRoute stopped round-tripping. Serialized with the same options
    // SettingsService uses.
    [Fact]
    public void PinnedRoutes_RoundTripThroughSettingsJson()
    {
        var pinnedAt = new DateTime(2026, 8, 1, 9, 30, 0, DateTimeKind.Utc);
        var settings = new AppSettings
        {
            PinnedRoutes = new List<PinnedRoute>
            {
                new()
                {
                    BuyTerminalId = 42, SellTerminalId = 77, CommodityId = 13,
                    CommodityName = "Titanium", BuyTerminalName = "ARC-L1 Wide Forest Station",
                    SellTerminalName = "Everus Harbor", TripQty = 96, PerScuMargin = 214.5,
                    PinnedUtc = pinnedAt, UpdatedUtc = pinnedAt.AddHours(2),
                },
            },
        };

        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        var back = JsonSerializer.Deserialize<AppSettings>(json)!;

        var pin = Assert.Single(back.PinnedRoutes);
        Assert.Equal(42, pin.BuyTerminalId);
        Assert.Equal(77, pin.SellTerminalId);
        Assert.Equal(13, pin.CommodityId);
        Assert.Equal("Titanium", pin.CommodityName);
        Assert.Equal("ARC-L1 Wide Forest Station", pin.BuyTerminalName);
        Assert.Equal("Everus Harbor", pin.SellTerminalName);
        Assert.Equal(96, pin.TripQty);
        Assert.Equal(214.5, pin.PerScuMargin);
        Assert.Equal(pinnedAt, pin.PinnedUtc);
        Assert.Equal(pinnedAt.AddHours(2), pin.UpdatedUtc);
    }

    [Fact]
    public void PinnedRoutes_OrderIsPreserved_BecauseTheCapDropsTheOldest()
    {
        // Pin order is load-bearing in two places: the overlay lists cards in it, and TogglePin
        // drops index 0 at the cap. A serializer that reordered would silently evict the wrong pin.
        var settings = new AppSettings
        {
            PinnedRoutes = new List<PinnedRoute>
            {
                new() { BuyTerminalId = 1, CommodityId = 1 },
                new() { BuyTerminalId = 2, CommodityId = 2 },
                new() { BuyTerminalId = 3, CommodityId = 3 },
            },
        };

        var back = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;

        // int?[] since BuyTerminalId went nullable for sell-only pins (2026-08-01).
        Assert.Equal(new int?[] { 1, 2, 3 }, back.PinnedRoutes.ConvertAll(p => p.BuyTerminalId).ToArray());
    }

    [Fact]
    public void SettingsWrittenBeforePinsPersisted_LoadWithNoPins_RatherThanNull()
    {
        // Every existing settings.json predates the field. It must land on an empty list, not null,
        // or the first read on an upgraded profile throws.
        var back = JsonSerializer.Deserialize<AppSettings>("{\"TradeScope\":\"ALL\"}")!;
        Assert.NotNull(back.PinnedRoutes);
        Assert.Empty(back.PinnedRoutes);
    }
}
