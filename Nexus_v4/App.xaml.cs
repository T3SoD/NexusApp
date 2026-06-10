using System.Windows;
using Nexus_v4.Services;

namespace Nexus_v4;

public partial class App : Application
{
    public static DataService Data { get; private set; } = null!;
    public static SettingsService Settings { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (s, ex) =>
        {
            System.Windows.MessageBox.Show(ex.Exception.ToString(),
                "Nexus — Error", System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
            ex.Handled = true;
            Shutdown(1);
        };

        AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
        {
            System.Windows.MessageBox.Show(ex.ExceptionObject.ToString(),
                "Nexus — Fatal Error", System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        };

        // Pick the palette BEFORE the main window is created. StartupUri builds
        // MainWindow inside base.OnStartup, and its StaticResource theme brushes
        // resolve once at load time — so the palette must already be swapped or
        // those brushes freeze on the default (Luxury) gold even in Classic.
        Settings = new SettingsService();
        ThemeService.Apply(Settings.Current.Theme, save: false);
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
