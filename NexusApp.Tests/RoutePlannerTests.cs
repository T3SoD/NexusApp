using System.Linq;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

public class RoutePlannerTests
{
    private static MarketTerminal Term(int id, string system, string location = "") =>
        new(id, $"Terminal {id}", "commodity", false, system, location);

    private static TradePriceRow Row(int terminalId, int commodityId, double buy, double sell,
        int buyStock = 0, int sellDemand = 0, string containerSizes = "1,2,4,8,16,24,32") =>
        new(terminalId, commodityId, buy, sell, buyStock, sellDemand, buy > 0 ? 1 : 0, sell > 0 ? 1 : 0,
            containerSizes, DateTime.UtcNow, $"Terminal {terminalId}", $"Commodity {commodityId}");

    [Fact]
    public void Rank_PairsBuyAndSellRowsOfSameCommodity_ComputesNetProfit()
    {
        var terminals = new Dictionary<int, MarketTerminal> { [1] = Term(1, "Stanton"), [2] = Term(2, "Stanton") };
        var rows = new List<TradePriceRow>
        {
            Row(1, 47, buy: 7541, sell: 0, buyStock: 1050),
            Row(2, 47, buy: 0, sell: 8500, sellDemand: 1594),
        };

        var routes = RoutePlanner.Rank(rows, terminals, shipScu: 100, shipMaxBox: 32, budget: null,
            originTerminalIds: null, scope: "ALL", take: 10);

        var route = Assert.Single(routes);
        Assert.Equal(1, route.BuyRow.TerminalId);
        Assert.Equal(2, route.SellRow.TerminalId);
        Assert.Equal(100, route.TripQty);            // min(ship 100, stock 1050)
        Assert.Equal(95900, route.Gross);             // 100 * (8500 - 7541)
        Assert.Equal(route.Gross, route.Net);
    }

    [Fact]
    public void Rank_ExcludesSameTerminalBuyAndSellPair()
    {
        var terminals = new Dictionary<int, MarketTerminal> { [1] = Term(1, "Stanton") };
        var rows = new List<TradePriceRow>
        {
            Row(1, 47, buy: 100, sell: 0, buyStock: 50),
            Row(1, 47, buy: 0, sell: 200, sellDemand: 50),
        };

        var routes = RoutePlanner.Rank(rows, terminals, 100, 32, null, null, "ALL", 10);

        Assert.Empty(routes);
    }

    [Fact]
    public void Rank_ExcludesPairsThatFailBoxFitsOnEitherLeg()
    {
        var terminals = new Dictionary<int, MarketTerminal> { [1] = Term(1, "Stanton"), [2] = Term(2, "Stanton") };
        var rows = new List<TradePriceRow>
        {
            Row(1, 47, buy: 100, sell: 0, buyStock: 50, containerSizes: "24,32"),   // ship can't load these
            Row(2, 47, buy: 0, sell: 200, sellDemand: 50),
        };

        var routes = RoutePlanner.Rank(rows, terminals, 100, 8, null, null, "ALL", 10);

        Assert.Empty(routes);
    }

    [Fact]
    public void Rank_ExcludesRowsWhoseTerminalIsNotInTheLookup()
    {
        var terminals = new Dictionary<int, MarketTerminal> { [1] = Term(1, "Stanton") };   // terminal 2 missing
        var rows = new List<TradePriceRow>
        {
            Row(1, 47, buy: 100, sell: 0, buyStock: 50),
            Row(2, 47, buy: 0, sell: 200, sellDemand: 50),
        };

        var routes = RoutePlanner.Rank(rows, terminals, 100, 32, null, null, "ALL", 10);

        Assert.Empty(routes);
    }

    [Fact]
    public void Rank_FromHereRestrictsBuyLegOnly()
    {
        var terminals = new Dictionary<int, MarketTerminal>
        {
            [1] = Term(1, "Stanton"), [2] = Term(2, "Stanton"), [3] = Term(3, "Stanton"),
        };
        var rows = new List<TradePriceRow>
        {
            Row(1, 47, buy: 100, sell: 0, buyStock: 50),    // origin: allowed
            Row(2, 47, buy: 90, sell: 0, buyStock: 50),     // NOT origin: excluded from buy legs
            Row(3, 47, buy: 0, sell: 200, sellDemand: 50),  // sell leg: never origin-restricted
        };

        var routes = RoutePlanner.Rank(rows, terminals, 100, 32, null,
            originTerminalIds: new HashSet<int> { 1 }, scope: "ALL", take: 10);

        var route = Assert.Single(routes);
        Assert.Equal(1, route.BuyRow.TerminalId);
    }

    [Fact]
    public void Rank_NullOriginSet_MeansAnywhere()
    {
        var terminals = new Dictionary<int, MarketTerminal> { [1] = Term(1, "Stanton"), [2] = Term(2, "Stanton") };
        var rows = new List<TradePriceRow>
        {
            Row(1, 47, buy: 100, sell: 0, buyStock: 50),
            Row(2, 47, buy: 0, sell: 200, sellDemand: 50),
        };

        Assert.Single(RoutePlanner.Rank(rows, terminals, 100, 32, null, null, "ALL", 10));
    }

