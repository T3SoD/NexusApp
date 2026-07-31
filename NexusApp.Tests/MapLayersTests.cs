using System.IO;
using System.Text.RegularExpressions;
using NexusApp.Models;
using NexusApp.Services;
using NexusApp.Services.Map;
using Xunit;

namespace NexusApp.Tests;

// The four MAP tab layer pin providers (Task 7): trade terminals, mission guide contested-zone
// sites, mineable ore locations, and the Exec Hangar object. All four feed MapLayerPins (Task 6,
// MapSceneBuilder.cs), whose per-object layer booleans are derived purely from key presence in
// these dictionaries - so the hard contract under test throughout this file is that a provider
// dictionary NEVER carries a key with an empty list value (that would falsely flag an object with
// nothing actually pinned to it).
public class MapLayersTests
{
    // Load once per class (pattern: MapCatalogTests, MapSceneBuilderTests) - the embedded artifact
    // is immutable within a test run.
    private static readonly MapCatalog Catalog = MapCatalog.LoadEmbedded();

    // --- GuideSites table: drift guard in both directions -----------------------------

    [Fact]
    public void GuideSites_AllSixRows_ResolveInEmbeddedCatalog()
    {
        foreach (var (guideId, system, place) in MapLayers.GuideSites)
        {
            var obj = Catalog.ByName(system, place);
            Assert.True(obj is not null, $"GuideSites row ({guideId}, {system}, {place}) did not resolve in the embedded catalog.");
        }
    }

    [Fact]
    public void GuideSites_HasSixRows()
    {
        Assert.Equal(6, MapLayers.GuideSites.Count);
    }

    [Fact]
    public void GuideSites_EveryGuideId_ExistsInGuideCatalog()
    {
        var knownIds = GuideCatalog.All.Select(g => g.Id).ToHashSet();
        foreach (var (guideId, _, _) in MapLayers.GuideSites)
            Assert.Contains(guideId, knownIds);
    }

    // --- BuildGuides --------------------------------------------------------------------

    [Fact]
    public void BuildGuides_ReturnsSixEntries()
    {
        var result = MapLayers.BuildGuides(Catalog);
        Assert.Equal(6, result.Count);
    }

    [Fact]
    public void BuildGuides_SupervisorAppearsOnTwoDistinctObjectIds()
    {
        var result = MapLayers.BuildGuides(Catalog);
        var supervisorIds = result.Where(kv => kv.Value == "supervisor").Select(kv => kv.Key).Distinct().ToList();
        Assert.Equal(2, supervisorIds.Count);
    }

    [Fact]
    public void BuildGuides_CheckmateMapsToItsCatalogObject()
    {
        var result = MapLayers.BuildGuides(Catalog);
        var checkmate = Catalog.ByName("Pyro", "Checkmate");
        Assert.NotNull(checkmate);
        Assert.Equal("checkmate", result[checkmate!.Id]);
    }

    // --- BuildTrade ---------------------------------------------------------------------

    [Fact]
    public void BuildTrade_Fixture_ReturnsExactlyOneObjectEntryContainingTerminal10()
    {
        var terminals = new List<MarketTerminal>
        {
            new(10, "T", "", false, "Stanton", "Everus Harbor"),
            new(11, "U", "", false, "Stanton", "NoSuchPlace"),
        };

        var result = MapLayers.BuildTrade(terminals, Catalog);

        Assert.Single(result);
        var everusHarbor = Catalog.ByName("Stanton", "Everus Harbor");
        Assert.NotNull(everusHarbor);
        Assert.True(result.ContainsKey(everusHarbor!.Id));
        Assert.Equal(new[] { 10 }, result[everusHarbor.Id]);
    }

    [Fact]
    public void BuildTrade_GroupsMultipleTerminalsAtSameObject()
    {
        var terminals = new List<MarketTerminal>
        {
            new(20, "A", "", false, "Stanton", "Everus Harbor"),
            new(21, "B", "", false, "Stanton", "Everus Harbor"),
        };

        var result = MapLayers.BuildTrade(terminals, Catalog);

        var everusHarbor = Catalog.ByName("Stanton", "Everus Harbor");
        Assert.NotNull(everusHarbor);
        Assert.Single(result);
        Assert.Equal(new[] { 20, 21 }, result[everusHarbor!.Id]);
    }

    [Fact]
    public void BuildTrade_AllUnmatched_ReturnsEmptyDictionary_NoEmptyListEntries()
    {
        var terminals = new List<MarketTerminal>
        {
            new(30, "A", "", false, "Stanton", "NoSuchPlaceAtAll"),
        };

        var result = MapLayers.BuildTrade(terminals, Catalog);

        Assert.Empty(result);
    }

    [Fact]
    public void BuildTrade_NeverProducesEmptyListEntries()
    {
        var terminals = new List<MarketTerminal>
        {
            new(40, "A", "", false, "Stanton", "Everus Harbor"),
            new(41, "B", "", false, "Stanton", "Nowhere Made Up"),
            new(42, "C", "", false, "Pyro", "Checkmate Station"),
        };

        var result = MapLayers.BuildTrade(terminals, Catalog);

        Assert.All(result.Values, list => Assert.NotEmpty(list));
    }

