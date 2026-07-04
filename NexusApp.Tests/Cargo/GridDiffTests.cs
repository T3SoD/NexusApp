using NexusApp.Models.Cargo;
using NexusApp.Services.Cargo;
using Xunit;

namespace NexusApp.Tests.Cargo;

public class GridDiffTests
{
    private static GridOverride G(int id, int w, int d, int h, int cap = 32) =>
        new() { Id = id, W = w, D = d, H = h, Cap = cap, Px = 0, Py = 0, Pz = 0 };

    [Fact]
    public void Identical_NoChanges()
    {
        var r = GridDiff.Compute(new[] { G(0, 4, 2, 2) }, new[] { G(0, 4, 2, 2) });
        Assert.False(r.HasChanges);
    }

    [Fact]
    public void ChangedSize_Reported()
    {
        var r = GridDiff.Compute(new[] { G(0, 4, 2, 2) }, new[] { G(0, 4, 2, 3) });
        Assert.True(r.HasChanges);
        Assert.Contains(r.Lines, l => l.Contains("Grid 1") && l.Contains("size"));
    }

    [Fact]
    public void AddedGrid_Reported()
    {
        var r = GridDiff.Compute(new[] { G(0, 4, 2, 2) }, new[] { G(0, 4, 2, 2), G(1, 2, 2, 2) });
        Assert.True(r.HasChanges);
        Assert.Contains(r.Lines, l => l.Contains("Grid 2") && l.Contains("added"));
    }

    [Fact]
    public void RemovedGrid_Reported()
    {
        var r = GridDiff.Compute(new[] { G(0, 4, 2, 2), G(1, 2, 2, 2) }, new[] { G(0, 4, 2, 2) });
        Assert.True(r.HasChanges);
        Assert.Contains(r.Lines, l => l.Contains("Grid 2") && l.Contains("removed"));
    }
}