    // A non-null EMPTY origin set means FROM HERE could not resolve the player's origin to any
    // terminal - it must restrict the buy leg to nothing, not silently fall back to ANYWHERE
    // (spec Decision 6: "every listed route is purchasable where the player stands"; escalated
    // finding: the prior "empty means anywhere" behavior let FROM HERE stay lit while showing
    // cross-map routes on first run, before any origin is known).
    [Fact]
    public void Rank_EmptyOriginSet_RestrictsToNothing()
    {
        var terminals = new Dictionary<int, MarketTerminal> { [1] = Term(1, "Stanton"), [2] = Term(2, "Stanton") };
        var rows = new List<TradePriceRow>
        {
            Row(1, 47, buy: 100, sell: 0, buyStock: 50),
            Row(2, 47, buy: 0, sell: 200, sellDemand: 50),
        };

        Assert.Empty(RoutePlanner.Rank(rows, terminals, 100, 32, null, new HashSet<int>(), "ALL", 10));
    }

    [Fact]
    public void Rank_ScopeRestrictsToMatchingSystem()
    {
        var terminals = new Dictionary<int, MarketTerminal> { [1] = Term(1, "Stanton"), [2] = Term(2, "Pyro") };
        var rows = new List<TradePriceRow>
        {
            Row(1, 47, buy: 100, sell: 0, buyStock: 50),
            Row(2, 47, buy: 0, sell: 200, sellDemand: 50),
        };

        // The sell leg is in Pyro, buy leg in Stanton: scoping to one system excludes the pair
        // because Rank only pairs rows that both survived the same scope filter.
        Assert.Empty(RoutePlanner.Rank(rows, terminals, 100, 32, null, null, "STANTON", 10));
        Assert.Single(RoutePlanner.Rank(rows, terminals, 100, 32, null, null, "ALL", 10));
    }

    [Fact]
    public void Rank_OrdersByNetDescendingAndTakesTopN()
    {
        var terminals = new Dictionary<int, MarketTerminal>
        {
            [1] = Term(1, "Stanton"), [2] = Term(2, "Stanton"), [3] = Term(3, "Stanton"), [4] = Term(4, "Stanton"),
        };
        var rows = new List<TradePriceRow>
        {
            Row(1, 10, buy: 100, sell: 0, buyStock: 100),
            Row(2, 10, buy: 0, sell: 150, sellDemand: 100),   // net 5,000
            Row(3, 20, buy: 100, sell: 0, buyStock: 100),
            Row(4, 20, buy: 0, sell: 300, sellDemand: 100),   // net 20,000
        };

        var routes = RoutePlanner.Rank(rows, terminals, 100, 32, null, null, "ALL", take: 1);

        var route = Assert.Single(routes);
        Assert.Equal(20000, route.Net);
    }

    [Fact]
    public void Rank_UsesProximityTiersDerive()
    {
        var terminals = new Dictionary<int, MarketTerminal>
        {
            [1] = Term(1, "Stanton", "ArcCorp"), [2] = Term(2, "Pyro"),
        };
        var rows = new List<TradePriceRow>
        {
            Row(1, 47, buy: 100, sell: 0, buyStock: 50),
            Row(2, 47, buy: 0, sell: 200, sellDemand: 50),
        };

        var route = Assert.Single(RoutePlanner.Rank(rows, terminals, 100, 32, null, null, "ALL", 10));
        Assert.Equal(ProximityTier.CrossSystem, route.Tier);
    }

    // Real capture (Task 1's fixture): HDMS-Lathan buys Laranite at 7541/SCU (stock 1050), TDD
    // Area 18 sells it at 8500/SCU (demand 1594). Property test against real numbers, not just
    // hand-picked ones.
    [Fact]
    public void Rank_RealLaraniteFixtureData_ProducesTheExpectedRoute()
    {
        var rows = MarketParse.ParseTradePriceRows(TradePricesFixture.LoadSampleJson(), out _)
            .Where(r => r.CommodityId == 47).ToList();
        var terminals = new Dictionary<int, MarketTerminal>
        {
            [rows.Single(r => r.TerminalName == "HDMS-Lathan").TerminalId] = Term(33, "Stanton", "Lyria"),
            [rows.Single(r => r.TerminalName == "TDD Area 18").TerminalId] = Term(12, "Stanton", "Area 18"),
        };

        var routes = RoutePlanner.Rank(rows, terminals, shipScu: 100, shipMaxBox: 32, budget: null,
            originTerminalIds: null, scope: "ALL", take: 10);

        var best = routes.OrderByDescending(r => r.Net).First();
        Assert.Equal(7541, best.BuyRow.Buy);
        Assert.Equal(8500, best.SellRow.Sell);
        Assert.Equal(100, best.TripQty);
        Assert.Equal(95900, best.Net);
        Assert.Equal(ProximityTier.SameSystem, best.Tier);   // different stations, same system
    }

