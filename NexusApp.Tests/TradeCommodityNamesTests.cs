using System;
using System.Collections.Generic;
using NexusApp.Services;
using NexusApp.Views;
using Xunit;

namespace NexusApp.Tests;

// The shared commodity-name derivation behind the planner COMMODITY filter and the Prices flow's
// picker (issue #41): one helper (TradePage.CommodityNames) so the dedup and ordering rules cannot
// drift between the two call sites. Pins the TerminalNames-style contract: null snapshot = empty,
// distinct names only, OrdinalIgnoreCase ascending order.
public class TradeCommodityNamesTests
{
    private static TradePriceRow PriceRow(int terminalId, int commodityId, string commodityName) =>
        new(terminalId, commodityId, 100, 200, 10, 10, 0, 0, "1,2,4", DateTime.UtcNow, $"Terminal {terminalId}", commodityName);

    private static MarketSnapshot Snapshot(List<TradePriceRow> prices) =>
        new()
        {
            Terminals = new MarketDataset<MarketTerminal> { Rows = new List<MarketTerminal>() },
            TradePrices = new MarketDataset<TradePriceRow> { Rows = prices },
        };

    [Fact]
    public void CommodityNames_NullSnapshot_ReturnsEmpty()
    {
        Assert.Empty(TradePage.CommodityNames(null));
    }

    [Fact]
    public void CommodityNames_DeduplicatesAcrossTerminals()
    {
        var snap = Snapshot(new List<TradePriceRow>
        {
            PriceRow(1, 10, "Gold"),
            PriceRow(2, 10, "Gold"),
            PriceRow(3, 20, "Laranite"),
        });

        var names = TradePage.CommodityNames(snap);

        Assert.Equal(new List<string> { "Gold", "Laranite" }, names);
    }

    [Fact]
    public void CommodityNames_OrdersOrdinalIgnoreCaseAscending()
    {
        var snap = Snapshot(new List<TradePriceRow>
        {
            PriceRow(1, 10, "widow"),
            PriceRow(1, 20, "Agricium"),
            PriceRow(1, 30, "Laranite"),
        });

        var names = TradePage.CommodityNames(snap);

        Assert.Equal(new List<string> { "Agricium", "Laranite", "widow" }, names);
    }
}
