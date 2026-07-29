using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// MarketQueries is the pure query layer (Task 6 of feature/uex-market-data) every price surface
// reads through: seed resource name -> UEX raw commodity -> its refined counterpart -> price
// rows, with the fresh-beats-stale ranking that is the whole point of tracking GameVersion per
// row. Every surface quotes REFINED prices (amendment 2026-07-27: UEX's raw ore-sales dataset has
// had no reports since patch 4.8), so there is no raw query path left to cover. Snapshots here
// are built by hand in code, never from JSON: MarketQueries never touches parsing.
public class MarketQueriesTests
{
    private static MarketSnapshot BuildSnapshot(string liveGameVersion, List<MarketCommodity> commodities,
        List<MarketPriceRow>? refinedPrices = null) =>
        new()
        {
            LiveGameVersion = liveGameVersion,
            Commodities = new MarketDataset<MarketCommodity> { Rows = commodities },
            RefinedPrices = new MarketDataset<MarketPriceRow> { Rows = refinedPrices ?? new List<MarketPriceRow>() },
        };

    private static MarketPriceRow Row(int terminalId, int commodityId, double sell, double sellAvgWeek,
        string gameVersion, string terminalName) =>
        new(terminalId, commodityId, sell, sellAvgWeek, gameVersion, new DateTime(2026, 7, 26, 0, 0, 0, DateTimeKind.Utc),
            terminalName);

    // A raw/refined commodity pair with the raw side linked to the refined side via IdParent,
    // reused by several tests below.
    private static List<MarketCommodity> BexaliteAndGoldCommodities() => new()
    {
        new MarketCommodity(10, "Bexalite (Raw)", "bexalite-raw", true, false, 11),
        new MarketCommodity(11, "Bexalite", "bexalite", false, true, 0),
        new MarketCommodity(30, "Gold (Ore)", "gold-ore", true, false, 31),
        new MarketCommodity(31, "Gold", "gold", false, true, 0),
    };

    // --- (a) fresh outranks a higher-priced stale row ------------------------------

    [Fact]
    public void BestRefinedSell_FreshBeatsHigherPricedStale()
    {
        var commodities = new List<MarketCommodity>
        {
            new(10, "Bexalite (Raw)", "bexalite-raw", true, false, 11),
            new(11, "Bexalite", "bexalite", false, true, 0),
        };
        var refinedPrices = new List<MarketPriceRow>
        {
            Row(1, 11, 5000, 5000, "4.9", "Fresh Terminal"),
            Row(2, 11, 9000, 9000, "4.8", "Stale Terminal"),
        };
        var snapshot = BuildSnapshot("4.9", commodities, refinedPrices);

        var hit = MarketQueries.BestRefinedSell(snapshot, "Bexalite");

        Assert.NotNull(hit);
        Assert.Equal("Fresh Terminal", hit!.TerminalName);
        Assert.Equal(5000, hit.Display);
        Assert.False(hit.Stale);
    }

    // --- (b) week-avg fallback to instant when week is 0 ----------------------------

    [Fact]
    public void BestRefinedSell_WeekAvgZero_FallsBackToInstant()
    {
        var commodities = new List<MarketCommodity>
        {
            new(10, "Bexalite (Raw)", "bexalite-raw", true, false, 11),
            new(11, "Bexalite", "bexalite", false, true, 0),
        };
        var refinedPrices = new List<MarketPriceRow> { Row(1, 11, 4200, 0, "4.9", "Terminal A") };
        var snapshot = BuildSnapshot("4.9", commodities, refinedPrices);

        var hit = MarketQueries.BestRefinedSell(snapshot, "Bexalite");

        Assert.NotNull(hit);
        Assert.Equal(0, hit!.WeekAvg);
        Assert.Equal(4200, hit.Instant);
        Assert.Equal(4200, hit.Display);
    }

    // --- (c) TopRefinedSells: count + fresh-desc-then-stale-desc ordering ------------