    [Fact]
    public void Rank_EmptyRowsOrZeroTake_ReturnsEmpty()
    {
        var terminals = new Dictionary<int, MarketTerminal>();
        Assert.Empty(RoutePlanner.Rank(new List<TradePriceRow>(), terminals, 100, 32, null, null, "ALL", 10));
        Assert.Empty(RoutePlanner.Rank(new List<TradePriceRow> { Row(1, 1, 10, 0) }, terminals, 100, 32, null, null, "ALL", 0));
    }

    // Demand-at-destination coverage filter (task 5; resemantic task 10 to DEMAND ONLY - the buy
    // leg's stock is no longer independently checked, only tripQty vs sellDemandScu). Applied per
    // pair, inside the pairing loop, before the take cutoff - a route that fails coverage is
    // skipped outright rather than merely ranked lower.
    [Fact]
    public void Rank_DemandExactlyEqualsTripQty_PassesCoversTripButFailsCoversTwoTrips()
    {
        var terminals = new Dictionary<int, MarketTerminal> { [1] = Term(1, "Stanton"), [2] = Term(2, "Stanton") };
        var rows = new List<TradePriceRow>
        {
            Row(1, 47, buy: 100, sell: 0, buyStock: 50),
            Row(2, 47, buy: 0, sell: 200, sellDemand: 50),
        };

        // shipScu 100, buyStock 50 => tripQty = min(100, 50) = 50, exactly matching demand.
        var trip = Assert.Single(RoutePlanner.Rank(rows, terminals, 100, 32, null, null, "ALL", 10, StockFilter.CoversTrip));
        Assert.Equal(50, trip.TripQty);

        Assert.Empty(RoutePlanner.Rank(rows, terminals, 100, 32, null, null, "ALL", 10, StockFilter.CoversTwoTrips));
    }

    // Task 10 resemantic: this used to be the "zero stock" case, but zero buy stock forces tripQty
    // to 0 (TradeMath.TripQty), which failed the old coverage filters' shared `tripQty > 0` guard
    // regardless of the stock check - it never actually exercised the buy-side comparison. Zero
    // DEMAND with HEALTHY stock is what actually proves the demand side is still enforced.
    [Fact]
    public void Rank_ZeroDemandPair_ExcludedUnderCoverageFiltersButKeptUnderAny()
    {
        var terminals = new Dictionary<int, MarketTerminal> { [1] = Term(1, "Stanton"), [2] = Term(2, "Stanton") };
        var rows = new List<TradePriceRow>
        {
            Row(1, 47, buy: 100, sell: 0, buyStock: 100),   // healthy stock -> tripQty > 0
            Row(2, 47, buy: 0, sell: 200, sellDemand: 0),   // zero demand -> fails both coverage filters
        };

        Assert.Single(RoutePlanner.Rank(rows, terminals, 100, 32, null, null, "ALL", 10, StockFilter.Any));
        Assert.Empty(RoutePlanner.Rank(rows, terminals, 100, 32, null, null, "ALL", 10, StockFilter.CoversTrip));
        Assert.Empty(RoutePlanner.Rank(rows, terminals, 100, 32, null, null, "ALL", 10, StockFilter.CoversTwoTrips));
    }

    // Task 10's explicit new case: proves the buy leg's stock is no longer checked. buyStock (5) is
    // low enough that the OLD CoversTwoTrips check (buyStockScu >= 2*tripQty, i.e. 5 >= 10) would
    // have excluded this route - stock was the binding constraint, so tripQty == buyStockScu, and
    // buyStockScu can never be >= 2*buyStockScu for a positive value. Demand (1000) comfortably
    // covers both tiers regardless of how tight stock is, so both now pass.
    [Fact]
    public void Rank_LowStockHighDemand_PassesCoverageFilters_StockNoLongerChecked()
    {
        var terminals = new Dictionary<int, MarketTerminal> { [1] = Term(1, "Stanton"), [2] = Term(2, "Stanton") };
        var rows = new List<TradePriceRow>
        {
            Row(1, 47, buy: 100, sell: 0, buyStock: 5),       // low stock -> tripQty caps at 5
            Row(2, 47, buy: 0, sell: 200, sellDemand: 1000),  // demand comfortably covers many multiples of tripQty
        };

        var trip = Assert.Single(RoutePlanner.Rank(rows, terminals, 100, 32, null, null, "ALL", 10, StockFilter.CoversTrip));
        Assert.Equal(5, trip.TripQty);

        var twoX = Assert.Single(RoutePlanner.Rank(rows, terminals, 100, 32, null, null, "ALL", 10, StockFilter.CoversTwoTrips));
        Assert.Equal(5, twoX.TripQty);
    }

