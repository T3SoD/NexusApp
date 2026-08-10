namespace NexusApp.Services;

/// <summary>
/// Swaps UEX's shipped container_sizes for what the kiosk itself said, per terminal (2026-08-10).
///
/// <para>THE DEFECT. RoutePlanner snaps a trip down to the box sizes the buy terminal can supply,
/// and its only source was TradePriceRow.ContainerSizes - one list per commodity from UEX, the
/// same at every terminal that sells it. The kiosks disagree: MIC-L4's admin office offers Scrap in
/// 1/2/4/8/16 while offering Waste at the same counter in sizes up to 32. Planning a 32 SCU box at
/// a kiosk that stocks 16s produces a quantity nobody can buy, and the route is priced on it.</para>
///
/// <para>Only rows we have actually observed are touched. Everything else keeps UEX's list
/// verbatim, so this can only ever make the planner more right about a terminal you have visited
/// and never changes one you have not.</para>
/// </summary>
public static class KioskBoxOverride
{
    /// <summary>
    /// Returns the rows with observed box sizes substituted in.
    ///
    /// <param name="sizesFor">(location, commodity) -> container sizes, or null when unobserved.
    /// A delegate rather than the store itself so this stays a pure fold under test.</param>
    /// </summary>
    public static IReadOnlyList<TradePriceRow> Apply(
        IReadOnlyList<TradePriceRow> rows,
        IReadOnlyDictionary<int, MarketTerminal> terminals,
        Func<string?, string?, string?> sizesFor)
    {
        List<TradePriceRow>? result = null;   // stays null, and the input is returned, when nothing matched

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            string? observed = null;
            if (terminals.TryGetValue(row.TerminalId, out var terminal))
                observed = sizesFor(terminal.Location, row.CommodityName);

            if (observed is null || observed == row.ContainerSizes)
            {
                result?.Add(row);
                continue;
            }

            result ??= new List<TradePriceRow>(rows.Count);
            for (var j = result.Count; j < i; j++) result.Add(rows[j]);   // backfill the untouched prefix
            result.Add(row with { ContainerSizes = observed });
        }

        return result ?? rows;
    }
}
