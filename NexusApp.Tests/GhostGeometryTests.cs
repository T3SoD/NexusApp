using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Ghost mode geometry (issue #27). All values are physical pixels; the monitor is
// 1000x1000 at origin unless a test says otherwise, so expectations stay mental math.
public class GhostGeometryTests
{
    private static readonly PxRect Mon = new(0, 0, 1000, 1000);

    [Fact]
    public void DirectionFor_RailInRightHalf_ExpandsLeft()
        => Assert.Equal(GhostExpandDirection.Left, GhostGeometry.DirectionFor(new PxRect(900, 100, 44, 332), Mon));

    [Fact]
    public void DirectionFor_RailInLeftHalf_ExpandsRight()
        => Assert.Equal(GhostExpandDirection.Right, GhostGeometry.DirectionFor(new PxRect(100, 100, 44, 332), Mon));

    [Fact]
    public void DirectionFor_RailCenterExactlyOnMonitorCenter_ExpandsRight()
        // 478 + 44/2 = 500 = monitor center; not strictly greater, so Right.
        => Assert.Equal(GhostExpandDirection.Right, GhostGeometry.DirectionFor(new PxRect(478, 0, 44, 332), Mon));

    [Fact]
    public void CollapsedRect_WindowInRightHalf_RailHugsWindowsRightEdge()
    {
        var win = new PxRect(600, 50, 320, 480);
        var rail = GhostGeometry.CollapsedRect(win, Mon, 44, 332);
        Assert.Equal(win.Right - 44, rail.Left);
        Assert.Equal(50, rail.Top);
        Assert.Equal(44, rail.Width);
        Assert.Equal(332, rail.Height);
    }

    [Fact]
    public void CollapsedRect_WindowInLeftHalf_RailKeepsWindowsLeftEdge()
    {
        var rail = GhostGeometry.CollapsedRect(new PxRect(100, 50, 320, 480), Mon, 44, 332);
        Assert.Equal(100, rail.Left);
    }

    [Fact]
    public void CollapsedRect_WindowAgainstRightScreenEdge_RailStaysOnScreen()
    {
        var rail = GhostGeometry.CollapsedRect(new PxRect(680, 50, 320, 480), Mon, 44, 332);
        Assert.Equal(1000 - 44, rail.Left);   // hugs the window's right edge, exactly at the screen edge
    }

    [Fact]
    public void ExpandedRect_Left_GrowsLeftFromRailsRightEdge()
    {
        var rail = new PxRect(940, 60, 44, 332);
        var win = GhostGeometry.ExpandedRect(rail, Mon, GhostExpandDirection.Left, 366, 480);
        Assert.Equal(rail.Right - 366, win.Left);
        Assert.Equal(60, win.Top);
        Assert.Equal(366, win.Width);
        Assert.Equal(480, win.Height);
    }

    [Fact]
    public void ExpandedRect_Right_KeepsRailsLeftEdge()
    {
        var rail = new PxRect(20, 60, 44, 332);
        var win = GhostGeometry.ExpandedRect(rail, Mon, GhostExpandDirection.Right, 366, 480);
        Assert.Equal(20, win.Left);
    }

    [Fact]
    public void ExpandedRect_WouldOverflowBottom_ClampsUpOntoMonitor()
    {
        var rail = new PxRect(20, 800, 44, 332);   // 800 + 480 > 1000
        var win = GhostGeometry.ExpandedRect(rail, Mon, GhostExpandDirection.Right, 366, 480);
        Assert.Equal(1000 - 480, win.Top);
    }

    [Fact]
    public void ExpandedRect_WouldOverflowLeft_ClampsToMonitorLeft()
    {
        var rail = new PxRect(100, 0, 44, 332);    // 144 - 366 < 0
        var win = GhostGeometry.ExpandedRect(rail, Mon, GhostExpandDirection.Left, 366, 480);
        Assert.Equal(0, win.Left);
    }

    [Fact]
    public void Clamp_RectWiderThanMonitor_PinsToMonitorLeft()
    {
        var r = GhostGeometry.Clamp(new PxRect(-50, 0, 1200, 100), Mon);
        Assert.Equal(0, r.Left);
    }

    [Fact]
    public void Clamp_MonitorWithNonZeroOrigin_UsesMonitorSpace()
    {
        // Second monitor to the left of primary: physical origin can be negative.
        var mon2 = new PxRect(-1920, 0, 1920, 1080);
        var r = GhostGeometry.Clamp(new PxRect(-2000, -50, 44, 332), mon2);
        Assert.Equal(-1920, r.Left);
        Assert.Equal(0, r.Top);
    }

    [Fact]
    public void PxRect_DerivedEdges()
    {
        var r = new PxRect(10, 20, 30, 40);
        Assert.Equal(40, r.Right);
        Assert.Equal(60, r.Bottom);
        Assert.Equal(25, r.CenterX);
    }
}
