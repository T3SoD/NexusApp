using NexusApp.Models;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Phase A of the trade/cargo fusion (spec 2026-08-09 section 4). The fold that lets the money
// surfaces say "converted into cargo" instead of "spent".
public class CargoValueTests
{
    private static CommodityTransaction Tx(TransactionKind kind, long amount, decimal scu,
                                           string guid = "g1", string? voided = null) => new()
    {
        TimestampUtc = new DateTime(2026, 8, 9, 12, 0, 0, DateTimeKind.Utc),
        Kind = kind, Amount = amount, Scu = scu, ResourceGuid = guid, Voided = voided,
    };

    [Fact]
    public void NoTransactions_HoldsNothing()
    {
        Assert.Empty(CargoValue.Held(Array.Empty<CommodityTransaction>()));
        Assert.Equal(0, CargoValue.TotalCost(Array.Empty<CommodityTransaction>()));
    }

    [Fact]
    public void BuyOnly_IsHeldAtWhatItCost()
    {
        var txs = new[] { Tx(TransactionKind.Buy, 693_600, 680m) };
        var held = Assert.Single(CargoValue.Held(txs));
        Assert.Equal(680m, held.Scu);
        Assert.Equal(693_600, held.Cost);
        Assert.Equal(693_600, CargoValue.TotalCost(txs));
    }

    [Fact]
    public void SellingHalf_LeavesHalfTheCostHeld()
    {
        var txs = new[]
        {
            Tx(TransactionKind.Buy, 693_600, 680m),
            Tx(TransactionKind.Sell, 418_880, 340m),
        };
        var held = Assert.Single(CargoValue.Held(txs));
        Assert.Equal(340m, held.Scu);
        Assert.Equal(346_800, held.Cost);   // 1020 aUEC/SCU average x 340 SCU
    }

    [Fact]
    public void SellingEverything_HoldsNothing()
    {
        var txs = new[]
        {
            Tx(TransactionKind.Buy, 693_600, 680m),
            Tx(TransactionKind.Sell, 837_760, 680m),
        };
        Assert.Empty(CargoValue.Held(txs));
        Assert.Equal(0, CargoValue.TotalCost(txs));
    }

    // Selling cargo bought in an EARLIER session is normal and must never produce negative cargo.
    [Fact]
    public void SellingMoreThanWasBought_ClampsToNothingRatherThanGoingNegative()
    {
        var txs = new[] { Tx(TransactionKind.Sell, 837_760, 680m) };
        Assert.Empty(CargoValue.Held(txs));
        Assert.Equal(0, CargoValue.TotalCost(txs));
    }

    [Fact]
    public void VoidedRows_NeverCount()
    {
        var txs = new[] { Tx(TransactionKind.Buy, 693_600, 680m, voided: "InsufficientFunds") };
        Assert.Empty(CargoValue.Held(txs));
    }

    [Fact]
    public void CommoditiesAreTrackedSeparately()
    {
        var txs = new[]
        {
            Tx(TransactionKind.Buy, 693_600, 680m, "scrap"),
            Tx(TransactionKind.Buy, 100_000, 100m, "laranite"),
            Tx(TransactionKind.Sell, 837_760, 680m, "scrap"),
        };
        var held = Assert.Single(CargoValue.Held(txs));
        Assert.Equal("laranite", held.ResourceGuid);
        Assert.Equal(100_000, CargoValue.TotalCost(txs));
    }

    [Fact]
    public void MultiplePartialBuys_AverageTheirCost()
    {
        var txs = new[]
        {
            Tx(TransactionKind.Buy, 200_000, 200m),   // 1000 aUEC/SCU
            Tx(TransactionKind.Buy, 240_000, 200m),   // 1200 aUEC/SCU
        };
        var held = Assert.Single(CargoValue.Held(txs));
        Assert.Equal(400m, held.Scu);
        Assert.Equal(440_000, held.Cost);
    }

    [Fact]
    public void ZeroScuBuy_DoesNotDivideByZero()
    {
        var txs = new[] { Tx(TransactionKind.Buy, 500, 0m) };
        Assert.Empty(CargoValue.Held(txs));
    }

    [Fact]
    public void HeldIsOrderedByCostDescending()
    {
        var txs = new[]
        {
            Tx(TransactionKind.Buy, 100_000, 100m, "cheap"),
            Tx(TransactionKind.Buy, 900_000, 300m, "dear"),
        };
        var held = CargoValue.Held(txs);
        Assert.Equal(new[] { "dear", "cheap" }, held.Select(h => h.ResourceGuid));
    }

    // The whole point of the phase: this fold must never grow a dependency on routes or terminals.
    [Fact]
    public void CargoValue_NeedsNoRouteOrTerminalContext()
    {
        var src = SourceFiles.ReadAppSource(@"Services\CargoValue.cs");
        Assert.DoesNotContain("AcceptedRoute", src);   // renamed from PinnedRoute 2026-08-09
        Assert.DoesNotContain("TerminalId", src);
        Assert.DoesNotContain("MarketSnapshot", src);
    }
}