    [Fact]
    public void BuildTrade_LogsUnmatchedCount_OncePerCall_ExactCount()
    {
        var terminals = new List<MarketTerminal>
        {
            new(50, "A", "", false, "Stanton", "Everus Harbor"),
            new(51, "B", "", false, "Stanton", "GhostPlaceOne"),
            new(52, "C", "", false, "Stanton", "GhostPlaceTwo"),
            new(53, "D", "", false, "Stanton", "GhostPlaceThree"),
            new(54, "E", "", false, "Stanton", "GhostPlaceFour"),
            new(55, "F", "", false, "Stanton", "GhostPlaceFive"),
        };

        var logPath = Environment.GetEnvironmentVariable("NEXUS_LOG_PATH");
        Assert.NotNull(logPath);
        // Snapshot the line count first: the message text itself ("5 terminals unmatched") has no
        // per-run unique token (unlike MarketQueriesTests.UnmappedResource_LogsMissOnceOnlyPerName,
        // which embeds a Guid), and the shared log file persists across separate `dotnet test`
        // invocations. Counting only lines appended after this snapshot isolates this assertion
        // from whatever this exact line count already accumulated in earlier runs. FileShare.ReadWrite:
        // same reasoning as MarketQueriesTests - this is the shared Logger.LogPath every parallel
        // test class can be appending to, and the log is append-only so lines already present stay
        // at the head regardless of concurrent writers.
        var before = TestFiles.ReadSharedLines(logPath!).Length;

        MapLayers.BuildTrade(terminals, Catalog);
        MapLayers.BuildTrade(terminals, Catalog);

        var occurrences = TestFiles.ReadSharedLines(logPath!)
            .Skip(before)
            .Count(l => l.Contains("[UI] map: 5 terminals unmatched"));
        Assert.Equal(2, occurrences);
    }

    [Fact]
    public void BuildTrade_NeverLogsZeroUnmatched()
    {
        var terminals = new List<MarketTerminal>
        {
            new(60, "A", "", false, "Stanton", "Everus Harbor"),
        };

        MapLayers.BuildTrade(terminals, Catalog);

        var logPath = Environment.GetEnvironmentVariable("NEXUS_LOG_PATH");
        Assert.NotNull(logPath);
        if (!File.Exists(logPath)) return;

        // Guarded by n > 0 in the provider: this exact line must never appear, regardless of what
        // else has run in this test process.
        Assert.DoesNotContain(TestFiles.ReadSharedLines(logPath!), l => l.Contains("[UI] map: 0 terminals unmatched"));
    }

    // --- BuildMining ----------------------------------------------------------------------

    [Fact]
    public void BuildMining_Fixture_YieldsLyriaObjectWithQuantainium()
    {
        var resources = new List<Resource>
        {
            new() { Name = "Quantainium", Locations = new List<string> { "Lyria" } },
        };

        var result = MapLayers.BuildMining(resources, Catalog);

        var lyria = Catalog.ByName("Stanton", "Lyria");
        Assert.NotNull(lyria);
        Assert.True(result.ContainsKey(lyria!.Id));
        Assert.Contains("Quantainium", result[lyria.Id]);
    }

    [Fact]
    public void BuildMining_LocationWithNoCatalogMatch_ContributesNothing()
    {
        var resources = new List<Resource>
        {
            new() { Name = "Taranite", Locations = new List<string> { "Nowhere Belt" } },
        };

        var result = MapLayers.BuildMining(resources, Catalog);

        Assert.Empty(result);
    }

    [Fact]
    public void BuildMining_MixedFixture_OnlyResolvedLocationContributes()
    {
        var resources = new List<Resource>
        {
            new() { Name = "Quantainium", Locations = new List<string> { "Lyria" } },
            new() { Name = "Taranite", Locations = new List<string> { "Nowhere Belt" } },
        };

        var result = MapLayers.BuildMining(resources, Catalog);

        Assert.Single(result);
    }

    [Fact]
    public void BuildMining_NeverProducesEmptyListEntries()
    {
        var resources = new List<Resource>
        {
            new() { Name = "Quantainium", Locations = new List<string> { "Lyria", "Nowhere Belt" } },
            new() { Name = "Taranite", Locations = new List<string> { "Not A Real Place" } },
            new() { Name = "Aluminum", Locations = new List<string> { "Hurston" } },
        };

        var result = MapLayers.BuildMining(resources, Catalog);

        Assert.All(result.Values, list => Assert.NotEmpty(list));
    }

    [Fact]
    public void BuildMining_TwoResourcesAtSameLocation_BothListedOnSameObject()
    {
        var resources = new List<Resource>
        {
            new() { Name = "Quantainium", Locations = new List<string> { "Lyria" } },
            new() { Name = "Beryl", Locations = new List<string> { "Lyria" } },
        };

        var result = MapLayers.BuildMining(resources, Catalog);

        var lyria = Catalog.ByName("Stanton", "Lyria");
        Assert.NotNull(lyria);
        Assert.Contains("Quantainium", result[lyria!.Id]);
        Assert.Contains("Beryl", result[lyria.Id]);
    }

    // --- HangarObject ------------------------------------------------------------------

    [Fact]
    public void HangarObject_IsNonNull()
    {
        Assert.NotNull(MapLayers.HangarObject(Catalog));
    }

    [Fact]
    public void HangarObject_MatchesExhangCatalogObject()
    {
        var expected = Catalog.ByName("Pyro", "PYAM-EXHANG-0-1");
        Assert.NotNull(expected);
        Assert.Equal(expected!.Id, MapLayers.HangarObject(Catalog));
    }
}
