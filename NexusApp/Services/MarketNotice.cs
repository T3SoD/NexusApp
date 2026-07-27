namespace NexusApp.Services;

// All user-facing copy for the market data feature, pure and testable, mirroring UpdateNotice.
internal static class MarketNotice
{
    public const string ConsentEyebrow = "Live market data";
    public const string ConsentBody = "Nexus can show live sell prices from UEX, a community-run price database. This uses the internet while Nexus is open. You can change this anytime in Settings.";
    public const string ConsentEnable = "Turn on";
    public const string ConsentDecline = "Not now";
    public const string SettingsTitle = "MARKET DATA";
    public const string SettingsToggleTitle = "Show live market prices";
    public const string SettingsToggleDesc = "Sell prices for ores and refined goods from UEX community reports. Refreshed about once an hour while Nexus is open.";
    public const string RefreshNow = "Refresh now";
    public const string SourceNote = "Data: UEX community reports";
    public const string DossierFooter = "Prices: UEX community data";
    public const string DossierSection = "MARKET PRICES";
    public const string NeverFetched = "No price data yet. Turn on live market prices and refresh.";

    public static bool ShouldShowConsent(bool? enabled, bool isDemoProfile) => !isDemoProfile && enabled is null;

    public static string PatchTag(string gameVersion) => $"patch {gameVersion}";

    public static string FormatAge(TimeSpan age) =>
        age < TimeSpan.Zero || age.TotalMinutes < 1 ? "just now"
        : age.TotalHours < 1 ? $"{(int)age.TotalMinutes}m ago"
        : age.TotalDays < 1 ? $"{(int)age.TotalHours}h ago"
        : $"{(int)age.TotalDays}d ago";

    // Refined, not raw: UEX's raw ore-sales dataset has had no community reports since patch 4.8,
    // so every price surface quotes the refined counterpart (amendment 2026-07-27).
    public static string DecoderLine(double weekAvg, string terminalName, string ageText) =>
        $"Sell (refined, avg): {weekAvg:n0}/SCU at {terminalName} ({ageText})";

    // The overlay scan card's compact twin of DecoderLine (amendment 2026-07-27 item 5). The card
    // is 452px wide and already carries a "Best refinery" line, so the label sheds the
    // "(refined, avg)" qualifier that the roomier app-window surfaces spell out. Same number and
    // the same never-a-bare-price rule: the caller passes age, or the patch tag when the row is
    // stale, exactly as it does for the decoder line.
    public static string OverlaySellLine(double display, string terminalName, string ageText) =>
        $"Sell: {display:n0}/SCU at {terminalName} ({ageText})";

    public static string StatusLine(DateTime? lastFetchLocal, string? lastError) =>
        lastFetchLocal is not { } t ? "Never refreshed"
        : lastError is null ? $"Last refresh: {t:HH:mm}"
        : $"Last refresh: {t:HH:mm} ({lastError})";

    public static string SnapshotAgeNote(TimeSpan age) => $"data from {FormatAge(age)}";
}
