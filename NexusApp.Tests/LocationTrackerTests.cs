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

    // The owner's real-world live bug (2026-07-31): his Game.log placed him at this raw gateway token.
    private const string PyroGatewayInventoryRequest =
        "<2026-07-29T22:39:34.863Z> [Notice] <RequestLocationInventory> Player[TestPilot] " +
        "requested inventory for Location[RR_JP_StantonPyro] " +
        "[Team_CoreGameplayFeatures][Inventory]";

    // Real capture (2026-08-01) at the Pyro-side Nyx gateway. The object it names, "Nyx Gateway",
    // exists in BOTH Stanton and Pyro, so this token is what disambiguates the two.
    private const string NyxGatewayInventoryRequest =
        "<2026-08-01T00:14:49.839Z> [Notice] <RequestLocationInventory> Player[TestPilot] " +
        "requested inventory for Location[RR_JP_PyroNyx] " +
        "[Team_CoreGameplayFeatures][Inventory]";

    // The Nyx-side counterpart of the gate above. Captured live on 2026-08-01, twice (00:19:50Z
    // and 00:42:15Z), which is what let it into the alias tables. Its display name COLLIDES with
    // the Stanton-side RR_JP_StantonPyro - both read "Pyro Gateway Station" - so this fixture is
    // also the tracker-level proof that the raw token has to be retained.
    private const string NyxSideGatewayInventoryRequest =
        "<2026-08-01T00:19:50.535Z> [Notice] <RequestLocationInventory> Player[TestPilot] " +
        "requested inventory for Location[RR_JP_NyxPyro] " +
        "[Team_CoreGameplayFeatures][Inventory]";

    // A gateway token that has NEVER been captured and is therefore deliberately absent from both
    // alias tables. Shaped exactly like a real one, which is the point. RR_JP_NyxPyro used to play
    // this role and was promoted out of it on 2026-08-01 when the owner's own log produced it twice;
    // "Stanton Gateway (Nyx)" is still listed in the UEX snapshot with no token to pair it with.
    private const string UncapturedGatewayInventoryRequest =
        "<2026-08-01T00:14:49.839Z> [Notice] <RequestLocationInventory> Player[TestPilot] " +
        "requested inventory for Location[RR_JP_NyxStanton] " +
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
    public void Ingest_JurisdictionLine_IsMarkedCoarse()
    {
        // The owner's live find, 2026-08-01: the LOCATION chip read "Crusader Industries" at
        // Crusader - a jurisdiction names whose SPACE you are in, not where you stand. The flag
        // lets display surfaces qualify it instead of dressing an area up as a place.
        var t = new LocationTracker(new GameLogFeed());
        t.Ingest(E(MicroTechJurisdiction));
        Assert.True(t.LastKnownIsJurisdiction);
    }

    [Fact]
    public void Ingest_InventoryLineAfterJurisdiction_ClearsTheCoarseFlag()
    {
        var t = new LocationTracker(new GameLogFeed());
        t.Ingest(E(MicroTechJurisdiction));
        t.Ingest(E(LocationInventoryRequest));
        Assert.Equal("New Babbage", t.LastKnownLocation);
        Assert.False(t.LastKnownIsJurisdiction);
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

    // LastKnownUexLocation (owner's live gateway bug fix, 2026-07-31): a second, raw-token-keyed
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

    // An uncaptured gateway token resolves to NOTHING rather than borrowing a neighbour's answer.
    // RR_JP_NyxStanton is shaped exactly like the real tokens and names a gate whose UEX Location
    // ("Stanton Gateway (Nyx)") really does sit in the snapshot, so a lookup that pattern-matched
    // instead of requiring a real capture would happily hand one back and place the player in the
    // wrong system. The raw passthrough asserted here is also the visible symptom of a missing
    // capture: the origin pill shows the ugly token, which is exactly how the gateway gap got
    // reported in the first place. That is intended - a wrong-but-tidy name is worse than an
    // obviously raw one.
    [Fact]
    public void Ingest_UncapturedGatewayToken_PassesThroughRaw_AndBorrowsNoUexLocation()
    {
        var t = new LocationTracker(new GameLogFeed());
        t.Ingest(E(UncapturedGatewayInventoryRequest));
        Assert.Equal("RR_JP_NyxStanton", t.LastKnownLocation);
        Assert.Null(t.LastKnownUexLocation);
    }

    // The promotion itself: the token that USED to be this file's uncaptured example now resolves
    // end to end, because it was captured for real. Both halves matter - the display name and the
    // UEX vocabulary diverge, and the UEX one is what makes a trade lookup succeed at the gate.
    [Fact]
    public void Ingest_NyxSideGatewayToken_NowResolves_AfterItsLiveCapture()
    {
        var t = new LocationTracker(new GameLogFeed());
        t.Ingest(E(NyxSideGatewayInventoryRequest));
        Assert.Equal("Pyro Gateway Station", t.LastKnownLocation);
        Assert.Equal("Pyro Gateway (Nyx)", t.LastKnownUexLocation);
        Assert.Equal("RR_JP_NyxPyro", t.LastKnownRawToken);
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
    public void Ingest_GatewayToken_KeepsTheRawToken_BecauseTheObjectNameRepeatsAcrossSystems()
    {
        // The display name alone cannot place this: "Nyx Gateway" names an object in BOTH Stanton
        // and Pyro. Retaining the raw token beside the display name is what lets the map's player
        // marker land in the right system (MapCatalog.RawTokenGateways).
        var t = new LocationTracker(new GameLogFeed());
        t.Ingest(E(NyxGatewayInventoryRequest));
        Assert.Equal("Nyx Gateway Station", t.LastKnownLocation);
        Assert.Equal("RR_JP_PyroNyx", t.LastKnownRawToken);
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
