using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

public class LocationLogParserTests
{
    // ---- Real, byte-verbatim Game.log lines (PTU\Game.log on disk, 2026-07-29 session).
    // Player handle genericized to "TestPilot" - an RSI handle is player-identifying and this repo
    // is public; every other byte (tags, timestamps, punctuation, spacing) is unchanged.

    private const string RealMonitoredSpace =
        "<2026-07-29T22:39:34.857Z> [Notice] <SHUDEvent_OnNotification> Added notification " +
        "\"Entered Monitored Space: \" [0] to queue. New queue size: 1, " +
        "MissionId: [00000000-0000-0000-0000-000000000000], ObjectiveId: [] " +
        "[Team_CoreGameplayFeatures][Missions][Comms]";

    private const string RealMicroTechJurisdiction =
        "<2026-07-29T22:39:44.908Z> [Notice] <SHUDEvent_OnNotification> Added notification " +
        "\"Entered microTech Jurisdiction: \" [1] to queue. New queue size: 2, " +
        "MissionId: [00000000-0000-0000-0000-000000000000], ObjectiveId: [] " +
        "[Team_CoreGameplayFeatures][Missions][Comms]";

    private const string RealLocationInventoryRequest =
        "<2026-07-29T22:39:34.863Z> [Notice] <RequestLocationInventory> Player[TestPilot] " +
        "requested inventory for Location[Stanton4_NewBabbage] " +
        "[Team_CoreGameplayFeatures][Inventory]";

    private const string RealInventoryLocationTransition =
        "<2026-07-29T22:39:34.851Z> [Notice] <Update Inventory Location> Player [TestPilot] " +
        "is changing location. Landing [0] -> [3170699229]. Location [0] -> [3170699229]. " +
        "Pending [0] [Team_CoreGameplayFeatures][Inventory]";

    // Modeled on the two real SHUDEvent_OnNotification lines above (identical wrapper bytes),
    // place text per the recon's own frequency count ("Entered Rough & Ready Jurisdiction x1" -
    // nexus-assets/specs/2026-07-28-gamelog-datacore-discovery-raw.json line 75). NOT an
    // independently captured verbatim sample - exercises a multi-word faction jurisdiction name.
    private const string ModeledRoughAndReadyJurisdiction =
        "<2026-07-24T09:12:03.114Z> [Notice] <SHUDEvent_OnNotification> Added notification " +
        "\"Entered Rough & Ready Jurisdiction: \" [1] to queue. New queue size: 1, " +
        "MissionId: [00000000-0000-0000-0000-000000000000], ObjectiveId: [] " +
        "[Team_CoreGameplayFeatures][Missions][Comms]";

    // Real, byte-verbatim line from the 2026-08-01 LIVE session that exposed the defect: this
    // People's Alliance crossing (Levski's controlling faction) landed 11 seconds before a
    // "Entered Monitored Space" line, and the status line overwrote it, leaving the origin pill
    // reading "Monitored Space" while the player stood in Levski. No handle appears in this line,
    // so nothing needed genericizing.
    private const string RealPeoplesAllianceJurisdiction =
        "<2026-08-01T00:24:18.718Z> [Notice] <SHUDEvent_OnNotification> Added notification " +
        "\"Entered People's Alliance Jurisdiction: \" [97] to queue. New queue size: 2, " +
        "MissionId: [00000000-0000-0000-0000-000000000000], ObjectiveId: [] " +
        "[Team_CoreGameplayFeatures][Missions][Comms]";

    // Modeled counterpart of the real Monitored Space line above (identical wrapper bytes, only
    // the status word changed). Lawless space is the documented opposite condition; NOT an
    // independently captured sample.
    private const string ModeledUnmonitoredSpace =
        "<2026-08-01T00:20:20.817Z> [Notice] <SHUDEvent_OnNotification> Added notification " +
        "\"Entered Unmonitored Space: \" [95] to queue. New queue size: 1, " +
        "MissionId: [00000000-0000-0000-0000-000000000000], ObjectiveId: [] " +
        "[Team_CoreGameplayFeatures][Missions][Comms]";

