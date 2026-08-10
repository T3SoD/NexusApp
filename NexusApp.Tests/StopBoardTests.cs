using NexusApp.Models;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// The merged stop board (trade/cargo fusion spec 2026-08-09, section 3.5). Contract legs and
// accepted-route sales become ONE list, one entry per thing that happens, grouped by place.
public class StopBoardTests
{
    private static Consolidation Con(
        (string Location, string Commodity, int Scu)[]? pickups = null,
        (string Location, string Commodity, int Scu)[]? dropoffs = null)
    {
        var con = new Consolidation();
        foreach (var group in (pickups ?? Array.Empty<(string, string, int)>()).GroupBy(p => p.Location))
        {
            var stop = new ConsolidationStop { Location = group.Key };
            foreach (var p in group) stop.Items.Add((p.Commodity, p.Scu, "m1"));
            con.Pickups.Add(stop);
        }
        foreach (var group in (dropoffs ?? Array.Empty<(string, string, int)>()).GroupBy(d => d.Location))
        {
            var stop = new ConsolidationStop { Location = group.Key };
            foreach (var d in group) stop.Items.Add((d.Commodity, d.Scu, "m1"));
            con.Dropoffs.Add(stop);
        }
        return con;
    }

    private static AcceptedRoute Route(string sellTerminal, string commodity = "Scrap",
        int tripQty = 750, int? actualQty = null, AcceptedStage stage = AcceptedStage.Accepted,
        int? buyTerminalId = 7) => new()
    {
        BuyTerminalId = buyTerminalId, SellTerminalId = 9, CommodityId = 1,
        CommodityName = commodity, BuyTerminalName = "Everus Harbor", SellTerminalName = sellTerminal,
        TripQty = tripQty, ActualQty = actualQty, PerScuMargin = 212, Stage = stage,
    };

    [Fact]
    public void NothingAtAll_IsAnEmptyBoard()
        => Assert.Empty(StopBoard.Merge(Con(), Array.Empty<AcceptedRoute>()));

    [Fact]
    public void ContractsOnly_KeepTheirCollectAndDeliverActions()
    {
        var board = StopBoard.Merge(
            Con(pickups: new[] { ("Everus Harbor", "Titanium", 32) },
                dropoffs: new[] { ("Area 18", "Titanium", 32) }),
            Array.Empty<AcceptedRoute>());

        Assert.Equal(2, board.Count);
        Assert.Equal(StopAction.Collect, board[0].Entries.Single().Action);
        Assert.Equal(StopAction.Deliver, board[1].Entries.Single().Action);
    }

    [Fact]
    public void AnAcceptedRoute_AddsASellAtItsSellTerminal()
    {
        var board = StopBoard.Merge(Con(), new[] { Route("Baijini Point") });

        var stop = Assert.Single(board);
        Assert.Equal("Baijini Point", stop.Location);
        var entry = Assert.Single(stop.Entries);
        Assert.Equal(StopAction.Sell, entry.Action);
        Assert.Equal("Scrap", entry.Commodity);
        Assert.Equal(750, entry.Scu);
    }

    // The point of the section: one place, one row group, whatever the source.
    [Fact]
    public void AContractStopAndARouteSale_AtTheSamePlace_AreOneStop()
    {
        var board = StopBoard.Merge(
            Con(pickups: new[] { ("Everus Harbor", "Titanium", 32) }),
            new[] { Route("Everus Harbor") });

        var stop = Assert.Single(board);
        Assert.Equal(2, stop.Entries.Count);
        Assert.Equal(782, stop.TotalScu);   // 32 contract + 750 route
    }

    // Contract stops are free text from OCR or Game.log legs; a route's sell leg is a UEX terminal
    // name. The two vocabularies will not agree on case, and a case split would silently produce
    // two stops for one place, which is exactly the cross-referencing this section removes.
    [Fact]
    public void LocationsMatchIgnoringCaseAndSurroundingSpace()
    {
        var board = StopBoard.Merge(
            Con(pickups: new[] { ("Everus Harbor", "Titanium", 32) }),
            new[] { Route("  everus harbor  ") });

        Assert.Single(board);
        Assert.Equal("Everus Harbor", board[0].Location);   // the first spelling seen wins
    }

    [Fact]
    public void WithinAStop_EntriesRunCollectThenDeliverThenSell()
    {
        var board = StopBoard.Merge(
            Con(pickups: new[] { ("Port Olisar", "Titanium", 32) },
                dropoffs: new[] { ("Port Olisar", "Laranite", 16) }),
            new[] { Route("Port Olisar") });

        var stop = Assert.Single(board);
        Assert.Equal(new[] { StopAction.Collect, StopAction.Deliver, StopAction.Sell },
                     stop.Entries.Select(e => e.Action));
    }

    [Fact]
    public void ARouteUsesItsCorrectedQuantityOnceABuyHasLanded()
    {
        var board = StopBoard.Merge(Con(),
            new[] { Route("Baijini Point", tripQty: 750, actualQty: 680, stage: AcceptedStage.Loaded) });

        Assert.Equal(680, board.Single().Entries.Single().Scu);
    }

    // Sold cargo is finished work and has already left the active list in the real app. The fold
    // must not render a stop for cargo that is gone even if a stale one reaches it.
    [Fact]
    public void ASoldRoute_ContributesNoStop()
        => Assert.Empty(StopBoard.Merge(Con(), new[] { Route("Baijini Point", stage: AcceptedStage.Sold) }));

    // A sell-only route is cargo already held with a known buyer, which is precisely a stop.
    [Fact]
    public void ASellOnlyRoute_StillContributesItsSell()
    {
        var board = StopBoard.Merge(Con(),
            new[] { Route("Baijini Point", stage: AcceptedStage.Loaded, buyTerminalId: null) });

        Assert.Equal(StopAction.Sell, board.Single().Entries.Single().Action);
    }

    // Ordering is the caller's job (it needs the map and the player), so the fold must hand back a
    // stable, predictable order for it to sort.
    [Fact]
    public void StopsComeBackInFirstSeenOrder()
    {
        var board = StopBoard.Merge(
            Con(pickups: new[] { ("A", "x", 1), ("B", "y", 2) },
                dropoffs: new[] { ("C", "z", 3) }),
            new[] { Route("D") });

        Assert.Equal(new[] { "A", "B", "C", "D" }, board.Select(s => s.Location));
    }

    [Fact]
    public void ABlankLocation_IsItsOwnStopRatherThanBeingDropped()
    {
        var board = StopBoard.Merge(Con(pickups: new[] { ("", "Titanium", 32) }), Array.Empty<AcceptedRoute>());
        Assert.Equal("", Assert.Single(board).Location);   // the page renders this as "Unknown"
    }
}
