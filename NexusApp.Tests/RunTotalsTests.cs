using NexusApp.Models;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// THIS RUN PAYS and COMMITTED (trade/cargo fusion spec 2026-08-09, sections 3.6 and 3.7).
public class RunTotalsTests
{
    private static AcceptedRoute Route(int tripQty = 750, int? actualQty = null,
        double perScuMargin = 212, AcceptedStage stage = AcceptedStage.Accepted,
        int? buyTerminalId = 7) => new()
    {
        BuyTerminalId = buyTerminalId, SellTerminalId = 9, CommodityId = 1, CommodityName = "Scrap",
        BuyTerminalName = "Everus Harbor", SellTerminalName = "Baijini Point",
        TripQty = tripQty, ActualQty = actualQty, PerScuMargin = perScuMargin, Stage = stage,
    };

    // ---- THIS RUN PAYS ------------------------------------------------------------------------

    [Fact]
    public void NothingAccepted_PaysNothing()
    {
        var pays = RunTotals.Pays(Array.Empty<int>(), Array.Empty<AcceptedRoute>());
        Assert.Equal(0, pays.ContractRewards);
        Assert.Equal(0, pays.RouteMargin);
        Assert.Equal(0, pays.ContractsWithUnknownReward);
        Assert.Equal(0, pays.UnpricedRoutes);
    }

    // The halves are NEVER summed into one figure. A reward is durable; a margin is recomputed from
    // live prices every market tick. The record exposes them separately and has no total property.
    [Fact]
    public void RewardsAndMargin_AreCountedApart()
    {
        var pays = RunTotals.Pays(new[] { 120_000, 80_000 }, new[] { Route(tripQty: 100, perScuMargin: 50) });
        Assert.Equal(200_000, pays.ContractRewards);
        Assert.Equal(5_000, pays.RouteMargin);
    }

    // Haul.Reward is OCR-sourced and its own model comment says "0 = unknown". Counting it as zero
    // pay would understate the run and read as a bug to anyone holding a contract they know pays.
    [Fact]
    public void AZeroReward_IsUnknownNotFreeWork()
    {
        var pays = RunTotals.Pays(new[] { 120_000, 0, 0 }, Array.Empty<AcceptedRoute>());
        Assert.Equal(120_000, pays.ContractRewards);
        Assert.Equal(2, pays.ContractsWithUnknownReward);
    }

    [Fact]
    public void ANegativeReward_IsTreatedAsUnknownToo()
    {
        var pays = RunTotals.Pays(new[] { -5 }, Array.Empty<AcceptedRoute>());
        Assert.Equal(0, pays.ContractRewards);
        Assert.Equal(1, pays.ContractsWithUnknownReward);
    }

    [Fact]
    public void ALoadedRoute_UsesItsCorrectedQuantity()
    {
        var pays = RunTotals.Pays(Array.Empty<int>(),
            new[] { Route(tripQty: 750, actualQty: 680, perScuMargin: 212, stage: AcceptedStage.Loaded) });
        Assert.Equal(680 * 212L, pays.RouteMargin);
    }

    // Same exclusion, same reason as AcceptedRouteMoney.ExpectedMargin: with no buy leg,
    // PerScuMargin holds a raw sell PRICE, and adding a price to a pile of margins inflates the
    // total by an entire cargo's gross revenue. Counted instead of silently dropped.
    [Fact]
    public void ASellOnlyRoute_IsNotPricedButIsCounted()
    {
        var pays = RunTotals.Pays(Array.Empty<int>(), new[]
        {
            Route(tripQty: 96, perScuMargin: 421, stage: AcceptedStage.Loaded, buyTerminalId: null),
            Route(tripQty: 100, perScuMargin: 50),
        });
        Assert.Equal(5_000, pays.RouteMargin);   // the planner route only
        Assert.Equal(1, pays.UnpricedRoutes);
    }

    [Fact]
    public void ASoldRoute_HasAlreadyPaid_AndDoesNotCountAgain()
    {
        var pays = RunTotals.Pays(Array.Empty<int>(),
            new[] { Route(tripQty: 100, perScuMargin: 50, stage: AcceptedStage.Sold) });
        Assert.Equal(0, pays.RouteMargin);
    }

    // ---- COMMITTED ----------------------------------------------------------------------------

    [Fact]
    public void CommittedSumsRoutesAndContracts()
    {
        var load = RunTotals.Committed(new[] { Route(tripQty: 500) }, contractScu: 64, shipScu: 696);
        Assert.Equal(500, load.RouteScu);
        Assert.Equal(64, load.ContractScu);
        Assert.Equal(564, load.TotalScu);
    }

