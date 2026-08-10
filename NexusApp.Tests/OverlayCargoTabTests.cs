using Xunit;

namespace NexusApp.Tests;

// Task C (overlay half), trade/cargo fusion spec, 2026-08-09 section 5: the PLANNER | PINNED
// segmented control is deleted and accepted routes fold into the CARGO tab (id "hauling", label
// "CARGO"). OverlayWindow cannot be constructed under test (App statics), so these pin the source
// text - the same SourceFiles.ReadAppSource idiom OverlayClearHistoryTests and ConversionDisplayTests
// already use.
public class OverlayCargoTabTests
{
    private static string Src() => SourceFiles.ReadAppSource(@"Views\OverlayWindow.xaml.cs");

    [Fact]
    public void TradeMode_NoLongerExists()
    {
        Assert.DoesNotContain("_tradeMode", Src());
    }

    [Fact]
    public void PlannerPinnedSegmentedControl_IsGone()
    {
        Assert.DoesNotContain("new[] { \"PLANNER\", \"PINNED\" }", Src());
        Assert.DoesNotContain("private void BuildPinnedSection", Src());
    }

    // The PINNED mode's own empty state named a destination ("PINNED") that no longer exists as a
    // display mode; the CARGO tab's ACCEPTED ROUTES section is instead simply omitted when empty.
    [Fact]
    public void OldPinnedEmptyState_IsGone()
    {
        Assert.DoesNotContain(
            "No routes pinned. Pin one in Trade > Planner and it shows here and on the Starmap.",
            Src());
    }

    [Fact]
    public void CargoTab_RendersAcceptedRoutesSection()
    {
        var src = Src();
        Assert.Contains("ACCEPTED ROUTES (", src);
        Assert.Contains("BuildAcceptedStagePips", src);
    }

    // Empty states must not compete (spec section 5 / Task C step 4): "No active hauls." is the
    // ONLY empty-state line on the tab - the accepted-routes section renders nothing at all, not a
    // second muted line, when there is nothing accepted.
    [Fact]
    public void NoActiveHaulsEmptyState_AppearsExactlyOnce()
    {
        var src = Src();
        var occurrences = src.Split("No active hauls.").Length - 1;
        Assert.Equal(1, occurrences);
    }
}