    // Destination constraint (task 6): mirror of the FROM HERE origin restriction, but for the sell
    // leg. Non-null non-empty restricts which terminals may sell; null (or the parameter omitted)
    // means ANY, unchanged from before this task; non-null EMPTY means an unresolved destination
    // pick, which must restrict to zero sell legs rather than silently widen back to ANY.
    [Fact]
    public void Rank_DestRestrictsSellLegOnly()
    {
        var terminals = new Dictionary<int, MarketTerminal>
        {
            [1] = Term(1, "Stanton"), [2] = Term(2, "Stanton"), [3] = Term(3, "Stanton"),
        };
        var rows = new List<TradePriceRow>
        {
            Row(1, 47, buy: 100, sell: 0, buyStock: 50),    // buy leg: never destination-restricted
            Row(2, 47, buy: 0, sell: 200, sellDemand: 50),  // destination: allowed
            Row(3, 47, buy: 0, sell: 210, sellDemand: 50),  // NOT destination: excluded from sell legs
        };

        var routes = RoutePlanner.Rank(rows, terminals, 100, 32, null, null, "ALL", 10, StockFilter.Any,
            destTerminalIds: new HashSet<int> { 2 });

        var route = Assert.Single(routes);
        Assert.Equal(2, route.SellRow.TerminalId);
    }

    [Fact]
    public void Rank_EmptyDestSet_RestrictsToNothing()
    {
        var terminals = new Dictionary<int, MarketTerminal> { [1] = Term(1, "Stanton"), [2] = Term(2, "Stanton") };
        var rows = new List<TradePriceRow>
        {
            Row(1, 47, buy: 100, sell: 0, buyStock: 50),
            Row(2, 47, buy: 0, sell: 200, sellDemand: 50),
        };

        Assert.Empty(RoutePlanner.Rank(rows, terminals, 100, 32, null, null, "ALL", 10, StockFilter.Any,
            destTerminalIds: new HashSet<int>()));
    }

    [Fact]
    public void Rank_NullDestSet_MatchesOmittedParameterOnRealFixtureData()
    {
        var rows = MarketParse.ParseTradePriceRows(TradePricesFixture.LoadSampleJson(), out _)
            .Where(r => r.CommodityId == 47).ToList();
        var terminals = new Dictionary<int, MarketTerminal>
        {
            [rows.Single(r => r.TerminalName == "HDMS-Lathan").TerminalId] = Term(33, "Stanton", "Lyria"),
            [rows.Single(r => r.TerminalName == "TDD Area 18").TerminalId] = Term(12, "Stanton", "Area 18"),
        };

        var omittedRoutes = RoutePlanner.Rank(rows, terminals, shipScu: 100, shipMaxBox: 32, budget: null,
            originTerminalIds: null, scope: "ALL", take: 10);
        var explicitNullRoutes = RoutePlanner.Rank(rows, terminals, shipScu: 100, shipMaxBox: 32, budget: null,
            originTerminalIds: null, scope: "ALL", take: 10, stockFilter: StockFilter.Any, destTerminalIds: null);

        Assert.Equal(omittedRoutes.Count, explicitNullRoutes.Count);
        for (int i = 0; i < omittedRoutes.Count; i++)
        {
            Assert.Equal(omittedRoutes[i].BuyRow, explicitNullRoutes[i].BuyRow);
            Assert.Equal(omittedRoutes[i].SellRow, explicitNullRoutes[i].SellRow);
            Assert.Equal(omittedRoutes[i].TripQty, explicitNullRoutes[i].TripQty);
            Assert.Equal(omittedRoutes[i].Gross, explicitNullRoutes[i].Gross);
            Assert.Equal(omittedRoutes[i].Net, explicitNullRoutes[i].Net);
            Assert.Equal(omittedRoutes[i].Tier, explicitNullRoutes[i].Tier);
            Assert.Equal(omittedRoutes[i].TripParts, explicitNullRoutes[i].TripParts);
        }
    }

