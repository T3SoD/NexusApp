using NexusApp.Models;
using NexusApp.Models.Cargo;
using NexusApp.Services.Cargo;
using Xunit;

namespace NexusApp.Tests.Cargo;

public class CargoImportServiceTests
{
    private static Haul Haul(string mission, int? cap, params (string Commodity, int Scu)[] dropoffs)
    {
        var h = new Haul { MissionId = mission, ContainerCap = cap };
        foreach (var (commodity, scu) in dropoffs)
            h.Legs.Add(new HaulLeg { Role = HaulRole.Dropoff, Commodity = commodity, TargetScu = scu });
        return h;
    }

    [Fact]
    public void Import_SumsDropoffScuPerCommodity()
    {
        var hauls = new[] { Haul("m1", 16, ("Titanium", 60), ("Titanium", 40)) };
        var items = CargoImportService.FromActiveHauls(hauls);

        var item = Assert.Single(items);
        Assert.Equal("Titanium", item.Label);
        Assert.Equal(100, item.Scu);
    }

    [Fact]
    public void Import_UsesContractCapWhenPresent()
    {
        var items = CargoImportService.FromActiveHauls(new[] { Haul("m1", 16, ("Ore", 50)) });
        Assert.Equal(16, items[0].Cap);
        Assert.Equal(CapSource.Ocr, items[0].CapSource);
        Assert.Equal(ManifestSource.Imported, items[0].Source);
    }

    [Fact]
    public void Import_DefaultsCapWhenUnknown()
    {
        var items = CargoImportService.FromActiveHauls(new[] { Haul("m1", null, ("Ore", 50)) });
        Assert.Null(items[0].Cap);
        Assert.Equal(CapSource.Default, items[0].CapSource);
    }

    [Fact]
    public void Import_MergesSameCommoditySameCapAcrossHauls()
    {
        var hauls = new[]
        {
            Haul("m1", 16, ("Titanium", 100)),
            Haul("m2", 16, ("Titanium", 50)),
        };
        var item = Assert.Single(CargoImportService.FromActiveHauls(hauls));
        Assert.Equal(150, item.Scu);
    }

    [Fact]
    public void Import_KeepsDifferentCapsSeparate()
    {
        var hauls = new[]
        {
            Haul("m1", 16, ("Titanium", 100)),
            Haul("m2", 32, ("Titanium", 50)),
        };
        var items = CargoImportService.FromActiveHauls(hauls);
        Assert.Equal(2, items.Count);
    }

    [Fact]
    public void Import_SkipsPickupLegsAndZeroScu()
    {
        var h = new Haul { MissionId = "m1", ContainerCap = 8 };
        h.Legs.Add(new HaulLeg { Role = HaulRole.Pickup, Commodity = "Ore", TargetScu = 0 });
        h.Legs.Add(new HaulLeg { Role = HaulRole.Dropoff, Commodity = "Ore", TargetScu = 0 });
        h.Legs.Add(new HaulLeg { Role = HaulRole.Dropoff, Commodity = "Ore", TargetScu = 40 });

        var item = Assert.Single(CargoImportService.FromActiveHauls(new[] { h }));
        Assert.Equal(40, item.Scu);
    }
}
