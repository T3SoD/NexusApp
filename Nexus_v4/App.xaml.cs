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

        base.OnStartup(e);
        Settings = new SettingsService();
        Data = new DataService();
        Data.Initialize();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Data?.Dispose();
        base.OnExit(e);
    }
}
