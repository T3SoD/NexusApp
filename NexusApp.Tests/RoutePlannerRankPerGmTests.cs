using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// RankMode.ProfitPerGm ranks by profit per Gm of straight-line travel. The planner has always
// COMPUTED and DISPLAYED that distance on every row while ranking purely on money: from the shipped
// positions, Area 18 to ARC-L1 is 2.89 Gm and Area 18 to ARC-L3 is 56.96 Gm, so a route paying 3%
// more at ARC-L3 outranked the near one and a hauler lost the difference twenty times over.
//
// Distance arrives as a DELEGATE, so these tests feed exact synthetic distances rather than
// depending on the real catalog's coordinates - the ordering rule is what is under test here, not
// the geometry.
public class RoutePlannerRankPerGmTests
{
    private const double Gm = 1_000_000_000.0;

    private static MarketTerminal Term(int id) => new(id, $"T{id}", "commodity", false, "Stanton", $"L{id}");

    private static readonly Dictionary<int, MarketTerminal> Terminals = new()
    {
        [1] = Term(1), [2] = Term(2), [3] = Term(3), [4] = Term(4),
    };

    private static TradePriceRow Buy(int terminalId, int commodityId, double buy, int stock) =>
        new(terminalId, commodityId, buy, 0, stock, 0, 1, 0, "1,2,4,8,16,24,32", DateTime.UtcNow,
            $"T{terminalId}", $"C{commodityId}");

    private static TradePriceRow Sell(int terminalId, int commodityId, double sell, int demand) =>
        new(terminalId, commodityId, 0, sell, 0, demand, 0, 1, "1,2,4,8,16,24,32", DateTime.UtcNow,
            $"T{terminalId}", $"C{commodityId}");

    // Two runnable routes: commodity 1 pays 100/SCU over terminals 1->2, commodity 2 pays 130/SCU
    // over terminals 3->4. On raw profit the second wins. Distance decides whether it should.
    private static List<TradePriceRow> TwoRoutes() =>
    [
        Buy(1, 1, 100, 1000), Sell(2, 1, 200, 1000),
        Buy(3, 2, 100, 1000), Sell(4, 2, 230, 1000),
    ];

    private static Func<MarketTerminal?, MarketTerminal?, double?> Distances(
        double? oneToTwo, double? threeToFour) =>
        (a, b) =>
        {
            if (a is null || b is null) return null;
            var pair = (a.Id, b.Id);
            if (pair is (1, 2) or (2, 1)) return oneToTwo;
            if (pair is (3, 4) or (4, 3)) return threeToFour;
            return null;
        };

    private static List<TradeRoute> Rank(Func<MarketTerminal?, MarketTerminal?, double?> dist) =>
        RoutePlanner.Rank(TwoRoutes(), Terminals, shipScu: 100, shipMaxBox: 32, budget: null,
            originTerminalIds: null, scope: "ALL", take: 10, rankMode: RankMode.ProfitPerGm,
            distanceMeters: dist).ToList();

    [Fact]
    public void TheNearerRouteWins_EvenThoughItPaysLess()
    {
        // The exact defect: 30% more profit, twenty times the distance.
        var ranked = Rank(Distances(oneToTwo: 2.89 * Gm, threeToFour: 56.96 * Gm));

        Assert.Equal(1, ranked[0].BuyRow.TerminalId);
        Assert.Equal(3, ranked[1].BuyRow.TerminalId);
    }

    [Fact]
    public void ProfitStillWins_WhenTheDistancesAreEqual()
    {
        // The ratio must not invert a fair comparison: same distance, more money, first.
        var ranked = Rank(Distances(oneToTwo: 10 * Gm, threeToFour: 10 * Gm));

        Assert.Equal(3, ranked[0].BuyRow.TerminalId);
    }

    [Fact]
    public void UnmeasurableRoutes_SortLast_AndAreNeverDropped()
    {
        // Cross-system pairs and unresolved terminals have no distance. Dropping them would silently
        // hide every cross-system route the moment this mode is picked, which is the opposite of
        // what someone comparing runs wants - so they rank last and stay in the list.
        var ranked = Rank(Distances(oneToTwo: null, threeToFour: 5 * Gm));

        Assert.Equal(2, ranked.Count);
        Assert.Equal(3, ranked[0].BuyRow.TerminalId);
        Assert.Equal(1, ranked[1].BuyRow.TerminalId);
    }

    [Fact]
    public void NothingMeasurable_KeepsEveryRoute()
    {
        var ranked = Rank(Distances(oneToTwo: null, threeToFour: null));
        Assert.Equal(2, ranked.Count);
    }

    [Fact]
    public void ZeroDistance_IsTheBestRatio_NotADivideByZero()
    {
        // Two terminals resolving to the same map object (a station's admin office and its cargo
        // deck) are a genuinely free move, so they belong at the top rather than crashing or
        // sorting as though they were infinitely far.
        var ranked = Rank(Distances(oneToTwo: 0, threeToFour: 1 * Gm));

        Assert.Equal(1, ranked[0].BuyRow.TerminalId);
    }

    [Fact]
    public void WithNoDistanceSource_FallsBackToProfit_RatherThanAnArbitraryOrder()
    {
        // A guard, not a user-reachable state: the pill is only offered with a catalog wired. It
        // must still be deterministic if it is ever hit.
        var ranked = RoutePlanner.Rank(TwoRoutes(), Terminals, shipScu: 100, shipMaxBox: 32,
            budget: null, originTerminalIds: null, scope: "ALL", take: 10,
            rankMode: RankMode.ProfitPerGm, distanceMeters: null).ToList();

        Assert.Equal(3, ranked[0].BuyRow.TerminalId);   // highest raw Net
    }

    [Fact]
    public void TheOtherTwoModesAreUnaffectedByThePresenceOfADistanceSource()
    {
        // Regression guard on the two shipped modes: passing a distance delegate must not perturb
        // them, so the new parameter cannot change behaviour for anyone who never picks the pill.
        var withDist = RoutePlanner.Rank(TwoRoutes(), Terminals, 100, 32, null, null, "ALL", 10,
            rankMode: RankMode.Profit, distanceMeters: Distances(2.89 * Gm, 56.96 * Gm)).ToList();
        var without = RoutePlanner.Rank(TwoRoutes(), Terminals, 100, 32, null, null, "ALL", 10,
            rankMode: RankMode.Profit).ToList();

        Assert.Equal(without.Select(r => r.BuyRow.TerminalId), withDist.Select(r => r.BuyRow.TerminalId));
        Assert.Equal(3, withDist[0].BuyRow.TerminalId);
    }
}
