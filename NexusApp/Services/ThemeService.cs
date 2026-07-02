using System;
using System.Windows;

namespace NexusApp.Services;

/// <summary>
/// App-wide visual assets and the restart helper. Nexus ships a single theme:
/// the palette is merged statically in App.xaml (Themes/Palette.Luxury.xaml),
/// with no first-run picker and no runtime switching.
/// </summary>
public static class ThemeService
{
    /// <summary>Relaunch the app (used after destructive actions like clearing saved data).</summary>
    public static void RestartApp()
    {
        var path = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(path))
            System.Diagnostics.Process.Start(path);
        Application.Current.Shutdown();
    }

    // The teal command-center identity marks. Kept as one expression per asset so a
    // distinct overhaul logo can be slotted in later without touching call sites.
    public static string LogoUri => "pack://application:,,,/Assets/nexus_logo_classic.png";

    public static string IconUri => "pack://application:,,,/Assets/nexus_icon_classic.png";
}
