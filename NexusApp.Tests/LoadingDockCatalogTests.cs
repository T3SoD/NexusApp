using System.Text;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

public class LoadingDockCatalogTests
{
    [Fact]
    public void LoadEmbedded_HasExactly28Docks()
        => Assert.Equal(28, LoadingDockCatalog.LoadEmbedded().Count);

    [Fact]
    public void Contains_KnownHub_CaseInsensitive()
    {
        var c = LoadingDockCatalog.LoadEmbedded();
        Assert.True(c.Contains("StarMapObject.RR_HUR_LEO"));
        Assert.True(c.Contains("starmapobject.rr_hur_leo"));
        Assert.False(c.Contains("StarMapObject.RR_ARC_L1"));
        Assert.False(c.Contains(null));
    }

    [Fact]
    public void Contains_JumpPointGateway_UsesStarmapLocationsNamespace()
    {
        var c = LoadingDockCatalog.LoadEmbedded();
        Assert.True(c.Contains("StarMapObject.JumpPoint_Stanton_Pyro"));
    }

    [Fact]
    public void Load_Malformed_FoldsToEmpty()
    {
        using var s = new MemoryStream(Encoding.UTF8.GetBytes("{"));
        Assert.Equal(0, LoadingDockCatalog.Load(s).Count);
    }
}
