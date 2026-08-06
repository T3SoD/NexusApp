namespace NexusApp.Models;

public class AppSettings
{
    public int SettingsSchemaVersion { get; set; } = 0;
    public double WindowLeft { get; set; } = 100;
    public double WindowTop { get; set; } = 100;
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 820;
    public double OverlayLeft { get; set; } = 20;
    public double OverlayTop { get; set; } = 20;
    public double OverlayWidth { get; set; } = 320;
    public double OverlayHeight { get; set; } = 480;
    public ScanRegion? ScanRegion { get; set; }
    public double OverlayOpacity { get; set; } = 1.0;

    // UI scale factors (issue #20). 1.0 = standard size. AppUiScale drives the main window and
    // every dialog opened from it; OverlayUiScale drives the in-game overlay and its work-order
    // flyout. Range is enforced by UiScaleService.ClampScale on read, not here.
    public double AppUiScale { get; set; } = 1.0;
    public double OverlayUiScale { get; set; } = 1.0;

    public List<string> PinnedResources { get; set; } = [];
    public List<string> OwnedBlueprints { get; set; } = [];
    // Trade routes pinned in the planner, persisted 2026-08-01 to match refinery orders. They
    // shipped session-only that morning; a run outlives a session, so that was wrong. Order is pin
    // order, oldest first, and it is load-bearing - the overlay lists cards in it and the cap drops
    // the oldest. See Models/PinnedRoute.cs for what is stored and what deliberately is not.
    public List<PinnedRoute> PinnedRoutes { get; set; } = [];
    public bool FirstRunComplete { get; set; }

    // BETA Session Tracking - remember whether the watch / auto-collect were on, so they
    // resume on next launch, plus the Game.log path the user picked (so a custom location
    // isn't lost on restart - it was reverting to the default C: path).
    public bool GameLogTrackSession { get; set; }
    public bool GameLogAutoTrack { get; set; }
    public string GameLogPath { get; set; } = "";

    // Optional path to Star Citizen's localization file (Data/Localization/english/global.ini).
    // Read-only; lets the importer translate blueprint names renamed by community localization mods
    // (any custom format) back to seed names. "" = auto-detect next to the Game.log path.
    public string GlobalIniPath { get; set; } = "";

    // Blueprint Network - local identity only. The shared roster (other people's libraries)
    // lives in network.db; these few fields are just "who you are" when you export/share.
    public string LocalNetworkId { get; set; } = "";          // stable GUID, generated once
    public string LocalDisplayName { get; set; } = "";        // the label other users see
    public string LocalIdentityKind { get; set; } = "handle"; // "handle" | "nickname"
    public string DetectedRsiHandle { get; set; } = "";       // auto-detected from Game.log (export default)

    // Server/shard display: rolling current + last 3 shards (most recent first), persisted so the
    // RECENT list survives app/SC relaunches. Populated from Game.log <Join PU> lines.
    public List<ShardSession> RecentShards { get; set; } = [];

    // Cargo contract OCR: screen region the ContractScanner reads and whether it starts automatically.
    // ContractRegion mirrors ScanRegion (same pixel-coordinate struct); null = not yet set by user.
    public ScanRegion? ContractRegion { get; set; }
    public bool AutoScanContracts { get; set; }

    // Wallet OCR: screen region where the mobiGlas renders the aUEC balance, and whether the
    // trigger-driven capture runs at all. WalletRegion mirrors ScanRegion; null = not yet set,
    // which keeps the whole feature inert regardless of the toggle.
    public ScanRegion? WalletRegion { get; set; }
    public bool WalletOcrEnabled { get; set; } = true;

    // Accessibility/comfort: when on, the app minimizes motion (skips page transitions,
    // dock/HUD pulses, count-ups, switch slides and the ambient panel glyphs). Default off.
    public bool ReduceAnimations { get; set; }

    // Top-bar clock format: true = 24-hour (HH:mm:ss), false = 12-hour with AM/PM. Default 24-hour.
    public bool Clock24Hour { get; set; } = true;

    // Compatibility: render Nexus on the CPU (RenderMode.SoftwareOnly) instead of the GPU, for
    // machines whose game/driver crashes keep killing WPF's render thread (0x88980406). Applied
    // once at startup - toggling takes effect on the next launch. Default off.
    public bool SoftwareRendering { get; set; }

    // Render-crash recovery: the UTC instant CrashGuard last auto-relaunched Nexus after Windows
    // reported a display error (0x88980406), surfaced in Settings > Diagnostics ("Last automatic
    // restart"). Stored UTC; the row localizes it. Null until the first auto-relaunch ever happens.
    public DateTime? LastAutoRelaunchUtc { get; set; }

