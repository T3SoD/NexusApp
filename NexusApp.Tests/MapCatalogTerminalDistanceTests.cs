using NexusApp.Models;
using NexusApp.Services;
using NexusApp.Services.Map;
using Xunit;

namespace NexusApp.Tests;

// The seam that let TRADE retire its own second geometry catalog (StarmapCatalog): MapCatalog now
// answers "how far apart are these two market terminals" with the same contract StarmapCatalog had.
//
// The most valuable test here is the PARITY one. The claim justifying the swap was that the two
// catalogs agree, and an assertion is not evidence - so while both still exist, this measures them
// against each other over the real embedded data. When StarmapCatalog is deleted, delete that test
// with it; the rest stand on their own.
public class MapCatalogTerminalDistanceTests
{
    private static readonly MapCatalog Map = MapCatalog.LoadEmbedded();
    private static readonly StarmapCatalog Legacy = StarmapCatalog.LoadEmbedded();

    private static GameLogEntry E(string raw) => new() { Raw = raw, Category = LogCategory.Other };

    private static MarketTerminal Term(string system, string location, string planetOrMoon = "", string orbit = "")
        => new(1, $"T:{location}", "commodity", false, system, location, planetOrMoon, orbit);

    [Fact]
    public void SameSystemPair_MatchesTheCatalogItReplaced()
    {
        // Two real Stanton locations that both catalogs carry. The embeds were built from the same
        // source and their coordinates agree to sub-metre precision, so any disagreement here means
        // the swap changed what a user sees.
        var a = Term("Stanton", "Everus Harbor");
        var b = Term("Stanton", "Baijini Point");

        var fromMap = Map.DistanceMeters(a, b);
        var fromLegacy = Legacy.DistanceMeters(a, b);

        Assert.NotNull(fromMap);
        Assert.NotNull(fromLegacy);
        // 5 m over distances measured in Gm. The formatted output rounds to 2 decimal places of a
        // Gm (10^7 m), so nothing this size can ever change a rendered figure.
        Assert.True(Math.Abs(fromMap!.Value - fromLegacy!.Value) < 5.0,
            $"map {fromMap} vs legacy {fromLegacy}");
        Assert.Equal(StarmapCatalog.FormatGm(fromLegacy.Value), MapCatalog.FormatGm(fromMap.Value));
    }

    [Fact]
    public void CrossSystemPair_IsNullInBoth()
    {
        // Jump travel is not Euclidean, so a cross-system straight line is meaningless and neither
        // catalog computes one.
        var stanton = Term("Stanton", "Everus Harbor");
        var pyro = Term("Pyro", "Ruin Station");

        Assert.Null(Map.DistanceMeters(stanton, pyro));
        Assert.Null(Legacy.DistanceMeters(stanton, pyro));
    }

    [Fact]
    public void UnresolvableTerminal_IsNull_NotZero()
    {
        // Zero would read as "same place" on screen. Null is the honest answer and renders as silence.
        var known = Term("Stanton", "Everus Harbor");
        var nonsense = Term("Stanton", "Nowhere At All");

        Assert.Null(Map.DistanceMeters(known, nonsense));
        Assert.Null(Map.DistanceMeters((MarketTerminal?)null, known));
    }

    [Fact]
    public void SameTerminalTwice_IsZero_NotNull()
    {
        var a = Term("Stanton", "Everus Harbor");
        Assert.Equal(0, Map.DistanceMeters(a, a));
    }

    // ── the excluded-object carve-out ──

    [Fact]
    public void ExcludedPlace_StillResolvesForDistance()
    {
        // "Stanton - Terra Jump Point" is held off the map surface (Terra is not in the game), but it
        // is the ONLY excluded object carrying UEX aliases, and its "Terra Gateway (Stanton)" location
        // covers 21 real UEX terminals. Refusing to measure them would have silently removed a
        // distance the Trade planner has always shown - a regression hidden inside a cosmetic ruling.
        var terminal = Term("Stanton", "Terra Gateway (Stanton)");

        Assert.NotNull(Map.ResolveTerminalForDistance(terminal));
        Assert.Equal("Stanton - Terra Jump Point", Map.ResolveTerminalForDistance(terminal)!.Name);
    }

    [Fact]
    public void ExcludedPlace_NeverResolvesForTheMapSurface()
    {
        // The other half of the carve-out, and the reason it is two methods rather than a flag on
        // one: whatever builds scene pins must NOT get this back, or it would place a pin for an
        // object the scene does not contain.
        Assert.Null(Map.ResolveTerminal(Term("Stanton", "Terra Gateway (Stanton)")));
    }

