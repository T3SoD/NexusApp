using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

public class SctDeltaTests
{
    [Theory]
    [InlineData(100, 110, 10)]     // SCT higher
    [InlineData(100, 90, -10)]     // SCT lower
    [InlineData(100, 100, 0)]      // exact agreement, the common case on live data
    [InlineData(3705, 3705, 0)]    // a real captured buy price, both sources agreeing
    [InlineData(200, 0, -100)]     // SCT saw none: comparable, and worth reading
    public void Pct_IsSignedFromUexsPointOfView(double uex, double sct, double expected)
        => Assert.Equal(expected, SctDelta.Pct(uex, sct)!.Value, 6);

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Pct_NullWhenUexHasNoUsableBaseline(double uex)
    {
        // A UEX zero is a real, common state (a terminal that neither buys nor sells today), and
        // it is also the divisor. There is no percentage to state.
        Assert.Null(SctDelta.Pct(uex, 500));
    }

    [Theory]
    [InlineData(0, "0%")]
    [InlineData(0.04, "0%")]        // below the rounding floor: not "+0.0%"
    [InlineData(-0.04, "0%")]
    [InlineData(3.21, "+3.2%")]
    [InlineData(-11.04, "-11.0%")]
    [InlineData(0.05, "+0.1%")]
    [InlineData(250.4, "+250%")]    // no decimal on a wild disagreement
    [InlineData(-3000, "-3000%")]
    public void Format_SignsExplicitlyAndAvoidsFalsePrecision(double pct, string expected)
        => Assert.Equal(expected, SctDelta.Format(pct));

    [Fact]
    public void Format_NeverReportsASignedZero()
    {
        // Guards the specific readability trap: the sources agreeing exactly is the median case on
        // live data, and "+0.0%" would read as a real difference that happened to round away.
        Assert.Equal("0%", SctDelta.Format(SctDelta.Pct(4515, 4515)!.Value));
        Assert.DoesNotContain("+", SctDelta.Format(0.001));
        Assert.DoesNotContain("-", SctDelta.Format(-0.001));
    }

    [Fact]
    public void PctAndFormat_ComposeOnRealCapturedNumbers()
    {
        // Aluminum, captured 2026-08-03: bought at 3,128/SCU. A second source reporting 3,570
        // is +14.1% against it.
        var pct = SctDelta.Pct(3128, 3570)!.Value;
        Assert.Equal("+14.1%", SctDelta.Format(pct));
    }
}
