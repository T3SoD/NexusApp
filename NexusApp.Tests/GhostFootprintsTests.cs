using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Ghost mode window footprints with two independent scales (issue #27 + independent rail
// scale): rail furniture scales by railK, the panel by panelK, and everything is physical
// pixels. Values are the mock's implementation table, so expectations stay mental math.
//
// RailHCollapsed used to be the literal 332 the mock's six-tab rail measured. It is now derived
// from the tab count, because adding a seventh tab overflowed the icon column into the settings
// gear. These tests therefore compute the expected height the same way the rail is BUILT - cap,
// per-tab buttons, gap, gear, close - rather than restating a number that is only correct until
// someone adds a tab.
public class GhostFootprintsTests
{
    // Cap 34 + one 32px button with a 4px gap per tab + a 24px breathing gap + gear 32 + close 26,
    // which is exactly the stack OverlayGhostRail docks into the rail.
    private static double ExpectedRailH => 34 + OverlayTabs.Ids.Length * (32 + 4) + 24 + 32 + 26;

    // The regression itself: the rail must be tall enough for what it contains. With the old
    // hard-coded 332 and seven tabs this was 344 > 332, and the DockPanel silently ran the last
    // tab icon into the settings gear.
    [Fact]
    public void CollapsedRail_IsTallEnoughForEveryTabPlusTheGearAndClose()
    {
        var (_, h) = GhostFootprints.CollapsedSize(railK: 1.0, dpi: 1.0);
        var furniture = 34 + OverlayTabs.Ids.Length * (32 + 4) + 32 + 26;
        Assert.True(h >= furniture, $"rail is {h}px but its contents need {furniture}px");
    }

    // Six tabs is what the mock was drawn against; the derivation must still reproduce its 332,
    // so the change is provably a growth rule and not a redesign of the shipped rail.
    [Fact]
    public void TheDerivation_ReproducesTheMocksOwnSixTabRail()
        => Assert.Equal(332, 34 + 6 * (32 + 4) + 24 + 32 + 26);

    [Fact]
    public void CollapsedSize_AtUnitScale_MatchesMockRail()
    {
        var (w, h) = GhostFootprints.CollapsedSize(railK: 1.0, dpi: 1.0);
        Assert.Equal(44, w); Assert.Equal(ExpectedRailH, h);
    }

    [Fact]
    public void CollapsedSize_ScalesByRailFactorAndDpi()
    {
        var (w, h) = GhostFootprints.CollapsedSize(railK: 0.75, dpi: 2.0);
        Assert.Equal(44 * 0.75 * 2.0, w, 5); Assert.Equal(ExpectedRailH * 0.75 * 2.0, h, 5);
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
        Assert.Equal(ExpectedRailH * 1.5, h, 5);
    }

    [Fact]
    public void FlyoutSize_AtUnitScale_MatchesMockFlyout()
    {
        var (w, h) = GhostFootprints.FlyoutSize(railK: 1.0, dpi: 1.0);
        Assert.Equal(44 + 2 + 230, w, 5); Assert.Equal(ExpectedRailH, h, 5);
    }

    [Fact]
    public void FlyoutSize_ScalesEveryTermByRailFactorAndDpi()
    {
        var (w, h) = GhostFootprints.FlyoutSize(railK: 0.75, dpi: 2.0);
        Assert.Equal((44 + 2 + 230) * 0.75 * 2.0, w, 5); Assert.Equal(ExpectedRailH * 0.75 * 2.0, h, 5);
    }

    [Fact]
    public void RailOnlyThreshold_TracksRailScale()
    {
        Assert.Equal((44 + 2) * 1.0 * 1.0 + 1, GhostFootprints.RailOnlyThreshold(1.0, 1.0), 5);
        Assert.Equal((44 + 2) * 0.75 * 2.0 + 1, GhostFootprints.RailOnlyThreshold(0.75, 2.0), 5);
    }
}
