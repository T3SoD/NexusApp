using System.Linq;

namespace NexusApp.Services;

// The sell-lookup flow's ranked buyer list: for a chosen commodity and quantity in hand, every
// terminal that will buy it (Sell > 0), ranked by what the load is actually worth there.
public static class SellLookup
{
    public sealed record Buyer(TradePriceRow Row, int SellableScu, double EffectiveValue, ProximityTier Tier);

    public static IReadOnlyList<Buyer> Rank(IReadOnlyList<TradePriceRow> rows,
        IReadOnlyDictionary<int, MarketTerminal> terminals, int commodityId, int qtyScu, int? originTerminalId,
        string scope)
    {
        var result = new List<Buyer>();
        if (rows is null || rows.Count == 0 || qtyScu <= 0) return result;

        MarketTerminal? origin = null;
        if (originTerminalId.HasValue) terminals.TryGetValue(originTerminalId.Value, out origin);

        foreach (var row in rows)
        {
            if (row.CommodityId != commodityId || row.Sell <= 0) continue;
            if (!terminals.TryGetValue(row.TerminalId, out var terminal)) continue;
            if (!InScope(terminal, scope)) continue;

            var sellable = Math.Min(qtyScu, row.SellDemandScu);
            // Origin unknown (no live session, no manual pick yet) or unresolvable: CrossSystem
            // is the conservative default - the same "cannot confirm a closer tier" rule
            // ProximityTiers itself applies to missing location fields.
            var tier = origin is not null ? ProximityTiers.Derive(origin, terminal) : ProximityTier.CrossSystem;
            result.Add(new Buyer(row, sellable, sellable * row.Sell, tier));
        }

        return result.OrderByDescending(b => b.EffectiveValue).ToList();
    }

    // Duplicated from RoutePlanner.InScope rather than shared: both are small, page-scoped
    // helpers, matching this codebase's existing precedent of hand-duplicating small pieces per
    // consumer (e.g. CascadeIn in CommandPage.cs/MainWindow.Codex.cs) rather than over-abstracting.
    private static bool InScope(MarketTerminal terminal, string scope)
    {
        if (string.IsNullOrEmpty(scope) || string.Equals(scope, "ALL", StringComparison.OrdinalIgnoreCase))
            return true;
        return !string.IsNullOrEmpty(terminal.System)
            && string.Equals(terminal.System, scope, StringComparison.OrdinalIgnoreCase);
    }
}
