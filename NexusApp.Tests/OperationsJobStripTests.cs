using NexusApp.Models;
using NexusApp.Views;
using Xunit;

namespace NexusApp.Tests;

// Pins the two pure folds behind the Operations job strip's AUTO LOAD card (system-view redesign,
// 2026-08-10). Both are static and touch no WPF state, so they are unit-tested directly - same
// shape as MapWebViewNavigationTests, which tests statics on another UserControl.
//
// The rule these encode: an entry with no PredictedSeconds is never counted as running. Without a
// prediction there is no moment to call it finished, so guessing would put a countdown on the card
// for a load that may already be done. That is AutoLoadBadge's own contract, restated here at the
// display edge because the card is where a wrong guess would be visible.
public class OperationsJobStripTests
{
    private static readonly DateTime Now = new(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);

    private static AutoLoadEntry Entry(int startsAgoSeconds, int? predicted) => new(
        Now.AddSeconds(-startsAgoSeconds), TransactionKind.Buy, "SCShop_Test",
        "Titanium", "Everus Harbor", false, 32m,
        Array.Empty<CargoBoxGroup>(), predicted);

    [Fact]
    public void AutoLoadValue_NoEntries_IsIdle()
    {
        Assert.Equal("IDLE", CommandPage.AutoLoadValue(Array.Empty<AutoLoadEntry>(), Now));
        Assert.Equal("no load running", CommandPage.AutoLoadSub(Array.Empty<AutoLoadEntry>(), Now));
    }

    [Fact]
    public void AutoLoadValue_RunningLoad_CountsDownInMinutesAndSeconds()
    {
        // Started 72s ago, predicted 180s: 108s left.
        var entries = new[] { Entry(72, 180) };
        Assert.Equal("01:48", CommandPage.AutoLoadValue(entries, Now));
        Assert.Equal("1 running", CommandPage.AutoLoadSub(entries, Now));
    }

    [Fact]
    public void AutoLoadValue_TwoRunning_UsesTheSoonest()
    {
        var entries = new[] { Entry(10, 600), Entry(10, 100) };   // 590s left and 90s left
        Assert.Equal("01:30", CommandPage.AutoLoadValue(entries, Now));
        Assert.Equal("2 running", CommandPage.AutoLoadSub(entries, Now));
    }

    // The headline never counts past zero. The game logs no completion event, so an overrun is not
    // proof the load ended - it is only proof the prediction was short.
    [Fact]
    public void AutoLoadValue_AllFinished_ReadsDoneNotNegative()
    {
        var entries = new[] { Entry(600, 120) };
        Assert.Equal("DONE", CommandPage.AutoLoadValue(entries, Now));
        Assert.Equal("load finished, not cleared", CommandPage.AutoLoadSub(entries, Now));
        Assert.DoesNotContain("-", CommandPage.AutoLoadValue(entries, Now));
    }

    [Fact]
    public void AutoLoadValue_AllFinished_Plural()
    {
        var entries = new[] { Entry(600, 120), Entry(700, 90) };
        Assert.Equal("2 loads finished", CommandPage.AutoLoadSub(entries, Now));
    }

    // An unpredicted entry is real work in flight, so it is stated, but it is stated separately: it
    // has no countdown and must not be folded into the running count as if it did.
    [Fact]
    public void AutoLoadSub_UnpredictedEntry_CountedSeparately()
    {
        var entries = new[] { Entry(30, 300), Entry(30, null) };
        Assert.Equal("1 running, 1 with no estimate", CommandPage.AutoLoadSub(entries, Now));
    }

    // The defect this test was written to catch: an unpredicted load has NOT finished, and the card
    // must never say it has. It is running and untimeable, which is a different fact.
    [Fact]
    public void AutoLoadValue_OnlyUnpredictedEntries_IsRunningNotFinished()
    {
        var entries = new[] { Entry(30, null) };
        Assert.Equal("RUNNING", CommandPage.AutoLoadValue(entries, Now));
        Assert.Equal("1 with no estimate", CommandPage.AutoLoadSub(entries, Now));
        Assert.DoesNotContain("finished", CommandPage.AutoLoadSub(entries, Now));
    }

    [Fact]
    public void AutoLoadSub_MixedStates_StatesAllThree()
    {
        var entries = new[] { Entry(30, 300), Entry(30, null), Entry(600, 120) };
        Assert.Equal("1 running, 1 with no estimate, 1 finished", CommandPage.AutoLoadSub(entries, Now));
    }
}
