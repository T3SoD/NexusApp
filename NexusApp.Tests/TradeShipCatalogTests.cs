using System.IO;
using System.Linq;
using System.Text;
using NexusApp.Services;
using NexusApp.Services.Cargo;
using Xunit;

namespace NexusApp.Tests;

// The trade ship list is generated from the DataCore and filtered by RSI's ship matrix
// (sc-datamine/extractors/build_trade_ships.py). These tests pin the two properties that a
// regeneration could silently break: continuity with the persisted TradeShipId, and agreement
// with the grid catalog wherever the two overlap.
public class TradeShipCatalogTests
{
    private static readonly TradeShipCatalog Trade = TradeShipCatalog.LoadEmbedded();
    private static readonly CargoShipCatalog Grids = CargoShipCatalog.LoadEmbedded();

    [Fact]
    public void Embedded_LoadsTheFullFlyableList()
    {
        // Deliberately a floor, not an exact count: CIG ships new hulls and a regeneration should
        // not need a test edit. A collapse to the old 15-ship grid catalog would still be caught.
        Assert.True(Trade.Ships.Count >= 80, $"only {Trade.Ships.Count} trade ships loaded");
    }

    [Fact]
    public void EveryGridCatalogShip_IsAlsoATradeShip_WithTheSameId()
    {
        // AppSettings.TradeShipId persists a catalog id. Before this list existed the planner read
        // the grid catalog, so every id it could have written must still resolve, or an owner's
        // saved ship silently reverts to the first row on upgrade.
        var missing = Grids.Ships
            .Where(g => Trade.ById(g.Id) is null)
            .Select(g => $"{g.Id} ({g.DisplayName})")
            .ToList();
        Assert.True(missing.Count == 0, "grid-catalog ships absent from the trade list: " + string.Join(", ", missing));
    }

    [Fact]
    public void OverlappingShips_ReportTheSameCapacityAndMaxContainer()
    {
        // The anti-drift guard. The two files are generated from the same DataCore extraction but
        // by different paths - the grid catalog carries per-cell geometry, the trade list carries
        // precomputed totals - so a regeneration of either could disagree with the other. The
        // planner ranks with the trade numbers while the Cargo Planner packs with the grid ones;
        // if they ever diverge, a route that says "696 SCU" would not physically fit.
        foreach (var g in Grids.Ships)
        {
            var t = Trade.ById(g.Id);
            Assert.NotNull(t);
            Assert.True(g.TotalScu == t!.TotalScu,
                $"{g.DisplayName}: grid catalog says {g.TotalScu} SCU, trade list says {t.TotalScu}");
            Assert.True(g.MaxContainerScu == t.MaxContainerScu,
                $"{g.DisplayName}: grid catalog max box {g.MaxContainerScu}, trade list {t.MaxContainerScu}");
        }
    }

    [Fact]
    public void Ids_AreUnique()
    {
        var dupes = Trade.Ships.GroupBy(s => s.Id).Where(x => x.Count() > 1).Select(x => x.Key).ToList();
        Assert.True(dupes.Count == 0, "duplicate trade ship ids: " + string.Join(", ", dupes));
    }

    [Fact]
    public void EveryShip_HasCargoAndAUsableContainerSize()
    {
        // A zero max container makes TradeMath.BoxFits reject every terminal, which would empty the
        // route list for that ship rather than fail loudly. Ground vehicles and snubs are filtered
        // out by the generator, so nothing here should be capacity-less.
        Assert.All(Trade.Ships, s =>
        {
            Assert.True(s.TotalScu > 0, $"{s.DisplayName} has no cargo capacity");
            Assert.True(s.MaxContainerScu > 0, $"{s.DisplayName} has no usable container size");
        });
    }

    [Fact]
    public void WholeList_IsAlphabetical()
    {
        // One ordering rule for all ~90 entries, with no priority-ranked head. A ship appearing
        // out of alphabetical order would be unfindable in a list this long.
        var names = Trade.Ships.Select(s => s.DisplayName).ToList();
        Assert.Equal(names.OrderBy(n => n, System.StringComparer.OrdinalIgnoreCase).ToList(), names);
    }

    [Fact]
    public void Load_RejectsAShipWithNoCapacity()
    {
        // Contract check, not a fallback: the generator cannot emit this, and silently admitting it
        // would put a ship in the dropdown that can never rank a route.
        const string json = """[{"id":"x","name":"Nothing","manufacturer":"","totalScu":0,"maxBoxScu":8}]""";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        Assert.Throws<InvalidDataException>(() => TradeShipCatalog.Load(stream));
    }

    [Fact]
    public void Load_RejectsAShipWithNoUsableContainerSize()
    {
        const string json = """[{"id":"x","name":"Nothing","manufacturer":"","totalScu":32,"maxBoxScu":0}]""";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        Assert.Throws<InvalidDataException>(() => TradeShipCatalog.Load(stream));
    }
}
