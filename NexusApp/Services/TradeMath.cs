using System.Globalization;

namespace NexusApp.Services;

// Pure capacity/quantity math for the trading tab (route planner + sell lookup): how much of a
// commodity actually moves in one trip, and whether a terminal's container sizes fit the ship's
// cargo grid. No I/O, no snapshot access - every input is a plain value.
public static class TradeMath
{
    // The smallest of what the ship can carry, what the terminal has to sell, and what the
    // entered budget affords at this row's buy price. A non-positive buyPrice makes "afford"
    // meaningless (and never occurs from RoutePlanner, which only pairs Buy > 0 rows), so it
    // drops the budget term instead of dividing by it.
    public static int TripQty(int shipScu, int buyStockScu, double? budget, double buyPrice)
    {
        var qty = Math.Min(Math.Max(shipScu, 0), Math.Max(buyStockScu, 0));
        if (budget.HasValue && buyPrice > 0)
            qty = Math.Min(qty, (int)Math.Floor(budget.Value / buyPrice));
        return Math.Max(qty, 0);
    }

    // Mirrors the mock's expanded-band narration (nexus-design-lab/trading-tab/index.html:732-733):
    // "ship N", "stock N", and "budget affords N" only when a budget was actually entered - the
    // same guard TripQty uses, so the two can never disagree about whether the budget term applies.
    // InvariantCulture, like every sibling number in the card that renders this narration (the
    // planner's profit/price/leg values all force it): a comma group separator on a machine set to
    // a comma-decimal locale would otherwise read as a decimal point right next to values that
    // never move.
    public static string[] TripParts(int shipScu, int buyStockScu, double? budget, double buyPrice)
    {
        var parts = new List<string>
        {
            $"ship {shipScu.ToString("N0", CultureInfo.InvariantCulture)}",
            $"stock {buyStockScu.ToString("N0", CultureInfo.InvariantCulture)}",
        };
        if (budget.HasValue && buyPrice > 0)
            parts.Add($"budget affords {((int)Math.Floor(budget.Value / buyPrice)).ToString("N0", CultureInfo.InvariantCulture)}");
        return parts.ToArray();
    }

    // True when the ship can load at least one of the terminal's container sizes - fits when the
    // terminal offers ANY size <= shipMaxBox, not when every size fits (a terminal that also
    // stocks 32 SCU boxes is still usable by an 8-SCU-box ship as long as a smaller size is on the
    // list too). Unparseable or empty container_sizes fails closed: an unknown fit is excluded,
    // never assumed.
    public static bool BoxFits(string containerSizes, int shipMaxBox)
    {
        if (string.IsNullOrWhiteSpace(containerSizes)) return false;
        foreach (var token in containerSizes.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(token, out var size) && size > 0 && size <= shipMaxBox) return true;
        }
        return false;
    }
}
