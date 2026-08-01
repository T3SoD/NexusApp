using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

public class LocationTrackerTests
{
    private static GameLogEntry E(string raw) => new() { Raw = raw, Category = LogCategory.Other };

    private const string MonitoredSpace =
        "<2026-07-29T22:39:34.857Z> [Notice] <SHUDEvent_OnNotification> Added notification " +
        "\"Entered Monitored Space: \" [0] to queue. New queue size: 1, " +
        "MissionId: [00000000-0000-0000-0000-000000000000], ObjectiveId: [] " +
        "[Team_CoreGameplayFeatures][Missions][Comms]";

    private const string MicroTechJurisdiction =
        "<2026-07-29T22:39:44.908Z> [Notice] <SHUDEvent_OnNotification> Added notification " +
        "\"Entered microTech Jurisdiction: \" [1] to queue. New queue size: 2, " +
        "MissionId: [00000000-0000-0000-0000-000000000000], ObjectiveId: [] " +
        "[Team_CoreGameplayFeatures][Missions][Comms]";

    private const string LocationInventoryRequest =
        "<2026-07-29T22:39:34.863Z> [Notice] <RequestLocationInventory> Player[TestPilot] " +
        "requested inventory for Location[Stanton4_NewBabbage] " +
        "[Team_CoreGameplayFeatures][Inventory]";

    // the owner's real-world live bug (2026-07-31): his Game.log placed him at this raw gateway token.
    private const string PyroGatewayInventoryRequest =
        "<2026-07-29T22:39:34.863Z> [Notice] <RequestLocationInventory> Player[TestPilot] " +
        "requested inventory for Location[RR_JP_StantonPyro] " +
        "[Team_CoreGameplayFeatures][Inventory]";

    private const string TerraGatewayReverseInventoryRequest =
        "<2026-07-29T22:39:34.863Z> [Notice] <RequestLocationInventory> Player[TestPilot] " +
        "requested inventory for Location[RR_JP_TerraStanton] " +
        "[Team_CoreGameplayFeatures][Inventory]";

    private const string InventoryLocationTransition =
        "<2026-07-29T22:39:34.851Z> [Notice] <Update Inventory Location> Player [TestPilot] " +
        "is changing location. Landing [0] -> [3170699229]. Location [0] -> [3170699229]. " +
        "Pending [0] [Team_CoreGameplayFeatures][Inventory]";

    [Fact]
    public void Ingest_JurisdictionLine_SetsLastKnownLocation()
    {
        var t = new LocationTracker(new GameLogFeed());
        t.Ingest(E(MicroTechJurisdiction));
        Assert.Equal("microTech", t.LastKnownLocation);
        Assert.NotNull(t.LastSeenUtc);
    }

    [Fact]
    public void Ingest_MonitoredSpaceLine_IsIgnored_NotStoredAsAPlace()
    {
        // A security-status crossing is a condition, not a location: the same string fires at
        // every monitored boundary in the universe, so storing it tells the user nothing about
        // where they are. Dropped in LocationLogParser before it ever reaches the tracker.
        var t = new LocationTracker(new GameLogFeed());
        t.Ingest(E(MonitoredSpace));
        Assert.Null(t.LastKnownLocation);
    }

    [Fact]
    public void Ingest_MonitoredSpaceAfterARealLocation_LeavesTheRealLocationStanding()
    {
        // The live defect, 2026-08-01: standing in Levski, the origin pill read "Monitored Space"
        // because a status crossing landed seconds after a real location line and overwrote it.
        // The whole point of dropping the status line is that the last real place survives.
        var t = new LocationTracker(new GameLogFeed());
        t.Ingest(E(LocationInventoryRequest));
        Assert.Equal("New Babbage", t.LastKnownLocation);

        t.Ingest(E(MonitoredSpace));
        Assert.Equal("New Babbage", t.LastKnownLocation);
    }

    [Fact]
    public void Ingest_LocationInventoryLine_SetsPreciseKey()
    {
        // Stored value is normalized through LocationAliases (Task 8: in-game names for logged
        // locations) - "Stanton4_NewBabbage" is the raw inventory key, "New Babbage" is the
        // in-game display name it resolves to.
        var t = new LocationTracker(new GameLogFeed());
        t.Ingest(E(LocationInventoryRequest));
        Assert.Equal("New Babbage", t.LastKnownLocation);
    }

    [Fact]
    public void Ingest_InventoryTransitionLine_UpdatesFreshnessOnly_LeavesPlaceUnset()
    {
        var t = new LocationTracker(new GameLogFeed());
        t.Ingest(E(InventoryLocationTransition));
        Assert.Null(t.LastKnownLocation);   // numeric ids only - no name to show yet
        Assert.NotNull(t.LastSeenUtc);
    }

