using NexusApp.Models;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

public class AutoLoadSampleStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"al_samples_{Guid.NewGuid():N}.json");
    public void Dispose() { try { File.Delete(_path); } catch { } }

    private static AutoLoadSample Sample(int elapsed) => new(1, new DateTime(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc),
        "Buy", "SCShop_Admin", 1440m, new() { ["24"] = 60 }, elapsed, 1020, false, "4.9.188.23497");

    [Fact]
    public void Append_ThenReadAll_RoundTrips()
    {
        var store = new AutoLoadSampleStore(_path);
        store.Append(Sample(1620));
        store.Append(Sample(1701) with { Abandoned = true });
        var all = store.ReadAll();
        Assert.Equal(2, all.Count);
        Assert.Equal(1620, all[0].ElapsedSeconds);
        Assert.True(all[1].Abandoned);
        Assert.Equal(60, all[0].Boxes["24"]);
    }

    [Fact]
    public void ReadAll_MissingFile_Empty()
        => Assert.Empty(new AutoLoadSampleStore(_path).ReadAll());

    [Fact]
    public void Append_CorruptExistingFile_DoesNotThrow()
    {
        File.WriteAllText(_path, "not json");
        var store = new AutoLoadSampleStore(_path);
        store.Append(Sample(5));
        Assert.Single(store.ReadAll());
    }
}
