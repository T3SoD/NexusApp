namespace NexusApp.Services;

// Visibility gate (NOT security) for the contributor-facing cargo tools. The OWNER (OwnerGate) and any
// BETA TESTER on the list below see the Cargo Planner and Grid Studio tabs and the Import-submission
// tool. The owner-only "Export to catalog patch" (promote a layout into the embedded catalog) stays on
// OwnerGate. The list is embedded and maintained by the owner, shipped with each release (the app is
// fully offline, no OTA), so onboarding a tester means adding a handle here and cutting a release.
public static class AccessGate
{
    // Beta-tester RSI handles (case-insensitive). Add one per line to onboard a tester: they get the
    // Cargo Planner + Grid Studio tabs and the Import tool, but NOT the catalog-patch button.
    private static readonly HashSet<string> BetaTesters = new(StringComparer.OrdinalIgnoreCase)
    {
        "Buck67",
        "CannonActual",
        "MangoMike",
        "RayKyuzo",
        "St-Myca",
        "Tenuis",
        "UnknownGhost",
        "CupOfKoffee_TV",
    };

    // Approved = the owner (single-sourced from OwnerGate) or a beta tester on the list above.
    public static bool IsApproved(string? handle) =>
        OwnerGate.IsOwner(handle) ||
        (!string.IsNullOrWhiteSpace(handle) && BetaTesters.Contains(handle.Trim()));

    // The handle last detected from Game.log (cached in settings). Empty until a login line is
    // seen, so the gate stays closed by default.
    public static bool IsApprovedActive
    {
        get
        {
            try { return IsApproved(App.Settings?.Current?.DetectedRsiHandle); }
            catch { return false; }
        }
    }
}
