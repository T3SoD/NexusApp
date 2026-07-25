namespace NexusApp.Services;

// Tab identity and restore rules for the owner Admin page's HUD tab strip, mirroring
// SettingsTabs so the guard logic stays headlessly testable.
public static class AdminTabs
{
    public static readonly string[] Ids = ["roster", "diagnostics", "tools"];

    public const string Default = "roster";

    // Restores only known tabs; unknown or legacy values fall back to the default. No admin
    // tab is destructive (demo mode never touches the live profile), so all three restore.
    public static string NormalizeForRestore(string? saved)
        => saved is "roster" or "diagnostics" or "tools" ? saved : Default;
}