    private static MarketSnapshot GoldOrderingSnapshot()
    {
        var commodities = new List<MarketCommodity>
        {
            new(20, "Gold (Ore)", "gold-ore", true, false, 21),
            new(21, "Gold", "gold", false, true, 0),
        };
        var refinedPrices = new List<MarketPriceRow>
        {
            Row(1, 21, 200, 200, "4.9", "Fresh A"),
            Row(2, 21, 500, 500, "4.9", "Fresh B"),
            Row(3, 21, 350, 350, "4.9", "Fresh C"),
            Row(4, 21, 900, 900, "4.8", "Stale D"),   // highest raw price of all, but stale
            Row(5, 21, 100, 100, "4.8", "Stale E"),
        };
        return BuildSnapshot("4.9", commodities, refinedPrices);
    }

    [Fact]
    public void TopRefinedSells_OrdersFreshDescendingThenStaleDescending()
    {
        var snapshot = GoldOrderingSnapshot();

        var all = MarketQueries.TopRefinedSells(snapshot, "Gold", 10);

        Assert.Equal(new[] { "Fresh B", "Fresh C", "Fresh A", "Stale D", "Stale E" },
            all.Select(h => h.TerminalName));
        Assert.False(all[0].Stale);
        Assert.False(all[1].Stale);
        Assert.False(all[2].Stale);
        Assert.True(all[3].Stale);
        Assert.True(all[4].Stale);
    }

    [Fact]
    public void TopRefinedSells_CountTruncatesToFreshRowsFirst()
    {
        var snapshot = GoldOrderingSnapshot();

        // Top 2 must be the two highest FRESH rows, not the stale 900 despite it being the
        // single highest price in the whole dataset.
        var top2 = MarketQueries.TopRefinedSells(snapshot, "Gold", 2);

        Assert.Equal(new[] { "Fresh B", "Fresh C" }, top2.Select(h => h.TerminalName));
    }

    [Fact]
    public void TopRefinedSells_RowsWithNonPositiveDisplay_AreDropped()
    {
        var commodities = new List<MarketCommodity>
        {
            new(10, "Bexalite (Raw)", "bexalite-raw", true, false, 11),
            new(11, "Bexalite", "bexalite", false, true, 0),
        };
        var refinedPrices = new List<MarketPriceRow>
        {
            Row(1, 11, 0, 0, "4.9", "Zero Terminal"),
            Row(2, 11, 4000, 4000, "4.9", "Good Terminal"),
        };
        var snapshot = BuildSnapshot("4.9", commodities, refinedPrices);

        var result = MarketQueries.TopRefinedSells(snapshot, "Bexalite", 10);

        var hit = Assert.Single(result);
        Assert.Equal("Good Terminal", hit.TerminalName);
    }

    // The dossier's own call shape: the top three, from a dataset that has more.
    [Fact]
    public void TopRefinedSells_TopThree_IsTheDossierSlice()
    {
        var snapshot = GoldOrderingSnapshot();

        var top3 = MarketQueries.TopRefinedSells(snapshot, "Gold", 3);

        Assert.Equal(new[] { "Fresh B", "Fresh C", "Fresh A" }, top3.Select(h => h.TerminalName));
    }

    // --- (d) refined resolution: idParent link vs. name-strip fallback ---------------

    [Fact]
    public void BestRefinedSell_ResolvesViaIdParentLink()
    {
        var commodities = new List<MarketCommodity>
        {
            new(10, "Bexalite (Raw)", "bexalite-raw", true, false, 11),
            new(11, "Bexalite", "bexalite", false, true, 0),
        };
        var refinedPrices = new List<MarketPriceRow> { Row(1, 11, 8000, 8000, "4.9", "Refinery A") };
        var snapshot = BuildSnapshot("4.9", commodities, refinedPrices);

        var hit = MarketQueries.BestRefinedSell(snapshot, "Bexalite");

        Assert.NotNull(hit);
        Assert.Equal(8000, hit!.Display);
        Assert.Equal("Refinery A", hit.TerminalName);
    }

