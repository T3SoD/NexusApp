using NexusApp.Services;
using NexusApp.Views;
using Xunit;

namespace NexusApp.Tests;

public class TradeBadgesTests
{
    [Fact]
    public void Corroborated_TextAndTooltip_Verbatim()
    {
        Assert.Equal("CORROBORATED", TradeBadges.Text(PriceSourceState.Corroborated, 0));
        Assert.Equal("2 sources agree within 3 percent, both under 48h", TradeBadges.Tooltip(PriceSourceState.Corroborated, 0));
    }

    [Theory]
    [InlineData(8.6, "SOURCES DISAGREE +8.6%")]
    [InlineData(3.0, "SOURCES DISAGREE +3%")]
    public void Disagree_TextIncludesPercent(double pct, string expected)
        => Assert.Equal(expected, TradeBadges.Text(PriceSourceState.Disagree, pct));

    [Fact]
    public void Disagree_Tooltip_NamesBothSourcesAndPercent()
        => Assert.Equal("UEX and SCT differ by 8.6 percent, price shown here is UEX's.",
            TradeBadges.Tooltip(PriceSourceState.Disagree, 8.6));

    [Fact]
    public void SctOnly_TextAndTooltip_Verbatim()
    {
        Assert.Equal("SCT ONLY", TradeBadges.Text(PriceSourceState.SctOnly, 0));
        Assert.Equal("SC Trade Tools only, no second source confirms this price.", TradeBadges.Tooltip(PriceSourceState.SctOnly, 0));
    }

    [Fact]
    public void UexOnly_RendersNoBadge()
        // UexOnly is the common case (SCT dark, or no SCT match) - never a placeholder badge.
        => Assert.Null(TradeBadges.Text(PriceSourceState.UexOnly, 0));
}
