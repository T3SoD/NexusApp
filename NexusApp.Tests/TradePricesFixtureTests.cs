using System.Linq;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

public class TradePricesFixtureTests
{
    [Fact]
    public void ParseTradePriceRows_FixtureFile_ParsesExpectedCountsAndSkipsTheBadRow()
    {
        var rows = MarketParse.ParseTradePriceRows(TradePricesFixture.LoadSampleJson(), out var skipped);

        Assert.Equal(27, rows.Count);
        Assert.Equal(1, skipped);
    }

    [Fact]
    public void ParseTradePriceRows_MalformedRow_NeverAppearsInResults()
    {
        var rows = MarketParse.ParseTradePriceRows(TradePricesFixture.LoadSampleJson(), out _);

        Assert.DoesNotContain(rows, r => r.TerminalName.Contains("Malformed Row"));
    }

    [Fact]
    public void ParseTradePriceRows_ZeroPriceRow_IsKeptNotDropped()
    {
        var rows = MarketParse.ParseTradePriceRows(TradePricesFixture.LoadSampleJson(), out _);

        var zero = rows.SingleOrDefault(r => r.CommodityName == "Synthetic Zero-Price Commodity");
        Assert.NotNull(zero);
        Assert.Equal(0, zero!.Buy);
        Assert.Equal(0, zero.Sell);
    }

    // Laranite (commodity id 47): a sell terminal (TDD Area 18, real capture 2026-07 UEX data)
    // and a buy terminal (HDMS-Lathan) with known real values, so Task 4's route math has a
    // real-data property test to run against, not just hand-picked numbers.
    [Fact]
    public void ParseTradePriceRows_LaraniteSellTerminal_MatchesKnownRealValues()
    {
        var rows = MarketParse.ParseTradePriceRows(TradePricesFixture.LoadSampleJson(), out _);

        var row = rows.Single(r => r.CommodityId == 47 && r.TerminalName == "TDD Area 18");
        Assert.Equal(0, row.Buy);
        Assert.Equal(8500, row.Sell);
        Assert.Equal(0, row.BuyStockScu);
        Assert.Equal(683, row.SellDemandScu);
        Assert.Equal(0, row.StatusBuy);
        Assert.Equal(3, row.StatusSell);
        Assert.Equal("1,2,4,8,16,24,32", row.ContainerSizes);
        Assert.Equal(new DateTime(2026, 7, 28, 8, 59, 27, DateTimeKind.Utc), row.ModifiedUtc);
    }

    [Fact]
    public void ParseTradePriceRows_LaraniteBuyTerminal_MatchesKnownRealValues()
    {
        var rows = MarketParse.ParseTradePriceRows(TradePricesFixture.LoadSampleJson(), out _);

        var row = rows.Single(r => r.CommodityId == 47 && r.TerminalName == "HDMS-Lathan");
        Assert.Equal(7541, row.Buy);
        Assert.Equal(0, row.Sell);
        Assert.Equal(1050, row.BuyStockScu);
        Assert.Equal(0, row.SellDemandScu);
        Assert.Equal(7, row.StatusBuy);
        Assert.Equal("1,2,4,8,16", row.ContainerSizes);
    }
}
