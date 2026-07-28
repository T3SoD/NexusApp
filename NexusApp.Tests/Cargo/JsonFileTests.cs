using System.Text.Json;
using NexusApp.Services.Cargo;
using Xunit;

namespace NexusApp.Tests.Cargo;

// JsonFile.LoadOrRecover/AtomicWrite (Services/Cargo/JsonFile.cs) back every Cargo persistence
// store (CargoGridOverrideStore, CargoSignoffStore, CargoOverrideProvenanceStore) but had zero
// direct or indirect test coverage - the existing store tests only exercise the happy-path reload.
// JsonFile is internal; NexusApp.csproj already declares <InternalsVisibleTo Include="NexusApp.Tests" />
// (added for Task 5), so it is reachable directly here with no access seam needed.
public class JsonFileTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"jsonfile_test_{Guid.NewGuid():N}.json");

    private static Dictionary<string, string> Empty() => new();

    [Fact]
    public void LoadOrRecover_CorruptPrimary_FallsBackToBak_AndQuarantinesPrimary()
    {
        var path = TempPath();
        var bakPath = path + ".bak";
        try
        {
            File.WriteAllText(bakPath, JsonSerializer.Serialize(new Dictionary<string, string> { ["a"] = "1" }));
            File.WriteAllText(path, "{ this is not valid json ][");

            var result = JsonFile.LoadOrRecover(path, Empty, "test store");

            Assert.Single(result);
            Assert.Equal("1", result["a"]);

            // The bad primary is set aside, never left in place.
            Assert.False(File.Exists(path));
            var quarantined = Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".corrupt-*");
            Assert.Single(quarantined);
            foreach (var f in quarantined) File.Delete(f);
        }
        finally
        {
            if (File.Exists(bakPath)) File.Delete(bakPath);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void LoadOrRecover_CorruptPrimary_NoBak_ReturnsEmpty()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, "{ this is not valid json ][");

            var result = JsonFile.LoadOrRecover(path, Empty, "test store");

            Assert.Empty(result);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            foreach (var f in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".corrupt-*"))
                File.Delete(f);
        }
    }

    [Fact]
    public void AtomicWrite_LeavesPriorContentReadableAtBak()
    {
        var path = TempPath();
        var bakPath = path + ".bak";
        try
        {
            JsonFile.AtomicWrite(path, JsonSerializer.Serialize(new Dictionary<string, string> { ["a"] = "1" }));
            JsonFile.AtomicWrite(path, JsonSerializer.Serialize(new Dictionary<string, string> { ["a"] = "2" }));

            var current = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            var previous = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(bakPath));

            Assert.Equal("2", current!["a"]);
            Assert.Equal("1", previous!["a"]);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(bakPath)) File.Delete(bakPath);
        }
    }
}
