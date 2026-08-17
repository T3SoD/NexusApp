using NexusApp.Services.Map;
using Xunit;

namespace NexusApp.Tests;

public class MapSceneBuilderTests
{
    // Load once per class (pattern: MapCatalogTests) - the embedded artifact is immutable within a
    // test run.
    private static readonly MapCatalog Catalog = MapCatalog.LoadEmbedded();

    private static readonly MapLayerPins EmptyPins = new(
        new Dictionary<int, IReadOnlyList<int>>(),
        new Dictionary<int, string>(),
        new Dictionary<int, IReadOnlyList<string>>(),
        null);

    [Fact]
    public void BuildInit_ContainsRequestedSystemName()
    {
        var json = MapSceneBuilder.BuildInit(Catalog, "Stanton", EmptyPins,
            tradeOn: false, guidesOn: false, miningOn: false, hangarOn: false, asteroidsOn: true,
            selection: null, draft: Array.Empty<int>(), planner: Array.Empty<int>(), reduced: false);

        Assert.Contains("\"system\":\"Stanton\"", json);
    }

    [Fact]
    public void BuildInit_FiltersToActiveSystem_IncludesStantonObject()
    {
        var json = MapSceneBuilder.BuildInit(Catalog, "Stanton", EmptyPins,
            tradeOn: false, guidesOn: false, miningOn: false, hangarOn: false, asteroidsOn: true,
            selection: null, draft: Array.Empty<int>(), planner: Array.Empty<int>(), reduced: false);

        Assert.Contains("Hurston", json);
    }

    [Fact]
    public void BuildInit_FiltersToActiveSystem_ExcludesOtherSystemObject()
    {
        // "Ruin Station" (id 748) belongs to Pyro per Data/starmap_map.json; a Stanton init must
        // not leak Pyro rows onto the payload.
        var json = MapSceneBuilder.BuildInit(Catalog, "Stanton", EmptyPins,
            tradeOn: false, guidesOn: false, miningOn: false, hangarOn: false, asteroidsOn: true,
            selection: null, draft: Array.Empty<int>(), planner: Array.Empty<int>(), reduced: false);

        Assert.DoesNotContain("Ruin Station", json);
    }

    [Fact]
    public void BuildInit_ObjectFlaggedInGuidePins_SerializesGuideTrue()
    {
        var hurston = Catalog.ByName("Stanton", "Hurston");
        Assert.NotNull(hurston);

        var pins = new MapLayerPins(
            new Dictionary<int, IReadOnlyList<int>>(),
            new Dictionary<int, string> { [hurston!.Id] = "some-guide-id" },
            new Dictionary<int, IReadOnlyList<string>>(),
            null);

        var json = MapSceneBuilder.BuildInit(Catalog, "Stanton", pins,
            tradeOn: false, guidesOn: false, miningOn: false, hangarOn: false, asteroidsOn: true,
            selection: null, draft: Array.Empty<int>(), planner: Array.Empty<int>(), reduced: false);

        Assert.Contains("\"guide\":true", json);
    }

    [Fact]
    public void BuildLayerToggle_MatchesExactShape()
    {
        var json = MapSceneBuilder.BuildLayerToggle("trade", true);
        Assert.Equal("{\"type\":\"layerToggle\",\"layer\":\"trade\",\"on\":true}", json);
    }

    [Fact]
    public void BuildSelect_Null_SerializesIdAsNull()
    {
        var json = MapSceneBuilder.BuildSelect(null);
        Assert.Contains("\"id\":null", json);
    }

    [Fact]
    public void BuildSelect_WithId_SerializesIdValue()
    {
        var json = MapSceneBuilder.BuildSelect(42);
        Assert.Contains("\"id\":42", json);
    }

    // ── locator mode (Operations system view) ──
    // The flag and its note ride the SAME init the full map uses, so these pin that the default
    // (absent) case still serializes the full map exactly as before.

    [Fact]
    public void BuildInit_LiteOmitted_SerializesFalseAndNoNote()
    {
        var json = MapSceneBuilder.BuildInit(Catalog, "Stanton", EmptyPins,
            tradeOn: false, guidesOn: false, miningOn: false, hangarOn: false, asteroidsOn: true,
            selection: null, draft: Array.Empty<int>(), planner: Array.Empty<int>(), reduced: false);

        Assert.Contains("\"lite\":false", json);
        Assert.Contains("\"playerNote\":null", json);
        Assert.Contains("\"playerName\":null", json);
    }

    // The middle state the locator card exists to render: the log named a place the catalog cannot
    // resolve to an object, so there is a name to show and no marker to place. Name and marker are
    // carried independently, or the card would have to call a known place unknown.
    [Fact]
    public void BuildInit_NamedPlaceWithNoMarker_CarriesNameWithNullPlayer()
    {
        var json = MapSceneBuilder.BuildInit(Catalog, "Stanton", MapLayerPins.Empty,
            tradeOn: false, guidesOn: false, miningOn: false, hangarOn: false, asteroidsOn: false,
            selection: null, draft: Array.Empty<int>(), planner: Array.Empty<int>(), reduced: false,
            player: null, haulsOn: false, ordersOn: false,
            lite: true, playerNote: "Stanton. Seen 4m ago.", playerName: "Rough and Ready space");

        Assert.Contains("\"player\":null", json);
        Assert.Contains("Rough and Ready space", json);
    }

