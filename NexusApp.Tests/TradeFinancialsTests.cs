using NexusApp.Views;
using Xunit;

namespace NexusApp.Tests;

// TradeFinancials: the financial rail's pure folds (spec 2026-08-04 Option E). The two pinned
// routes are the live 2026-08-04 session's, the same pair the approved mock renders, so the
// implementation and the mock can be diffed number for number.
public class TradeFinancialsTests
{
    [Fact]
    public void Compute_RecycledMaterialCompositeRoute()
    {
        var (investment, gross, roi) = TradeFinancials.Compute(7587, 8500, 1440, 1_314_720);
        Assert.Equal(10_925_280, investment);
        // Sale PROCEEDS, sell price times quantity - deliberately NOT TradeRoute.Gross, which
        // RoutePlanner stores as the margin (equal to Net with no fee schedule). Painting that
        // model field here made GROSS read the same number as NET on every card (live 2026-08-04).
        Assert.Equal(12_240_000, gross);
        Assert.NotNull(roi);
        Assert.Equal("12.0%", TradeFinancials.RoiText(roi!.Value));
        Assert.Equal(RoiTier.Mid, TradeFinancials.Tier(roi.Value));
    }

    [Fact]
    public void Compute_AgriculturalSuppliesRoute()
    {
        var (investment, gross, roi) = TradeFinancials.Compute(1031, 1600, 1440, 819_360);
        Assert.Equal(1_484_640, investment);
        Assert.Equal(2_304_000, gross);
        Assert.Equal("55.2%", TradeFinancials.RoiText(roi!.Value));
        Assert.Equal(RoiTier.High, TradeFinancials.Tier(roi.Value));
    }

    // Silence over a guessed percentage when no capital is tied up: null, never NaN, never a throw.
    [Fact]
    public void Compute_ZeroInvestment_RoiIsNull()
    {
        var (investment, gross, roi) = TradeFinancials.Compute(0, 1600, 1440, 1000);
        Assert.Equal(0, investment);
        Assert.Equal(2_304_000, gross);
        Assert.Null(roi);
    }

    [Theory]
    [InlineData(0, RoiTier.None)]
    [InlineData(9.9, RoiTier.None)]
    [InlineData(10, RoiTier.Mid)]      // boundary: exactly 10 reads Mid
    [InlineData(24.9, RoiTier.Mid)]
    [InlineData(25, RoiTier.High)]     // boundary: exactly 25 reads High
    [InlineData(55.2, RoiTier.High)]
    public void Tier_Boundaries(double roi, RoiTier expected)
        => Assert.Equal(expected, TradeFinancials.Tier(roi));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(12, 0.24)]
    [InlineData(50, 1)]      // full scale
    [InlineData(80, 1)]      // pegs, never overflows the track
    [InlineData(-5, 0)]      // never negative
    public void MeterFraction_ClampsToFullScale(double roi, double expected)
        => Assert.Equal(expected, TradeFinancials.MeterFraction(roi), precision: 10);

    [Fact]
    public void RoiText_OneDecimalRounding()
        => Assert.Equal("7.5%", TradeFinancials.RoiText(7.4999));
}