    [Fact]
    public void BestRefinedSell_ResolvesViaNameStripFallback()
    {
        // IdParent == 0 on the raw side: BestRefinedSell must fall back to matching the refined
        // commodity by stripping " (Raw)" from the raw commodity's own name.
        var commodities = new List<MarketCommodity>
        {
            new(12, "Taranite (Raw)", "taranite-raw", true, false, 0),
            new(13, "Taranite", "taranite", false, true, 0),
        };
        var refinedPrices = new List<MarketPriceRow> { Row(2, 13, 6500, 6500, "4.9", "Refinery B") };
        var snapshot = BuildSnapshot("4.9", commodities, refinedPrices);

        var hit = MarketQueries.BestRefinedSell(snapshot, "Taranite");

        Assert.NotNull(hit);
        Assert.Equal(6500, hit!.Display);
        Assert.Equal("Refinery B", hit.TerminalName);
    }

    [Fact]
    public void TopRefinedSells_ResolvesViaNameStripFallback()
    {
        var commodities = new List<MarketCommodity>
        {
            new(12, "Taranite (Raw)", "taranite-raw", true, false, 0),
            new(13, "Taranite", "taranite", false, true, 0),
        };
        var refinedPrices = new List<MarketPriceRow>
        {
            Row(1, 13, 6500, 6500, "4.9", "Refinery B"),
            Row(2, 13, 7100, 7100, "4.9", "Refinery C"),
        };
        var snapshot = BuildSnapshot("4.9", commodities, refinedPrices);

        var result = MarketQueries.TopRefinedSells(snapshot, "Taranite", 3);

        Assert.Equal(new[] { "Refinery C", "Refinery B" }, result.Select(h => h.TerminalName));
    }

    // --- (e) RefinedSellsForOrder: tokenize, resolve, order by Display desc ---------

    [Fact]
    public void RefinedSellsForOrder_OrdersByDisplayDescendingAndIgnoresUnrecognizedToken()
    {
        var commodities = BexaliteAndGoldCommodities();
        var refinedPrices = new List<MarketPriceRow>
        {
            Row(1, 11, 8000, 8000, "4.9", "Bexalite Refinery"),
            Row(2, 31, 3000, 3000, "4.9", "Gold Refinery"),
        };
        var snapshot = BuildSnapshot("4.9", commodities, refinedPrices);

        var result = MarketQueries.RefinedSellsForOrder(snapshot, "Bexalite, Gold, Wibblefrotz");

        Assert.Equal(new[] { "Bexalite", "Gold" }, result.Select(r => r.SeedName));
        Assert.Equal(8000, result[0].Hit.Display);
        Assert.Equal(3000, result[1].Hit.Display);
    }

    [Fact]
    public void RefinedSellsForOrder_EmptyText_ReturnsEmpty()
    {
        var snapshot = BuildSnapshot("4.9", BexaliteAndGoldCommodities());

        var result = MarketQueries.RefinedSellsForOrder(snapshot, "");

        Assert.Empty(result);
    }

    // --- (f) unmapped resource / null snapshot -> null or empty, never throw --------

    [Fact]
    public void BestRefinedSell_UnmappedResource_ReturnsNullNoThrow()
    {
        var snapshot = BuildSnapshot("4.9", new List<MarketCommodity>());

        var ex = Record.Exception(() => MarketQueries.BestRefinedSell(snapshot, "NotARealResourceZzz"));

        Assert.Null(ex);
        Assert.Null(MarketQueries.BestRefinedSell(snapshot, "NotARealResourceZzz"));
    }

    [Fact]
    public void TopRefinedSells_UnmappedResource_ReturnsEmpty()
    {
        var snapshot = BuildSnapshot("4.9", new List<MarketCommodity>());

        Assert.Empty(MarketQueries.TopRefinedSells(snapshot, "NotARealResourceZzz", 3));
    }

    // Mapped seed name, raw commodity present, but nothing in the catalogue it can refine into:
    // the surface shows nothing rather than falling back to a raw number.
    [Fact]
    public void TopRefinedSells_NoRefinedCounterpart_ReturnsEmpty()
    {
        var commodities = new List<MarketCommodity> { new(10, "Bexalite (Raw)", "bexalite-raw", true, false, 0) };
        var refinedPrices = new List<MarketPriceRow> { Row(1, 11, 8000, 8000, "4.9", "Refinery A") };
        var snapshot = BuildSnapshot("4.9", commodities, refinedPrices);

        Assert.Empty(MarketQueries.TopRefinedSells(snapshot, "Bexalite", 3));
        Assert.Null(MarketQueries.BestRefinedSell(snapshot, "Bexalite"));
    }