    // Overlay input (issue #7): when on, the overlay passes the mouse straight through (click-through)
    // while the game hides the OS cursor (FPS / flight), so a stray click can't land on it or steal
    // focus. It becomes interactive again the moment the cursor is shown. Default on.
    public bool OverlayPassThroughWhenCursorHidden { get; set; } = true;

    // Overlay UI state: which tab was last on screen, and how tall the RECENT scan-history strip was
    // dragged to, so both survive a hide/close and come back on next launch/show.
    public string OverlayActiveTab { get; set; } = "stats";
    public double OverlayHistoryHeight { get; set; } = 120;

    // Ghost mode (issue #27): overlay collapses to the 44px icon rail. Panel/flyout open
    // state is deliberately NOT persisted; ghost always wakes collapsed.
    public bool OverlayGhostMode { get; set; }

    // Ghost rail size, independent of OverlayUiScale so the rail can shrink below 1.0 while the
    // panel stays readable. Clamped to [UiScaleService.RailMin, UiScaleService.Max] on read.
    public double OverlayGhostRailScale { get; set; } = 1.0;

    // Settings page UI state: which settings tab (game / diagnostics / updates / interface / data)
    // was last active, so the page reopens where the user left off. The destructive "data" tab is
    // deliberately never restored; SettingsTabs.NormalizeForRestore maps it back to the default.
    public string SettingsActiveTab { get; set; } = "game";

    // Admin page UI state (owner-only tab): which admin tab (roster / diagnostics / tools)
    // was last active. Restore is whitelist-guarded by AdminTabs.NormalizeForRestore.
    public string AdminActiveTab { get; set; } = "roster";

    // Auto-update consent: null = the one-time opt-in strip has not been answered yet (and no
    // network call ever happens), true/false = the user's standing choice, changeable anytime
    // in Settings > Updates. With consent on, the check runs on every launch (the 24h throttle
    // died 2026-08-01).
    public bool? UpdateCheckEnabled { get; set; }

    // UTC instant of the last completed update check (success or failure), driving the
    // "Last checked" row in Settings > Updates. Null until the first check ever.
    public DateTime? LastUpdateCheckUtc { get; set; }

    // Live market data consent: null = the one-time strip has not been answered, true/false =
    // the standing choice, changeable anytime in Settings. Off or unanswered means the market
    // service never touches the network.
    public bool? MarketDataEnabled { get; set; }

    // UTC instant of the last completed market fetch cycle (success or failure). Null until
    // the first cycle ever runs. Drives the Settings status line.
    public DateTime? LastMarketFetchUtc { get; set; }

    // Sell prices as an extra column in the Mining Codex list, off by default: the list is a
    // reference table first, so prices are opt-in per reader. A plain bool and not a tri-state
    // because there is no consent question here - MarketDataEnabled already gates the feature,
    // and this only chooses whether the list shows what the dossier already has.
    public bool CodexSellColumn { get; set; }

    // The app version that ran last session, updated at every startup. A jump upward triggers
    // the one-time "Nexus updated to vX.Y.Z" strip on Operations. Null on fresh installs.
    public string? LastSeenVersion { get; set; }

    // Issue #26: user-observed Executive Hangar open instant, replacing the embedded calibration
    // anchor (set from the Guides page re-anchor control; null = built-in calibration).
    public DateTime? ExecHangarAnchorOverrideUtc { get; set; }

    // Issue #28: blueprint recording authorization for a CUSTOM (unrecognized) Game.log folder.
    // Known channels never consult this: LIVE/HOTFIX always record, PTU/EPTU/TECH-PREVIEW never do.
    // Default false - a non-standard install must opt in explicitly in Settings > Game.
    public bool CustomChannelRecordsBlueprints { get; set; }

    // Issue #28: the custom-folder path the one-time Operations notice was last dismissed for,
    // so the notice shows once per DISTINCT custom path instead of once ever or every launch.
    public string CustomChannelNoticePath { get; set; } = "";

    // Trading tab (2026-07-29): which flow (planner/sell/prices) was last open, so the tab
    // reopens where the user left it.
    public string TradeActiveFlow { get; set; } = "planner";

    // The cargo ship catalog id (ShipCargoDef.Id) last selected in the route planner. "" = no
    // selection yet.
    public string TradeShipId { get; set; } = "";

    // Manual origin override (a terminal identity string) for when no live Game.log session is
    // running, or the player wants to plan from somewhere other than where they stand. "" = none
    // set; the planner falls back to whatever the live-location facility reports, if anything.
    // DORMANT since task 10 (2026-07-31): the shared ORIGIN chip's manual dropdown that used to
    // read/write this is gone (the chip is display-only now). Kept, unused, rather than deleted -
    // removing a persisted settings field is a bigger, unrelated cleanup.
    public string TradeOriginManual { get; set; } = "";

