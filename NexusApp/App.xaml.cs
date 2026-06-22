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

    // Diagnostic-only: logs which process takes the OS foreground (for the mid-session tab-out reports).
    private static ForegroundMonitor? _foreground;

    protected override void OnStartup(StartupEventArgs e)
    {
        const string crashMessage =
            "Nexus hit an unexpected error and needs to close.\n\n" +
            "Details have been saved to the log file at:\n%AppData%\\NexusApp\\logs\\nexus.log\n\n" +
            "To help fix this: reopen Nexus, go to Settings (cog) → Diagnostics, click \"Save snapshot\", " +
            "and send the file to T3SoD on Discord.";

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

        // BETA Game.log blueprint watch. Created after seed data loads (the importer needs
        // the blueprint name list). One instance app-wide, shared by the standalone monitor
        // window and the overlay STATS tab so they stay in sync. A live auto-mark pops a
        // toast and refreshes the Blueprint Library count; a past-logs import just refreshes.
        GameLog = new GameLogSession(
            () => Data.GetAllBlueprints().Select(b => b.Name),
            name => Settings.IsBlueprintOwned(name),
            (name, owned) => Settings.SetBlueprintOwned(name, owned));
        GameLog.Marked += m =>
        {
            Views.ToastWindow.Show($"Marked owned: {m.Name}");
            (Current.MainWindow as Views.MainWindow)?.RefreshBlueprintOwnership();
        };
        GameLog.BulkOwnershipChanged += () =>
            (Current.MainWindow as Views.MainWindow)?.RefreshBlueprintOwnership();

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
        GameLog?.Dispose();
        Data?.Dispose();
        base.OnExit(e);
    }
}
