using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using NexusApp.Services;

namespace NexusApp;

public partial class App : Application
{
    public static DataService Data { get; private set; } = null!;
    public static SettingsService Settings { get; private set; } = null!;

    // BETA: shared Game.log blueprint-watch session (standalone window + overlay STATS tab).
    public static GameLogSession GameLog { get; private set; } = null!;

    /// <summary>Blueprint Network store (network.db) - imported members, their owned blueprints, and groups.</summary>
    public static NetworkStore Network { get; private set; } = null!;

    // BETA: cargo-hauling tracker (own Game.log watcher, decoupled from blueprint session).
    public static HaulTracker Hauls { get; private set; } = null!;

    // Server/shard tracker (own Game.log watcher, always on; rolling history persisted to settings).
    public static ShardTracker Shards { get; private set; } = null!;

    // Contract OCR scanner: reads the in-game Contracts panel and enriches the active haul record.
    public static ContractOcrService ContractOcr { get; private set; } = null!;
    public static ContractScanner ContractScan { get; private set; } = null!;

    // Diagnostic-only: logs which process takes the OS foreground (for the mid-session tab-out reports).
    private static ForegroundMonitor? _foreground;

    // True while Nexus or Star Citizen holds the OS foreground; false means OCR auto-scans are paused.
    // Status indicators read this (and subscribe to the event) to show the paused (yellow) state.
    public static bool IsForegroundRelevant => _foreground?.IsRelevantForeground ?? true;
    public static event System.Action<bool>? ForegroundRelevanceChanged;

    // Single source of truth for the yellow contract-detection box visibility, so the main Cargo Hauling
    // page, the overlay HAULING tab, and the MainWindow indicator all agree. Writers call
    // SetContractBoxVisible; readers subscribe to ContractBoxVisibilityChanged + re-sync their toggle.
    public static bool ContractBoxVisible { get; private set; }
    public static event System.Action<bool>? ContractBoxVisibilityChanged;
    public static void SetContractBoxVisible(bool on)
    {
        if (ContractBoxVisible == on) return;
        ContractBoxVisible = on;
        ContractBoxVisibilityChanged?.Invoke(on);
    }

