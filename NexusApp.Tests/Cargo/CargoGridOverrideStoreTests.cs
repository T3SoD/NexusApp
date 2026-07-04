using NexusApp.Models.Cargo;
using NexusApp.Services.Cargo;
using Xunit;

namespace NexusApp.Tests.Cargo;

public class CargoGridOverrideStoreTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"gridoverride_test_{Guid.NewGuid():N}.json");

    private static GridOverride Grid(int id, int w = 2, int d = 2, int h = 2, int cap = 8,
        double px = 0, double py = 0, double pz = 0, bool wy = false) =>
        new() { Id = id, W = w, D = d, H = h, Cap = cap, Px = px, Py = py, Pz = pz, Wy = wy };

    [Fact]
    public void Unedited_ByDefault()
    {
        var s = new CargoGridOverrideStore(TempPath());
        Assert.False(s.Has("drak-ironclad"));
        Assert.Null(s.Get("drak-ironclad"));
        Assert.Empty(s.EditedShipIds);
    }

    [Fact]
    public void Set_Persists_AcrossReload()
    {
        var path = TempPath();
        var s = new CargoGridOverrideStore(path);
        s.Set("aegs-idris-p", "Idris-P", new[] { Grid(0, px: 1, wy: true), Grid(1, px: 5) });

        var reloaded = new CargoGridOverrideStore(path);
        Assert.True(reloaded.Has("aegs-idris-p"));
        var g = reloaded.Get("aegs-idris-p")!;
        Assert.Equal(2, g.Count);
        Assert.Equal(5, g[1].Px);
        Assert.True(g[0].Wy);
        File.Delete(path);
    }

    [Fact]
    public void Set_Overwrites_PreviousGridSet()
    {
        var path = TempPath();
        var s = new CargoGridOverrideStore(path);
        s.Set("cutter", "Cutter", new[] { Grid(0), Grid(1) });
        s.Set("cutter", "Cutter", new[] { Grid(0) });
        Assert.Single(s.Get("cutter")!);
        File.Delete(path);
    }

    [Fact]
    public void AcceptedSizes_Persist_AcrossReload()
    {
        var path = TempPath();
        var s = new CargoGridOverrideStore(path);
        s.Set("caterpillar", "Caterpillar",
            new[] { Grid(0, w: 8, d: 2, h: 2, cap: 32) with { Accepts = new List<int> { 8, 32 } } });

        var reloaded = new CargoGridOverrideStore(path).Get("caterpillar")!;
        Assert.Equal(new[] { 8, 32 }, reloaded[0].Accepts);
        File.Delete(path);
    }

    [Fact]
    public void Clear_RemovesEntry()
    {
        var path = TempPath();
        var s = new CargoGridOverrideStore(path);
        s.Set("cutter", "Cutter", new[] { Grid(0) });
        s.Clear("cutter");
        Assert.False(s.Has("cutter"));
        Assert.Null(s.Get("cutter"));
        File.Delete(path);
    }
}
