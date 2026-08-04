using NexusApp.Views;
using Xunit;

namespace NexusApp.Tests;

// TradePage.LiveOriginMoved: the planner's live-origin rebuild guard. A location raise re-ranks
// the planner (RebuildPlanner refreshes the LIVE start combo and the origin restriction) only
// when the location actually changed - the tracker also re-raises for freshness-only inventory
// ticks (LocationTrackerTests pins that), and a full re-rank is too heavy to run for a raise
// that moved nothing. The Dispatcher wiring itself is WPF plumbing, code-traced not unit-tested
// (TradePlannerBudgetDebounceTests' precedent).
public class TradeLiveOriginTests
{
    [Fact]
    public void FirstReading_IsAMove()
        => Assert.True(TradePage.LiveOriginMoved(null, "Lorville"));

    [Fact]
    public void SameReading_IsNotAMove()
        // The freshness-only re-raise: an inventory tick that confirms the same place.
        => Assert.False(TradePage.LiveOriginMoved("Lorville", "Lorville"));

    [Fact]
    public void NewPlace_IsAMove()
        => Assert.True(TradePage.LiveOriginMoved("Lorville", "Area18"));

    [Fact]
    public void CaseDiffers_IsAMove()
        // Ordinal, not fuzzy: location strings are the log's own words, never user input, so a
        // casing change is a different reported place, not a spelling variant to forgive.
        => Assert.True(TradePage.LiveOriginMoved("Lorville", "LORVILLE"));

    [Fact]
    public void BothUnknown_IsNotAMove()
        => Assert.False(TradePage.LiveOriginMoved(null, null));
}