    [Fact]
    public void Ingest_InventoryTransitionLine_DoesNotOverwritePriorPlace()
    {
        // Normalized display name (see Ingest_LocationInventoryLine_SetsPreciseKey) - unchanged
        // by the freshness-only transition line either way.
        var t = new LocationTracker(new GameLogFeed());
        t.Ingest(E(LocationInventoryRequest));           // sets LastKnownLocation
        t.Ingest(E(InventoryLocationTransition));        // freshness-only signal
        Assert.Equal("New Babbage", t.LastKnownLocation);   // unchanged
    }

    [Fact]
    public void Ingest_UnrelatedLine_Ignored()
    {
        var t = new LocationTracker(new GameLogFeed());
        t.Ingest(E("<2026-07-29T22:39:00.000Z> [Notice] <Something> foo"));
        Assert.Null(t.LastKnownLocation);
        Assert.Null(t.LastSeenUtc);
    }

    [Fact]
    public void Changed_FiresOnEachAcceptedSignal()
    {
        int changed = 0;
        var t = new LocationTracker(new GameLogFeed());
        t.Changed += () => changed++;
        t.Ingest(E(MicroTechJurisdiction));
        t.Ingest(E(LocationInventoryRequest));
        t.Ingest(E("<2026-07-29T22:40:00.000Z> [Notice] <Something> foo"));   // unrelated: no fire
        Assert.Equal(2, changed);
    }

    // A station's inventory can be opened twenty times in one stop, and every launch replays the
    // whole Game.log (includeReplay: true) on top of that. Only a real transition is news:
    // ShardTracker, the sibling on this same feed, logs and raises on transitions only, and this
    // tracker now matches it. LastSeenUtc still advances on every matching line (freshness is
    // exactly what the repeats are evidence of), it just does so silently.
    [Fact]
    public void Ingest_SamePlaceTwice_LogsOnceAndRaisesChangedOnce()
    {
        var place = $"ZzzUnitTestPlace_{Guid.NewGuid():N}";
        string Line(string ts) =>
            $"<{ts}> [Notice] <RequestLocationInventory> Player[TestPilot] requested inventory " +
            $"for Location[{place}] [Team_CoreGameplayFeatures][Inventory]";

        int changed = 0;
        var t = new LocationTracker(new GameLogFeed());
        t.Changed += () => changed++;

        t.Ingest(E(Line("2026-07-29T22:39:34.863Z")));
        t.Ingest(E(Line("2026-07-29T22:41:10.100Z")));
        t.Ingest(E(Line("2026-07-29T22:44:02.500Z")));

        Assert.Equal(1, changed);
        Assert.Equal(place, t.LastKnownLocation);
        // The repeats are still ACTIVITY: LastSeenUtc tracks the newest line, silently.
        Assert.Equal(DateTime.Parse("2026-07-29T22:44:02.500Z").ToUniversalTime(), t.LastSeenUtc);

        // Logged once too. Same shared-log read idiom as MarketQueriesTests'
        // UnmappedResource_LogsMissOnceOnlyPerName: the place name is unique to this test, so its
        // occurrence count in the shared test log is this tracker's own line count.
        var logPath = Environment.GetEnvironmentVariable("NEXUS_LOG_PATH");
        Assert.NotNull(logPath);
        var occurrences = TestFiles.ReadSharedLines(logPath!)
            .Sum(l => Regex.Matches(l, Regex.Escape(place)).Count);
        Assert.Equal(1, occurrences);
    }

    [Fact]
    public void Ingest_DifferentPlaceAfterRepeats_LogsAndRaisesAgain()
    {
        int changed = 0;
        var t = new LocationTracker(new GameLogFeed());
        t.Changed += () => changed++;

        t.Ingest(E(LocationInventoryRequest));   // Stanton4_NewBabbage
        t.Ingest(E(LocationInventoryRequest));   // same place again: silent
        t.Ingest(E(MicroTechJurisdiction));      // a real transition: news again

        Assert.Equal(2, changed);
        Assert.Equal("microTech", t.LastKnownLocation);
    }

    // LastKnownUexLocation (the owner's live gateway bug fix, 2026-07-31): a second, raw-token-keyed
    // resolution alongside LastKnownLocation's display name, needed because TradeOriginResolver
    // could not connect "Pyro Gateway Station" (the in-game display name) to UEX's own
    // "Pyro Gateway (Stanton)" Location string by exact or substring match - the live planner
    // produced zero routes at a real, correctly-identified station until this was added.

