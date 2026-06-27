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

    /// <summary>Blueprint Network store (network.db) — imported members, their owned blueprints, and groups.</summary>
    public static NetworkStore Network { get; private set; } = null!;

    // BETA: cargo-hauling tracker (own Game.log watcher, decoupled from blueprint session).
    public static HaulTracker Hauls { get; private set; } = null!;

    // Server/shard tracker (own Game.log watcher, always on; rolling history persisted to settings).
    public static ShardTracker Shards { get; private set; } = null!;

    // Diagnostic-only: logs which process takes the OS foreground (for the mid-session tab-out reports).
    private static ForegroundMonitor? _foreground;

    // Reports the process's live DPI-awareness so nexus.log can confirm the shipped exe is actually
    // Per-Monitor V2 (issue #6) — DPI awareness is an embedded runtime property the CI compile can't verify.
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
                "Nexus — Unexpected Error", System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
            ex.Handled = true;
            Shutdown(1);
        };

        AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
        {
            Logger.Error("Unhandled non-UI exception", ex.ExceptionObject as Exception);
            System.Windows.MessageBox.Show(crashMessage,
                "Nexus — Unexpected Error", System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        };

        Logger.Info($"Nexus {AppInfo.Version} starting");
        Logger.Info($"Distribution: {AppInfo.Distribution}");
        Logger.Info($"[WIN] process DPI awareness: {DpiAwarenessLabel()}");
        RegisterInteractionLogging();
        _foreground = new ForegroundMonitor();
        _foreground.Start();

        // Pick the palette BEFORE the main window is created. StartupUri builds
        // MainWindow inside base.OnStartup, and its StaticResource theme brushes
        // resolve once at load time — so the palette must already be swapped or
        // those brushes freeze on the default (Luxury) gold even in Classic.
        // One-time migration of user data from the old %AppData%\Nexus_v4 folder
        // (pre-5.0.1) to the version-neutral %AppData%\NexusApp, so upgraders
        // keep their settings, work orders and history. Runs before anything reads.
        SettingsService.MigrateLegacyAppData();
        Settings = new SettingsService();
        // Establish the local Blueprint Network identity from first launch (not lazily at first
        // export), so the import self-skip can always recognise "you" and never double-count.
        Settings.EnsureLocalNetworkId();

        if (!Settings.Current.FirstRunComplete)
        {
            // First run: let the user pick a theme before MainWindow is built, so
            // the app opens directly in their choice with no restart. The picker is
            // the only window at this point, so it gets auto-assigned as MainWindow —
            // guard against OnMainWindowClose shutting us down when it closes, then
            // clear MainWindow so StartupUri reassigns the real window below.
            var prevShutdownMode = ShutdownMode;
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var picker = new Views.ThemePickerWindow();
            picker.ShowDialog();
            ThemeService.Apply(picker.SelectedTheme, save: true);

            MainWindow = null;
            ShutdownMode = prevShutdownMode;
        }
        else
        {
            ThemeService.Apply(Settings.Current.Theme, save: false);
        }

        base.OnStartup(e);
        Data = new DataService();
        Data.Initialize();

        // Blueprint Network store (separate db, survives the nexus.db reseed).
        Network = new NetworkStore();

        // BETA Game.log blueprint watch. Created after seed data loads (the importer needs
        // the blueprint name list). One instance app-wide, shared by the standalone monitor
        // window and the overlay STATS tab so they stay in sync. A live auto-mark pops a
        // toast and refreshes the Blueprint Library count; a past-logs import just refreshes.
        GameLog = new GameLogSession(
            () => Data.GetAllBlueprints().Select(b => b.Name),
            name => Settings.IsBlueprintOwned(name),
            (name, owned) => Settings.SetBlueprintOwned(name, owned),
            BuildLocalizationMap);
        GameLog.Marked += m =>
        {
            Views.ToastWindow.Show($"Marked owned: {m.Name}");
            (Current.MainWindow as Views.MainWindow)?.RefreshBlueprintOwnership();
        };
        GameLog.BulkOwnershipChanged += () =>
            (Current.MainWindow as Views.MainWindow)?.RefreshBlueprintOwnership();

        // Cache the RSI handle auto-detected from Game.log (read-only) so export can pre-fill it.
        GameLog.HandleDetected += handle => Settings.SetDetectedRsiHandle(handle);

        // Restore the saved Game.log path + Session Tracking toggles, then persist future changes
        // so the path and the watch / auto-collect selections survive a reboot.
        GameLog.PreferredPath = Settings.Current.GameLogPath;
        if (Settings.Current.GameLogAutoTrack) GameLog.SetAutoMark(true);            // also starts the watch
        else if (Settings.Current.GameLogTrackSession) GameLog.Start(GameLog.StartPath());
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
        Hauls = new HaulTracker { PreferredPath = Settings.Current.GameLogPath };
        Hauls.HaulEnded += h => Views.ToastWindow.Show($"Haul {h.Outcome}: {h.Company}");
        Hauls.Start(Hauls.StartPath(), fromBeginning: true);

        // Server/shard tracker. Own watcher (always on); persists the rolling list to settings so the
        // RECENT shards survive relaunches. Replays the current Game.log for this session's joins.
        Shards = new ShardTracker(
            () => Settings.Current.RecentShards,
            list => { Settings.Current.RecentShards = list.ToList(); Settings.Save(); })
        { PreferredPath = Settings.Current.GameLogPath };
        Shards.Start(Shards.StartPath(), fromBeginning: true);
    }

    // Builds the user's customDisplay -> official localization map from their global.ini, so the
    // importer can translate blueprint names renamed by any community localization mod. The file is
    // read-only and CIG-sanctioned community localization. Uses the saved override path if set, else
    // auto-detects next to the active Game.log. Null (and a no-op) when nothing is found.
    private IReadOnlyDictionary<string, string>? BuildLocalizationMap(string logPath)
    {
        var path = !string.IsNullOrWhiteSpace(Settings.Current.GlobalIniPath)
            ? Settings.Current.GlobalIniPath
            : GlobalIniReader.DeriveGlobalIniPath(logPath);
        if (string.IsNullOrEmpty(path)) { Logger.Info("[LOC] no global.ini path resolved"); return null; }

        var map = GlobalIniReader.TryBuildFromFile(path, ComponentStringReference.KeyToOfficialName);
        Logger.Info(map is null
            ? "[LOC] global.ini not found or unreadable"
            : $"[LOC] global.ini parsed: {map.Count} custom blueprint name(s)");
        return map;
    }

    // App-wide class handlers that log every Button click and nav (RadioButton) to nexus.log via
    // InteractionLog, so a diagnostic snapshot shows what the user was doing. Non-Button clickables
    // (overlay toggle switches, links, filter chips, the version badge) log themselves from their
    // own handlers. The 150ms scan tick is NOT logged here — it has its own sparse [SCAN]
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

    protected override void OnExit(ExitEventArgs e)
    {
        _foreground?.Dispose();
        Hauls?.Dispose();
        Shards?.Dispose();
        GameLog?.Dispose();
        Network?.Dispose();
        Data?.Dispose();
        base.OnExit(e);
    }
}
