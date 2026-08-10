using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Swapping UEX's per-commodity container_sizes for what the kiosk actually said, per terminal.
public class KioskBoxOverrideTests
{
    private static readonly DateTime T0 = new(2026, 8, 10, 22, 0, 0, DateTimeKind.Utc);

    private static TradePriceRow Row(int terminalId, string commodity, string sizes) =>
        new(terminalId, 1, 1000, 1200, 5000, 5000, 1, 1, sizes, T0, $"Terminal {terminalId}", commodity);

    private static Dictionary<int, MarketTerminal> Terminals(params (int Id, string Location)[] rows)
        => rows.ToDictionary(r => r.Id,
            r => new MarketTerminal(r.Id, $"Terminal {r.Id}", "trading", false, "Stanton", r.Location));

    [Fact]
    public void AnObservedTerminal_GetsTheKiosksOwnSizes()
    {
        var rows = new[] { Row(7, "Scrap", "1,2,4,8,16,24,32") };
        var terminals = Terminals((7, "MIC-L4"));

        var result = KioskBoxOverride.Apply(rows, terminals,
            (loc, com) => loc == "MIC-L4" && com == "Scrap" ? "1,2,4,8,16" : null);

        Assert.Equal("1,2,4,8,16", result[0].ContainerSizes);
    }

    // Everything else keeps UEX's list verbatim, so this can only make the planner more right
    // about a terminal you have visited and never changes one you have not.
    [Fact]
    public void AnUnobservedTerminal_IsUntouched()
    {
        var rows = new[] { Row(9, "Scrap", "1,2,4,8,16,24,32") };
        var result = KioskBoxOverride.Apply(rows, Terminals((9, "Area 18")), (_, _) => null);
        Assert.Equal("1,2,4,8,16,24,32", result[0].ContainerSizes);
    }

    // The same counter can sell two commodities in different sizes - the observation that started
    // this whole thing - so the lookup is per commodity, not per terminal.
    [Fact]
    public void TwoCommoditiesAtOneTerminal_GetTheirOwnSizes()
    {
        var rows = new[] { Row(7, "Scrap", "1,2,4,8,16,24,32"), Row(7, "Waste", "1,2,4,8,16,24,32") };
        var terminals = Terminals((7, "MIC-L4"));

        var result = KioskBoxOverride.Apply(rows, terminals,
            (_, com) => com == "Scrap" ? "1,2,4,8,16" : "1,2,4,8,16,24,32");

        Assert.Equal("1,2,4,8,16", result[0].ContainerSizes);
        Assert.Equal("1,2,4,8,16,24,32", result[1].ContainerSizes);
    }

    [Fact]
    public void ATerminalWithNoCatalogEntry_IsUntouched()
    {
        var rows = new[] { Row(99, "Scrap", "1,2,4") };
        var result = KioskBoxOverride.Apply(rows, Terminals((7, "MIC-L4")), (_, _) => "1,2");
        Assert.Equal("1,2,4", result[0].ContainerSizes);
    }

    // Nothing observed at all is the overwhelmingly common case, and it must not copy the list.
    [Fact]
    public void NothingObserved_ReturnsTheSameInstance()
    {
        var rows = new[] { Row(7, "Scrap", "1,2,4"), Row(8, "Waste", "1,2") };
        Assert.Same(rows, KioskBoxOverride.Apply(rows, Terminals((7, "MIC-L4")), (_, _) => null));
    }

    [Fact]
    public void AnIdenticalObservation_ChangesNothing()
    {
        var rows = new[] { Row(7, "Scrap", "1,2,4") };
        Assert.Same(rows, KioskBoxOverride.Apply(rows, Terminals((7, "MIC-L4")), (_, _) => "1,2,4"));
    }

    // The prefix before the first override must survive the copy, or every earlier row is dropped.
    [Fact]
    public void RowsBeforeAndAfterAnOverride_AreAllKept()
    {
        var rows = new[]
        {
            Row(1, "Scrap", "1,2,4"), Row(2, "Scrap", "1,2,4"),
            Row(7, "Scrap", "1,2,4,8,16,24,32"),
            Row(3, "Scrap", "1,2,4"),
        };
        var terminals = Terminals((1, "A"), (2, "B"), (7, "MIC-L4"), (3, "C"));

        var result = KioskBoxOverride.Apply(rows, terminals,
            (loc, _) => loc == "MIC-L4" ? "1,2,4,8,16" : null);

        Assert.Equal(4, result.Count);
        Assert.Equal(new[] { 1, 2, 7, 3 }, result.Select(r => r.TerminalId));
        Assert.Equal("1,2,4,8,16", result[2].ContainerSizes);
        Assert.Equal("1,2,4", result[0].ContainerSizes);
    }

