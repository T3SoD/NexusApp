using NexusApp.Models.Cargo;
using NexusApp.Services.Cargo;
using Xunit;

namespace NexusApp.Tests.Cargo;

public class CargoShipCatalogTests
{
    private static readonly CargoShipCatalog Catalog = CargoShipCatalog.LoadEmbedded();

    // The catalog is the curated hauling set (freight-focus cut, low-relevance ships removed),
    // rebuilt from game-file data per patch. 18 as of the 2026-07-04 keep/remove pass (MPUV Cargo removed).
    [Fact]
    public void Load_HasAllCargoShips()
    {
        Assert.Equal(18, Catalog.Ships.Count);
    }

    [Fact]
    public void Load_SortsByHaulingPriority()
    {
        // Rank 1 leads; the top three are the most-used haulers from the research.
        Assert.Equal(new[] { "C2 Hercules", "RAFT", "Constellation Taurus" },
            Catalog.Ships.Take(3).Select(s => s.DisplayName).ToArray());
    }

    [Fact]
    public void Load_EveryGridIsValid()
    {
        foreach (var ship in Catalog.Ships)
        {
            Assert.NotEmpty(ship.Grids);
            foreach (var g in ship.Grids)
            {
                Assert.True(g.W > 0 && g.D > 0 && g.H > 0, $"{ship.DisplayName}: bad dims");
                Assert.True(BoxType.IsStandard(g.MaxContainerScu), $"{ship.DisplayName}: bad cap");
                Assert.Equal(g.W * g.D * g.H, g.Capacity);
            }
            Assert.True(ship.TotalScu > 0);
        }
    }

    [Fact]
    public void Load_KnownShipsHaveExpectedCapacity()
    {
        Assert.Equal(4608, Ship("Hull C").TotalScu);
        Assert.Equal(120, Ship("Freelancer MAX").TotalScu);
    }

    [Fact]
    public void EveryShipCanPackAtLeastOneUnitOfCargo()
    {
        // A single 1 SCU box must fit every ship (sanity: no ship is unusable).
        var manifest = new List<ManifestItem> { new() { Scu = 1, Cap = 1 } };
        foreach (var ship in Catalog.Ships)
        {
            var boxes = CargoPacker.BuildBoxes(manifest, ship.MaxContainerScu);
            var plan = MultiTripPlanner.Plan(ship.Grids, boxes);
            Assert.True(plan.FitsInOneTrip, $"{ship.DisplayName} could not hold 1 SCU");
        }
    }

    [Fact]
    public void Search_MatchesNameAndManufacturer()
    {
        Assert.Contains(Catalog.Search("hull"), s => s.DisplayName == "Hull C");
        Assert.NotEmpty(Catalog.Search("freelancer"));
    }

    // -- local grid overrides (Edit Layout mode) ----------------------------------

    private static CargoGridOverrideStore StoreWith(string shipId, string shipName, params GridOverride[] grids)
    {
        var store = new CargoGridOverrideStore(
            Path.Combine(Path.GetTempPath(), $"ov_{Guid.NewGuid():N}.json"));
        store.Set(shipId, shipName, grids);
        return store;
    }

    [Fact]
    public void WithOverrides_ReplacesGridsAndRecomputesScu()
    {
        var ship = Ship("Avenger Titan");
        var store = StoreWith(ship.Id, ship.DisplayName,
            new GridOverride { Id = 0, W = 4, D = 2, H = 2, Cap = 16, Px = 0, Py = 0, Pz = 0 },
            new GridOverride { Id = 1, W = 2, D = 2, H = 2, Cap = 8, Px = 5, Py = 0, Pz = 0 });

        var merged = Catalog.WithOverrides(store).Ships.First(s => s.Id == ship.Id);
        Assert.Equal(2, merged.GridCount);
        Assert.Equal(16 + 8, merged.TotalScu);          // 4*2*2 + 2*2*2
        Assert.True(merged.Grids.All(g => g.HasPos));   // an edited ship is always positioned
    }

    [Fact]
    public void WithOverrides_LeavesUneditedShipsUnchanged()
    {
        var cutter = Ship("Avenger Titan");
        var store = StoreWith(cutter.Id, cutter.DisplayName,
            new GridOverride { Id = 0, W = 1, D = 1, H = 1, Cap = 1 });

        var merged = Catalog.WithOverrides(store);
        Assert.Equal(Ship("Hull C").TotalScu, merged.Ships.First(s => s.DisplayName == "Hull C").TotalScu);
    }

    [Fact]
    public void WithOverrides_NoEntries_ReturnsSameInstance()
    {
        var store = new CargoGridOverrideStore(
            Path.Combine(Path.GetTempPath(), $"ov_{Guid.NewGuid():N}.json"));
        Assert.Same(Catalog, Catalog.WithOverrides(store));
    }

    [Fact]
    public void WithOverrides_InvalidOverride_FallsBackToEmbedded()
    {
        // A non-standard cap must not crash the catalog; that ship keeps its embedded layout.
        var ship = Ship("Avenger Titan");
        var store = StoreWith(ship.Id, ship.DisplayName,
            new GridOverride { Id = 0, W = 2, D = 2, H = 2, Cap = 7 });

        var merged = Catalog.WithOverrides(store).Ships.First(s => s.Id == ship.Id);
        Assert.Equal(ship.TotalScu, merged.TotalScu);   // unchanged embedded value
    }

    [Fact]
    public void WithOverrides_HonorsExactAcceptedSet()
    {
        // A grid can accept a non-contiguous set (large box but not a middle size).
        var ship = Ship("Avenger Titan");
        var store = StoreWith(ship.Id, ship.DisplayName,
            new GridOverride { Id = 0, W = 8, D = 2, H = 2, Cap = 32, Accepts = new List<int> { 8, 32 },
                               Px = 0, Py = 0, Pz = 0 });

        var g = Catalog.WithOverrides(store).Ships.First(s => s.Id == ship.Id).Grids[0];
        Assert.Equal(new[] { 8, 32 }, g.AcceptedCaps);
        Assert.Equal(32, g.MaxContainerScu);   // largest accepted
        Assert.True(g.Accepts(8));
        Assert.False(g.Accepts(16));           // physically fits but not accepted
    }

    [Fact]
    public void SingleCap_ExpandsToEveryStandardSizeUpToIt()
    {
        // Datamined data carries one cap; it must mean "every standard size up to it".
        var ship = Ship("Avenger Titan");
        var store = StoreWith(ship.Id, ship.DisplayName,
            new GridOverride { Id = 0, W = 4, D = 2, H = 2, Cap = 16, Px = 0, Py = 0, Pz = 0 });

        var g = Catalog.WithOverrides(store).Ships.First(s => s.Id == ship.Id).Grids[0];
        Assert.Equal(new[] { 1, 2, 4, 8, 16 }, g.AcceptedCaps);
        Assert.True(g.Accepts(1) && g.Accepts(16));
        Assert.False(g.Accepts(24));
    }

    private static ShipCargoDef Ship(string name) =>
        Catalog.Ships.First(s => s.DisplayName == name);
}
