namespace NexusApp.Services;

// Tab identity and restore rules for the overlay's tab strip. Kept out of the WPF view so the
// restore guard is headlessly testable, the same split SettingsTabs uses for the settings strip.
public static class OverlayTabs
{
    public static readonly string[] Ids = ["stats", "scan", "orders", "shopping", "hauling", "guides"];

    public const string Default = "stats";

    // Restores only tabs that still exist. Unknown or legacy saved values (and a mis-cased id, since
    // these are case-sensitive keys) fall back to the default rather than leaving the overlay blank.
    public static string NormalizeForRestore(string? saved)
        => saved is not null && Array.IndexOf(Ids, saved) >= 0 ? saved : Default;
}
