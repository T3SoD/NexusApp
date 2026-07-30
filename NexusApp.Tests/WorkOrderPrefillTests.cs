using NexusApp.Models;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// WorkOrderPrefill's real shape deviates from the task brief per architect resolution: it takes
// an IReadOnlyList<MarketCommodity> directly (no MarketSnapshot wrapper) and adds LatestCompleted
// to pick the work order to prefill from. See WorkOrderPrefill.cs for the accessibility note (the
// class stays internal, not public, because MarketCommodity itself is internal - CS0051).
public class WorkOrderPrefillTests
{
    // Bexalite (Raw, id 10, idParent 11) -> Bexalite (refined, id 11). Gold (Ore, id 20,
    // idParent 21) -> Gold (refined, id 21). Mirrors MarketNameMapTests/MarketDataServiceTests.
    private static List<MarketCommodity> Commodities() => new()
    {
        new(10, "Bexalite (Raw)", "bexalite-raw", true, false, 11),
        new(11, "Bexalite", "bexalite", false, true, 0),
        new(20, "Gold (Ore)", "gold-ore", true, false, 21),
        new(21, "Gold", "gold", false, true, 0),
    };

    private static WorkOrder Order(string resources, WorkOrderStatus status = WorkOrderStatus.Complete) =>
        new() { Resources = resources, Status = status };

    // --- ResolveCommodityId -----------------------------------------------------

    [Fact]
    public void ResolveCommodityId_RecognizedSeedName_ResolvesToRefinedCommodityId()
    {
        Assert.Equal(11, WorkOrderPrefill.ResolveCommodityId(Order("Bexalite"), Commodities()));
    }

    [Fact]
    public void ResolveCommodityId_MultipleResourcesFreeText_ResolvesTheFirstRecognizedOnly()
    {
        // RecognizeSeedNames preserves first-occurrence order; the resolver takes only the first.
        Assert.Equal(21, WorkOrderPrefill.ResolveCommodityId(Order("Gold + Bexalite"), Commodities()));
    }

    [Fact]
    public void ResolveCommodityId_UnrecognizedFreeText_ReturnsNull()
    {
        Assert.Null(WorkOrderPrefill.ResolveCommodityId(Order("Space Junk"), Commodities()));
    }

    [Fact]
    public void ResolveCommodityId_EmptyResources_ReturnsNull()
    {
        Assert.Null(WorkOrderPrefill.ResolveCommodityId(Order(""), Commodities()));
    }

    [Fact]
    public void ResolveCommodityId_RawRecognizedButNotInCommodityList_ReturnsNull()
    {
        // "Laranite" is a real seed->UEX mapping, but this commodity list doesn't carry it at all
        // (only Bexalite/Gold), so the raw-name lookup fails cleanly.
        Assert.Null(WorkOrderPrefill.ResolveCommodityId(Order("Laranite"), Commodities()));
    }

    [Fact]
    public void ResolveCommodityId_EmptyCommodityList_ReturnsNull()
    {
        Assert.Null(WorkOrderPrefill.ResolveCommodityId(Order("Bexalite"), new List<MarketCommodity>()));
    }

    // --- LatestCompleted ----------------------------------------------------------

    [Fact]
    public void LatestCompleted_NoOrders_ReturnsNull()
    {
        Assert.Null(WorkOrderPrefill.LatestCompleted(new List<WorkOrder>()));
    }

    [Fact]
    public void LatestCompleted_NoCompletedOrders_ReturnsNull()
    {
        var orders = new List<WorkOrder>
        {
            Order("Bexalite", WorkOrderStatus.Mining),
            Order("Gold", WorkOrderStatus.Refining),
            Order("Gold", WorkOrderStatus.ReadyToCollect),
        };

        Assert.Null(WorkOrderPrefill.LatestCompleted(orders));
    }

    [Fact]
    public void LatestCompleted_SingleCompleted_ReturnsIt()
    {
        var order = Order("Bexalite");

        Assert.Same(order, WorkOrderPrefill.LatestCompleted(new List<WorkOrder> { order }));
    }

    [Fact]
    public void LatestCompleted_MultipleCompleted_ReturnsNewestByCreatedAt()
    {
        var older = Order("Gold");
        older.CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var newer = Order("Bexalite");
        newer.CreatedAt = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);

        var result = WorkOrderPrefill.LatestCompleted(new List<WorkOrder> { older, newer });

        Assert.Same(newer, result);
    }

    [Fact]
    public void LatestCompleted_IgnoresNonCompletedEvenIfCreatedLater()
    {
        // A Complete order stays the prefill source even when a newer, still-in-flight order
        // exists: CreatedAt only breaks ties within the Complete set, it never overrides Status.
        var complete = Order("Gold");
        complete.CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var refiningLater = Order("Bexalite", WorkOrderStatus.Refining);
        refiningLater.CreatedAt = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);

        var result = WorkOrderPrefill.LatestCompleted(new List<WorkOrder> { complete, refiningLater });

        Assert.Same(complete, result);
    }
}
