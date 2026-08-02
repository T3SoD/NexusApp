using System.Collections.Generic;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// TradePlanArgs: the setting-interpretation seams shared by the main planner and the overlay
// planner (overlay spec 2026-08-02). Extracted from TradePage.Planner.cs so the two surfaces can
// never interpret TradeRankMode/TradeStockFilter/TradeDestManual differently. These pin the exact
// pre-extraction behavior, including the fail-open fallbacks for corrupt settings.
public class TradePlanArgsTests
{
    private static MarketTerminal Terminal(int id, string name) =>
        new(id, name, "trading", false, "Stanton", "Somewhere");

    [Theory]
    [InlineData("PROFIT PER SCU", RankMode.ProfitPerScu)]
    [InlineData("PROFIT PER GM", RankMode.ProfitPerGm)]
    [InlineData("PROFIT", RankMode.Profit)]
    [InlineData(null, RankMode.Profit)]
    [InlineData("garbage", RankMode.Profit)]
    public void ParseRankMode_MapsLabelsAndFailsOpenToProfit(string? value, RankMode expected)
    {
        Assert.Equal(expected, TradePlanArgs.ParseRankMode(value));
    }

    [Theory]
    [InlineData("MIN", StockFilter.CoversTrip)]
    [InlineData("COVERS TRIP", StockFilter.CoversTrip)]
    [InlineData("2X", StockFilter.CoversTwoTrips)]
    [InlineData("COVERS 2X", StockFilter.CoversTwoTrips)]
    [InlineData(null, StockFilter.Any)]
    [InlineData("garbage", StockFilter.Any)]
    public void ParseDemandFilter_MapsLabelsAndFailsOpenToAny(string? value, StockFilter expected)
    {
        Assert.Equal(expected, TradePlanArgs.ParseDemandFilter(value));
    }

    [Fact]
    public void DestTerminalIds_NullEmptyOrAny_MeansUnconstrained()
    {
        var terminals = new List<MarketTerminal> { Terminal(1, "TDD Area 18") };
        Assert.Null(TradePlanArgs.DestTerminalIds(null, terminals));
        Assert.Null(TradePlanArgs.DestTerminalIds("", terminals));
        Assert.Null(TradePlanArgs.DestTerminalIds("ANY", terminals));
    }

    [Fact]
    public void DestTerminalIds_ResolvedName_YieldsThatId()
    {
        var terminals = new List<MarketTerminal> { Terminal(7, "TDD Area 18") };
        var ids = TradePlanArgs.DestTerminalIds("TDD Area 18", terminals);
        Assert.NotNull(ids);
        Assert.Equal(new[] { 7 }, ids!);
    }

    [Fact]
    public void DestTerminalIds_UnresolvedName_YieldsEmptyNotNull()
    {
        // The no-silent-widening contract: an unresolved constraint restricts to nothing.
        var terminals = new List<MarketTerminal> { Terminal(7, "TDD Area 18") };
        var ids = TradePlanArgs.DestTerminalIds("Gone Terminal", terminals);
        Assert.NotNull(ids);
        Assert.Empty(ids!);
    }
}
