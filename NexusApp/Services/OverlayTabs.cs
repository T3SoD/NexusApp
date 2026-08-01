namespace NexusApp.Services;

// Tab identity and restore rules for the overlay's tab strip. Kept out of the WPF view so the
// restore guard is headlessly testable, the same split SettingsTabs uses for the settings strip.
public static class OverlayTabs
{
    // "trade" shipped 2026-08-01 (app review: the overlay carried no trade information at all, on
    // the one surface that is actually on screen while you fly a route). It had been reserved here
    // with a label and a glyph since the tab strip was built, so turning it on is this one entry.
    public static readonly string[] Ids = ["stats", "scan", "orders", "shopping", "hauling", "guides", "trade"];

    public const string Default = "stats";

    // Restores only tabs that still exist. Unknown or legacy saved values (and a mis-cased id, since
    // these are case-sensitive keys) fall back to the default rather than leaving the overlay blank.
    public static string NormalizeForRestore(string? saved)
        => saved is not null && Array.IndexOf(Ids, saved) >= 0 ? saved : Default;

    // Display labels for the strip's pill and hover chips.
    public static string LabelFor(string id) => id switch
    {
        "stats" => "HUB",
        "scan" => "SCAN",
        "orders" => "REFINERY",
        "shopping" => "SHOPPING",
        "hauling" => "HAULING",
        "guides" => "GUIDES",
        "trade" => "TRADE",
        _ => id.ToUpperInvariant(),
    };

    // One log line per user-driven tab switch (App Log Monitor + diagnostic snapshot).
    public static string SwitchLogLine(string from, string to) => $"[WIN] Overlay tab: {from} -> {to}";
}
