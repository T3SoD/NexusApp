using System.IO;
using System.Text;
using NexusApp.Models.Cargo;
using NexusApp.Services.Cargo;
using Xunit;

namespace NexusApp.Tests.Cargo;

public class CatalogPatchExportTests
{
    [Fact]
    public void PatchJson_RoundTripsARealShipThroughTheCatalog()
    {
        var ship = CargoShipCatalog.LoadEmbedded().ById("argo-raft")!;
        var patch = CatalogPatchExport.ToPatchJson(ship);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("[" + patch + "]"));
        var reloaded = CargoShipCatalog.Load(stream).ById("argo-raft")!;

        Assert.Equal(ship.DisplayName, reloaded.DisplayName);
        Assert.Equal(ship.Priority, reloaded.Priority);
        Assert.Equal(ship.Grids.Count, reloaded.Grids.Count);
        for (int i = 0; i < ship.Grids.Count; i++)
        {
            var a = ship.Grids[i]; var b = reloaded.Grids[i];
            Assert.Equal(a.W, b.W); Assert.Equal(a.D, b.D); Assert.Equal(a.H, b.H);
            Assert.Equal(a.PosX, b.PosX); Assert.Equal(a.PosY, b.PosY); Assert.Equal(a.PosZ, b.PosZ);
            Assert.Equal(a.WAlongShipY, b.WAlongShipY);
            Assert.Equal(a.AcceptedCaps, b.AcceptedCaps);
        }
    }

    [Fact]
    public void PatchJson_PreservesNarrowedAcceptsAndNullPositions()
    {
        var ship = new ShipCargoDef
        {
            Id = "test-x", DisplayName = "Test X", Manufacturer = "M", Classification = "Transport", Priority = 5,
            Grids = new[]
            {
                new GridDef { Id = 0, W = 6, D = 4, H = 2, AcceptedCaps = new[] { 1, 4 } },   // narrowed set, null pos
                new GridDef { Id = 1, W = 3, D = 3, H = 3, AcceptedCaps = new[] { 1, 2, 4, 8 }, PosX = 1, PosY = 2, PosZ = 3, WAlongShipY = true },
            },
        };
        var patch = CatalogPatchExport.ToPatchJson(ship);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("[" + patch + "]"));
        var reloaded = CargoShipCatalog.Load(stream).ById("test-x")!;

        Assert.Equal(new[] { 1, 4 }, reloaded.Grids[0].AcceptedCaps);
        Assert.False(reloaded.Grids[0].HasPos);   // null positions round-trip as null
        Assert.True(reloaded.Grids[1].HasPos);
        Assert.True(reloaded.Grids[1].WAlongShipY);
    }
}
