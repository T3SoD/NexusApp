using System;
using NexusApp.Models;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Exercises the overlay quick-add builder: a minimal Refining work order with a running
// timer. Everything richer stays in the main-window WorkOrderEditorPanel. Timer math is
// UtcNow-based in the model, so callers pass DateTime.UtcNow; these tests pass a fixed
// instant to assert the exact TimerStart/TimerEnd the model will read.
public class QuickOrderFactoryTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void CreatesRefiningOrderWithRunningTimer()
    {
        var wo = QuickOrderFactory.Create("Quantanium", "ARC-L1", 45, Now);

        Assert.Equal("Quantanium", wo.Resources);
        Assert.Equal("ARC-L1", wo.Refinery);
        Assert.Equal(WorkOrderStatus.Refining, wo.Status);
        Assert.Equal(Now, wo.TimerStart);
        Assert.Equal(Now.AddMinutes(45), wo.TimerEnd);
        Assert.False(string.IsNullOrEmpty(wo.Label));
    }

    [Fact]
    public void EmptyResource_Throws() =>
        Assert.Throws<ArgumentException>(() => QuickOrderFactory.Create(" ", "ARC-L1", 45, Now));

    [Fact]
    public void NonPositiveMinutes_Throws() =>
        Assert.Throws<ArgumentException>(() => QuickOrderFactory.Create("Quantanium", "ARC-L1", 0, Now));

    [Fact]
    public void EmptyRefinery_Allowed()
    {
        var wo = QuickOrderFactory.Create("Quantanium", "", 30, Now);
        Assert.Equal("", wo.Refinery);
    }
}