    // Reports the process's live DPI-awareness so nexus.log can confirm the shipped exe is actually
    // Per-Monitor V2 (issue #6) - DPI awareness is an embedded runtime property the CI compile can't verify.
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] private static extern bool AreDpiAwarenessContextsEqual(IntPtr a, IntPtr b);
    private static readonly IntPtr DPI_PMV2   = (IntPtr)(-4);
    private static readonly IntPtr DPI_PM     = (IntPtr)(-3);
    private static readonly IntPtr DPI_SYSTEM = (IntPtr)(-2);
    private static string DpiAwarenessLabel()
    {
        try
        {
            var ctx = GetThreadDpiAwarenessContext();
            if (AreDpiAwarenessContextsEqual(ctx, DPI_PMV2))   return "Per-Monitor V2";
            if (AreDpiAwarenessContextsEqual(ctx, DPI_PM))     return "Per-Monitor";
            if (AreDpiAwarenessContextsEqual(ctx, DPI_SYSTEM)) return "System";
            return "Unaware/Unknown";
        }
        catch { return "unknown"; }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        const string crashMessage =
            "Nexus hit an unexpected error and needs to close.\n\n" +
            "Details have been saved to the log file at:\n%AppData%\\NexusApp\\logs\\nexus.log\n\n" +
            "To help fix this: reopen Nexus, go to Settings (cog) → Diagnostics, click \"Save snapshot\", " +
            "and send the file to T3SoD on Discord, or attach it to a GitHub issue:\n" +
            "https://github.com/T3SoD/NexusApp/issues";

        DispatcherUnhandledException += (s, ex) =>
        {
            Logger.Error("Unhandled UI exception", ex.Exception);
            System.Windows.MessageBox.Show(crashMessage,
                "Nexus - Unexpected Error", System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
            ex.Handled = true;
            Shutdown(1);
        };

        AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
        {
            Logger.Error("Unhandled non-UI exception", ex.ExceptionObject as Exception);
            System.Windows.MessageBox.Show(crashMessage,
                "Nexus - Unexpected Error", System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        };

        Logger.Info($"Nexus {AppInfo.Version} starting");
        Logger.Info($"Distribution: {AppInfo.Distribution}");
        Logger.Info($"[WIN] process DPI awareness: {DpiAwarenessLabel()}");
        RegisterInteractionLogging();
        _foreground = new ForegroundMonitor();
        _foreground.RelevanceChanged += OnForegroundRelevanceChanged;
        _foreground.Start();

        // One-time migration of user data from the old %AppData%\Nexus_v4 folder
        // (pre-5.0.1) to the version-neutral %AppData%\NexusApp, so upgraders
        // keep their settings, work orders and history. Runs before anything reads.
        SettingsService.MigrateLegacyAppData();
        Settings = new SettingsService();
        NexusApp.Views.Motion.Reduced = Settings.Current.ReduceAnimations;
        // Establish the local Blueprint Network identity from first launch (not lazily at first
        // export), so the import self-skip can always recognise "you" and never double-count.
        Settings.EnsureLocalNetworkId();

        // Single theme: the palette is merged statically in App.xaml
        // (Themes/Palette.Luxury.xaml); there is no picker and no runtime switch.

        base.OnStartup(e);
        Data = new DataService();
        Data.Initialize();

        // Blueprint Network store (separate db, survives the nexus.db reseed).
        Network = new NetworkStore();

        // BETA Game.log blueprint watch. Created after seed data loads (the importer needs
        // the blueprint name list). One instance app-wide, shared by the standalone monitor
        // window and the overlay STATS tab so they stay in sync. Auto-marks refresh the
        // Blueprint Library count quietly - no popups by design (toasts removed app-wide);
        // the overlay HUB's count and Collection Log carry the feedback.
        GameLog = new GameLogSession(
            () => Data.GetAllBlueprints().Select(b => b.Name),
            name => Settings.IsBlueprintOwned(name),
            (name, owned) => Settings.SetBlueprintOwned(name, owned),
            BuildLocalizationMap);
        GameLog.Marked += m =>
            (Current.MainWindow as Views.MainWindow)?.RefreshBlueprintOwnership();
        GameLog.BulkOwnershipChanged += () =>
            (Current.MainWindow as Views.MainWindow)?.RefreshBlueprintOwnership();

        // Cache the RSI handle auto-detected from Game.log (read-only) so export can pre-fill it.
        GameLog.HandleDetected += handle => Settings.SetDetectedRsiHandle(handle);

        // Session Tracking + Auto-Track Blueprints are ALWAYS ON; there is no user toggle. Restore the
        // saved Game.log path, then start the watch and auto-collect unconditionally on every launch.
        // SetAutoMark(true) both enables auto-collect and starts the watcher (probing common installs if
        // no path is saved yet). The path is still persisted below when the watcher resolves it.
        GameLog.PreferredPath = Settings.Current.GameLogPath;
        GameLog.SetAutoMark(true);
        Logger.Info($"[GameLog] session tracking + auto-track always on; watching: {(string.IsNullOrEmpty(GameLog.Path) ? "<no log found yet>" : GameLog.Path)}");
        GameLog.StateChanged += () =>
        {
            Settings.Current.GameLogTrackSession = GameLog.IsRunning;
            Settings.Current.GameLogAutoTrack = GameLog.AutoMark;
            if (!string.IsNullOrEmpty(GameLog.Path)) Settings.Current.GameLogPath = GameLog.Path;
            Settings.Save();
        };

        // BETA Game.log cargo-hauling tracker. Own watcher (decoupled from the blueprint
        // session so haul tracking runs regardless of the blueprint toggle). Replays the
        // current Game.log from the top so a mid-session restart rebuilds active hauls.
        // No toast on HaulEnded: the startup replay re-raises last session's ended hauls,
        // which popped stale "Haul Abandoned" toasts on every launch. The Hauling tab and
        // [HAUL] log lines carry the outcome instead.
        Hauls = new HaulTracker { PreferredPath = Settings.Current.GameLogPath };
        Hauls.Start(Hauls.StartPath(), fromBeginning: true);

        // Server/shard tracker. Own watcher (always on); persists the rolling list to settings so the
        // RECENT shards survive relaunches. Replays the current Game.log for this session's joins.
        Shards = new ShardTracker(
            () => Settings.Current.RecentShards,
            list => { Settings.Current.RecentShards = list.ToList(); Settings.Save(); })
        { PreferredPath = Settings.Current.GameLogPath };
        Shards.Start(Shards.StartPath(), fromBeginning: true);

        // Contract OCR: scans the in-game Contracts panel and enriches the matching haul.
        ContractOcr = new ContractOcrService();
        if (Settings.Current.ContractRegion is { } cr) ContractOcr.SetRegion(cr.X, cr.Y, cr.Width, cr.Height);
        ContractScan = new ContractScanner(ContractOcr);
        // ContractScanner runs on a System.Timers.Timer thread; ApplyContractDetails raises Changed which
        // the overlay handles by touching WPF, so marshal onto the UI thread.
        ContractScan.ContractScanned += d => Current.Dispatcher.Invoke(() => Hauls.ApplyContractDetails(d));
        // When an OCR scan first pairs with a log-detected haul, confirm it with a green flash of
        // the yellow contract box (mirrors the RS scan-success flash). No popup - toasts are
        // removed app-wide by design.
        Hauls.ContractPaired += h => Current.Dispatcher.Invoke(() =>
            (Current.MainWindow as Views.MainWindow)?.FlashContractIndicator());
        if (Settings.Current.AutoScanContracts) ContractScan.Start();
    }

    // Builds the user's customDisplay -> official localization map from their global.ini, so the
    // importer can translate blueprint names renamed by any community localization mod. The file is
    // read-only and CIG-sanctioned community localization. Uses the saved override path if set, else
    // auto-detects next to the active Game.log. Null (and a no-op) when nothing is found.
    private IReadOnlyDictionary<string, string>? BuildLocalizationMap(string logPath)
    {
        bool overridden = !string.IsNullOrWhiteSpace(Settings.Current.GlobalIniPath);
        var path = overridden
            ? Settings.Current.GlobalIniPath
            : GlobalIniReader.DeriveGlobalIniPath(logPath);
        if (string.IsNullOrEmpty(path)) { Logger.Info("[LOC] no global.ini path resolved"); return null; }

        // The path itself is never logged (it can contain a Windows username), but its ORIGIN is -
        // a stale Settings override vs a bad derivation need different fixes (issue #17 diagnostics).
        var origin = overridden ? "Settings override" : "derived from Game.log path";
        var map = GlobalIniReader.TryBuildFromFile(path, ComponentStringReference.KeyToOfficialName);
        Logger.Info(map is null
            ? $"[LOC] global.ini not found or unreadable ({origin})"
            : $"[LOC] global.ini parsed: {map.Count} custom blueprint name(s) ({origin})");
        return map;
    }

    // App-wide class handlers that log every Button click and nav (RadioButton) to nexus.log via
    // InteractionLog, so a diagnostic snapshot shows what the user was doing. Non-Button clickables
    // (overlay toggle switches, links, filter chips, the version badge) log themselves from their
    // own handlers. The 150ms scan tick is NOT logged here - it has its own sparse [SCAN]
    // breadcrumbs (start/stop + a 10s heartbeat) in ScannerService.
    private static void RegisterInteractionLogging()
    {
        EventManager.RegisterClassHandler(typeof(Button), ButtonBase.ClickEvent,
            new RoutedEventHandler((s, _) =>
            {
                if (s is Button b)
                    InteractionLog.Click(!string.IsNullOrEmpty(b.Name) ? b.Name : b.Content?.ToString(), b);
            }));

        EventManager.RegisterClassHandler(typeof(RadioButton), ToggleButton.CheckedEvent,
            new RoutedEventHandler((s, _) =>
            {
                if (s is RadioButton rb)
                    InteractionLog.Nav((rb.Tag as string) ?? rb.Content?.ToString(), rb);
            }));
    }

    // Pause RS + contract OCR auto-scans while neither Nexus nor Star Citizen is in front (saves CPU and
    // avoids scanning whatever app the user tabbed to); resume them when one returns to the foreground.
    private static void OnForegroundRelevanceChanged(bool relevant)
    {
        Logger.Info($"[FG] auto-scans {(relevant ? "resumed" : "paused")} (Nexus/Star Citizen {(relevant ? "in front" : "in background")})");

        (Current.MainWindow as Views.MainWindow)?.SetScanForegroundActive(relevant);   // RS scanner

        // Contract scanner: the persisted toggle is the user's intent; the gate only suspends/restores it.
        if (ContractScan is not null)
        {
            if (relevant) { if (Settings.Current.AutoScanContracts && !ContractScan.IsRunning) ContractScan.Start(); }
            else if (ContractScan.IsRunning) ContractScan.Stop();
        }

        // Notify status indicators (HUB LEDs, header SCAN chip) so they flip to/from the paused state.
        ForegroundRelevanceChanged?.Invoke(relevant);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _foreground?.Dispose();
        ContractScan?.Dispose();
        ContractOcr?.Dispose();
        Hauls?.Dispose();
        Shards?.Dispose();
        GameLog?.Dispose();
        Network?.Dispose();
        Data?.Dispose();
        base.OnExit(e);
    }
}
