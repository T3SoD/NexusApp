using NexusApp.Models;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Real (redacted) current-build (12061511) Game.log lines as fixtures, inline per repo convention.
public class HaulLogParserTests
{
    private const string MarkerPickup =
        "<2026-06-27T13:17:10.856Z> [Notice] <CLocalMissionPhaseMarker::CreateMarker> Creating objective marker: " +
        "missionId [a6c598e0-e8d2-4613-a7a3-e80f4c30d9c6], generator name [RedWind_Hauling], " +
        "contract [RedWind_Pyro_SupplyGrade_RegionA_CFP_StationToTradepost_Carbon_CargoHauling_AtoB_Intro], " +
        "contractDefinitionId[db9d9b2d-d850-4df1-ab0f-7fa2261e8f62], " +
        "objectiveId [pickup_6e9335e8-f62e-4eca-8fc5-a42ab1c3ab7d_0], markerEntityId [2806], zoneHostId [525246124367], " +
        "position [x: 2281204.528538, y: -14897443.959268, z: 2752554.224023] [Team_MissionFeatures][Missions]";

    private const string MarkerDropoff =
        "<2026-06-27T13:17:10.856Z> [Notice] <CLocalMissionPhaseMarker::CreateMarker> Creating objective marker: " +
        "missionId [a6c598e0-e8d2-4613-a7a3-e80f4c30d9c6], generator name [RedWind_Hauling], " +
        "contract [RedWind_Pyro_SupplyGrade_RegionA_CFP_StationToTradepost_Carbon_CargoHauling_AtoB_Intro], " +
        "contractDefinitionId[db9d9b2d-d850-4df1-ab0f-7fa2261e8f62], " +
        "objectiveId [dropoff_6e9335e8-f62e-4eca-8fc5-a42ab1c3ab7d_0], markerEntityId [2807], zoneHostId [525246124540], " +
        "position [x: 383115.366423, y: -245829.717381, z: -272467.223889] [Team_MissionFeatures][Missions]";

    private const string DeliverLine =
        "<2026-06-27T13:17:10.862Z> [Notice] <SHUDEvent_OnNotification> Added notification " +
        "\"New Objective: Deliver 0/158 SCU of Carbon to Jackson's Swap: \" [6] to queue. New queue size: 7, " +
        "MissionId: [a6c598e0-e8d2-4613-a7a3-e80f4c30d9c6], ObjectiveId: [dropoff_6e9335e8-f62e-4eca-8fc5-a42ab1c3ab7d_0] " +
        "[Team_CoreGameplayFeatures][Missions][Comms]";

    private const string AcceptRouteLine =
        "<2026-06-13T13:31:38.068Z> [Notice] <SHUDEvent_OnNotification> Added notification " +
        "\"Contract Accepted:  Master | <EM3>DIRECT</EM3> Bulk Haul | Ruin Station > Baijini Point <EM4>[BP]*</EM4>: \" [16] to queue. " +
        "New queue size: 8, MissionId: [a6c598e0-e8d2-4613-a7a3-e80f4c30d9c6], ObjectiveId: [] [Team_CoreGameplayFeatures][Missions][Comms]";

    private const string ObjectiveCompletedLine =
        "<2026-06-27T13:42:44.088Z> [Notice] <ObjectiveUpserted> Received ObjectiveUpserted push message for: " +
        "mission_id a6c598e0-e8d2-4613-a7a3-e80f4c30d9c6 - objective_id pickup_6e9335e8-f62e-4eca-8fc5-a42ab1c3ab7d_0 " +
        "- state MISSION_OBJECTIVE_STATE_COMPLETED - created 0 - flags=ShowInLog| [Team_GameServices][Missions]";

    private const string ObjectiveInProgressLine =
        "<2026-06-27T13:42:44.088Z> [Notice] <ObjectiveUpserted> Received ObjectiveUpserted push message for: " +
        "mission_id a6c598e0-e8d2-4613-a7a3-e80f4c30d9c6 - objective_id dropoff_6e9335e8-f62e-4eca-8fc5-a42ab1c3ab7d_0 " +
        "- state MISSION_OBJECTIVE_STATE_INPROGRESS - created 1 - flags=ShowInLog| [Team_GameServices][Missions]";

    private const string EndComplete =
        "<2026-06-13T13:35:51.224Z> [Notice] <EndMission> Ending mission for player. " +
        "MissionId[6ea3467c-83c8-4b41-b25c-ead2c558cc54] Player[REDACTED] PlayerId[REDACTED] " +
        "CompletionType[Complete] Reason[Mission Ended] [Team_MissionFeatures][Missions]";

    private const string EndAbandon =
        "<2026-06-27T13:30:27.252Z> [Notice] <EndMission> Ending mission for player. " +
        "MissionId[e179ea2b-3099-48a3-b0ef-6795bfb5337b] Player[REDACTED] PlayerId[REDACTED] " +
        "CompletionType[Abandon] Reason[Player left] [Team_MissionFeatures][Missions]";

