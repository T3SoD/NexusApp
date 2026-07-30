namespace NexusApp.Services;

// TRADE page sub-tab persistence (AppSettings.TradeActiveFlow), same shape as SettingsTabs.cs /
// AdminTabs.cs / OverlayTabs.cs - one small file per page with its own tab strip, not a shared
// base class (house convention).
public static class TradeFlows
{
    public static readonly string[] Ids = { "planner", "sell", "prices" };
    public const string Default = "planner";

    public static string NormalizeForRestore(string? saved)
        => saved is "planner" or "sell" or "prices" ? saved : Default;
}