    [Fact]
    public void Ingest_GroundedGatewayToken_SetsLastKnownUexLocation()
    {
        var t = new LocationTracker(new GameLogFeed());
        t.Ingest(E(PyroGatewayInventoryRequest));
        Assert.Equal("Pyro Gateway Station", t.LastKnownLocation);      // display name, unchanged
        Assert.Equal("Pyro Gateway (Stanton)", t.LastKnownUexLocation); // new: the UEX Location
    }

    // RR_JP_TerraStanton Normalizes to the SAME display name as RR_JP_PyroStanton
    // ("Stanton Gateway Station" - not live, no UEX terminal), but is deliberately NOT in
    // uexLocations (Terra is not a reachable system). Proves LastKnownUexLocation is resolved
    // from the raw token, never guessed from the ambiguous shared display name.
    [Fact]
    public void Ingest_UngroundedGatewayTokenSharingADisplayName_LeavesLastKnownUexLocationNull()
    {
        var t = new LocationTracker(new GameLogFeed());
        t.Ingest(E(TerraGatewayReverseInventoryRequest));
        Assert.Equal("Stanton Gateway Station", t.LastKnownLocation);
        Assert.Null(t.LastKnownUexLocation);
    }

    [Fact]
    public void Ingest_JurisdictionLine_LeavesLastKnownUexLocationNull()
    {
        var t = new LocationTracker(new GameLogFeed());
        t.Ingest(E(MicroTechJurisdiction));
        Assert.Equal("microTech", t.LastKnownLocation);
        Assert.Null(t.LastKnownUexLocation);
    }

    [Fact]
    public void Ingest_NonGatewayInventoryKey_LeavesLastKnownUexLocationNull()
    {
        var t = new LocationTracker(new GameLogFeed());
        t.Ingest(E(LocationInventoryRequest));   // Stanton4_NewBabbage
        Assert.Equal("New Babbage", t.LastKnownLocation);
        Assert.Null(t.LastKnownUexLocation);
    }

    // The <Update Inventory Location> freshness branch is a deliberate contract of its own: a
    // silent LastSeenUtc update that STILL raises Changed (the place did not change, but the
    // signal's age did, and the ORIGIN chip renders that age). Asserted here with a handler
    // attached so the raise itself is covered, not just the LastSeenUtc side effect.
    // LastKnownRawToken (the MAP tab player marker, 2026-07-31): retains the raw Game.log token
    // itself alongside the normalized display name, for the same reason LastKnownUexLocation does -
    // MapCatalog.ResolvePlayerLocation needs the unique raw token to disambiguate a gateway display
    // name that repeats across systems, and cannot safely reverse-derive it from the display name.

    [Fact]
    public void Ingest_LocationInventoryLine_SetsLastKnownRawToken()
    {
        var t = new LocationTracker(new GameLogFeed());
        t.Ingest(E(LocationInventoryRequest));
        Assert.Equal("New Babbage", t.LastKnownLocation);
        Assert.Equal("Stanton4_NewBabbage", t.LastKnownRawToken);
    }

    [Fact]
    public void Ingest_GatewayToken_SetsLastKnownRawToken_EvenThoughDisplayNameRepeats()
    {
        var t = new LocationTracker(new GameLogFeed());
        t.Ingest(E(TerraGatewayReverseInventoryRequest));
        Assert.Equal("Stanton Gateway Station", t.LastKnownLocation);
        Assert.Equal("RR_JP_TerraStanton", t.LastKnownRawToken);
    }

    [Fact]
    public void Ingest_JurisdictionLine_SetsLastKnownRawTokenToTheJurisdictionText()
    {
        // Jurisdiction lines have no separate raw-token concept from their place text - the "place"
        // IS the raw signal for that ingest path, same as LastKnownLocation for a miss.
        var t = new LocationTracker(new GameLogFeed());
        t.Ingest(E(MicroTechJurisdiction));
        Assert.Equal("microTech", t.LastKnownRawToken);
    }

    [Fact]
    public void Ingest_NoSignalYet_LastKnownRawTokenIsNull()
    {
        var t = new LocationTracker(new GameLogFeed());
        Assert.Null(t.LastKnownRawToken);
    }

    [Fact]
    public void Ingest_InventoryTransitionLine_RaisesChanged()
    {
        int changed = 0;
        var t = new LocationTracker(new GameLogFeed());
        t.Changed += () => changed++;

        t.Ingest(E(InventoryLocationTransition));

        Assert.Equal(1, changed);
        Assert.NotNull(t.LastSeenUtc);
    }
}
