using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

public class SellLookupTests
{
    private static MarketTerminal Term(int id, string system) => new(id, $"Terminal {id}", "commodity", false, system, "");

    private static TradePriceRow SellRow(int terminalId, int commodityId, double sell, int demand) =>
        new(terminalId, commodityId, 0, sell, 0, demand, 0, 1, "1,2,4,8,16,24,32", DateTime.UtcNow,
            $"Terminal {terminalId}", $"Commodity {commodityId}");

    [Fact]
    public void Rank_FiltersToCommodityIdAndPositiveSell()
    {
        var terminals = new Dictionary<int, MarketTerminal> { [1] = Term(1, "Stanton"), [2] = Term(2, "Stanton") };
        var rows = new List<TradePriceRow>
        {
            SellRow(1, 47, 8500, 1594),
            SellRow(2, 99, 100, 50),                              // different commodity: excluded
            new(3, 47, 100, 0, 10, 0, 1, 0, "1,2", DateTime.UtcNow, "Terminal 3", "Laranite"), // Sell 0: excluded
        };

        var buyers = SellLookup.Rank(rows, terminals, commodityId: 47, qtyScu: 50, originTerminalId: null, scope: "ALL");

        var buyer = Assert.Single(buyers);
        Assert.Equal(1, buyer.Row.TerminalId);
    }

    [Fact]
    public void Rank_SellableScuCappedByDemand()
    {
        var terminals = new Dictionary<int, MarketTerminal> { [1] = Term(1, "Stanton") };
        var rows = new List<TradePriceRow> { SellRow(1, 47, 8500, demand: 30) };

        var buyer = Assert.Single(SellLookup.Rank(rows, terminals, 47, qtyScu: 100, null, "ALL"));

        Assert.Equal(30, buyer.SellableScu);
        Assert.Equal(30 * 8500, buyer.EffectiveValue);
    }

    [Fact]
    public void Rank_QuantityBelowDemand_SellableScuIsTheQuantity()
    {
        var terminals = new Dictionary<int, MarketTerminal> { [1] = Term(1, "Stanton") };
        var rows = new List<TradePriceRow> { SellRow(1, 47, 8500, demand: 1000) };

        var buyer = Assert.Single(SellLookup.Rank(rows, terminals, 47, qtyScu: 40, null, "ALL"));

        Assert.Equal(40, buyer.SellableScu);
        Assert.Equal(40 * 8500, buyer.EffectiveValue);
    }

    [Fact]
    public void Rank_OriginProvided_UsesProximityTiersDerive()
    {
        var terminals = new Dictionary<int, MarketTerminal> { [1] = Term(1, "Stanton"), [2] = Term(2, "Pyro") };
        var rows = new List<TradePriceRow> { SellRow(2, 47, 8500, 100) };

        var buyer = Assert.Single(SellLookup.Rank(rows, terminals, 47, 50, originTerminalId: 1, scope: "ALL"));

        Assert.Equal(ProximityTier.CrossSystem, buyer.Tier);
    }

    [Fact]
    public void Rank_NoOriginProvided_DefaultsToCrossSystem()
    {
        var terminals = new Dictionary<int, MarketTerminal> { [1] = Term(1, "Stanton") };
        var rows = new List<TradePriceRow> { SellRow(1, 47, 8500, 100) };

        var buyer = Assert.Single(SellLookup.Rank(rows, terminals, 47, 50, originTerminalId: null, scope: "ALL"));

        Assert.Equal(ProximityTier.CrossSystem, buyer.Tier);
    }

    [Fact]
    public void Rank_OriginTerminalNotFoundInLookup_DefaultsToCrossSystem()
    {
        var terminals = new Dictionary<int, MarketTerminal> { [1] = Term(1, "Stanton") };   // no id 99
        var rows = new List<TradePriceRow> { SellRow(1, 47, 8500, 100) };

        var buyer = Assert.Single(SellLookup.Rank(rows, terminals, 47, 50, originTerminalId: 99, scope: "ALL"));

        Assert.Equal(ProximityTier.CrossSystem, buyer.Tier);
    }

    [Fact]
    public void Rank_ScopeRestrictsToMatchingSystem()
    {
        var terminals = new Dictionary<int, MarketTerminal> { [1] = Term(1, "Stanton"), [2] = Term(2, "Pyro") };
        var rows = new List<TradePriceRow> { SellRow(1, 47, 8500, 100), SellRow(2, 47, 9000, 100) };

        var buyers = SellLookup.Rank(rows, terminals, 47, 50, null, scope: "STANTON");

        var buyer = Assert.Single(buyers);
        Assert.Equal(1, buyer.Row.TerminalId);
    }

    [Fact]
    public void Rank_OrdersByEffectiveValueDescending()
    {
        var terminals = new Dictionary<int, MarketTerminal> { [1] = Term(1, "Stanton"), [2] = Term(2, "Stanton") };
        var rows = new List<TradePriceRow> { SellRow(1, 47, 8000, 100), SellRow(2, 47, 9000, 100) };

        var buyers = SellLookup.Rank(rows, terminals, 47, 50, null, "ALL");

        Assert.Equal(2, buyers[0].Row.TerminalId);
        Assert.Equal(1, buyers[1].Row.TerminalId);
    }

    [Fact]
    public void Rank_ZeroOrNegativeQty_ReturnsEmpty()
    {
        var terminals = new Dictionary<int, MarketTerminal> { [1] = Term(1, "Stanton") };
        var rows = new List<TradePriceRow> { SellRow(1, 47, 8500, 100) };

        Assert.Empty(SellLookup.Rank(rows, terminals, 47, 0, null, "ALL"));
        Assert.Empty(SellLookup.Rank(rows, terminals, 47, -5, null, "ALL"));
    }
}
