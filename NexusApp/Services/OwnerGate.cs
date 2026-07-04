namespace NexusApp.Services;

// Gates owner-only dev tooling (the Grid Studio tab) to the app owner's Star Citizen account,
// identified by the RSI handle read from Game.log. This is NOT a security boundary - it just
// keeps the internal layout-authoring tools out of sight for anyone else who runs the app.
public static class OwnerGate
{
    private const string OwnerHandle = "TurboV1RG1N";

    public static bool IsOwner(string? handle) =>
        !string.IsNullOrWhiteSpace(handle) &&
        handle.Trim().Equals(OwnerHandle, StringComparison.OrdinalIgnoreCase);

    // The handle last detected from Game.log (cached in settings). Empty until a login line is
    // seen, so the gate stays closed by default.
    public static bool IsOwnerActive
    {
        get
        {
            try { return IsOwner(App.Settings?.Current?.DetectedRsiHandle); }
            catch { return false; }
        }
    }
}
