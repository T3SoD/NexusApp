using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Ghost mode window footprints with two independent scales (issue #27 + independent rail
// scale): rail furniture scales by railK, the panel by panelK, and everything is physical
// pixels. Values are the mock's implementation table, so expectations stay mental math.
public class GhostFootprintsTests
{
    [Fact]
    public void CollapsedSize_AtUnitScale_MatchesMockRail()
    {
        var (w, h) = GhostFootprints.CollapsedSize(railK: 1.0, dpi: 1.0);
        Assert.Equal(44, w); Assert.Equal(332, h);
    }

    [Fact]
    public void CollapsedSize_ScalesByRailFactorAndDpi()
    {
        var (w, h) = GhostFootprints.CollapsedSize(railK: 0.75, dpi: 2.0);
        Assert.Equal(44 * 0.75 * 2.0, w, 5); Assert.Equal(332 * 0.75 * 2.0, h, 5);
    }

    [Fact]
    public void ExpandedSize_AtUnitScales_MatchesMockPanelPlusRail()
    {
        var (w, h) = GhostFootprints.ExpandedSize(railK: 1.0, panelK: 1.0, panelW: 320, panelH: 480, dpi: 1.0);
        Assert.Equal(44 + 2 + 320, w, 5); Assert.Equal(480, h, 5);
    }

    [Fact]
    public void ExpandedSize_SmallRail_PanelDominatesHeight()
    {
        var (w, h) = GhostFootprints.ExpandedSize(railK: 0.75, panelK: 1.25, panelW: 320, panelH: 480, dpi: 1.0);
        Assert.Equal((44 + 2) * 0.75 + 320 * 1.25, w, 5); Assert.Equal(480 * 1.25, h, 5);
    }

    [Fact]
    public void ExpandedSize_TallRail_RailDominatesHeight()
    {
        var (_, h) = GhostFootprints.ExpandedSize(railK: 1.5, panelK: 1.0, panelW: 320, panelH: 300, dpi: 1.0);
        Assert.Equal(332 * 1.5, h, 5);
    }

    [Fact]
    public void FlyoutSize_AtUnitScale_MatchesMockFlyout()
    {
        var (w, h) = GhostFootprints.FlyoutSize(railK: 1.0, dpi: 1.0);
        Assert.Equal(44 + 2 + 230, w, 5); Assert.Equal(332, h, 5);
    }

    [Fact]
    public void FlyoutSize_ScalesEveryTermByRailFactorAndDpi()
    {
        var (w, h) = GhostFootprints.FlyoutSize(railK: 0.75, dpi: 2.0);
        Assert.Equal((44 + 2 + 230) * 0.75 * 2.0, w, 5); Assert.Equal(332 * 0.75 * 2.0, h, 5);
    }

    [Fact]
    public void RailOnlyThreshold_TracksRailScale()
    {
        Assert.Equal((44 + 2) * 1.0 * 1.0 + 1, GhostFootprints.RailOnlyThreshold(1.0, 1.0), 5);
        Assert.Equal((44 + 2) * 0.75 * 2.0 + 1, GhostFootprints.RailOnlyThreshold(0.75, 2.0), 5);
    }
}
