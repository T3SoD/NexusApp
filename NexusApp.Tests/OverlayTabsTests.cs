using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

public class OverlayTabsLabelTests
{
    [Fact]
    public void EveryTab_HasUppercaseLabel_NoCountSuffix()
    {
        foreach (var id in OverlayTabs.Ids)
        {
            var label = OverlayTabs.LabelFor(id);
            Assert.False(string.IsNullOrWhiteSpace(label));
            Assert.Equal(label.ToUpperInvariant(), label);
            Assert.DoesNotContain("(", label);
        }
        Assert.Equal("TRADE", OverlayTabs.LabelFor("trade"));
        Assert.Equal("HUB", OverlayTabs.LabelFor("stats"));
        Assert.Equal("REFINERY", OverlayTabs.LabelFor("orders"));
    }

    [Fact]
    public void SwitchLogLine_IsWinTagged()
    {
        Assert.Equal("[WIN] Overlay tab: scan -> guides", OverlayTabs.SwitchLogLine("scan", "guides"));
    }

    // Trade/cargo fusion spec, 2026-08-09 section 5: the HAULING tab's label became CARGO, but its
    // id must not change - it is persisted in AppSettings as the restored tab, and changing it would
    // reset every user's tab on upgrade.
    [Fact]
    public void HaulingId_IsUnchanged_ButLabelIsCargo()
    {
        Assert.Contains("hauling", OverlayTabs.Ids);
        Assert.Equal("CARGO", OverlayTabs.LabelFor("hauling"));
    }
}
