using System;
using System.Collections.Generic;
using System.Linq;

namespace NexusApp.Services;

// One row of the Prices flow's merged UEX+SCT-only list (TradePage.Prices.cs's own merge: exactly
// one of Uex/Sct is populated per row). Lives here rather than as a private nested type on
// TradePage so PriceSort.SortRows - and its own tests, NexusApp.Tests via the InternalsVisibleTo
// entry in NexusApp.csproj - can build and compare rows without a WPF UserControl in the loop.
internal readonly record struct PriceRowItem(double SellValue, TradePriceRow? Uex, SctListing? Sct);

internal enum PriceSortColumn { Sell, Buy, Stock, Status, Age }

// Prices flow: sortable columns (owner live-pass ask, 2026-07-30). Pure and static so the SCT-only
// placement rules are unit-testable on their own - TradePage.Prices.cs's RebuildPrices calls this
// once per rebuild with whatever column/direction the last header click set, then applies the
// top-50 display cap to what comes back (this sorts the FULL merged list, never truncates itself).
internal static class PriceSort
{
    public static List<PriceRowItem> SortRows(IReadOnlyList<PriceRowItem> rows, PriceSortColumn column, bool descending)
    {
        IOrderedEnumerable<PriceRowItem> ordered = column switch
        {
            PriceSortColumn.Sell   => OrderByValue(rows, r => r.SellValue, descending),
            PriceSortColumn.Buy    => OrderWithLastBucket(rows, r => r.Uex?.Buy, descending),
            PriceSortColumn.Stock  => OrderWithLastBucket(rows, r => r.Uex?.BuyStockScu, descending),
            // A UEX row with StatusBuy 0 means "no report" (TradeFlows.BuyStatusLabel's own dash
            // case) - the same "nothing to sort by" bucket an SCT-only row falls into for this
            // column, so both share the one has-value check below.
            PriceSortColumn.Status => OrderWithLastBucket(rows, r => r.Uex is { StatusBuy: not 0 } u ? u.StatusBuy : (int?)null, descending),
            PriceSortColumn.Age    => OrderByValue(rows, AgeUtc, descending),
            _ => throw new ArgumentOutOfRangeException(nameof(column), column, null),
        };
        // Stable tie-break, every column (brief: "Ties break by Sell descending, stable"). LINQ's
        // OrderBy/ThenBy family is a stable sort, so two rows already equal on the primary key (and,
        // for the last-bucket columns, in the same bucket) keep their original merge-list order once
        // this also ties - never reshuffled by chance.
        return ordered.ThenByDescending(r => r.SellValue).ToList();
    }

    // Age: UEX rows key off ModifiedUtc, SCT-only rows off TimestampUtc - both mean "when this price
    // was last reported," so they sort inline together here, unlike Buy/Stock/Status where an
    // SCT-only row has no UEX-side value to sort by at all.
    private static DateTime AgeUtc(PriceRowItem r) => r.Uex?.ModifiedUtc ?? r.Sct!.TimestampUtc;

    private static IOrderedEnumerable<PriceRowItem> OrderByValue<TKey>(
        IReadOnlyList<PriceRowItem> rows, Func<PriceRowItem, TKey> key, bool descending)
        => descending ? rows.OrderByDescending(key) : rows.OrderBy(key);

    // Buy/Stock/Status: a row with no value for this column (SCT-only for all three; a UEX row with
    // StatusBuy 0 for Status specifically) sorts LAST in EITHER direction - the brief's placement
    // rule. The has-value/no-value bucket order itself never flips; only the ordering inside the
    // has-value bucket follows the requested direction.
    private static IOrderedEnumerable<PriceRowItem> OrderWithLastBucket<TKey>(
        IReadOnlyList<PriceRowItem> rows, Func<PriceRowItem, TKey?> key, bool descending)
        where TKey : struct, IComparable<TKey>
    {
        var byBucket = rows.OrderBy(r => key(r).HasValue ? 0 : 1);
        return descending
            ? byBucket.ThenByDescending(r => key(r) ?? default)
            : byBucket.ThenBy(r => key(r) ?? default);
    }
}
