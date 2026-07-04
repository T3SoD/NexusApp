namespace NexusApp.Services;

// Visibility gate (NOT security) for the contributor-facing cargo tools. Approved RSI handles
// see the Cargo Planner and Grid Studio tabs; everyone else does not. The list is embedded and
// maintained by the owner, shipped with each release (the app is fully offline, no OTA). The
// owner handle is always a member. Import/Compare stays on OwnerGate (owner only).
public static class AccessGate
{
    // Approved contributor handles (owner included). Add a handle here and cut a release to
    // onboard a contributor. Case-insensitive.
    private static readonly HashSet<string> ApprovedHandles = new(StringComparer.OrdinalIgnoreCase)
    {
        "TurboV1RG1N",
    };

    public static bool IsApproved(string? handle) =>
        !string.IsNullOrWhiteSpace(handle) && ApprovedHandles.Contains(handle.Trim());

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
