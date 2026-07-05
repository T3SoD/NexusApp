using System.IO;
using NexusApp.Models.Cargo;
using NexusApp.Services.Cargo;
using Xunit;

namespace NexusApp.Tests.Cargo;

public class ProvenanceStoreTests
{
    private static string TempFile() => Path.Combine(Path.GetTempPath(), $"prov_{Guid.NewGuid():N}.json");

    [Fact]
    public void SetGetClear_RoundTrips()
    {
        var path = TempFile();
        try
        {
            var s = new CargoOverrideProvenanceStore(path);
            Assert.False(s.Has("raft"));

            s.Set("raft", new OverrideProvenance
            {
                Handle = "Pilot", Summary = "fix depth", Aspects = "Positions, Sizes",
                AppliedUtc = "2026-07-04T00:00:00Z",
            });
            Assert.True(s.Has("raft"));
            var p = s.Get("raft")!;
            Assert.Equal("Pilot", p.Handle);
            Assert.Equal("raft", p.ShipId);   // Set stamps the ship id

            s.Clear("raft");
            Assert.False(s.Has("raft"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Reload_PicksUpAnotherWritersChange()
    {
        var path = TempFile();
        try
        {
            var a = new CargoOverrideProvenanceStore(path);
            var b = new CargoOverrideProvenanceStore(path);
            a.Set("raft", new OverrideProvenance { Handle = "Pilot" });
            Assert.False(b.Has("raft"));
            b.Reload();
            Assert.True(b.Has("raft"));
        }
        finally { File.Delete(path); }
    }
}
