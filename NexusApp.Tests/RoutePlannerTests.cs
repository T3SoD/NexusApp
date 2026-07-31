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

    // Stock/demand coverage filter (task 5): applied per pair, inside the pairing loop, before the
    // take cutoff - a route that fails coverage is skipped outright rather than merely ranked lower.
    [Fact]
    public void Rank_StockExactlyEqualsTripQty_PassesCoversTripButFailsCoversTwoTrips()
    {
        var terminals = new Dictionary<int, MarketTerminal> { [1] = Term(1, "Stanton"), [2] = Term(2, "Stanton") };
        var rows = new List<TradePriceRow>
        {
            Row(1, 47, buy: 100, sell: 0, buyStock: 50),
            Row(2, 47, buy: 0, sell: 200, sellDemand: 50),
        };

        // shipScu 100, buyStock 50 => tripQty = min(100, 50) = 50, exactly matching both stock and demand.
        var trip = Assert.Single(RoutePlanner.Rank(rows, terminals, 100, 32, null, null, "ALL", 10, StockFilter.CoversTrip));
        Assert.Equal(50, trip.TripQty);

        Assert.Empty(RoutePlanner.Rank(rows, terminals, 100, 32, null, null, "ALL", 10, StockFilter.CoversTwoTrips));
    }

    [Fact]
    public void Rank_ZeroStockPair_ExcludedUnderCoverageFiltersButKeptUnderAny()
    {
        var terminals = new Dictionary<int, MarketTerminal> { [1] = Term(1, "Stanton"), [2] = Term(2, "Stanton") };
        var rows = new List<TradePriceRow>
        {
            Row(1, 47, buy: 100, sell: 0, buyStock: 0),     // zero stock -> tripQty is always 0
            Row(2, 47, buy: 0, sell: 200, sellDemand: 100),
        };

        Assert.Single(RoutePlanner.Rank(rows, terminals, 100, 32, null, null, "ALL", 10, StockFilter.Any));
        Assert.Empty(RoutePlanner.Rank(rows, terminals, 100, 32, null, null, "ALL", 10, StockFilter.CoversTrip));
        Assert.Empty(RoutePlanner.Rank(rows, terminals, 100, 32, null, null, "ALL", 10, StockFilter.CoversTwoTrips));
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
}
