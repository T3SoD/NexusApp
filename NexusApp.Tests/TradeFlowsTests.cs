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
}
