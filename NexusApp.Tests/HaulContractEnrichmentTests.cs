using System.Collections.Generic;
using NexusApp.Models;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

public class HaulContractEnrichmentTests
{
    private static GameLogEntry E(string raw) => new() { Raw = raw, Category = LogCategory.Other };

    // Real OCR for the CFP "Need a Hauler" detail pane (contractor "Citizens For Prosperity").
    private static ContractDetails Cfp() => ContractParser.Parse(
        "PRIMARY OBJECTIVES x Ä 139,250 N/A Citizens For Prosperity [50/200 Rep] Need a Hauler DETAILS " +
        "O Deliver 0/11 SCU of Hydrogen to Ruin Station above Pyro VI. o Collect Hydrogen from Jackson's Swap. TRACK")!;

    [Fact]
    public void Enrich_MatchesActiveHaulByContractorCompany()
    {
        var t = new HaulTracker();
        t.Ingest(E(HaulLogParserFixtures.CfpMarkerPickup));   // haul, company "Citizens For Prosperity"
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
        t.ApplyContractDetails(Cfp());                        // no haul yet -> stashed by contractor org
        t.Ingest(E(HaulLogParserFixtures.CfpMarkerPickup));   // company known -> pending applied
        Assert.Equal(139250, Assert.Single(t.AllHauls).Reward);
    }

    [Fact]
    public void Enrich_DoesNotClobberKnownRewardWithZero()
    {
        var t = new HaulTracker();
        t.Ingest(E(HaulLogParserFixtures.CfpMarkerPickup));
        t.ApplyContractDetails(Cfp());                                                // sets reward 139250
        t.ApplyContractDetails(ContractParser.Parse(                                  // noisy later scan, reward unreadable
            "N/A Citizens For Prosperity O Deliver 0/11 SCU of Hydrogen to Ruin Station. o Collect Hydrogen from Jackson's Swap.")!);
        Assert.Equal(139250, Assert.Single(t.AllHauls).Reward);
    }

    [Fact]
    public void Enrich_DuplicateCompany_DisambiguatesByLogCargo()
    {
        var t = new HaulTracker();
        t.Ingest(E(HaulLogParserFixtures.MarkerPickup));   // mission a6c5..., Carbon dropoff leg from the Deliver line
        t.Ingest(E(HaulLogParserFixtures.DeliverLine));
        t.Ingest(E(RedWind2));                             // mission b7d6...
        t.Ingest(E(RedWind2IronDeliver));                  // b7d6... gets an Iron dropoff leg

        t.ApplyContractDetails(ContractParser.Parse(
            "x Ä 95,000 N/A Red Wind Linehaul Deliver 0/158 SCU of Carbon to Jackson's Swap. o Collect Carbon from X.")!);

        foreach (var h in t.AllHauls)
            Assert.Equal(h.MissionId.StartsWith("a6c5") ? 95000 : 0, h.Reward);   // the Carbon haul, not the Iron one
    }

    [Fact]
    public void Enrich_IndistinguishableHauls_FillOnePerContract()
    {
        var t = new HaulTracker();
        t.Ingest(E(HaulLogParserFixtures.MarkerPickup));   // Red Wind #1, no commodity on its legs
        t.Ingest(E(RedWind2));                             // Red Wind #2, no commodity on its legs

        t.ApplyContractDetails(ContractParser.Parse("x Ä 95,000 N/A Red Wind Linehaul Deliver 0/10 SCU of Carbon to A. o Collect Carbon from B.")!);
        t.ApplyContractDetails(ContractParser.Parse("x Ä 88,000 N/A Red Wind Linehaul Deliver 0/20 SCU of Iron to C. o Collect Iron from D.")!);

        var rewards = new List<int>();
        foreach (var h in t.AllHauls) rewards.Add(h.Reward);
        rewards.Sort();
        Assert.Equal(new List<int> { 88000, 95000 }, rewards);   // each contract filled a distinct haul
    }

    [Fact]
    public void Enrich_Rescan_DoesNotDuplicateOntoAnotherHaul()
    {
        var t = new HaulTracker();
        t.Ingest(E(HaulLogParserFixtures.MarkerPickup));
        t.Ingest(E(RedWind2));
        var carbon = ContractParser.Parse("x Ä 95,000 N/A Red Wind Linehaul Deliver 0/10 SCU of Carbon to A. o Collect Carbon from B.")!;
        t.ApplyContractDetails(carbon);
        t.ApplyContractDetails(carbon);   // same contract re-scanned -> updates the same haul, not the other

        var enriched = 0;
        foreach (var h in t.AllHauls) if (h.Reward > 0) enriched++;
        Assert.Equal(1, enriched);
    }

    // A second Red Wind haul (distinct missionId) so the company is ambiguous.
    private const string RedWind2 =
        "<2026-06-27T13:17:11.000Z> [Notice] <CLocalMissionPhaseMarker::CreateMarker> Creating objective marker: " +
        "missionId [b7d6a4f1-1111-2222-3333-444455556666], generator name [RedWind_Hauling], " +
        "contract [RedWind_Pyro_SupplyGrade_RegionA_CFP_StationToTradepost_Carbon_CargoHauling_AtoB_Intro], " +
        "contractDefinitionId[db9d9b2d-d850-4df1-ab0f-7fa2261e8f62], " +
        "objectiveId [pickup_0540719d-0ef2-4e2b-87d5-41ae5cffe412_0], markerEntityId [9999], zoneHostId [525246124367], " +
        "position [x: 1.0, y: 2.0, z: 3.0] [Team_MissionFeatures][Missions]";

    // Gives RedWind2 an Iron dropoff leg so it differs from the Carbon haul.
    private const string RedWind2IronDeliver =
        "<2026-06-27T13:17:11.100Z> [Notice] <SHUDEvent_OnNotification> Added notification " +
        "\"New Objective: Deliver 0/40 SCU of Iron to Checkmate: \" [6] to queue. New queue size: 7, " +
        "MissionId: [b7d6a4f1-1111-2222-3333-444455556666], ObjectiveId: [dropoff_0540719d-0ef2-4e2b-87d5-41ae5cffe412_0] " +
        "[Team_CoreGameplayFeatures][Missions][Comms]";
}
