using NexusApp.Models;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

public class HaulContractEnrichmentTests
{
    private static GameLogEntry E(string raw) => new() { Raw = raw, Category = LogCategory.Other };

    private static ContractDetails Cfp() => new()
    {
        Title = "Need a Hauler [50/200 Rep]",
        Reward = 139250,
        ContractedBy = "Citizens For Prosperity",
        Objectives = { new ContractObjective { Commodity = "Hydrogen", Scu = 6, Pickup = "Jackson's Swap", Dropoff = "Ruin Station" } },
    };

    // The log haul whose RouteTitle is "Need a Hauler" comes from this Contract Accepted line.
    private const string CfpAccept =
        "<2026-06-27T21:49:56.564Z> [Notice] <SHUDEvent_OnNotification> Added notification " +
        "\"Contract Accepted:  Need a Hauler <EM4>[50/200 Rep]</EM4>: \" [8] to queue. New queue size: 5, " +
        "MissionId: [6b4e3396-1506-4051-b348-79c4eabae9d9], ObjectiveId: [] [x]";

    [Fact]
    public void Enrich_MatchesActiveHaulByTitle()
    {
        var t = new HaulTracker();
        t.Ingest(E(HaulLogParserFixtures.CfpMarkerPickup));   // creates the haul (mission 6b4e3396)
        t.Ingest(E(CfpAccept));                               // sets RouteTitle "Need a Hauler"
        t.ApplyContractDetails(Cfp());
        var h = Assert.Single(t.AllHauls);
        Assert.Equal(139250, h.Reward);
        Assert.Equal("Citizens For Prosperity", h.ContractedBy);
        Assert.Equal("Hydrogen", Assert.Single(h.ContractObjectives).Commodity);
    }

    [Fact]
    public void Enrich_BeforeHaulExists_AppliesWhenItAppears()
    {
        var t = new HaulTracker();
        t.ApplyContractDetails(Cfp());                        // no haul yet -> stashed
        t.Ingest(E(HaulLogParserFixtures.CfpMarkerPickup));
        t.Ingest(E(CfpAccept));                               // RouteTitle set -> pending applied
        Assert.Equal(139250, Assert.Single(t.AllHauls).Reward);
    }
}
