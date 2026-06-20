using System.Windows;
using NexusApp.Services;

namespace NexusApp;

public partial class App : Application
{
    public static DataService Data { get; private set; } = null!;
    public static SettingsService Settings { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        const string crashMessage =
            "Nexus hit an unexpected error and needs to close.\n\n" +
            "Details have been saved to the log file at:\n%AppData%\\NexusApp\\logs\\nexus.log";

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
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Data?.Dispose();
        base.OnExit(e);
    }
}
