using NexusApp.Services.Map;
using Xunit;

namespace NexusApp.Tests;

// The rail on the overlay's TRADE cards (mock nexus-design-lab/overlay-trade, candidate B) paints
// from this one number. Null is the normal answer - the player's position is unknown far more often
// than it is known - and the caller draws a bare track for it, so "returns null" is as much a
// behaviour worth pinning here as any of the arithmetic.
public class RouteProgressTests
{
    [Fact]
    public void UnknownEitherEnd_ReturnsNull_SoTheRailShowsNoPosition()
    {
        Assert.Null(RouteProgress.Fraction(null, 5_000));
        Assert.Null(RouteProgress.Fraction(5_000, null));
        Assert.Null(RouteProgress.Fraction(null, null));
    }

    [Fact]
    public void StandingAtTheBuyStop_IsZero()
        => Assert.Equal(0, RouteProgress.Fraction(0, 12_000));

    [Fact]
    public void StandingAtTheSellStop_IsOne()
        => Assert.Equal(1, RouteProgress.Fraction(12_000, 0));

    [Fact]
    public void EquidistantFromBothStops_IsHalfway()
        => Assert.Equal(0.5, RouteProgress.Fraction(6_000, 6_000));

    [Fact]
    public void ItIsTheRatio_NotTheRawDistance()
    {
        // Three quarters of the way there, whatever the scale.
        Assert.Equal(0.75, RouteProgress.Fraction(300, 100));
        Assert.Equal(0.75, RouteProgress.Fraction(3_000_000_000, 1_000_000_000));
    }

    [Fact]
    public void BothStopsAtThePlayer_ReadsAsNothingTravelled_RatherThanDividingByZero()
        => Assert.Equal(0, RouteProgress.Fraction(0, 0));

    [Fact]
    public void NegativeReadings_ReturnNull_RatherThanAPositionOutsideTheRail()
    {
        // Distances are never negative today; this is the guard that keeps a future sign error
        // from painting a pip outside its own track.
        Assert.Null(RouteProgress.Fraction(-1, 100));
        Assert.Null(RouteProgress.Fraction(100, -1));
    }
}
