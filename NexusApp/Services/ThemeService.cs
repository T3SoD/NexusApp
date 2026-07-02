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

    // The teal command-center identity mark. Kept as one expression so a distinct
    // overhaul logo can be slotted in later without touching call sites. The window
    // and header icons reference Assets/nexus_icon_classic.png directly in XAML.
    public static string LogoUri => "pack://application:,,,/Assets/nexus_logo_classic.png";
}
