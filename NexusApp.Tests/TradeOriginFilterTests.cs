using System;
using System.Collections.Generic;
using NexusApp.Services;
using NexusApp.Views;
using Xunit;

namespace NexusApp.Tests;

// Task 4 of the trade-fix batch: the manual ORIGIN dropdown (TradePage.cs) must offer only
// terminals that actually have price data, not the raw /terminals list - live data is 823
// terminals -> 135 priced. Filtering keys off TerminalId, never TerminalName: the price row's
// TerminalName and Terminals.Name vocabularies differ for the same terminal (see
// TerminalNames_FiltersByTerminalId_NotByName below), so a name-based filter would silently
// drop or duplicate real terminals.
public class TradeOriginFilterTests
{
    private static MarketTerminal Terminal(int id, string name) =>
        new(id, name, "trading", false, "Stanton", "Somewhere");

    private static TradePriceRow PriceRow(int terminalId, string terminalName) =>
        new(terminalId, 1, 100, 200, 10, 10, 0, 0, "1,2,4", DateTime.UtcNow, terminalName, "Commodity");

    private static MarketSnapshot Snapshot(List<MarketTerminal> terminals, List<TradePriceRow> prices) =>
        new()
        {
            Terminals = new MarketDataset<MarketTerminal> { Rows = terminals },
            TradePrices = new MarketDataset<TradePriceRow> { Rows = prices },
        };

    [Fact]
    public void TerminalNames_TerminalWithPriceRow_Appears()
    {
        var terminals = new List<MarketTerminal> { Terminal(1, "Priced Terminal") };
        var prices = new List<TradePriceRow> { PriceRow(1, "Priced Terminal") };
        var snap = Snapshot(terminals, prices);

        var names = TradePage.TerminalNames(snap);

        Assert.Contains("Priced Terminal", names);
    }

    [Fact]
    public void TerminalNames_TerminalWithoutPriceRow_DoesNotAppear()
    {
        var terminals = new List<MarketTerminal>
        {
            Terminal(1, "Priced Terminal"),
            Terminal(2, "Unpriced Terminal"),
        };
        var prices = new List<TradePriceRow> { PriceRow(1, "Priced Terminal") };
        var snap = Snapshot(terminals, prices);

        var names = TradePage.TerminalNames(snap);

        Assert.DoesNotContain("Unpriced Terminal", names);
    }

    [Fact]
    public void TerminalNames_NullSnapshot_ReturnsEmpty()
    {
        Assert.Empty(TradePage.TerminalNames(null));
    }

    [Fact]
    public void TerminalNames_OrdersOrdinalIgnoreCaseAscending()
    {
        var terminals = new List<MarketTerminal>
        {
            Terminal(1, "zeta station"),
            Terminal(2, "Alpha Station"),
            Terminal(3, "beta Station"),
        };
        var prices = new List<TradePriceRow>
        {
            PriceRow(1, "zeta station"),
            PriceRow(2, "Alpha Station"),
            PriceRow(3, "beta Station"),
        };
        var snap = Snapshot(terminals, prices);

        var names = TradePage.TerminalNames(snap);

        Assert.Equal(new[] { "Alpha Station", "beta Station", "zeta station" }, names);
    }

    // Real UEX captures show the same terminal reported under different strings by the two
    // endpoints (id 18 is "CBD - Central Business District - Lorville" in /terminals but
    // "CBD Lorville" in /commodities_prices_all's terminal_name). The filter must still match
    // this terminal via TerminalId, never by comparing names.
    [Fact]
    public void TerminalNames_FiltersByTerminalId_NotByName()
    {
        var terminals = new List<MarketTerminal> { Terminal(18, "CBD - Central Business District - Lorville") };
        var prices = new List<TradePriceRow> { PriceRow(18, "CBD Lorville") };
        var snap = Snapshot(terminals, prices);

        var names = TradePage.TerminalNames(snap);

        Assert.Contains("CBD - Central Business District - Lorville", names);
    }
}