    [Fact]
    public void Rank_DefaultStockFilter_MatchesExplicitAnyOnRealFixtureData()
    {
        var rows = MarketParse.ParseTradePriceRows(TradePricesFixture.LoadSampleJson(), out _)
            .Where(r => r.CommodityId == 47).ToList();
        var terminals = new Dictionary<int, MarketTerminal>
        {
            [rows.Single(r => r.TerminalName == "HDMS-Lathan").TerminalId] = Term(33, "Stanton", "Lyria"),
            [rows.Single(r => r.TerminalName == "TDD Area 18").TerminalId] = Term(12, "Stanton", "Area 18"),
        };

        var defaultRoutes = RoutePlanner.Rank(rows, terminals, shipScu: 100, shipMaxBox: 32, budget: null,
            originTerminalIds: null, scope: "ALL", take: 10);
        var explicitAnyRoutes = RoutePlanner.Rank(rows, terminals, shipScu: 100, shipMaxBox: 32, budget: null,
            originTerminalIds: null, scope: "ALL", take: 10, stockFilter: StockFilter.Any);

        Assert.Equal(defaultRoutes.Count, explicitAnyRoutes.Count);
        for (int i = 0; i < defaultRoutes.Count; i++)
        {
            Assert.Equal(defaultRoutes[i].BuyRow, explicitAnyRoutes[i].BuyRow);
            Assert.Equal(defaultRoutes[i].SellRow, explicitAnyRoutes[i].SellRow);
            Assert.Equal(defaultRoutes[i].TripQty, explicitAnyRoutes[i].TripQty);
            Assert.Equal(defaultRoutes[i].Gross, explicitAnyRoutes[i].Gross);
            Assert.Equal(defaultRoutes[i].Net, explicitAnyRoutes[i].Net);
            Assert.Equal(defaultRoutes[i].Tier, explicitAnyRoutes[i].Tier);
            Assert.Equal(defaultRoutes[i].TripParts, explicitAnyRoutes[i].TripParts);
        }
    }

    // Rank mode (task 7): PROFIT (default) orders by raw Net descending, unchanged from before this
    // task. PROFIT PER SCU re-orders by Net/TripQty descending, so a high-margin small-qty route
    // (worth more per unit of the ship's limited cargo) outranks a high-net bulk route whose margin
    // is thin once spread across a much bigger trip.
    [Fact]
    public void Rank_ProfitPerScu_OutranksHighMarginSmallQtyOverHighNetBulk()
    {
        var terminals = new Dictionary<int, MarketTerminal>
        {
            [1] = Term(1, "Stanton"), [2] = Term(2, "Stanton"), [3] = Term(3, "Stanton"), [4] = Term(4, "Stanton"),
        };
        var rows = new List<TradePriceRow>
        {
            // Route A: net 20,000 over a 100 SCU trip = 200 aUEC/SCU.
            Row(1, 10, buy: 100, sell: 0, buyStock: 100),
            Row(2, 10, buy: 0, sell: 300, sellDemand: 100),
            // Route B: net 5,000 over a 10 SCU trip = 500 aUEC/SCU - smaller net, bigger margin.
            Row(3, 20, buy: 100, sell: 0, buyStock: 10),
            Row(4, 20, buy: 0, sell: 600, sellDemand: 10),
        };

        var byProfit = RoutePlanner.Rank(rows, terminals, shipScu: 1000, shipMaxBox: 32, budget: null,
            originTerminalIds: null, scope: "ALL", take: 10, rankMode: RankMode.Profit);
        Assert.Equal(20000, byProfit[0].Net);   // route A leads under raw Net

        var byPerScu = RoutePlanner.Rank(rows, terminals, shipScu: 1000, shipMaxBox: 32, budget: null,
            originTerminalIds: null, scope: "ALL", take: 10, rankMode: RankMode.ProfitPerScu);
        Assert.Equal(5000, byPerScu[0].Net);    // route B leads under PROFIT PER SCU
        Assert.Equal(10, byPerScu[0].TripQty);
    }

    // A zero-TripQty route (nobody can actually run it once) is the lowest possible per-SCU value
    // (0) as well as tying for the lowest possible Net contribution among profitable routes, so it
    // sinks to the bottom of the list under either ranking mode.
    [Fact]
    public void Rank_ZeroTripQtyRoute_SinksToTheBottomUnderBothModes()
    {
        var terminals = new Dictionary<int, MarketTerminal>
        {
            [1] = Term(1, "Stanton"), [2] = Term(2, "Stanton"),
            [3] = Term(3, "Stanton"), [4] = Term(4, "Stanton"),
            [5] = Term(5, "Stanton"), [6] = Term(6, "Stanton"),
        };
        var rows = new List<TradePriceRow>
        {
            Row(1, 10, buy: 100, sell: 0, buyStock: 100),
            Row(2, 10, buy: 0, sell: 300, sellDemand: 100),   // net 20,000
            Row(3, 20, buy: 100, sell: 0, buyStock: 10),
            Row(4, 20, buy: 0, sell: 600, sellDemand: 10),    // net 5,000
            Row(5, 30, buy: 100, sell: 0, buyStock: 0),       // zero stock -> tripQty always 0
            Row(6, 30, buy: 0, sell: 900, sellDemand: 100),   // net 0
        };

        var byProfit = RoutePlanner.Rank(rows, terminals, shipScu: 1000, shipMaxBox: 32, budget: null,
            originTerminalIds: null, scope: "ALL", take: 10, rankMode: RankMode.Profit);
        Assert.Equal(0, byProfit[^1].TripQty);

        var byPerScu = RoutePlanner.Rank(rows, terminals, shipScu: 1000, shipMaxBox: 32, budget: null,
            originTerminalIds: null, scope: "ALL", take: 10, rankMode: RankMode.ProfitPerScu);
        Assert.Equal(0, byPerScu[^1].TripQty);
    }

