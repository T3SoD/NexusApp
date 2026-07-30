using NexusApp.Views;
using Xunit;

namespace NexusApp.Tests;

public class TradeBarMathTests
{
    [Theory]
    [InlineData(1000, 500, "ok")]      // n >= tripQty
    [InlineData(500, 500, "ok")]       // exact boundary counts as ok
    [InlineData(300, 500, "amber")]    // n >= tripQty/2
    [InlineData(250, 500, "amber")]    // exact half boundary counts as amber
    [InlineData(100, 500, "danger")]   // below half
    public void Tier_MatchesMockThresholds(int n, int tripQty, string expected)
        => Assert.Equal(expected, TradeBarMath.Tier(n, tripQty));

    [Theory]
    [InlineData(1000, 500, 1.0)]   // capped at 1 (full bar), never overflows past the track
    [InlineData(250, 500, 0.5)]
    [InlineData(0, 500, 0.0)]
    public void FillFraction_IsMinOfRatioAndOne(int n, int tripQty, double expected)
        => Assert.Equal(expected, TradeBarMath.FillFraction(n, tripQty), 3);

    [Fact]
    public void FillFraction_ZeroTripQty_ReturnsFullBar_NoDivideByZero()
        // mock:579 guards this exact edge case (a cleared budget/qty field never produces a NaN bar).
        => Assert.Equal(1.0, TradeBarMath.FillFraction(50, 0), 3);
}