    // Route planner STARTING LOCATION picker (task 10, replaces the FROM HERE/ANYWHERE anchor
    // pills entirely): "ANY" (no constraint, old ANYWHERE), "LIVE" (the live-session location, old
    // FROM HERE's live half), or a terminal name (old FROM HERE's manual-pick half, now scoped to
    // just this one planner-local picker instead of the shared ORIGIN chip). Default "LIVE"
    // preserves the old FROM HERE default behavior. TradeOriginResolver.StartTerminalIds is the
    // pure seam that resolves this to RoutePlanner's terminal id set.
    public string? TradeStartManual { get; set; } = "LIVE";

    // Route planner DESTINATION picker (task 6): a terminal name restricting sell legs, mirroring
    // TradeOriginManual's persistence but for the sell side. Null (or "") = ANY, the planner's
    // original unrestricted behavior - unlike TradeOriginManual this has no "falls back to live
    // location" concept, so null is the honest default rather than an empty string sentinel.
    public string? TradeDestManual { get; set; }

    // Route planner COMMODITY picker (issue #41): a commodity name restricting routes to that one
    // commodity, same persistence contract as TradeDestManual. Null (or "") = ANY, the planner's
    // original unconstrained behavior. Revalidated against the live snapshot's commodity list on
    // every rebuild; a stale name falls back to ANY.
    public string? TradeCommodityFilter { get; set; }

    // MAP tab: the star system the user was last looking at. Null = never set, so the map opens on
    // its built-in default. Validated against the catalog on load - a system that no longer exists
    // (a renamed or removed one) falls back to the default rather than opening on nothing.
    public string? MapSystem { get; set; }

    // MAP tab: which data layers are switched on, as a comma-separated list of layer keys
    // ("trade,mining"). One setting rather than five booleans because the set is small, the keys
    // are already the vocabulary the page and the scene both speak, and it stays readable in
    // settings.json. NULL IS MEANINGFUL and distinct from empty: null = never saved, so first-run
    // defaults apply; "" = the user deliberately switched everything off, which must survive a
    // restart. See MapPage.ParseLayers.
    public string? MapLayers { get; set; }

    // System-name scope filter pill (ALL / a specific star system name). Default ALL.
    public string TradeScope { get; set; } = "ALL";

    // Route planner DEMAND AT DESTINATION coverage filter pill (task 5; resemantic task 10:
    // ANY / MIN FOR TRIP / 2X FOR TRIP, demand-only - the buy leg's stock is no longer
    // independently checked, since TradeMath.TripQty already caps tripQty at it). ANY applies no
    // filter (default, byte-identical to the planner's original behavior); MIN requires the sell
    // leg to carry at least one full trip's worth of demand; 2X requires two trips' worth. Persisted
    // as the short "ANY"/"MIN"/"2X" values (distinct from the longer pill display text);
    // TradePage.Planner.cs's ParseDemandFilter fail-opens the pre-task-10 "COVERS TRIP"/"COVERS 2X"
    // strings to the same tiers.
    public string TradeStockFilter { get; set; } = "ANY";

    // Route planner rank mode pill (PROFIT / PROFIT PER SCU, task 7). PROFIT (default) orders by
    // raw net/trip, byte-identical to the planner's original ordering; PROFIT PER SCU re-ranks by
    // net/tripQty, surfacing high-margin small-qty routes over high-net bulk ones. Stored as the
    // exact pill label, same convention as TradeStockFilter.
    public string TradeRankMode { get; set; } = "PROFIT";

    // FROM HERE (true, default) restricts the route planner's buy legs to the current origin;
    // ANYWHERE (false) lifts that restriction. Same ranking math either way.
    // DORMANT since task 10 (2026-07-31): the route planner's Starting Location picker
    // (TradeStartManual, above) replaced the FROM HERE/ANYWHERE anchor pills entirely. Kept,
    // unused, rather than deleted - removing a persisted settings field is a bigger, unrelated
    // cleanup.
    public bool TradeAnchorFromHere { get; set; } = true;

    // NOTE: SctDataEnabled was removed 2026-08-03. Live market data is one yes/no covering both
    // UEX and SC Trade Tools (one combined toggle, all or nothing), so
    // MarketDataEnabled above gates both feeds. A stale SctDataEnabled key left in an existing
    // settings.json is simply ignored on load, which is the intended migration: anyone who had
    // market data on gets the SCT cross-check with it.
}

public class ScanRegion
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}