    // Commodity filter (issue #41, planner half): null (or the parameter omitted) means ANY - no
    // constraint, the planner's original behavior. A non-null name keeps only routes hauling that
    // one commodity, matched case-insensitively against TradePriceRow.CommodityName (the same
    // string the picker's own items come from); a name matching nothing yields zero routes rather
    // than silently widening back to ANY, the same contract every other picker on this page keeps.
    [Fact]
    public void Rank_CommodityNameFilter_KeepsOnlyThatCommodity_CaseInsensitive()
    {
        var terminals = new Dictionary<int, MarketTerminal>
        {
            [1] = Term(1, "Stanton"), [2] = Term(2, "Stanton"), [3] = Term(3, "Stanton"), [4] = Term(4, "Stanton"),
        };
        var rows = new List<TradePriceRow>
        {
            Row(1, 10, buy: 100, sell: 0, buyStock: 100),
            Row(2, 10, buy: 0, sell: 300, sellDemand: 100),   // Commodity 10: net 20,000
            Row(3, 20, buy: 100, sell: 0, buyStock: 100),
            Row(4, 20, buy: 0, sell: 150, sellDemand: 100),   // Commodity 20: net 5,000
        };

        // Row() synthesizes CommodityName as "Commodity {id}"; the lowercase query must still match.
        var routes = RoutePlanner.Rank(rows, terminals, 100, 32, null, null, "ALL", 10,
            commodityName: "commodity 20");

        var route = Assert.Single(routes);
        Assert.Equal(20, route.BuyRow.CommodityId);
        Assert.Equal(5000, route.Net);
    }

    [Fact]
    public void Rank_CommodityNameFilter_UnknownName_YieldsZeroRoutes()
    {
        var terminals = new Dictionary<int, MarketTerminal> { [1] = Term(1, "Stanton"), [2] = Term(2, "Stanton") };
        var rows = new List<TradePriceRow>
        {
            Row(1, 47, buy: 100, sell: 0, buyStock: 50),
            Row(2, 47, buy: 0, sell: 200, sellDemand: 50),
        };

        Assert.Empty(RoutePlanner.Rank(rows, terminals, 100, 32, null, null, "ALL", 10,
            commodityName: "Commodity 99"));
    }

    [Fact]
    public void Rank_NullCommodityName_MeansUnconstrained()
    {
        var terminals = new Dictionary<int, MarketTerminal>
        {
            [1] = Term(1, "Stanton"), [2] = Term(2, "Stanton"), [3] = Term(3, "Stanton"), [4] = Term(4, "Stanton"),
        };
        var rows = new List<TradePriceRow>
        {
            Row(1, 10, buy: 100, sell: 0, buyStock: 100),
            Row(2, 10, buy: 0, sell: 300, sellDemand: 100),
            Row(3, 20, buy: 100, sell: 0, buyStock: 100),
            Row(4, 20, buy: 0, sell: 150, sellDemand: 100),
        };

        var routes = RoutePlanner.Rank(rows, terminals, 100, 32, null, null, "ALL", 10,
            commodityName: null);

        Assert.Equal(2, routes.Count);   // both commodities' routes survive
    }

    [Fact]
    public void Rank_NullCommodityName_MatchesOmittedParameterOnRealFixtureData()
    {
        var rows = MarketParse.ParseTradePriceRows(TradePricesFixture.LoadSampleJson(), out _)
            .Where(r => r.CommodityId == 47).ToList();
        var terminals = new Dictionary<int, MarketTerminal>
        {
            [rows.Single(r => r.TerminalName == "HDMS-Lathan").TerminalId] = Term(33, "Stanton", "Lyria"),
            [rows.Single(r => r.TerminalName == "TDD Area 18").TerminalId] = Term(12, "Stanton", "Area 18"),
        };

        var omittedRoutes = RoutePlanner.Rank(rows, terminals, shipScu: 100, shipMaxBox: 32, budget: null,
            originTerminalIds: null, scope: "ALL", take: 10);
        var explicitNullRoutes = RoutePlanner.Rank(rows, terminals, shipScu: 100, shipMaxBox: 32, budget: null,
            originTerminalIds: null, scope: "ALL", take: 10, commodityName: null);

        Assert.Equal(omittedRoutes.Count, explicitNullRoutes.Count);
        for (int i = 0; i < omittedRoutes.Count; i++)
        {
            Assert.Equal(omittedRoutes[i].BuyRow, explicitNullRoutes[i].BuyRow);
            Assert.Equal(omittedRoutes[i].SellRow, explicitNullRoutes[i].SellRow);
            Assert.Equal(omittedRoutes[i].TripQty, explicitNullRoutes[i].TripQty);
            Assert.Equal(omittedRoutes[i].Gross, explicitNullRoutes[i].Gross);
            Assert.Equal(omittedRoutes[i].Net, explicitNullRoutes[i].Net);
            Assert.Equal(omittedRoutes[i].Tier, explicitNullRoutes[i].Tier);
            Assert.Equal(omittedRoutes[i].TripParts, explicitNullRoutes[i].TripParts);
        }
    }

