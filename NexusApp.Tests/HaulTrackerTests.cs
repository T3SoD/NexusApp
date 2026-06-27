using NexusApp.Models;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Headless: feed real (redacted) lines straight into Ingest, no watcher tick, no real file.
public class HaulTrackerTests
{
    private const string Mid = "a6c598e0-e8d2-4613-a7a3-e80f4c30d9c6";

    private static GameLogEntry E(string raw) => new() { Raw = raw, Category = LogCategory.Other };

    private static readonly string MarkerPickup = HaulLogParserFixtures.MarkerPickup;
    private static readonly string MarkerDropoff = HaulLogParserFixtures.MarkerDropoff;
    private static readonly string DeliverLine = HaulLogParserFixtures.DeliverLine;
    private static readonly string PickupCompleted = HaulLogParserFixtures.ObjectiveCompletedLine;
    private const string EndComplete =
        "<2026-06-27T14:40:00.000Z> [Notice] <EndMission> Ending mission for player. " +
        "MissionId[a6c598e0-e8d2-4613-a7a3-e80f4c30d9c6] Player[REDACTED] PlayerId[REDACTED] " +
        "CompletionType[Complete] Reason[Mission Ended] [Team_MissionFeatures][Missions]";

    [Fact]
    public void Marker_CreatesHaul_WithCompanyAndTopologyAndLeg()
    {
        var t = new HaulTracker();
        t.Ingest(E(MarkerPickup));
        var h = Assert.Single(t.ActiveHauls);
        Assert.Equal(Mid, h.MissionId);
        Assert.Equal("Red Wind", h.Company);
        Assert.Equal("1 to 1", h.Topology);
        Assert.Single(h.Legs);
        Assert.Equal(HaulRole.Pickup, h.Legs[0].Role);
    }

    [Fact]
    public void DuplicateMarker_DoesNotDuplicateLeg()
    {
        var t = new HaulTracker();
        t.Ingest(E(MarkerPickup));
        t.Ingest(E(MarkerPickup));   // re-emitted on zone reload
        Assert.Single(t.ActiveHauls[0].Legs);
    }

    [Fact]
    public void Deliver_EnrichesDropoffLeg_WithCommodityScuDestination()
    {
        var t = new HaulTracker();
        t.Ingest(E(MarkerDropoff));
        t.Ingest(E(DeliverLine));
        var leg = t.ActiveHauls[0].Legs[0];
        Assert.Equal("Carbon", leg.Commodity);
        Assert.Equal(158, leg.TargetScu);
        Assert.Equal("Jackson's Swap", leg.Destination);
    }

    [Fact]
    public void ObjectiveCompleted_MarksLegDone()
    {
        var t = new HaulTracker();
        t.Ingest(E(MarkerPickup));
        t.Ingest(E(PickupCompleted));
        Assert.True(t.ActiveHauls[0].Legs[0].Completed);
    }

    [Fact]
    public void EndMission_MovesHaulToFinished_WithOutcome()
    {
        var t = new HaulTracker();
        t.Ingest(E(MarkerPickup));
        t.Ingest(E(EndComplete));
        Assert.Empty(t.ActiveHauls);
        var h = Assert.Single(t.FinishedHauls);
        Assert.Equal(HaulOutcome.Complete, h.Outcome);
    }

    [Fact]
    public void EndMission_ForUnknownNonHaulMission_Ignored()
    {
        var t = new HaulTracker();
        t.Ingest(E(EndComplete.Replace(Mid, "ffffffff-0000-0000-0000-000000000000")));
        Assert.Empty(t.AllHauls);
    }

    [Fact]
    public void Reset_ClearsEverything()
    {
        var t = new HaulTracker();
        t.Ingest(E(MarkerPickup));
        t.Reset();
        Assert.Empty(t.AllHauls);
    }

    [Fact]
    public void ChangedEvent_FiresOnNewHaul()
    {
        var t = new HaulTracker();
        int fired = 0;
        t.Changed += () => fired++;
        t.Ingest(E(MarkerPickup));
        Assert.True(fired >= 1);
    }
}
