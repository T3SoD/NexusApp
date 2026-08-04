using System.Collections.Generic;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// RankMode.Roi ranks by capital efficiency: net over the buy-side capital the trip ties up (the
// same derivation the card rail's TradeFinancials fold renders). A small wallet cares whether
// 100,000 aUEC in returns 13,000 or 10,000 - raw profit ordering hides that entirely. Harness
// mirrors RoutePlannerRankPerGmTests: synthetic rows, ordering rule under test, not the market.
public class RoutePlannerRankRoiTests
{
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

    private static List<TradeRoute> Rank(List<TradePriceRow> rows) =>
        RoutePlanner.Rank(rows, Terminals, shipScu: 100, shipMaxBox: 32, budget: null,
            originTerminalIds: null, scope: "ALL", take: 10, rankMode: RankMode.Roi).ToList();

    [Fact]
    public void TheEfficientRouteWins_EvenThoughItPaysLess()
    {
        // Commodity 1: 100 in, 100/SCU margin - 100% return. Commodity 2: 1,000 in, 130/SCU
        // margin - 13% return but MORE raw profit. Profit mode puts commodity 2 first; ROI mode
        // must invert that.
        var ranked = Rank(
        [
            Buy(1, 1, 100, 1000), Sell(2, 1, 200, 1000),
            Buy(3, 2, 1000, 1000), Sell(4, 2, 1130, 1000),
        ]);

        Assert.Equal(1, ranked[0].BuyRow.TerminalId);
        Assert.Equal(3, ranked[1].BuyRow.TerminalId);
    }

    [Fact]
    public void NetBreaksAnRoiTie()
    {
        // Same 100% return on both; the one that pays more in absolute aUEC ranks first.
        var ranked = Rank(
        [
            Buy(1, 1, 100, 1000), Sell(2, 1, 200, 1000),   // net 10,000
            Buy(3, 2, 200, 1000), Sell(4, 2, 400, 1000),   // net 20,000
        ]);

        Assert.Equal(3, ranked[0].BuyRow.TerminalId);
    }
}
