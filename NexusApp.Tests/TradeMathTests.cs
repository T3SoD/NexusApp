using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

public class TradeMathTests
{
    // --- TripQty ---------------------------------------------------------

    [Fact]
    public void TripQty_ShipIsTheSmallest_ReturnsShipScu()
    {
        Assert.Equal(10, TradeMath.TripQty(shipScu: 10, buyStockScu: 500, budget: null, buyPrice: 100));
    }

    [Fact]
    public void TripQty_StockIsTheSmallest_ReturnsBuyStockScu()
    {
        Assert.Equal(20, TradeMath.TripQty(shipScu: 500, buyStockScu: 20, budget: null, buyPrice: 100));
    }

    [Fact]
    public void TripQty_BudgetIsTheSmallest_ReturnsFloorOfBudgetOverPrice()
    {
        Assert.Equal(15, TradeMath.TripQty(shipScu: 500, buyStockScu: 500, budget: 1550, buyPrice: 100));
    }

    [Fact]
    public void TripQty_NoBudget_IgnoresTheBudgetTerm()
    {
        Assert.Equal(500, TradeMath.TripQty(shipScu: 500, buyStockScu: 500, budget: null, buyPrice: 100));
    }

    [Fact]
    public void TripQty_BuyPriceZero_IgnoresTheBudgetTermEvenWithABudget()
    {
        Assert.Equal(500, TradeMath.TripQty(shipScu: 500, buyStockScu: 500, budget: 1000, buyPrice: 0));
    }

    [Fact]
    public void TripQty_BuyPriceNegative_IgnoresTheBudgetTerm()
    {
        Assert.Equal(500, TradeMath.TripQty(shipScu: 500, buyStockScu: 500, budget: 1000, buyPrice: -5));
    }

    [Fact]
    public void TripQty_RealLaraniteNumbers_HdmsLathanBuyLeg()
    {
        // HDMS-Lathan, real capture: scu_buy 1050, price_buy 7541.
        Assert.Equal(100, TradeMath.TripQty(shipScu: 100, buyStockScu: 1050, budget: null, buyPrice: 7541));
        Assert.Equal(1050, TradeMath.TripQty(shipScu: 5000, buyStockScu: 1050, budget: null, buyPrice: 7541));
        Assert.Equal(198, TradeMath.TripQty(shipScu: 5000, buyStockScu: 1050, budget: 1_500_000, buyPrice: 7541));
    }

    // --- TripParts ---------------------------------------------------------

    [Fact]
    public void TripParts_NoBudget_OmitsTheBudgetPart()
    {
        var parts = TradeMath.TripParts(shipScu: 100, buyStockScu: 1050, budget: null, buyPrice: 7541);
        Assert.Equal(new[] { "ship 100", "stock 1,050" }, parts);
    }

    [Fact]
    public void TripParts_WithBudget_AppendsTheBudgetPart()
    {
        var parts = TradeMath.TripParts(shipScu: 5000, buyStockScu: 1050, budget: 1_500_000, buyPrice: 7541);
        Assert.Equal(new[] { "ship 5,000", "stock 1,050", "budget affords 198" }, parts);
    }

    [Fact]
    public void TripParts_BuyPriceNonPositive_OmitsBudgetPartEvenWithABudget()
    {
        var parts = TradeMath.TripParts(shipScu: 100, buyStockScu: 50, budget: 1000, buyPrice: 0);
        Assert.Equal(new[] { "ship 100", "stock 50" }, parts);
    }

    // --- BoxFits -----------------------------------------------------------

    [Theory]
    [InlineData("1,2,4,8,16,24,32", 8, true)]     // 1/2/4/8 all fit
    [InlineData("24,32", 8, false)]                 // nothing small enough
    [InlineData("1,2,4,8,16", 32, true)]            // ship can take more than the terminal offers
    public void BoxFits_AnySizeAtOrBelowShipMax_DecidesFit(string sizes, int shipMaxBox, bool expected) =>
        Assert.Equal(expected, TradeMath.BoxFits(sizes, shipMaxBox));

    [Fact]
    public void BoxFits_EmptyOrWhitespace_ReturnsFalse()
    {
        Assert.False(TradeMath.BoxFits("", 32));
        Assert.False(TradeMath.BoxFits("   ", 32));
    }

    [Fact]
    public void BoxFits_GarbageTokensIgnored_ValidTokensStillCount()
    {
        Assert.True(TradeMath.BoxFits("abc,,8,xyz", 8));
    }

    [Fact]
    public void BoxFits_ZeroOrNegativeSizeToken_Ignored()
    {
        Assert.False(TradeMath.BoxFits("0,-4", 32));
    }
}
