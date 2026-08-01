using NexusApp.Views;
using Xunit;

namespace NexusApp.Tests;

// WorkOrderEditorPanel.ShouldRestampTimer is the pure rule behind a defect the app review found:
// the editor's hour/minute boxes arrive PREFILLED with the order's remaining time, and Save used to
// restamp TimerStart/TimerEnd whenever either box was non-zero. So editing a label on a 2 hour order
// 10 minutes from done restamped a fresh 10 minute timer starting now. The countdown still read
// ~10m, but TimerFraction ((now - TimerStart) / (TimerEnd - TimerStart)) evaluated to ~0, so both
// progress bars snapped to empty and raced to full over the last 10 minutes. Repeated edits also
// shaved up to 59 seconds off the order each time, because the prefill truncates seconds.
public class WorkOrderEditorTimerTests
{
    [Fact]
    public void UntouchedTimerOnARunningOrder_IsNotRestamped()
    {
        // The exact defect: a 2 hour order with 1h 47m left, opened and saved without touching the
        // timer. Non-zero, but the user changed nothing about it.
        Assert.False(WorkOrderEditorPanel.ShouldRestampTimer("1", "47", "1", "47"));
    }

    [Fact]
    public void ChangingTheHours_Restamps()
    {
        Assert.True(WorkOrderEditorPanel.ShouldRestampTimer("3", "47", "1", "47"));
    }

    [Fact]
    public void ChangingTheMinutes_Restamps()
    {
        Assert.True(WorkOrderEditorPanel.ShouldRestampTimer("1", "5", "1", "47"));
    }

    [Fact]
    public void SettingATimerOnAnOrderThatHadNone_Restamps()
    {
        // An order with no timer builds with empty boxes; typing a duration is a real edit.
        Assert.True(WorkOrderEditorPanel.ShouldRestampTimer("2", "30", "", ""));
    }

    [Fact]
    public void ClearingTheTimerToZero_DoesNotRestamp()
    {
        // Changed, but there is no duration to stamp. Deliberately leaves the existing timer alone
        // rather than stamping a zero-length one; clearing a timer is not a flow this editor offers.
        Assert.False(WorkOrderEditorPanel.ShouldRestampTimer("0", "0", "1", "47"));
    }

    [Fact]
    public void WhitespaceOnlyDifference_IsNotAnEdit()
    {
        // The boxes are free-text, and a stray space is not a user intent to reset the clock.
        Assert.False(WorkOrderEditorPanel.ShouldRestampTimer(" 1 ", "47 ", "1", "47"));
    }

    [Fact]
    public void HoursOnly_AndMinutesOnly_BothCountAsRealDurations()
    {
        Assert.True(WorkOrderEditorPanel.ShouldRestampTimer("1", "0", "", ""));
        Assert.True(WorkOrderEditorPanel.ShouldRestampTimer("0", "20", "", ""));
    }

    [Fact]
    public void GarbageText_IsTreatedAsZero_NotAsATimer()
    {
        // int.TryParse fails, so there is no duration. Changed but unusable.
        Assert.False(WorkOrderEditorPanel.ShouldRestampTimer("abc", "xyz", "1", "47"));
    }

    [Fact]
    public void NullsNeverThrow()
    {
        // Defensive: this runs off live TextBox.Text, and the rule must never be the thing that
        // breaks a save.
        Assert.False(WorkOrderEditorPanel.ShouldRestampTimer(null, null, null, null));
        Assert.True(WorkOrderEditorPanel.ShouldRestampTimer("1", null, null, null));
    }
}