    [Theory]
    [InlineData(true)]    // the real captured line
    [InlineData(false)]   // its modeled lawless-space counterpart
    public void ParseJurisdiction_SecurityStatusCrossing_IsDroppedAsNotAPlace(bool monitored)
    {
        // A security-status crossing names a CONDITION, not a location - the identical string
        // fires at every monitored boundary in the universe. Storing it as the last known place
        // is what put "Monitored Space" in the origin pill while the player was in Levski.
        Assert.Null(LocationLogParser.ParseJurisdiction(monitored ? RealMonitoredSpace : ModeledUnmonitoredSpace));
    }

    [Fact]
    public void ParseJurisdiction_NamedJurisdictionWithApostrophe_SurvivesTheStatusFilter()
    {
        // The other half of the fix: dropping status crossings must not touch real faction
        // jurisdictions, so the previous good signal is what stays standing.
        var s = LocationLogParser.ParseJurisdiction(RealPeoplesAllianceJurisdiction);
        Assert.NotNull(s);
        Assert.Equal("People's Alliance", s!.Value.Place);
        Assert.Equal(new DateTime(2026, 8, 1, 0, 24, 18, 718, DateTimeKind.Utc), s.Value.SeenUtc);
    }

    [Fact]
    public void ParseJurisdiction_NamedJurisdiction_StripsTheWordJurisdiction()
    {
        var s = LocationLogParser.ParseJurisdiction(RealMicroTechJurisdiction);
        Assert.NotNull(s);
        Assert.Equal("microTech", s!.Value.Place);
    }

    [Fact]
    public void ParseJurisdiction_MultiWordFactionName_ParsesWhole()
    {
        var s = LocationLogParser.ParseJurisdiction(ModeledRoughAndReadyJurisdiction);
        Assert.NotNull(s);
        Assert.Equal("Rough & Ready", s!.Value.Place);
    }

    [Fact]
    public void ParseJurisdiction_UnrelatedNotificationLine_ReturnsNull()
    {
        // Contract Accepted / New Objective SHUDEvent_OnNotification variants belong to
        // HaulLogParser already - this parser must not also claim them.
        const string contractAccepted =
            "<2026-07-24T09:00:00.000Z> [Notice] <SHUDEvent_OnNotification> Added notification " +
            "\"Contract Accepted: Some Contract\" [0] to queue. New queue size: 1, " +
            "MissionId: [11111111-1111-1111-1111-111111111111], ObjectiveId: [] " +
            "[Team_CoreGameplayFeatures][Missions][Comms]";
        Assert.Null(LocationLogParser.ParseJurisdiction(contractAccepted));
    }

    [Fact]
    public void ParseLocationInventory_RealLine_ExtractsReadableKey()
    {
        var s = LocationLogParser.ParseLocationInventory(RealLocationInventoryRequest);
        Assert.NotNull(s);
        Assert.Equal("Stanton4_NewBabbage", s!.Value.Place);
        Assert.Equal(new DateTime(2026, 7, 29, 22, 39, 34, 863, DateTimeKind.Utc), s.Value.SeenUtc);
    }

    [Fact]
    public void ParseLocationInventory_NoPlayerBracket_HandlesRealSpacingDifference()
    {
        // RequestLocationInventory has NO space before "[" ("Player[TestPilot]"); Update Inventory
        // Location DOES ("Player [TestPilot]") - verified byte-for-byte in the real PTU capture.
        // This asserts the inventory-request parser is not fooled by the transition line's shape.
        Assert.Null(LocationLogParser.ParseLocationInventory(RealInventoryLocationTransition));
    }

    [Fact]
    public void ParseInventoryTransitionUtc_RealLine_ReturnsTimestampOnly()
    {
        // The numeric landing/location ids have no name lookup table yet (recon: "joinable to
        // readable keys over time" - deferred) - this signal is freshness-only by design.
        var seenUtc = LocationLogParser.ParseInventoryTransitionUtc(RealInventoryLocationTransition);
        Assert.Equal(new DateTime(2026, 7, 29, 22, 39, 34, 851, DateTimeKind.Utc), seenUtc);
    }

    [Fact]
    public void ParseInventoryTransitionUtc_UnrelatedLine_ReturnsNull()
        => Assert.Null(LocationLogParser.ParseInventoryTransitionUtc(RealMonitoredSpace));
}
