using System;
using System.Linq;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Prices flow: sortable columns (owner live-pass ask, 2026-07-30). PriceSort.SortRows is the pure
// extraction TradePage.Prices.cs's RebuildPrices calls once per rebuild; these tests pin the five
// column keys, the status-0/SCT-only "sorts last" placement rule, and the Sell-descending stable
// tie-break, all independent of any WPF control.
public class PriceSortTests
{
    private static readonly DateTime Now = new(2026, 7, 30, 12, 0, 0, DateTimeKind.Utc);

    private static PriceRowItem UexRow(double sell, double buy, int stock, int status, DateTime modified, string name = "T")
        => new(sell, new TradePriceRow(1, 1, buy, sell, stock, 0, status, 0, "", modified, name, "Commodity"), null);

    private static PriceRowItem SctRow(double price, DateTime timestamp, string location = "S")
        => new(price, null, new SctListing(location, "BUYS", "Commodity", price, 0, 0, timestamp));

    [Fact]
    public void SortRows_Sell_Descending_HighestFirst()
    {
        var rows = new[] { UexRow(10, 1, 1, 1, Now), UexRow(30, 1, 1, 1, Now), SctRow(20, Now) };
        var sorted = PriceSort.SortRows(rows, PriceSortColumn.Sell, descending: true);
        Assert.Equal(new[] { 30.0, 20.0, 10.0 }, sorted.Select(r => r.SellValue));
    }

    [Fact]
    public void SortRows_Sell_Ascending_LowestFirst()
    {
        var rows = new[] { UexRow(10, 1, 1, 1, Now), UexRow(30, 1, 1, 1, Now), SctRow(20, Now) };
        var sorted = PriceSort.SortRows(rows, PriceSortColumn.Sell, descending: false);
        Assert.Equal(new[] { 10.0, 20.0, 30.0 }, sorted.Select(r => r.SellValue));
    }

    [Fact]
    public void SortRows_Buy_Descending_SctOnlyRowsSortLast()
    {
        var rows = new[] { UexRow(5, 10, 1, 1, Now), SctRow(999, Now), UexRow(5, 50, 1, 1, Now) };
        var sorted = PriceSort.SortRows(rows, PriceSortColumn.Buy, descending: true);
        Assert.Equal(50, sorted[0].Uex!.Buy);
        Assert.Equal(10, sorted[1].Uex!.Buy);
        Assert.Null(sorted[2].Uex);
    }

    [Fact]
    public void SortRows_Buy_Ascending_SctOnlyRowsStillSortLast()
    {
        // Ascending would naively put a "missing" value first; the placement rule keeps SCT-only
        // rows last regardless of which direction is active.
        var rows = new[] { UexRow(5, 10, 1, 1, Now), SctRow(999, Now), UexRow(5, 50, 1, 1, Now) };
        var sorted = PriceSort.SortRows(rows, PriceSortColumn.Buy, descending: false);
        Assert.Equal(10, sorted[0].Uex!.Buy);
        Assert.Equal(50, sorted[1].Uex!.Buy);
        Assert.Null(sorted[2].Uex);
    }

    [Fact]
    public void SortRows_Stock_SctOnlyRowsSortLast_BothDirections()
    {
        var rows = new[] { UexRow(1, 1, 40, 1, Now), SctRow(1, Now), UexRow(1, 1, 10, 1, Now) };

        var desc = PriceSort.SortRows(rows, PriceSortColumn.Stock, descending: true);
        Assert.Equal(new[] { 40, 10 }, desc.Take(2).Select(r => r.Uex!.BuyStockScu));
        Assert.Null(desc[2].Uex);

        var asc = PriceSort.SortRows(rows, PriceSortColumn.Stock, descending: false);
        Assert.Equal(new[] { 10, 40 }, asc.Take(2).Select(r => r.Uex!.BuyStockScu));
        Assert.Null(asc[2].Uex);
    }

    [Fact]
    public void SortRows_Status_ZeroCodeSortsLast_BothDirections()
    {
        var rows = new[]
        {
            UexRow(1, 1, 1, 0, Now, "no-report"),
            UexRow(1, 1, 1, 5, Now, "high"),
            UexRow(1, 1, 1, 2, Now, "very-low"),
        };

        var desc = PriceSort.SortRows(rows, PriceSortColumn.Status, descending: true);
        Assert.Equal(new[] { "high", "very-low", "no-report" }, desc.Select(r => r.Uex!.TerminalName));

        var asc = PriceSort.SortRows(rows, PriceSortColumn.Status, descending: false);
        Assert.Equal(new[] { "very-low", "high", "no-report" }, asc.Select(r => r.Uex!.TerminalName));
    }

    [Fact]
    public void SortRows_Status_SctOnlyRowsSortLast()
    {
        var rows = new[] { UexRow(1, 1, 1, 5, Now, "reported"), SctRow(1, Now) };
        var sorted = PriceSort.SortRows(rows, PriceSortColumn.Status, descending: true);
        Assert.Equal("reported", sorted[0].Uex!.TerminalName);
        Assert.Null(sorted[1].Uex);
    }

    [Fact]
    public void SortRows_Age_Descending_NewestFirst_SctInterleavesNormally()
    {
        var oldest = Now.AddDays(-3);
        var middle = Now.AddDays(-1);
        var rows = new[] { UexRow(1, 1, 1, 1, oldest, "old"), SctRow(1, Now, "sct-newest"), UexRow(1, 1, 1, 1, middle, "mid") };
        var sorted = PriceSort.SortRows(rows, PriceSortColumn.Age, descending: true);
        Assert.Equal(new[] { "sct-newest", "mid", "old" }, sorted.Select(r => r.Uex?.TerminalName ?? r.Sct!.Location));
    }

    [Fact]
    public void SortRows_Age_Ascending_OldestFirst()
    {
        var oldest = Now.AddDays(-3);
        var rows = new[] { UexRow(1, 1, 1, 1, Now, "new"), UexRow(1, 1, 1, 1, oldest, "old") };
        var sorted = PriceSort.SortRows(rows, PriceSortColumn.Age, descending: false);
        Assert.Equal(new[] { "old", "new" }, sorted.Select(r => r.Uex!.TerminalName));
    }

    [Fact]
    public void SortRows_TieBreak_EqualPrimaryKey_FallsBackToSellDescending()
    {
        var rows = new[] { UexRow(5, 20, 1, 1, Now, "low-sell"), UexRow(15, 20, 1, 1, Now, "high-sell") };
        var sorted = PriceSort.SortRows(rows, PriceSortColumn.Buy, descending: true);
        Assert.Equal(new[] { "high-sell", "low-sell" }, sorted.Select(r => r.Uex!.TerminalName));
    }

    [Fact]
    public void SortRows_TieBreak_FullyEqualRows_PreservesMergeOrder_Stable()
    {
        var rows = new[]
        {
            UexRow(10, 10, 1, 1, Now, "first"),
            UexRow(10, 10, 1, 1, Now, "second"),
            UexRow(10, 10, 1, 1, Now, "third"),
        };
        var sorted = PriceSort.SortRows(rows, PriceSortColumn.Buy, descending: true);
        Assert.Equal(new[] { "first", "second", "third" }, sorted.Select(r => r.Uex!.TerminalName));
    }
}
