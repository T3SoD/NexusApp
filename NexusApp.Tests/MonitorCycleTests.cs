using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Issue #36: the region picker dims ONE monitor and carries a NEXT MONITOR button instead of
// tinting every screen at once (owner's call). The hop math and the 1-based button label live in
// this seam; the window supplies the monitor rects and applies the moves.
public class MonitorCycleTests
{
    [Theory]
    [InlineData(0, 3, 1)]
    [InlineData(1, 3, 2)]
    [InlineData(2, 3, 0)]   // wraps
    [InlineData(0, 2, 1)]
    [InlineData(1, 2, 0)]
    public void Next_AdvancesAndWraps(int current, int count, int expected)
    {
        Assert.Equal(expected, MonitorCycle.Next(current, count));
    }

    [Fact]
    public void Next_SingleMonitor_StaysPut()
    {
        Assert.Equal(0, MonitorCycle.Next(0, 1));
    }

    [Fact]
    public void Label_IsOneBased()
    {
        Assert.Equal("MONITOR 1 OF 3", MonitorCycle.Label(0, 3));
        Assert.Equal("MONITOR 3 OF 3", MonitorCycle.Label(2, 3));
    }
}