    [Fact]
    public void BuildInit_LiteTrue_CarriesNote()
    {
        var json = MapSceneBuilder.BuildInit(Catalog, "Stanton", EmptyPins,
            tradeOn: false, guidesOn: false, miningOn: false, hangarOn: false, asteroidsOn: false,
            selection: null, draft: Array.Empty<int>(), planner: Array.Empty<int>(), reduced: false,
            player: null, haulsOn: false, ordersOn: false,
            lite: true, playerNote: "Everus Harbor. Seen 4m ago.");

        Assert.Contains("\"lite\":true", json);
        Assert.Contains("Everus Harbor. Seen 4m ago.", json);
    }

    // Empty is the pin set locator mode posts. Nothing may be flagged for any layer, or the
    // "no pins at all" promise breaks the moment the catalog gains an object.
    [Fact]
    public void EmptyPins_FlagNothing()
    {
        var json = MapSceneBuilder.BuildInit(Catalog, "Stanton", MapLayerPins.Empty,
            tradeOn: false, guidesOn: false, miningOn: false, hangarOn: false, asteroidsOn: false,
            selection: null, draft: Array.Empty<int>(), planner: Array.Empty<int>(), reduced: false,
            lite: true);

        Assert.DoesNotContain("\"trade\":true", json);
        Assert.DoesNotContain("\"guide\":true", json);
        Assert.DoesNotContain("\"mine\":true", json);
        Assert.DoesNotContain("\"hangar\":true", json);
    }

    [Fact]
    public void BuildInit_ReducedTrue_RoundTrips()
    {
        var json = MapSceneBuilder.BuildInit(Catalog, "Stanton", EmptyPins,
            tradeOn: false, guidesOn: false, miningOn: false, hangarOn: false, asteroidsOn: true,
            selection: null, draft: Array.Empty<int>(), planner: Array.Empty<int>(), reduced: true);

        Assert.Contains("\"reduced\":true", json);
    }

    [Fact]
    public void BuildInit_ReducedFalse_RoundTrips()
    {
        var json = MapSceneBuilder.BuildInit(Catalog, "Stanton", EmptyPins,
            tradeOn: false, guidesOn: false, miningOn: false, hangarOn: false, asteroidsOn: true,
            selection: null, draft: Array.Empty<int>(), planner: Array.Empty<int>(), reduced: false);

        Assert.Contains("\"reduced\":false", json);
    }

    [Fact]
    public void BuildFocus_SerializesTypeAndId()
    {
        var json = MapSceneBuilder.BuildFocus(7);
        Assert.Equal("{\"type\":\"focusObject\",\"id\":7}", json);
    }

    [Fact]
    public void BuildRoute_SerializesTypeAndIds()
    {
        var json = MapSceneBuilder.BuildRoute(new[] { 1, 2, 3 });
        Assert.Equal("{\"type\":\"routeChanged\",\"ids\":[1,2,3]}", json);
    }

    [Fact]
    public void BuildPlanner_SerializesTypeAndIds()
    {
        var json = MapSceneBuilder.BuildPlanner(new[] { 4, 5 });
        Assert.Equal("{\"type\":\"plannerRoute\",\"ids\":[4,5]}", json);
    }

    [Fact]
    public void BuildMeasureArm_SerializesTypeAndOn()
    {
        var json = MapSceneBuilder.BuildMeasureArm(true);
        Assert.Equal("{\"type\":\"measureArm\",\"on\":true}", json);
    }

    [Fact]
    public void BuildSystemView_SerializesTypeOnly()
    {
        var json = MapSceneBuilder.BuildSystemView();
        Assert.Equal("{\"type\":\"systemView\"}", json);
    }

    // ── player marker (MAP tab, live Game.log location) ──

    [Fact]
    public void BuildInit_PlayerOmitted_SerializesPlayerAsNull()
    {
        var json = MapSceneBuilder.BuildInit(Catalog, "Stanton", EmptyPins,
            tradeOn: false, guidesOn: false, miningOn: false, hangarOn: false, asteroidsOn: true,
            selection: null, draft: Array.Empty<int>(), planner: Array.Empty<int>(), reduced: false);

        Assert.Contains("\"player\":null", json);
    }

    [Fact]
    public void BuildInit_PlayerProvided_SerializesPlayerValue()
    {
        var json = MapSceneBuilder.BuildInit(Catalog, "Stanton", EmptyPins,
            tradeOn: false, guidesOn: false, miningOn: false, hangarOn: false, asteroidsOn: true,
            selection: null, draft: Array.Empty<int>(), planner: Array.Empty<int>(), reduced: false,
            player: 99);

        Assert.Contains("\"player\":99", json);
    }

    [Fact]
    public void BuildPlayerMarker_Null_SerializesIdAsNull()
    {
        var json = MapSceneBuilder.BuildPlayerMarker(null);
        Assert.Equal("{\"type\":\"playerMarker\",\"id\":null,\"live\":true}", json);
    }

    [Fact]
    public void BuildPlayerMarker_WithId_SerializesIdValue()
    {
        var json = MapSceneBuilder.BuildPlayerMarker(543);
        Assert.Equal("{\"type\":\"playerMarker\",\"id\":543,\"live\":true}", json);
    }

    // Two liveness states on every location surface (2026-08-17): the marker message carries the
    // flag so the page can drop the pulse and grey the YOU tag when the reading is last-known.
    [Fact]
    public void BuildPlayerMarker_Offline_SerializesLiveFalse()
    {
        var json = MapSceneBuilder.BuildPlayerMarker(543, live: false);
        Assert.Equal("{\"type\":\"playerMarker\",\"id\":543,\"live\":false}", json);
    }
}
