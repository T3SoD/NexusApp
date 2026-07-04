using System.IO;
using NexusApp.Models.Cargo;
using NexusApp.Services.Cargo;
using Xunit;

namespace NexusApp.Tests.Cargo;

public class GridShareServiceTests
{
    private static string TempFile() => Path.Combine(Path.GetTempPath(), $"gs_{Guid.NewGuid():N}.nexusgrid");

    private static GridSharePackage Sample() => new()
    {
        ShipId = "crus-c1-spirit", ShipName = "C1 Spirit", RsiHandle = "SomePilot",
        Summary = "fix depth", Notes = "measured with 1 SCU boxes",
        CreatedUtc = "2026-07-04T21:00:00Z", AppVersion = "6.1.0",
        Grids = new()
        {
            new GridOverride { Id = 0, W = 8, D = 6, H = 4, Cap = 32,
                Accepts = new List<int> { 1, 2, 4, 8, 16, 32 }, Px = 0, Py = 14, Pz = -1, Wy = true },
        },
    };

    [Fact]
    public void Export_Then_Import_RoundTrips()
    {
        var path = TempFile();
        try
        {
            GridShareService.Export(Sample(), path);
            var back = GridShareService.Import(path);
            Assert.Equal("crus-c1-spirit", back.ShipId);
            Assert.Equal("SomePilot", back.RsiHandle);
            Assert.Equal("fix depth", back.Summary);
            Assert.Single(back.Grids);
            Assert.Equal(8, back.Grids[0].W);
            Assert.Equal(new List<int> { 1, 2, 4, 8, 16, 32 }, back.Grids[0].Accepts);
            Assert.True(back.Grids[0].Wy);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Import_RejectsUnknownFormat()
    {
        var path = TempFile();
        try
        {
            File.WriteAllText(path, "{\"format\":\"bogus\",\"shipId\":\"x\",\"grids\":[{\"id\":0,\"w\":1,\"d\":1,\"h\":1,\"cap\":1}]}");
            Assert.Throws<GridShareException>(() => GridShareService.Import(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Import_RejectsMissingShipId()
    {
        var path = TempFile();
        try
        {
            File.WriteAllText(path, "{\"format\":\"nexus.cargo.grid.v1\",\"grids\":[{\"id\":0,\"w\":1,\"d\":1,\"h\":1,\"cap\":1}]}");
            Assert.Throws<GridShareException>(() => GridShareService.Import(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Import_RejectsMalformedJson()
    {
        var path = TempFile();
        try
        {
            File.WriteAllText(path, "not json at all");
            Assert.Throws<GridShareException>(() => GridShareService.Import(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Import_RejectsNullGrids()
    {
        var path = TempFile();
        try
        {
            File.WriteAllText(path, "{\"format\":\"nexus.cargo.grid.v1\",\"shipId\":\"x\",\"grids\":null}");
            Assert.Throws<GridShareException>(() => GridShareService.Import(path));
        }
        finally { File.Delete(path); }
    }
}
