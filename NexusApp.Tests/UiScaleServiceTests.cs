using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Headless coverage for the UI scale clamp (issue #20). The clamp guards both slider restore
// and hand-edited settings.json values, so garbage input must always land on a usable scale.
public class UiScaleServiceTests
{
    [Theory]
    [InlineData(1.0, 1.0)]
    [InlineData(1.25, 1.25)]
    [InlineData(1.5, 1.5)]
    [InlineData(0.5, 1.0)]     // below Min clamps up
    [InlineData(2.0, 1.5)]     // above Max clamps down
    [InlineData(0.0, 1.0)]     // zero is not a scale
    [InlineData(-3.0, 1.0)]    // negative is not a scale
    [InlineData(double.NaN, 1.0)]  // NaN would poison Math.Clamp; must fall back to 1.0
    public void ClampScale_LandsInRange(double input, double expected)
        => Assert.Equal(expected, UiScaleService.ClampScale(input));

    [Fact]
    public void Range_IsOneToOnePointFive()
    {
        Assert.Equal(1.0, UiScaleService.Min);
        Assert.Equal(1.5, UiScaleService.Max);
        Assert.Equal(0.05, UiScaleService.Step);
    }

    // The ghost rail's own scale is independent of the overlay panel's and may go SMALLER
    // than 1.0 (the point of the rail is a minimal footprint), so it has its own floor.
    [Theory]
    [InlineData(double.NaN, 1.0)]
    [InlineData(-1.0, 1.0)]
    [InlineData(0.0, 1.0)]
    [InlineData(0.5, 0.75)]
    [InlineData(0.75, 0.75)]
    [InlineData(1.2, 1.2)]
    [InlineData(2.0, 1.5)]
    public void ClampRailScale_DefendsRange(double input, double expected)
        => Assert.Equal(expected, UiScaleService.ClampRailScale(input), 5);

    [Fact]
    public void RailRange_FloorIsBelowOne()
    {
        Assert.Equal(0.75, UiScaleService.RailMin);
        Assert.True(UiScaleService.RailMin < UiScaleService.Min);
    }
}
