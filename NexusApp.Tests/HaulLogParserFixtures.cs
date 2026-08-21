namespace NexusApp.Tests;

// Shared real (redacted) current-build (12061511) Game.log lines for use across haul test classes.
public static class HaulLogParserFixtures
{
    public const string MarkerPickup =
        "<2026-06-27T13:17:10.856Z> [Notice] <CLocalMissionPhaseMarker::CreateMarker> Creating objective marker: " +
        "missionId [a6c598e0-e8d2-4613-a7a3-e80f4c30d9c6], generator name [RedWind_Hauling], " +
        "contract [RedWind_Pyro_SupplyGrade_RegionA_CFP_StationToTradepost_Carbon_CargoHauling_AtoB_Intro], " +
        "contractDefinitionId[db9d9b2d-d850-4df1-ab0f-7fa2261e8f62], " +
        "objectiveId [pickup_6e9335e8-f62e-4eca-8fc5-a42ab1c3ab7d_0], markerEntityId [2806], zoneHostId [525246124367], " +
        "position [x: 2281204.528538, y: -14897443.959268, z: 2752554.224023] [Team_MissionFeatures][Missions]";

    public const string MarkerDropoff =
        "<2026-06-27T13:17:10.856Z> [Notice] <CLocalMissionPhaseMarker::CreateMarker> Creating objective marker: " +
        "missionId [a6c598e0-e8d2-4613-a7a3-e80f4c30d9c6], generator name [RedWind_Hauling], " +
        "contract [RedWind_Pyro_SupplyGrade_RegionA_CFP_StationToTradepost_Carbon_CargoHauling_AtoB_Intro], " +
        "contractDefinitionId[db9d9b2d-d850-4df1-ab0f-7fa2261e8f62], " +
        "objectiveId [dropoff_6e9335e8-f62e-4eca-8fc5-a42ab1c3ab7d_0], markerEntityId [2807], zoneHostId [525246124540], " +
        "position [x: 383115.366423, y: -245829.717381, z: -272467.223889] [Team_MissionFeatures][Missions]";

    public const string DeliverLine =
        "<2026-06-27T13:17:10.862Z> [Notice] <SHUDEvent_OnNotification> Added notification " +
        "\"New Objective: Deliver 0/158 SCU of Carbon to Jackson's Swap: \" [6] to queue. New queue size: 7, " +
        "MissionId: [a6c598e0-e8d2-4613-a7a3-e80f4c30d9c6], ObjectiveId: [dropoff_6e9335e8-f62e-4eca-8fc5-a42ab1c3ab7d_0] " +
        "[Team_CoreGameplayFeatures][Missions][Comms]";

    public const string AcceptRouteLine =
        "<2026-06-13T13:31:38.068Z> [Notice] <SHUDEvent_OnNotification> Added notification " +
        "\"Contract Accepted:  Master | <EM3>DIRECT</EM3> Bulk Haul | Ruin Station > Baijini Point <EM4>[BP]*</EM4>: \" [16] to queue. " +
        "New queue size: 8, MissionId: [a6c598e0-e8d2-4613-a7a3-e80f4c30d9c6], ObjectiveId: [] [Team_CoreGameplayFeatures][Missions][Comms]";

    public const string ObjectiveCompletedLine =
        "<2026-06-27T13:42:44.088Z> [Notice] <ObjectiveUpserted> Received ObjectiveUpserted push message for: " +
        "mission_id a6c598e0-e8d2-4613-a7a3-e80f4c30d9c6 - objective_id pickup_6e9335e8-f62e-4eca-8fc5-a42ab1c3ab7d_0 " +
        "- state MISSION_OBJECTIVE_STATE_COMPLETED - created 0 - flags=ShowInLog| [Team_GameServices][Missions]";

    public const string ObjectiveInProgressLine =
        "<2026-06-27T13:42:44.088Z> [Notice] <ObjectiveUpserted> Received ObjectiveUpserted push message for: " +
        "mission_id a6c598e0-e8d2-4613-a7a3-e80f4c30d9c6 - objective_id dropoff_6e9335e8-f62e-4eca-8fc5-a42ab1c3ab7d_0 " +
        "- state MISSION_OBJECTIVE_STATE_INPROGRESS - created 1 - flags=ShowInLog| [Team_GameServices][Missions]";

    public const string EndComplete =
        "<2026-06-13T13:35:51.224Z> [Notice] <EndMission> Ending mission for player. " +
        "MissionId[6ea3467c-83c8-4b41-b25c-ead2c558cc54] Player[REDACTED] PlayerId[REDACTED] " +
        "CompletionType[Complete] Reason[Mission Ended] [Team_MissionFeatures][Missions]";

    // CFP / Citizens For Prosperity "Need a Hauler" RegionLink haul: a different family whose contract
    // ends in "_Hauling" (no "HaulCargo"/"CargoHauling"). Structurally a normal haul (pickup/dropoff markers).
    public const string CfpMarkerPickup =
        "<2026-06-27T21:49:56.557Z> [Notice] <CLocalMissionPhaseMarker::CreateMarker> Creating objective marker: " +
        "missionId [6b4e3396-1506-4051-b348-79c4eabae9d9], generator name [CitizensForProsperity_Generator], " +
        "contract [CFP_Pyro_RegionA_Stage1_RegionLink_ToRegionD_Hauling], " +
        "contractDefinitionId[adb9586c-4c26-465f-a6a2-bc97af16eedb], " +
        "objectiveId [pickup_0540719d-0ef2-4e2b-87d5-41ae5cffe412_0], markerEntityId [3322], zoneHostId [524846443194], " +
        "position [x: 383115.366423, y: -245829.717381, z: -272467.223889] [Team_MissionFeatures][Missions]";

    // Headhunters "Stock Taking" (HH_Hauling family): the contract token contains neither
    // "HaulCargo" nor "Hauling", so haul-ness must come from the datamined contract catalog.
    // Line synthesized from the real marker shape above with fresh ids; the generator and
    // contract names are verified against the datamined contract generator records.
    public const string HhPafMarkerDropoff =
        "<2026-08-18T10:02:31.101Z> [Notice] <CLocalMissionPhaseMarker::CreateMarker> Creating objective marker: " +
        "missionId [3d8a1c62-90f4-4f2e-9b1a-52a7c46d1c11], generator name [HeadHunters_Generator], " +
        "contract [HH_PAF_AtoB_Small_Slam_unproccessed], " +
        "contractDefinitionId[8fb0a7ce-2f34-4f8f-9a0f-6f1f0a5f2b77], " +
        "objectiveId [dropoff_9c2f16b4-8f34-4b0a-b8f6-1de2c66a9a10_0], markerEntityId [4101], zoneHostId [612345678901], " +
        "position [x: 12345.678901, y: -23456.789012, z: 3456.789012] [Team_MissionFeatures][Missions]";

    public const string EndAbandon =
        "<2026-06-27T13:30:27.252Z> [Notice] <EndMission> Ending mission for player. " +
        "MissionId[e179ea2b-3099-48a3-b0ef-6795bfb5337b] Player[REDACTED] PlayerId[REDACTED] " +
        "CompletionType[Abandon] Reason[Player left] [Team_MissionFeatures][Missions]";
}
