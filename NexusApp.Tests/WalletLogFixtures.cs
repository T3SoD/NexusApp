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
}
