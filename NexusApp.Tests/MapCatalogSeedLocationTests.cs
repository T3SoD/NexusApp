using NexusApp.Services.Map;
using Xunit;

namespace NexusApp.Tests;

// MapCatalog.ResolveSeedLocation turns a MINING SEED location string into a map object, so the
// Mining Codex dossier's LOCATIONS list can open a place on the Starmap. That list was the only one
// in the dossier with no interaction at all, and the Starmap was a one-way leaf nothing could jump
// into.
//
// COVERAGE IS PARTIAL BY NATURE and returning null is the feature. Whole classes of seed strings are
// regions rather than catalogued objects and will never resolve, so callers must resolve-then-
// decorate per row. A list that looked navigable and did nothing on most rows would look broken
// exactly where miners spend their time.
public class MapCatalogSeedLocationTests
{
    private static readonly MapCatalog Map = MapCatalog.LoadEmbedded();

    [Theory]
    [InlineData("Hurston")]
    [InlineData("Daymar")]
    [InlineData("Yela")]
    [InlineData("Lyria")]
    public void ExactNames_Resolve(string seed)
    {
        var obj = Map.ResolveSeedLocation(seed);
        Assert.NotNull(obj);
        Assert.Equal(seed, obj!.Name, ignoreCase: true);
    }

    [Fact]
    public void ParentheticalInnerName_Resolves_WhichIsWhatLiftsPyroCoverage()
    {
        // The seed writes Pyro's planets as "Pyro II (Monox)"; the catalog knows them as "Monox".
        // Without this fallback every Pyro planet row would be dead, which is most of where the
        // interesting mining is.
        var obj = Map.ResolveSeedLocation("Pyro II (Monox)");
        Assert.NotNull(obj);
        Assert.Equal("Monox", obj!.Name);
        Assert.Equal("Pyro", obj.System);
    }

    [Fact]
    public void ExactMatchWinsOverTheParenthetical()
    {
        // Order matters: a name that resolves outright must never be reinterpreted through its
        // parenthetical, or a real object could be swapped for a different one.
        var obj = Map.ResolveSeedLocation("Hurston");
        Assert.Equal("Hurston", obj!.Name);
    }

    [Theory]
    [InlineData("Aaron Halo")]          // a debris field, not an object
    [InlineData("Glaciem Ring")]        // a ring system
    [InlineData("ARC-L1")]              // Lagrange entries are regions in the seed's vocabulary
    [InlineData("Breaker Stations")]    // a class of sites, not one place
    [InlineData("Hathor Caves")]        // a cave network
    public void RegionsThatAreNotObjects_ReturnNull_SoTheRowStaysInert(string seed)
    {
        // These are the rows that must NOT become clickable. Null here is what keeps the list
        // honest rather than making two rows in three look broken.
        Assert.Null(Map.ResolveSeedLocation(seed));
    }

    [Fact]
    public void NullEmptyAndWhitespace_NeverThrow()
    {
        Assert.Null(Map.ResolveSeedLocation(null));
        Assert.Null(Map.ResolveSeedLocation(""));
        Assert.Null(Map.ResolveSeedLocation("   "));
    }

    [Fact]
    public void MalformedParentheticals_DoNotThrowOrMatchNothing()
    {
        // Defensive: the seed is hand-authored text, so unbalanced or empty brackets are possible.
        Assert.Null(Map.ResolveSeedLocation("Somewhere ("));
        Assert.Null(Map.ResolveSeedLocation("Somewhere ()"));
        Assert.Null(Map.ResolveSeedLocation(")backwards("));
    }

    [Fact]
    public void SurroundingWhitespaceIsTolerated()
        => Assert.NotNull(Map.ResolveSeedLocation("  Hurston  "));
}
