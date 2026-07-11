namespace NexusApp.Tests;

using NexusApp.Models;
using NexusApp.Services;
using Xunit;

public class CompositionCacheTests
{
    [Fact]
    public void QueriesLoaderOncePerResource()
    {
        var calls = 0;
        var cache = new CompositionCache(_ => { calls++; return new List<CompositionPart>(); });
        cache.Get("Quantanium");
        cache.Get("Quantanium");
        Assert.Equal(1, calls);
    }

    [Fact]
    public void IsCaseInsensitive()
    {
        var calls = 0;
        var cache = new CompositionCache(_ => { calls++; return new List<CompositionPart>(); });
        cache.Get("Quantanium");
        cache.Get("QUANTANIUM");
        Assert.Equal(1, calls);
    }

    [Fact]
    public void CachesEmptyResults()
    {
        var calls = 0;
        var cache = new CompositionCache(_ => { calls++; return new List<CompositionPart>(); });
        Assert.Empty(cache.Get("HandOre"));
        Assert.Empty(cache.Get("HandOre"));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void ReturnsLoaderData()
    {
        var parts = new List<CompositionPart> { new("Bexalite", 8, 14, false) };
        var cache = new CompositionCache(_ => parts);
        Assert.Same(parts[0], cache.Get("Bexalite")[0]);
    }
}
