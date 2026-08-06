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

    // UEX /commodities_status codes (TradePriceRow.StatusBuy), approved wording (task-14
    // review finding 1). Code 0 or anything outside 1-7 means UEX has no status report for that
    // side of the terminal - rendered as a plain dash, not a fabricated state.
    internal static string BuyStatusLabel(int statusCode) => statusCode switch
    {
        1 => "OUT OF STOCK",
        2 => "VERY LOW",
        3 => "LOW",
        4 => "MEDIUM",
        5 => "HIGH",
        6 => "VERY HIGH",
        7 => "MAX INVENTORY",
        _ => "-",
    };
}
