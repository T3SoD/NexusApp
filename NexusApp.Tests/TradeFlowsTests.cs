using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

public class TradeFlowsTests
{
    [Fact]
    public void Ids_PlannerFirst_MatchesTabOrder()
    {
        // Planner first: the tab strip's default tab and the mock's FLOWS array both put
        // Planner first (mock index.html:1046-1050).
        Assert.Equal(new[] { "planner", "sell", "prices" }, TradeFlows.Ids);
    }

    [Theory]
    [InlineData("sell", "sell")]
    [InlineData("prices", "prices")]
    [InlineData("planner", "planner")]
    [InlineData("bogus", "planner")]
    [InlineData(null, "planner")]
    [InlineData("", "planner")]
    public void NormalizeForRestore_WhitelistsKnownIdsOnly(string? saved, string expected)
        => Assert.Equal(expected, TradeFlows.NormalizeForRestore(saved));

    // UEX /commodities_status codes 1-7 (TradePriceRow.StatusBuy), owner-approved wording
    // (task-14 review finding 1). Code 0 and any out-of-range code both mean "no status report" -
    // a plain dash, not a fabricated state.
    [Theory]
    [InlineData(1, "OUT OF STOCK")]
    [InlineData(2, "VERY LOW")]
    [InlineData(3, "LOW")]
    [InlineData(4, "MEDIUM")]
    [InlineData(5, "HIGH")]
    [InlineData(6, "VERY HIGH")]
    [InlineData(7, "MAX INVENTORY")]
    [InlineData(0, "-")]
    [InlineData(8, "-")]
    public void BuyStatusLabel_MapsUexCodes_OrDashWhenUnknown(int statusCode, string expected)
        => Assert.Equal(expected, TradeFlows.BuyStatusLabel(statusCode));
}
