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
}
