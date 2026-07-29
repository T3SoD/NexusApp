using NexusApp.Models;
using Xunit;

namespace NexusApp.Tests;

public class AppSettingsTradeFieldsTests
{
    [Fact]
    public void TradeFields_Defaults_MatchTheTradingTabSpec()
    {
        var s = new AppSettings();

        Assert.Equal("planner", s.TradeActiveFlow);
        Assert.Equal("", s.TradeShipId);
        Assert.Equal("", s.TradeOriginManual);
        Assert.Equal("ALL", s.TradeScope);
        Assert.True(s.TradeAnchorFromHere);
        Assert.False(s.SctDataEnabled);
    }
}
