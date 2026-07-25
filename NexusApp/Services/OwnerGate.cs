namespace NexusApp.Services;

// Gates owner-only dev tooling (the Grid Studio catalog-patch export and the Admin tab) to the
// app owner's Star Citizen account, identified by the RSI handle read from Game.log. This is
// NOT a security boundary - it just keeps internal tools out of sight for anyone else.
public static class OwnerGate
{
    // Public so the Admin tab's roster pane can display it (the value was always in source).
    public const string OwnerHandle = "TurboV1RG1N";

    public static bool IsOwner(string? handle) =>
        !string.IsNullOrWhiteSpace(handle) &&
        handle.Trim().Equals(OwnerHandle, StringComparison.OrdinalIgnoreCase);

    // Preview-aware: while the owner is previewing the app as a visitor or beta tester
    // (GatePreview), this reports false so owner-only UI hides for real. All pre-existing
    // call sites want exactly that.
    public static bool IsOwnerActive => !GatePreview.IsActive && IsOwnerReal;

    // Preview-BLIND owner check. The Admin nav tile is gated on this and only this, so the
    // owner can never preview themselves out of the way back (Exit preview lives on that tab).
    // The handle comes from Game.log via settings; empty until a login line is seen, so the
    // gate stays closed by default.
    public static bool IsOwnerReal
    {
        get
        {
            try { return IsOwner(App.Settings?.Current?.DetectedRsiHandle); }
            catch { return false; }
        }
    }
}
