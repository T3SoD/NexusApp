using System;
using System.Collections.Generic;
using System.Linq;

namespace NexusApp.Services;

// Commodity type-or-browse picker (issue #41): the one filter behind CommodityPickerBox on the
// trade flows (Sell, Prices, and the planner's COMMODITY filter). Pure and static so the
// empty-query "browse everything" rule and the substring match are unit-testable without a WPF
// control in the loop - same reasoning as PriceSort's extraction out of TradePage.Prices.cs.
internal static class CommoditySuggest
{
    // Empty/whitespace query = browse mode: the FULL list, so the chevron acts as the old plain
    // dropdown. No result cap in either mode (the old inline Sell picker's Take(8) is gone) - the
    // popup scrolls instead of hiding matches past an arbitrary cut. Always OrdinalIgnoreCase
    // order, matching how the flows already sort their commodity lists. pinnedFirst is a sentinel
    // row (the planner's "ANY") that outranks the alphabetical order in browse mode and matches
    // like any name when typing; callers never put the sentinel in `names` itself.
    public static List<string> Filter(IReadOnlyList<string> names, string query, string? pinnedFirst = null)
    {
        var ordered = names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(query))
        {
            var all = ordered.ToList();
            if (pinnedFirst is not null) all.Insert(0, pinnedFirst);
            return all;
        }
        var token = query.Trim();   // house autocomplete rule (WorkOrderEditorPanel): match the trimmed token
        var matches = ordered.Where(n => n.Contains(token, StringComparison.OrdinalIgnoreCase)).ToList();
        if (pinnedFirst is not null && pinnedFirst.Contains(token, StringComparison.OrdinalIgnoreCase))
            matches.Insert(0, pinnedFirst);
        return matches;
    }
}
