using NexusApp.Models;
using NexusApp.Models.Cargo;
using NexusApp.Services.Cargo;
using NexusApp.ViewModels;
using Xunit;

namespace NexusApp.Tests.Cargo;

public class CargoPlannerViewModelTests
{
    private static ShipCargoDef Ship(string id, int w, int d, int h, int cap) =>
        new()
        {
            Id = id, DisplayName = id,
            Grids = new[] { new GridDef { Id = 0, W = w, D = d, H = h, AcceptedCaps = BoxType.SizesDesc.Where(s => s <= cap).ToArray() } },
        };

    private static CargoPlannerViewModel Vm(params ShipCargoDef[] ships) =>
        new(new CargoShipCatalog(ships));

    [Fact]
    public void Ctor_SelectsAShip_AndPacksEmptyManifest()
    {
        var vm = Vm(Ship("s", 4, 4, 4, 32));
        Assert.NotNull(vm.SelectedShip);
        Assert.NotNull(vm.Plan);
        Assert.Equal(0, vm.ManifestScu);
    }

    [Fact]
    public void AddManualLine_PacksIntoSelectedShip()
    {
        var vm = Vm(Ship("s", 4, 4, 4, 32));
        vm.AddManualLine("Titanium", 16, 8);

        Assert.Equal(16, vm.ManifestScu);
        Assert.True(vm.Plan!.FitsInOneTrip);
        Assert.True(vm.CurrentTrip!.Placed.Count > 0);
    }

    [Fact]
    public void ImportFromHauls_AddsLinesAndRepacks()
    {
        var vm = Vm(Ship("s", 8, 8, 6, 32));
        var haul = new Haul { MissionId = "m1", ContainerCap = 16 };
        haul.Legs.Add(new HaulLeg { Role = HaulRole.Dropoff, Commodity = "Ore", TargetScu = 32 });

        vm.ImportFromHauls(new[] { haul });

        Assert.Single(vm.Manifest);
        Assert.Equal(32, vm.ManifestScu);
        Assert.True(vm.Plan!.FitsInOneTrip);
    }

    [Fact]
    public void SelectShip_ChangesSelectionAndRepacks()
    {
        var big = Ship("big", 8, 8, 6, 32);
        var small = Ship("small", 2, 2, 2, 8);
        var vm = Vm(big, small);

        vm.AddManualLine("Ore", 8, 8);
        vm.SelectShip(small);

        Assert.Equal("small", vm.SelectedShip!.Id);
        Assert.True(vm.Plan!.FitsInOneTrip);   // one 8 cube fits the 2x2x2 grid
    }

    [Fact]
    public void ClearManifest_EmptiesAndRepacks()
    {
        var vm = Vm(Ship("s", 4, 4, 4, 32));
        vm.AddManualLine("Ore", 16, 8);
        vm.ClearManifest();

        Assert.Empty(vm.Manifest);
        Assert.Equal(0, vm.ManifestScu);
    }

    [Fact]
    public void RankShips_ReturnsAllShipsRanked()
    {
        var vm = Vm(Ship("big", 8, 8, 6, 32), Ship("small", 2, 2, 2, 8));
        vm.AddManualLine("Ore", 8, 8);

        var ranked = vm.RankShips();
        Assert.Equal(2, ranked.Count);
        Assert.Equal("small", ranked[0].Ship.Id);   // smallest that fits in one trip
    }
}
