using System.Linq;
using NexusApp.Models;

namespace NexusApp.Services;

// Trade tab prefill (Sell flow): finds the work order to prefill from and resolves its free-text
// Resources field to a UEX commodity id the trading tab can look up, reusing the exact
// seed-name -> raw -> refined chain MarketQueries/MarketNameMap already use for the Codex/
// work-order sell hints. WorkOrder carries no output-quantity field (see NexusApp.Models.
// WorkOrder), so this resolves the COMMODITY only; the Sell flow's quantity field is always the
// user's own entry, never prefilled.
//
// Internal, not public: MarketCommodity (the commodities parameter's element type) is itself
// internal, and a public method cannot expose a less-accessible parameter type (CS0051 - the same
// rule that already forced MarketTerminal/TradePriceRow public in Tasks 1/3, per progress.md).
// MarketCommodity is on this task's "consume, do not modify" list, so widening it was not an
// option here; keeping this class internal instead compiles cleanly and changes nothing about the
// specified method names or parameter shapes, only the class-level access modifier.
internal static class WorkOrderPrefill
{
    // The order to prefill the Sell flow from: the most recently created Complete order.
    // CreatedAt is the only timestamp guaranteed on every order - TimerStart/TimerEnd stay null
    // whenever an order was created or edited without setting a timer (WorkOrderEditorPanel's
    // save path only stamps them when hours/minutes > 0), so a Complete order can easily have no
    // timer fields to sort by instead.
    public static WorkOrder? LatestCompleted(IEnumerable<WorkOrder> orders)
    {
        if (orders is null) return null;
        return orders.Where(o => o.Status == WorkOrderStatus.Complete)
                      .OrderByDescending(o => o.CreatedAt)
                      .FirstOrDefault();
    }

    // Returns null at any resolution step that fails - never a guess.
    public static int? ResolveCommodityId(WorkOrder order, IReadOnlyList<MarketCommodity> commodities)
    {
        if (order is null || commodities is null || commodities.Count == 0) return null;

        var seedName = MarketNameMap.RecognizeSeedNames(order.Resources).FirstOrDefault();
        if (seedName is null) return null;

        var uexRawName = MarketNameMap.UexRawNameFor(seedName);
        if (uexRawName is null) return null;

        MarketCommodity? raw = null;
        foreach (var c in commodities)
        {
            if (string.Equals(c.Name, uexRawName, StringComparison.OrdinalIgnoreCase)) { raw = c; break; }
        }
        if (raw is null) return null;

        return MarketNameMap.RefinedFor(raw, commodities)?.Id;
    }
}