    [Fact]
    public void ParseMarker_Pickup_ExtractsAllFields()
    {
        var m = HaulLogParser.ParseMarker(MarkerPickup);
        Assert.NotNull(m);
        Assert.Equal("a6c598e0-e8d2-4613-a7a3-e80f4c30d9c6", m!.MissionId);
        Assert.Equal("RedWind_Hauling", m.Generator);
        Assert.Equal(HaulRole.Pickup, m.Role);
        Assert.Equal("pickup_6e9335e8-f62e-4eca-8fc5-a42ab1c3ab7d_0", m.ObjectiveId);
        Assert.Equal("6e9335e8-f62e-4eca-8fc5-a42ab1c3ab7d_0", m.CargoKey);
        Assert.Equal(0, m.LegIndex);
        Assert.Equal(2281204.528538, m.X, 3);
    }

    [Fact]
    public void ParseMarker_Dropoff_RoleIsDropoff()
    {
        var m = HaulLogParser.ParseMarker(MarkerDropoff);
        Assert.Equal(HaulRole.Dropoff, m!.Role);
        Assert.Equal("6e9335e8-f62e-4eca-8fc5-a42ab1c3ab7d_0", m.CargoKey);
    }

    [Fact]
    public void ParseMarker_NonHaulContract_ReturnsNull()
    {
        var bounty = MarkerPickup.Replace(
            "contract [RedWind_Pyro_SupplyGrade_RegionA_CFP_StationToTradepost_Carbon_CargoHauling_AtoB_Intro]",
            "contract [BountyHuntersGuild_Bounty_Stanton_Medium_2]");
        Assert.Null(HaulLogParser.ParseMarker(bounty));
    }

    [Fact]
    public void ParseDeliver_ExtractsCommodityScuDestination()
    {
        var d = HaulLogParser.ParseDeliver(DeliverLine);
        Assert.NotNull(d);
        Assert.Equal("a6c598e0-e8d2-4613-a7a3-e80f4c30d9c6", d!.MissionId);
        Assert.Equal("dropoff_6e9335e8-f62e-4eca-8fc5-a42ab1c3ab7d_0", d.ObjectiveId);
        Assert.Equal("Carbon", d.Commodity);
        Assert.Equal(158, d.TargetScu);
        Assert.Equal("Jackson's Swap", d.Destination);
    }

    [Fact]
    public void ParseDeliver_NonScuObjective_ReturnsNull()
    {
        var dataObjective = DeliverLine.Replace("Deliver 0/158 SCU of Carbon to Jackson's Swap",
                                                "Deliver Energy Anomaly Data to Port Tressler's Freight Elevator");
        Assert.Null(HaulLogParser.ParseDeliver(dataObjective));
    }

    [Fact]
    public void ParseContractAccepted_StripsEmTags_AndKeepsRoute()
    {
        var a = HaulLogParser.ParseContractAccepted(AcceptRouteLine);
        Assert.NotNull(a);
        Assert.Equal("a6c598e0-e8d2-4613-a7a3-e80f4c30d9c6", a!.MissionId);
        Assert.DoesNotContain("<EM", a.Title);
        Assert.Contains("Ruin Station > Baijini Point", a.Title);
    }

    [Fact]
    public void ParseObjectiveCompleted_OnlyCompletedState()
    {
        var c = HaulLogParser.ParseObjectiveCompleted(ObjectiveCompletedLine);
        Assert.NotNull(c);
        Assert.Equal("pickup_6e9335e8-f62e-4eca-8fc5-a42ab1c3ab7d_0", c!.ObjectiveId);
        Assert.Null(HaulLogParser.ParseObjectiveCompleted(ObjectiveInProgressLine));
    }

    [Fact]
    public void ParseEndMission_MapsCompletionType_NoPii()
    {
        var done = HaulLogParser.ParseEndMission(EndComplete);
        Assert.Equal("6ea3467c-83c8-4b41-b25c-ead2c558cc54", done!.MissionId);
        Assert.Equal(HaulOutcome.Complete, done.Outcome);
        Assert.Equal(HaulOutcome.Abandoned, HaulLogParser.ParseEndMission(EndAbandon)!.Outcome);
    }

    [Theory]
    [InlineData("HaulCargo_AToB_NonMetal_Carbon_Stanton2_SmallGrade", "1 to 1")]
    [InlineData("RedWind_Pyro_..._CargoHauling_AtoB_Intro", "1 to 1")]
    [InlineData("HaulCargo_SingleToMulti2_Processed_AgriculturalSupplies_Stanton2_SmallGrade", "1 to 2")]
    [InlineData("HaulCargo_SingleToMulti3_Processed_Stims_Stanton2_SmallGrade1", "1 to 3")]
    [InlineData("HaulCargo_Multi2ToSingle_Waste_Waste_Stanton1_SupplyGrade", "2 to 1")]
    [InlineData("Something_Unrecognized", "Unknown")]
    public void ParseTopology_FromContractName(string contract, string expected) =>
        Assert.Equal(expected, HaulLogParser.ParseTopology(contract));

    [Theory]
    [InlineData("RedWind_Hauling", "Red Wind")]
    [InlineData("Covalex_Hauling", "Covalex")]
    public void CompanyDisplay_KnownGenerators(string generator, string expected) =>
        Assert.Equal(expected, HaulLogParser.CompanyDisplay(generator));
}
