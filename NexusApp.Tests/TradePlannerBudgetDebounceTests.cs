using NexusApp.Views;
using Xunit;

namespace NexusApp.Tests;

// Pure seam for the budget box's live-rerank debounce (the owner's live-lag report, 2026-07-31 -
// TradePage.Planner.cs). The DispatcherTimer wiring itself is WPF dispatcher plumbing and not
// unit-testable (same documented precedent as ExecHangarStatusLine.cs's own 1-second ticker -
// see ExecHangarCycleTests.cs, which only ever exercises the pure ExecHangarCycle math, never
// the timer that drives it). The one piece of the debounce that IS a plain value is the interval
// constant, so that gets pinned directly here; the timer construction/restart/cancel-on-blur
// behavior around it was verified by a careful written code trace instead (see this task's
// report), matching the established precedent for WPF timer wiring in this codebase.
public class TradePlannerBudgetDebounceTests
{
    [Fact]
    public void BudgetDebounceMs_Is250()
        // 250ms: long enough that ordinary typing speed never fires a rebuild mid-entry, short
        // enough that the results still feel live once typing pauses.
        => Assert.Equal(250, TradePage.BudgetDebounceMs);
}
