using NexusApp.Services.Cargo;
using NexusApp.Views;
using Xunit;

// Guards the hull outline buffers that now ship with the app (Web/cargo/hulls, globbed into the
// build output by the csproj). Every ship in the embedded catalog must have a shipped bin
// (resolving the shared-hull alias first) so no ship can enter the planner hologram-less, and
// the shipped folder must stay clean of stray files that would otherwise leak into the installer.
// Deliberately keyed to the EMBEDDED catalog, not the per-user signoff store: the signoff file
// lives in the user profile and is absent on CI, which would make this test pass vacuously.
public class ShippedHullsTests
{
    private static string ShippedHullsDir =>
        Path.Combine(AppContext.BaseDirectory, "Web", "cargo", "hulls");

    [Fact]
    public void EveryCatalogShip_HasShippedHullBin()
    {
        var catalog = CargoShipCatalog.LoadEmbedded();

        var missing = catalog.Ships
            .Select(s => CargoWebView.HullAlias.TryGetValue(s.Id, out var alias) ? alias : s.Id)
            .Distinct()
            .Where(hullId => !File.Exists(Path.Combine(ShippedHullsDir, hullId + ".bin")))
            .ToList();

        Assert.True(missing.Count == 0,
            "Catalog ships missing a shipped hull bin: " + string.Join(", ", missing));
    }

    [Fact]
    public void ShippedHullsFolder_ContainsOnlyBinFiles()
    {
        Assert.True(Directory.Exists(ShippedHullsDir),
            "Shipped hulls folder not found in build output: " + ShippedHullsDir);
        var strays = Directory.GetFiles(ShippedHullsDir)
            .Where(f => !f.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName)
            .ToList();
        Assert.True(strays.Count == 0,
            "Non-.bin files in shipped hulls folder: " + string.Join(", ", strays));
    }
}
