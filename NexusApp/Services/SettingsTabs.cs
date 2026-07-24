namespace NexusApp.Services;

// Tab identity and restore rules for the settings page's HUD tab strip. Kept out of the WPF
// view so the guard logic is headlessly testable, mirroring how OverlayActiveTab is whitelist
// checked on restore in OverlayWindow.
public static class SettingsTabs
{
    public static readonly string[] Ids = ["game", "diagnostics", "interface", "data"];

    public const string Default = "game";

    // Restores only known, non-destructive tabs. Unknown or legacy values and the "data" tab
    // (Clear saved data lives there; a destructive surface must never be what greets the user)
    // both fall back to the default.
    public static string NormalizeForRestore(string? saved)
        => saved is "game" or "diagnostics" or "interface" ? saved : Default;
}