    [Fact]
    public void Rank_DefaultRankMode_MatchesExplicitProfitOnRealFixtureData()
    {
        var rows = MarketParse.ParseTradePriceRows(TradePricesFixture.LoadSampleJson(), out _)
            .Where(r => r.CommodityId == 47).ToList();
        var terminals = new Dictionary<int, MarketTerminal>
        {
            [rows.Single(r => r.TerminalName == "HDMS-Lathan").TerminalId] = Term(33, "Stanton", "Lyria"),
            [rows.Single(r => r.TerminalName == "TDD Area 18").TerminalId] = Term(12, "Stanton", "Area 18"),
        };

        var defaultRoutes = RoutePlanner.Rank(rows, terminals, shipScu: 100, shipMaxBox: 32, budget: null,
            originTerminalIds: null, scope: "ALL", take: 10);
        var explicitProfitRoutes = RoutePlanner.Rank(rows, terminals, shipScu: 100, shipMaxBox: 32, budget: null,
            originTerminalIds: null, scope: "ALL", take: 10, rankMode: RankMode.Profit);

        Assert.Equal(defaultRoutes.Count, explicitProfitRoutes.Count);
        for (int i = 0; i < defaultRoutes.Count; i++)
        {
            Assert.Equal(defaultRoutes[i].BuyRow, explicitProfitRoutes[i].BuyRow);
            Assert.Equal(defaultRoutes[i].SellRow, explicitProfitRoutes[i].SellRow);
            Assert.Equal(defaultRoutes[i].TripQty, explicitProfitRoutes[i].TripQty);
            Assert.Equal(defaultRoutes[i].Gross, explicitProfitRoutes[i].Gross);
            Assert.Equal(defaultRoutes[i].Net, explicitProfitRoutes[i].Net);
            Assert.Equal(defaultRoutes[i].Tier, explicitProfitRoutes[i].Tier);
            Assert.Equal(defaultRoutes[i].TripParts, explicitProfitRoutes[i].TripParts);
        }
    }

    [Fact]
    public void Rank_SnapsTripQtyToWhatTheBuyMenuCanActuallySupply()
    {
        // A Cutlass Black's 46 SCU at a terminal whose smallest container is 8: the fullest
        // buyable load is 5x8 = 40, so 6 SCU cannot be bought and must not be priced.
        var terminals = new Dictionary<int, MarketTerminal> { [1] = Term(1, "Stanton"), [2] = Term(2, "Stanton") };
        var rows = new List<TradePriceRow>
        {
            Row(1, 47, buy: 100, sell: 0, buyStock: 500, containerSizes: "8,16,24,32"),
            Row(2, 47, buy: 0, sell: 200, sellDemand: 500),
        };

        var routes = RoutePlanner.Rank(rows, terminals, shipScu: 46, shipMaxBox: 16, budget: null,
            originTerminalIds: null, scope: "ALL", take: 10);

        var route = Assert.Single(routes);
        Assert.Equal(40, route.TripQty);        // 5x8, the fullest load at or under 46
        Assert.Equal(46, route.PlannedQty);     // what ship and stock alone allowed
        Assert.Equal(4000, route.Gross);        // 40 * (200 - 100), NOT 46 * 100
        Assert.Equal(route.Gross, route.Net);
    }

    [Fact]
    public void Rank_LeavesTripQtyAlone_WhenTheMenuReachesTheTarget()
    {
        // A menu containing 1 hits any target, so nothing moves and PlannedQty equals TripQty.
        var terminals = new Dictionary<int, MarketTerminal> { [1] = Term(1, "Stanton"), [2] = Term(2, "Stanton") };
        var rows = new List<TradePriceRow>
        {
            Row(1, 47, buy: 100, sell: 0, buyStock: 500),
            Row(2, 47, buy: 0, sell: 200, sellDemand: 500),
        };

        var routes = RoutePlanner.Rank(rows, terminals, 46, 16, null, null, "ALL", 10);

        var route = Assert.Single(routes);
        Assert.Equal(46, route.TripQty);
        Assert.Equal(46, route.PlannedQty);
        Assert.Equal(4600, route.Gross);
    }

