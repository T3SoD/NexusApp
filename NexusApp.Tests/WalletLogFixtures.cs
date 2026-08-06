namespace NexusApp.Tests;

// Shared real (redacted) wallet-trigger Game.log lines, verbatim from the OCR wallet spec's
// section 2 live differential test. PlayerId redacted per repo convention (never parsed).
// CIG's "vehicules" typo is verbatim and must stay.
public static class WalletLogFixtures
{
    // One line per mobiGlas open. Also fired by ASOP terminals (harmless false positive by
    // construction: the wallet region parses nothing there).
    public const string TriggerLine =
        "<2026-08-06T00:26:37.290Z> [Notice] <VehicleListQuery> Fetching vehicle list for player " +
        "REDACTED completed. Retrieved 0 entitlements out of 2 vehicules. " +
        "[Team_GameServices][ASOP][Entitlement][Insurance]";

    // The noisier twin that fires a triple at the same instants; the spec rules it out as the
    // trigger. Synthetic shape (subsystem name is real, body is representative, never parsed).
    public const string NoisyTwinLine =
        "<2026-08-06T00:26:37.290Z> [Notice] <OnRequestFetchVehicles> Requesting vehicle list " +
        "for player REDACTED [Team_GameServices][ASOP][Entitlement][Insurance]";

    // The inventory-open location signal, present in every session; must never trigger a burst.
    // Synthetic shape (subsystem name is real, body is representative, never parsed).
    public const string InventoryLine =
        "<2026-08-06T00:27:02.114Z> [Notice] <RequestLocationInventory> Requesting inventory " +
        "for location REDACTED [Team_CoreGameplayFeatures][Inventory]";

    // Contract completion HUD notification, verbatim shape from the 2026-08-06 contract recon
    // (mission names are game content, not player data). The trailing ': " [' closes the text.
    public const string ContractCompleteLine =
        "<2026-08-06T00:28:10.500Z> [Notice] <SHUDEvent_OnNotification> Added notification " +
        "\"Contract Complete: Rookie Rank - Direct Medium Cargo Haul: \" [100] to queue. " +
        "New queue size: 1, MissionId: [a3c22670-0a01-4f20-87ce-6e0d3ac098b8], ObjectiveId: [] " +
        "[Team_CoreGameplayFeatures][Missions][Comms]";

    // Same shape with the two recon hazards: a colon inside the display name and an <EM4> meta
    // span (rep/beacon tags) that must strip out whole.
    public const string ContractCompleteMarkupLine =
        "<2026-08-06T00:29:44.687Z> [Notice] <SHUDEvent_OnNotification> Added notification " +
        "\"Contract Complete: Jorrit Dossier: Project Hyperion <EM4>[150 Rep] [BP]*</EM4>: \" [62] to queue. " +
        "New queue size: 1, MissionId: [18790f40-6347-48e9-8976-665dc0637205], ObjectiveId: [] " +
        "[Team_CoreGameplayFeatures][Missions][Comms]";

    // An acceptance is not a completion; the double space after the colon is verbatim.
    public const string ContractAcceptedLine =
        "<2026-08-06T00:27:19.072Z> [Notice] <SHUDEvent_OnNotification> Added notification " +
        "\"Contract Accepted:  Orange Level Contract: Security Patrol <EM4>[150 Rep] [BP]*</EM4>: \" [50] to queue. " +
        "New queue size: 1, MissionId: [18790f40-6347-48e9-8976-665dc0637205], ObjectiveId: [] " +
        "[Team_CoreGameplayFeatures][Missions][Comms]";
}