    [Fact]
    public void CommittedUsesTheCorrectedQuantityOnceABuyHasLanded()
    {
        var load = RunTotals.Committed(
            new[] { Route(tripQty: 750, actualQty: 680, stage: AcceptedStage.Loaded) }, 0, 696);
        Assert.Equal(680, load.RouteScu);
    }

    [Fact]
    public void ASoldRoute_CommitsYouToNothing()
    {
        var load = RunTotals.Committed(new[] { Route(stage: AcceptedStage.Sold) }, 0, 696);
        Assert.Equal(0, load.RouteScu);
    }

    [Fact]
    public void FittingInOneRun_IsOneRun_AndIsNotFlagged()
    {
        var load = RunTotals.Committed(new[] { Route(tripQty: 500) }, 0, 696);
        Assert.Equal(1, load.Runs);
        Assert.False(load.ExceedsOneRun);
    }

    // The flag the section exists for: more cargo than the hull carries.
    [Fact]
    public void MoreThanTheHullCarries_RoundsUpAndFlags()
    {
        var load = RunTotals.Committed(new[] { Route(tripQty: 700) }, 0, 696);
        Assert.Equal(2, load.Runs);
        Assert.True(load.ExceedsOneRun);
    }

    [Fact]
    public void ExactlyThreeHullsWorth_IsThreeRuns_NotFour()
    {
        var load = RunTotals.Committed(new[] { Route(tripQty: 300) }, contractScu: 0, shipScu: 100);
        Assert.Equal(3, load.Runs);
    }

    // No ship picked: the question "does this fit" has no answer, and a guessed one would be worse
    // than the page saying so. Same absence convention the money folds follow.
    [Fact]
    public void NoShipSelected_HasNoRunCount()
    {
        var load = RunTotals.Committed(new[] { Route(tripQty: 500) }, 0, shipScu: null);
        Assert.Null(load.Runs);
        Assert.False(load.ExceedsOneRun);
    }

    [Fact]
    public void NothingCommitted_HasNoRunCount_EvenWithAShip()
    {
        var load = RunTotals.Committed(Array.Empty<AcceptedRoute>(), 0, 696);
        Assert.Null(load.Runs);
    }

    [Fact]
    public void ANegativeContractTotal_ClampsToZero()
    {
        var load = RunTotals.Committed(Array.Empty<AcceptedRoute>(), contractScu: -10, shipScu: 696);
        Assert.Equal(0, load.ContractScu);
    }

    // ---- source pins --------------------------------------------------------------------------

    // Every contract leg appears twice in the consolidation, once to collect and once to deliver.
    // Summing both sides would double every contract's committed load.
    [Fact]
    public void CargoHauling_CountsContractScuFromPickupsOnly()
    {
        var src = SourceFiles.ReadAppSource(@"Views\HaulingPage.cs");
        Assert.Contains("BuildConsolidation().Pickups.Sum(p => p.TotalScu)", src);
        Assert.DoesNotContain("Dropoffs.Sum", src);
    }

    // COMMITTED measures against the hull the user picked in the planner, not a default.
    [Fact]
    public void CargoHauling_MeasuresAgainstThePlannersShip()
    {
        var src = SourceFiles.ReadAppSource(@"Views\HaulingPage.cs");
        Assert.Contains("Ships.ById(App.Settings.Current.TradeShipId)", src);
    }

    // The page renders all three new sections, in the spec's order.
    [Fact]
    public void CargoHauling_RendersStopsThenTotalsThenFinished()
    {
        var src = SourceFiles.ReadAppSource(@"Views\HaulingPage.cs");
        var stops = src.IndexOf("RenderStops();", StringComparison.Ordinal);
        var totals = src.IndexOf("RenderRunTotals();", StringComparison.Ordinal);
        var finished = src.IndexOf("RenderFinished();", StringComparison.Ordinal);
        Assert.True(stops > 0 && totals > stops && finished > totals,
                    "sections must render in the spec's order");
    }

    // The empty state used to turn on contracts alone, so a hauler with routes and no contract saw
    // "No active hauls" and never reached these three sections at all.
    [Fact]
    public void CargoHauling_IsOnlyEmptyWhenBothHalvesAreEmpty()
    {
        var src = SourceFiles.ReadAppSource(@"Views\HaulingPage.cs");
        Assert.Contains("App.Hauls.AllHauls.Count == 0 && routes.Count == 0", src);
    }
}
