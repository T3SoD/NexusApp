using System;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

public class MarketMergeTests
{
    private static readonly DateTime Now = new(2026, 8, 3, 18, 0, 0, DateTimeKind.Utc);
    private static DateTime AgoHours(double h) => Now.AddHours(-h);

    [Fact]
    public void PrefersSct_WhenItIsNewer()
    {
        // The owner's own example: UEX two days old, SCT one hour old.
        var r = MarketMerge.Choose(3000, 100, AgoHours(48), 3100, 250, AgoHours(1), Now);
        Assert.True(r.FromSct);
        Assert.Equal(3100, r.Price);
        Assert.Equal(250, r.QuantityScu);
        Assert.Equal(AgoHours(1), r.AsOfUtc);
    }

    [Fact]
    public void KeepsUex_WhenItIsNewer()
    {
        var r = MarketMerge.Choose(3000, 100, AgoHours(1), 3100, 250, AgoHours(48), Now);
        Assert.False(r.FromSct);
        Assert.Equal(3000, r.Price);
        Assert.Equal(100, r.QuantityScu);
    }

    [Fact]
    public void KeepsUex_OnAnExactTie()
    {
        // No reason to switch source when neither is newer, and UEX is the app's backbone: it also
        // supplies the container sizes and the terminal identity the row is built on.
        var t = AgoHours(5);
        Assert.False(MarketMerge.Choose(3000, 100, t, 3100, 250, t, Now).FromSct);
    }

    [Fact]
    public void KeepsUex_WhenThereIsNoSctReading()
    {
        var r = MarketMerge.Choose(3000, 100, AgoHours(48), null, null, null, Now);
        Assert.False(r.FromSct);
        Assert.Equal(3000, r.Price);
    }

    [Fact]
    public void IgnoresAZeroSctPrice_EvenWhenNewer()
    {
        // Zero is not a cheaper price, it is the absence of one. Substituting it would delete a
        // route UEX can still price.
        var r = MarketMerge.Choose(3000, 100, AgoHours(48), 0, 500, AgoHours(1), Now);
        Assert.False(r.FromSct);
        Assert.Equal(3000, r.Price);
    }

    [Fact]
    public void NeverPromotesAnSctOnlyReading()
    {
        // With no usable UEX price this side is not traded as far as the terminal list is
        // concerned. Substituting is in scope; inventing a rankable route from one source is not.
        var r = MarketMerge.Choose(0, 0, AgoHours(48), 3100, 250, AgoHours(1), Now);
        Assert.False(r.FromSct);
        Assert.Equal(0, r.Price);
    }

    [Fact]
    public void IgnoresAFutureSctStamp()
    {
        // A stamp ahead of now is a clock problem, not freshness - the same stance the two refresh
        // schedulers already take.
        var r = MarketMerge.Choose(3000, 100, AgoHours(48), 3100, 250, Now.AddHours(2), Now);
        Assert.False(r.FromSct);
    }

    [Fact]
    public void TakesPriceAndQuantityFromTheSameObservation()
    {
        // The guard that matters most: never UEX's price beside SCT's stock. That pair never
        // existed at any one moment, and the trip size and the profit would then be computed from
        // different snapshots of the terminal.
        var r = MarketMerge.Choose(3000, 100, AgoHours(48), 3100, 250, AgoHours(1), Now);
        Assert.True(r.FromSct);
        Assert.Equal(3100, r.Price);
        Assert.Equal(250, r.QuantityScu);

        var keep = MarketMerge.Choose(3000, 100, AgoHours(1), 3100, 250, AgoHours(48), Now);
        Assert.False(keep.FromSct);
        Assert.Equal(3000, keep.Price);
        Assert.Equal(100, keep.QuantityScu);
    }

    [Fact]
    public void TreatsAMissingSctQuantityAsZero_NotNegative()
    {
        // SCT reporting a price with no quantity is a real shape (its "quantity 0" rows are common).
        // Zero stock is a legitimate reading; a negative one would corrupt the trip-size maths.
        var r = MarketMerge.Choose(3000, 100, AgoHours(48), 3100, null, AgoHours(1), Now);
        Assert.True(r.FromSct);
        Assert.Equal(0, r.QuantityScu);

        var neg = MarketMerge.Choose(3000, 100, AgoHours(48), 3100, -5, AgoHours(1), Now);
        Assert.Equal(0, neg.QuantityScu);
    }

    [Fact]
    public void AsOfAlwaysMatchesTheChosenSource()
    {
        // The freshness pill reads this, so it must never show UEX's age beside SCT's numbers.
        var sct = MarketMerge.Choose(3000, 100, AgoHours(48), 3100, 250, AgoHours(1), Now);
        Assert.Equal(AgoHours(1), sct.AsOfUtc);

        var uex = MarketMerge.Choose(3000, 100, AgoHours(1), 3100, 250, AgoHours(48), Now);
        Assert.Equal(AgoHours(1), uex.AsOfUtc);
    }
}