    [Fact]
    public void Rank_ReordersRoutes_WhenSnappingChangesWhichPaysMore()
    {
        // The whole point of the fix. On raw capacity commodity 10 wins (46 x 105 = 4,830 against
        // 46 x 100 = 4,600). Once commodity 10's menu is honoured it can only load 40 (4,200), so
        // commodity 20 must come first.
        var terminals = new Dictionary<int, MarketTerminal>
        {
            [1] = Term(1, "Stanton"), [2] = Term(2, "Stanton"), [3] = Term(3, "Stanton"), [4] = Term(4, "Stanton"),
        };
        var rows = new List<TradePriceRow>
        {
            Row(1, 10, buy: 100, sell: 0, buyStock: 500, containerSizes: "8,16,24,32"),
            Row(2, 10, buy: 0, sell: 205, sellDemand: 500, containerSizes: "8,16,24,32"),
            Row(3, 20, buy: 100, sell: 0, buyStock: 500),
            Row(4, 20, buy: 0, sell: 200, sellDemand: 500),
        };

        var routes = RoutePlanner.Rank(rows, terminals, 46, 16, null, null, "ALL", 10);

        Assert.Equal(2, routes.Count);
        Assert.Equal(20, routes[0].BuyRow.CommodityId);
        Assert.Equal(4600, routes[0].Net);
        Assert.Equal(10, routes[1].BuyRow.CommodityId);
        Assert.Equal(4200, routes[1].Net);
    }

    [Fact]
    public void Rank_KeepsRoutesThatCanBuyNothing_RatherThanDroppingThem()
    {
        // Stock is 6 but the smallest container sold here is 8. The route is unrunnable, and the
        // card's "none sold here small enough" line is the only place that is ever explained, so
        // the row stays and simply pays nothing.
        var terminals = new Dictionary<int, MarketTerminal> { [1] = Term(1, "Stanton"), [2] = Term(2, "Stanton") };
        var rows = new List<TradePriceRow>
        {
            Row(1, 47, buy: 100, sell: 0, buyStock: 6, containerSizes: "8,16,24,32"),
            Row(2, 47, buy: 0, sell: 200, sellDemand: 500),
        };

        var routes = RoutePlanner.Rank(rows, terminals, 46, 16, null, null, "ALL", 10);

        var route = Assert.Single(routes);
        Assert.Equal(0, route.TripQty);
        Assert.Equal(6, route.PlannedQty);
        Assert.Equal(0, route.Net);
    }

    [Fact]
    public void Rank_JudgesDemandCoverageAgainstTheSnappedQuantity()
    {
        // Demand is 42, which does not cover the 46 the hull wanted but does cover the 40 that can
        // actually be bought and therefore actually delivered. CoversTrip must pass.
        var terminals = new Dictionary<int, MarketTerminal> { [1] = Term(1, "Stanton"), [2] = Term(2, "Stanton") };
        var rows = new List<TradePriceRow>
        {
            Row(1, 47, buy: 100, sell: 0, buyStock: 500, containerSizes: "8,16,24,32"),
            Row(2, 47, buy: 0, sell: 200, sellDemand: 42),
        };

        var routes = RoutePlanner.Rank(rows, terminals, 46, 16, null, null, "ALL", 10,
            StockFilter.CoversTrip);

        var route = Assert.Single(routes);
        Assert.Equal(40, route.TripQty);
    }

    [Fact]
    public void Rank_SnapsAfterTheBudgetHasAlreadyBound()
    {
        // Budget affords 30 (3,000 / 100), below the hull's 46. The menu then snaps that 30 down
        // to 24, so both limits apply in order and the smaller wins.
        var terminals = new Dictionary<int, MarketTerminal> { [1] = Term(1, "Stanton"), [2] = Term(2, "Stanton") };
        var rows = new List<TradePriceRow>
        {
            Row(1, 47, buy: 100, sell: 0, buyStock: 500, containerSizes: "8,16,24,32"),
            Row(2, 47, buy: 0, sell: 200, sellDemand: 500),
        };

        var routes = RoutePlanner.Rank(rows, terminals, 46, 16, budget: 3000,
            originTerminalIds: null, scope: "ALL", take: 10);

        var route = Assert.Single(routes);
        Assert.Equal(24, route.TripQty);     // 16 + 8; 30 is not reachable from 8,16
        Assert.Equal(30, route.PlannedQty);
        Assert.Equal(2400, route.Net);
    }

    [Fact]
    public void Rank_NarratesTheContainerLimit_OnlyWhenItActuallyBit()
    {
        var terminals = new Dictionary<int, MarketTerminal> { [1] = Term(1, "Stanton"), [2] = Term(2, "Stanton") };
        var snapped = RoutePlanner.Rank(
            new List<TradePriceRow>
            {
                Row(1, 47, buy: 100, sell: 0, buyStock: 500, containerSizes: "8,16,24,32"),
                Row(2, 47, buy: 0, sell: 200, sellDemand: 500),
            },
            terminals, 46, 16, null, null, "ALL", 10);
        Assert.Contains("containers reach 40", snapped[0].TripParts);

        var untouched = RoutePlanner.Rank(
            new List<TradePriceRow>
            {
                Row(1, 47, buy: 100, sell: 0, buyStock: 500),
                Row(2, 47, buy: 0, sell: 200, sellDemand: 500),
            },
            terminals, 46, 16, null, null, "ALL", 10);
        Assert.DoesNotContain(untouched[0].TripParts, p => p.StartsWith("containers reach"));
    }
}