    [Fact]
    public void ExcludedPlace_YieldsARealDistance_MatchingTheOldCatalog()
    {
        // End to end: the carve-out exists so this number does not disappear.
        var terra = Term("Stanton", "Terra Gateway (Stanton)");
        var everus = Term("Stanton", "Everus Harbor");

        var fromMap = Map.DistanceMeters(terra, everus);
        Assert.NotNull(fromMap);

        var fromLegacy = Legacy.DistanceMeters(terra, everus);
        Assert.NotNull(fromLegacy);
        Assert.Equal(StarmapCatalog.FormatGm(fromLegacy!.Value), MapCatalog.FormatGm(fromMap!.Value));
    }

    // ── PlayerPlace, the app-wide position seam ──

    [Fact]
    public void PlayerPlace_WithNoSession_IsAllNulls_NeverThrows()
    {
        // The normal state with Star Citizen closed. "We do not know" is a first-class answer and
        // every caller renders it as silence, so none of these may throw or invent a placeholder.
        var place = new PlayerPlace(Map, new LocationTracker(new GameLogFeed()));

        Assert.Null(place.Current);
        Assert.Null(place.Label);
        Assert.Null(place.SeenUtc);
        Assert.Null(place.System);
        Assert.Null(place.DistanceFrom(Map.ByName("Stanton", "Hurston")));
        Assert.Null(place.DistanceFrom((MarketTerminal?)null));
    }

    [Fact]
    public void PlayerPlace_AfterALocationLine_ResolvesAndMeasures()
    {
        var tracker = new LocationTracker(new GameLogFeed());
        tracker.Ingest(E("<2026-07-29T22:39:34.863Z> [Notice] <RequestLocationInventory> Player[TestPilot] " +
            "requested inventory for Location[Stanton4_NewBabbage] [Team_CoreGameplayFeatures][Inventory]"));

        var place = new PlayerPlace(Map, tracker);

        Assert.Equal("New Babbage", place.Label);
        Assert.NotNull(place.Current);
        Assert.Equal("Stanton", place.System);
        Assert.NotNull(place.SeenUtc);

        // A real formatted distance to somewhere else in the same system...
        Assert.NotNull(place.DistanceFrom(Map.ByName("Stanton", "Hurston")));
        // ...and silence across a jump point, which is not a straight line.
        Assert.Null(place.DistanceFrom(Map.ByName("Pyro", "Ruin Station")));
    }

    [Fact]
    public void PlayerPlace_AtAPlanetNamedJurisdiction_ResolvesToThatPlanet()
    {
        // Better than expected, and worth pinning because it is not obvious: four of Stanton's
        // jurisdictions are named after the planets that own them, so a jurisdiction crossing - the
        // coarsest signal Game.log produces - still lands on a real object with real coordinates.
        // A jurisdiction is not usually a place, but "microTech" happens to be both.
        var tracker = new LocationTracker(new GameLogFeed());
        tracker.Ingest(E("<2026-07-29T22:39:44.908Z> [Notice] <SHUDEvent_OnNotification> Added notification " +
            "\"Entered microTech Jurisdiction: \" [1] to queue. New queue size: 2, " +
            "MissionId: [00000000-0000-0000-0000-000000000000], ObjectiveId: [] " +
            "[Team_CoreGameplayFeatures][Missions][Comms]"));

        var place = new PlayerPlace(Map, tracker);

        Assert.Equal("microTech", place.Label);
        Assert.NotNull(place.Current);
        Assert.Equal("Planet", place.Current!.Type);
        Assert.Equal("Stanton", place.System);
        Assert.NotNull(place.DistanceFrom(Map.ByName("Stanton", "Hurston")));
    }

    [Fact]
    public void PlayerPlace_AtAFactionJurisdiction_KeepsTheLabelButCannotMeasure()
    {
        // The genuinely half-known case: a faction whose name is not a place. Saying where you are
        // and measuring from it are different capabilities, and the seam keeps them separate rather
        // than dropping the label just because it cannot produce coordinates.
        var tracker = new LocationTracker(new GameLogFeed());
        tracker.Ingest(E("<2026-08-01T00:24:18.718Z> [Notice] <SHUDEvent_OnNotification> Added notification " +
            "\"Entered People's Alliance Jurisdiction: \" [97] to queue. New queue size: 2, " +
            "MissionId: [00000000-0000-0000-0000-000000000000], ObjectiveId: [] " +
            "[Team_CoreGameplayFeatures][Missions][Comms]"));

        var place = new PlayerPlace(Map, tracker);

        Assert.Equal("People's Alliance", place.Label);
        Assert.Null(place.Current);
        Assert.Null(place.System);
        Assert.Null(place.DistanceFrom(Map.ByName("Stanton", "Hurston")));
    }
}