    [Fact]
    public void AnEmptyRowSet_IsFine()
        => Assert.Empty(KioskBoxOverride.Apply(Array.Empty<TradePriceRow>(), Terminals(), (_, _) => "1,2"));

    // ---- store + tracker ----------------------------------------------------------------------

    private static KioskBoxSizes Observed(string commodity, params int[] sizes) =>
        new(T0, "747398598793", "SCShop_Admin_lt_base_g", commodity, sizes);

    private static KioskBoxSizeStore NewStore(out string path)
    {
        path = Path.Combine(Path.GetTempPath(), $"kbs_{Guid.NewGuid():N}.json");
        return new KioskBoxSizeStore(path);
    }

    [Fact]
    public void TheStore_RemembersAndReturnsWhatAKioskSaid()
    {
        var store = NewStore(out var path);
        try
        {
            store.Record("MIC-L4", Observed("Scrap", 1, 2, 4, 8, 16));
            Assert.Equal("1,2,4,8,16", store.SizesFor("MIC-L4", "Scrap"));
            Assert.Null(store.SizesFor("MIC-L4", "Laranite"));
            Assert.Null(store.SizesFor("Area 18", "Scrap"));
        }
        finally { try { File.Delete(path); } catch { } }
    }

    // Newest wins: a kiosk's sizes can change between patches, and reading the live log is the
    // whole point of not freezing the first answer ever seen.
    [Fact]
    public void TheStore_TakesTheNewestAnswer()
    {
        var store = NewStore(out var path);
        try
        {
            store.Record("MIC-L4", Observed("Scrap", 1, 2, 4, 8, 16));
            store.Record("MIC-L4", new KioskBoxSizes(T0.AddDays(30), "s", "n", "Scrap", new[] { 1, 2, 4 }));
            Assert.Equal("1,2,4", store.SizesFor("MIC-L4", "Scrap"));
        }
        finally { try { File.Delete(path); } catch { } }
    }

    // Learn a kiosk once, keep it: the planner is right about that terminal on every later launch.
    [Fact]
    public void TheStore_SurvivesARestart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kbs_{Guid.NewGuid():N}.json");
        try
        {
            new KioskBoxSizeStore(path).Record("MIC-L4", Observed("Scrap", 1, 2, 4, 8, 16));
            Assert.Equal("1,2,4,8,16", new KioskBoxSizeStore(path).SizesFor("MIC-L4", "Scrap"));
        }
        finally { try { File.Delete(path); } catch { } }
    }

    // Without somewhere to attach it, an answer can never be looked up again.
    [Fact]
    public void TheStore_DropsAnObservationWithNoPlace()
    {
        var store = NewStore(out var path);
        try
        {
            store.Record(null, Observed("Scrap", 1, 2));
            store.Record("  ", Observed("Scrap", 1, 2));
            Assert.Empty(store.All());
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void TheTracker_FilesALineAgainstWhereThePlayerWas()
    {
        var store = NewStore(out var path);
        try
        {
            var tracker = new KioskBoxTracker(new GameLogFeed(), store, () => ("Everus Harbor", "MIC-L4"));
            tracker.Apply(
                "<2026-08-10T22:09:25.519Z> [Notice] <CEntityComponentCommodityUIProvider::LoadShopInventoryData::<lambda_1>::operator ()> " +
                "AddingCommodityBox - playerId[1] shopId[2] shopName[SCShop_Admin_lt_base_g] " +
                "commodityName[ResourceType.Scrap] Available Box Sizes:  boxSize[1] boxSize[2] boxSize[4] boxSize[8] boxSize[16]");

            // The UEX Location wins over the display label, the same first-hint-then-fall-back
            // order TradeOriginResolver applies.
            Assert.Equal("1,2,4,8,16", store.SizesFor("MIC-L4", "Scrap"));
            tracker.Dispose();
        }
        finally { try { File.Delete(path); } catch { } }
    }

    // ---- wiring pin ---------------------------------------------------------------------------

    [Fact]
    public void ThePlanner_PrefersTheKiosksSizesOverUex()
    {
        var src = SourceFiles.ReadAppSource(@"Views\TradePage.Planner.cs");
        Assert.Contains("KioskBoxOverride.Apply(snap.TradePrices.Rows, terminals, App.KioskBoxes.SizesFor)", src);
        Assert.Contains("BuildSourcePairs(pricedRows)", src);
    }
}
