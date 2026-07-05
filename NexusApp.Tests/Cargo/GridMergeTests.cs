using NexusApp.Models.Cargo;
using NexusApp.Services.Cargo;
using Xunit;

namespace NexusApp.Tests.Cargo;

public class GridMergeTests
{
    private static GridOverride Cur(int id) =>
        new() { Id = id, W = 4, D = 2, H = 2, Cap = 8, Accepts = new List<int> { 1, 2, 4, 8 },
                Px = 1, Py = 1, Pz = 1, Wy = false };

    private static GridOverride Inc(int id) =>
        new() { Id = id, W = 6, D = 3, H = 3, Cap = 32, Accepts = new List<int> { 16, 32 },
                Px = 9, Py = 9, Pz = 9, Wy = true };

    [Fact]
    public void Positions_TakesIncomingPos_KeepsCurrentSizeAndCap()
    {
        var g = GridMerge.Apply(new[] { Cur(0) }, new[] { Inc(0) }, GridMergeAspect.Positions).Single();
        Assert.Equal(9, g.Px);
        Assert.Equal(9, g.Py);
        Assert.Equal(9, g.Pz);
        Assert.Equal(4, g.W);            // size unchanged
        Assert.Equal(8, g.Cap);          // cap unchanged
        Assert.False(g.Wy);              // orientation unchanged
    }

    [Fact]
    public void Sizes_TakesIncomingDimsAndWy_KeepsCurrentPosAndCap()
    {
        var g = GridMerge.Apply(new[] { Cur(0) }, new[] { Inc(0) }, GridMergeAspect.Sizes).Single();
        Assert.Equal(6, g.W);
        Assert.Equal(3, g.D);
        Assert.Equal(3, g.H);
        Assert.True(g.Wy);
        Assert.Equal(1, g.Px);           // position unchanged
        Assert.Equal(8, g.Cap);          // cap unchanged
    }

    [Fact]
    public void Caps_TakesIncomingCapAndAccepts_KeepsCurrentSizeAndPos()
    {
        var g = GridMerge.Apply(new[] { Cur(0) }, new[] { Inc(0) }, GridMergeAspect.Caps).Single();
        Assert.Equal(32, g.Cap);
        Assert.Equal(new List<int> { 16, 32 }, g.Accepts);
        Assert.Equal(4, g.W);            // size unchanged
        Assert.Equal(1, g.Px);           // position unchanged
    }

    [Fact]
    public void GridSet_AddsIncomingOnlyGrid_AndDropsCurrentOnlyGrid()
    {
        var current = new[] { Cur(0), Cur(1) };            // ids 0,1
        var incoming = new[] { Inc(0), Inc(2) };           // ids 0,2
        var ids = GridMerge.Apply(current, incoming, GridMergeAspect.GridSet).Select(g => g.Id).OrderBy(i => i);
        Assert.Equal(new[] { 0, 2 }, ids);                 // 1 dropped, 2 added
    }

    [Fact]
    public void WithoutGridSet_KeepsCurrentRoster_IgnoringIncomingOnlyGrids()
    {
        var current = new[] { Cur(0), Cur(1) };
        var incoming = new[] { Inc(0), Inc(2) };
        var ids = GridMerge.Apply(current, incoming, GridMergeAspect.Positions).Select(g => g.Id).OrderBy(i => i);
        Assert.Equal(new[] { 0, 1 }, ids);                 // roster unchanged; grid 1 kept, grid 2 not added
    }

    [Fact]
    public void All_ReplacesMatchedGridWithIncoming()
    {
        var g = GridMerge.Apply(new[] { Cur(0) }, new[] { Inc(0) }, GridMergeAspect.All).Single();
        Assert.Equal(6, g.W);
        Assert.Equal(9, g.Px);
        Assert.Equal(32, g.Cap);
        Assert.True(g.Wy);
    }

    [Fact]
    public void None_LeavesCurrentUnchanged()
    {
        var g = GridMerge.Apply(new[] { Cur(0) }, new[] { Inc(0) }, GridMergeAspect.None).Single();
        Assert.Equal(4, g.W);
        Assert.Equal(1, g.Px);
        Assert.Equal(8, g.Cap);
        Assert.False(g.Wy);
    }
}
