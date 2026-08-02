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
    // order, matching how the flows already sort their commodity lists. `pinned` is a LIST of
    // sentinel rows (the planner's "ANY"; Task B's overlay start picker pins "ANY" and "LIVE"
    // together) that keep their given order, outrank the alphabetical order in browse mode, and
    // are matched like any name when typing (a pinned row that does not match is excluded, same
    // as any other name); callers never put a pinned row in `names` itself.
    public static List<string> Filter(IReadOnlyList<string> names, string query, IReadOnlyList<string>? pinned = null)
    {
        var ordered = names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(query))
        {
            var all = ordered.ToList();
            if (pinned is { Count: > 0 })
                for (int i = pinned.Count - 1; i >= 0; i--) all.Insert(0, pinned[i]);
            return all;
        }
        var token = query.Trim();   // house autocomplete rule (WorkOrderEditorPanel): match the trimmed token
        var matches = ordered.Where(n => n.Contains(token, StringComparison.OrdinalIgnoreCase)).ToList();
        if (pinned is { Count: > 0 })
        {
            int insertAt = 0;
            foreach (var p in pinned)
                if (p.Contains(token, StringComparison.OrdinalIgnoreCase))
                    matches.Insert(insertAt++, p);
        }
        return matches;
    }

    // Bridges the original single-sentinel call shape (CommodityPickerBox.PinnedFirst) onto the
    // list overload above, so every existing caller keeps compiling and behaving unchanged.
    public static List<string> Filter(IReadOnlyList<string> names, string query, string? pinnedFirst) =>
        Filter(names, query, pinnedFirst is null ? null : new[] { pinnedFirst });
}
