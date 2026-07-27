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
    // Shown in the Settings status area, under the last-refresh line: the cadence is a promise
    // about network activity, so it belongs where a reader checking on the feature is already
    // looking and not only in the toggle description (owner ruling 2026-07-27).
    public const string CadenceNote = "Checks about once an hour while Nexus is open.";
    public const string DossierFooter = "Prices: UEX community data";
    public const string NeverFetched = "No price data yet. Turn on live market prices and refresh.";

    // The Codex dossier's one worth section: live sell prices and the seed's refinery yield
    // modifiers merged under a single label, directly under the hero (amendment 2026-07-27 item
    // 6b, mock section 07A). It replaces the separate MARKET PRICES and REFINERY YIELDS sections,
    // so the reader answers "what is this ore worth and where do I take it" without scrolling.
    public const string ValueSection = "VALUE";
    public const string ValueDetailsShow = "+ details";
    public const string ValueDetailsHide = "Show less";
    // Right-hand label of the always-visible VALUE row: the seed's best refinery for this ore.
    // Seed data, so it renders whether or not live market data is on.
    public const string BestRefineryLabel = "Best refinery";

    // MARKET status pill in the top status strip (amendment 2026-07-27 item 6d, mock section 07B).
    // The pill is not rendered at all when the feature is off, so there is no "off" string here:
    // silence over placeholder, the same rule every price surface follows.
    public const string PillLabel = "MARKET";
    public const string PillOffline = "offline";   // a cycle failed: the dot also goes DangerColor
    public const string PillSyncing = "syncing";   // a first-ever cycle is running, no clock to show yet
    public const string PillNoData = "no data";    // enabled, nothing fetched yet, nothing failed yet
    public const string PillTooltip = "Live market data from UEX. Click to open Settings.";

    public static bool ShouldShowConsent(bool? enabled, bool isDemoProfile) => !isDemoProfile && enabled is null;

    public static string PatchTag(string gameVersion) => $"patch {gameVersion}";

    public static string FormatAge(TimeSpan age) =>
        age < TimeSpan.Zero || age.TotalMinutes < 1 ? "just now"
        : age.TotalHours < 1 ? $"{(int)age.TotalMinutes}m ago"
        : age.TotalDays < 1 ? $"{(int)age.TotalHours}h ago"
        : $"{(int)age.TotalDays}d ago";

    // Refined, not raw: UEX's raw ore-sales dataset has had no community reports since patch 4.8,
    // so every price surface quotes the refined counterpart (amendment 2026-07-27). The value
    // carries the game's currency unit, "aUEC/SCU", on every surface that renders a price
    // (owner ruling 2026-07-27 after live review).
    public static string DecoderLine(double weekAvg, string terminalName, string ageText) =>
        $"Sell (refined, avg): {weekAvg:n0} aUEC/SCU at {terminalName} ({ageText})";

    // The overlay scan card's compact twin of DecoderLine (amendment 2026-07-27 item 5). The card
    // is 452px wide and already carries a "Best refinery" line, so the label sheds the
    // "(refined, avg)" qualifier that the roomier app-window surfaces spell out. Same number and
    // the same never-a-bare-price rule: the caller passes age, or the patch tag when the row is
    // stale, exactly as it does for the decoder line.
    public static string OverlaySellLine(double display, string terminalName, string ageText) =>
        $"Sell: {display:n0} aUEC/SCU at {terminalName} ({ageText})";

    // The Codex dossier hero's own sell line (amendment 2026-07-27 item 6a): the same fact the
    // decoder hero states, in the dossier's voice. "Best sell" because the dossier's VALUE section
    // right below it lists the runners-up, where the decoder only ever shows one. Refined, like
    // every price in the app, and the caller passes age or the patch tag as ageText so the number
    // never renders bare.
    public static string DossierHeroLine(double display, string terminalName, string ageText) =>
        $"Best sell: {display:n0} aUEC/SCU at {terminalName} ({ageText})";

    // MARKET pill value, fresh and busy states: the local time of the last successful refresh.
    // HH:mm regardless of the app's 12/24 hour clock setting, matching StatusLine above (the two
    // read the same fact and must not disagree).
    public static string PillClock(DateTime local) => $"{local:HH:mm}";

    // MARKET pill value, stale state: how old the prices are, as text and not colour alone. Hours
    // up to two days, then days, so the pill never grows past four characters.
    public static string PillAge(TimeSpan age) =>
        age.TotalHours < 48 ? $"{(int)age.TotalHours}h" : $"{(int)age.TotalDays}d";

    public static string StatusLine(DateTime? lastFetchLocal, string? lastError) =>
        lastFetchLocal is not { } t ? "Never refreshed"
        : lastError is null ? $"Last refresh: {t:HH:mm}"
        : $"Last refresh: {t:HH:mm} ({lastError})";

    public static string SnapshotAgeNote(TimeSpan age) => $"data from {FormatAge(age)}";
}