    [Fact]
    public void BestRefinedSell_NullSnapshot_ReturnsNull()
    {
        Assert.Null(MarketQueries.BestRefinedSell(null!, "Bexalite"));
    }

    [Fact]
    public void TopRefinedSells_NullSnapshot_ReturnsEmpty()
    {
        Assert.Empty(MarketQueries.TopRefinedSells(null!, "Bexalite", 5));
    }

    [Fact]
    public void RefinedSellsForOrder_NullSnapshot_ReturnsEmpty()
    {
        Assert.Empty(MarketQueries.RefinedSellsForOrder(null!, "Bexalite"));
    }

    // --- additional contract coverage: commodity present, no price rows -------------

    [Fact]
    public void BestRefinedSell_CommodityWithNoPriceRows_ReturnsNull()
    {
        var commodities = new List<MarketCommodity>
        {
            new(10, "Bexalite (Raw)", "bexalite-raw", true, false, 11),
            new(11, "Bexalite", "bexalite", false, true, 0),
        };
        var snapshot = BuildSnapshot("4.9", commodities);

        Assert.Null(MarketQueries.BestRefinedSell(snapshot, "Bexalite"));
    }

    // --- additional contract coverage: empty LiveGameVersion means nothing is stale -

    [Fact]
    public void EmptyLiveGameVersion_NothingIsStale()
    {
        var commodities = new List<MarketCommodity>
        {
            new(10, "Bexalite (Raw)", "bexalite-raw", true, false, 11),
            new(11, "Bexalite", "bexalite", false, true, 0),
        };
        var refinedPrices = new List<MarketPriceRow>
        {
            Row(1, 11, 100, 100, "4.7", "Old Terminal"),
            Row(2, 11, 200, 200, "4.9", "New Terminal"),
        };
        var snapshot = BuildSnapshot("", commodities, refinedPrices);

        var result = MarketQueries.TopRefinedSells(snapshot, "Bexalite", 10);

        Assert.All(result, h => Assert.False(h.Stale));
        Assert.Equal(new[] { "New Terminal", "Old Terminal" }, result.Select(h => h.TerminalName));
    }

    // --- additional contract coverage: GameVersion compares OrdinalIgnoreCase -------

    [Fact]
    public void GameVersionComparison_IsCaseInsensitive()
    {
        var commodities = new List<MarketCommodity>
        {
            new(10, "Bexalite (Raw)", "bexalite-raw", true, false, 11),
            new(11, "Bexalite", "bexalite", false, true, 0),
        };
        var refinedPrices = new List<MarketPriceRow> { Row(1, 11, 100, 100, "4.9-LIVE", "Terminal") };
        var snapshot = BuildSnapshot("4.9-live", commodities, refinedPrices);

        var hit = MarketQueries.BestRefinedSell(snapshot, "Bexalite");

        Assert.NotNull(hit);
        Assert.False(hit!.Stale);
    }

    // --- additional contract coverage: a name-map miss is logged once per name ------

    [Fact]
    public void UnmappedResource_LogsMissOnceOnlyPerName()
    {
        var name = $"ZzzUnitTestUnmapped_{Guid.NewGuid():N}";
        var snapshot = BuildSnapshot("4.9", new List<MarketCommodity>());

        MarketQueries.BestRefinedSell(snapshot, name);
        MarketQueries.BestRefinedSell(snapshot, name);
        MarketQueries.BestRefinedSell(snapshot, name);

        var logPath = Environment.GetEnvironmentVariable("NEXUS_LOG_PATH");
        Assert.NotNull(logPath);
        Assert.True(File.Exists(logPath));
        // FileShare.ReadWrite (not File.ReadAllText's default FileShare.Read): this file is the
        // shared Logger.LogPath every parallel test class can be appending to, and a plain
        // exclusive-of-writers read would deny the writer, whose never-throw guarantee then
        // silently drops the line - see TestFiles.cs for the same fix applied elsewhere.
        var occurrences = TestFiles.ReadSharedLines(logPath!)
            .Sum(l => Regex.Matches(l, Regex.Escape(name)).Count);
        Assert.Equal(1, occurrences);
    }
}
