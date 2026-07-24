using System;
using NexusApp.Models;
using Xunit;

namespace NexusApp.Tests;

// The main-gallery and overlay tickers auto-flip a refine timer that has run out from Refining to
// ReadyToCollect so the ready flash plays live. The elapsed/idempotence decision lives in a pure,
// time-injectable model method (WorkOrder.ShouldAutoFlipToReady) so it can be asserted without WPF.
// TimerEnd is UtcNow-based, so these tests pin a fixed instant and place TimerEnd before/after it.
public class WorkOrderReadyFlipTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static WorkOrder Order(WorkOrderStatus status, DateTime? timerEnd) => new()
    {
        Status = status,
        TimerStart = timerEnd.HasValue ? Now.AddHours(-1) : null,
        TimerEnd = timerEnd,
    };

    [Fact]
    public void ElapsedRefining_Flips()
    {
        var wo = Order(WorkOrderStatus.Refining, Now.AddSeconds(-1));
        Assert.True(wo.ShouldAutoFlipToReady(Now));
    }

    [Fact]
    public void ElapsedExactlyNowRefining_Flips()
    {
        var wo = Order(WorkOrderStatus.Refining, Now);
        Assert.True(wo.ShouldAutoFlipToReady(Now));
    }

    [Fact]
    public void NotElapsedRefining_DoesNotFlip()
    {
        var wo = Order(WorkOrderStatus.Refining, Now.AddSeconds(1));
        Assert.False(wo.ShouldAutoFlipToReady(Now));
    }

    [Fact]
    public void AlreadyReady_DoesNotDoubleFlip()
    {
        var wo = Order(WorkOrderStatus.ReadyToCollect, Now.AddSeconds(-1));
        Assert.False(wo.ShouldAutoFlipToReady(Now));
    }

    [Fact]
    public void ElapsedMining_Flips()
    {
        // Mining is retired but legacy orders can still carry it; the editor flips Mining too, so parity here.
        var wo = Order(WorkOrderStatus.Mining, Now.AddSeconds(-1));
        Assert.True(wo.ShouldAutoFlipToReady(Now));
    }

    [Fact]
    public void ElapsedComplete_DoesNotFlip()
    {
        var wo = Order(WorkOrderStatus.Complete, Now.AddSeconds(-1));
        Assert.False(wo.ShouldAutoFlipToReady(Now));
    }

    [Fact]
    public void NoTimer_DoesNotFlip()
    {
        var wo = Order(WorkOrderStatus.Refining, null);
        Assert.False(wo.ShouldAutoFlipToReady(Now));
    }
}
