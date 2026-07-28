using System.Text.Json;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Shared embedded-seed loader for tests that inspect Data/seed_data.json's raw JSON structure
// (SeedHygieneTests, MarketNameMapTests). Reads the resource the same way DataService does at
// runtime. Was previously a byte-for-byte identical private LoadSeed() copy-pasted in both test
// classes; extracted here mirroring the existing TestFiles.cs shared-helper pattern.
internal static class SeedTestFixture
{
    public static JsonDocument LoadSeed()
    {
        using var stream = typeof(DataService).Assembly
            .GetManifestResourceStream("NexusApp.Data.seed_data.json");
        Assert.NotNull(stream);
        using var ms = new MemoryStream();
        stream!.CopyTo(ms);
        return JsonDocument.Parse(ms.ToArray());
    }
}
