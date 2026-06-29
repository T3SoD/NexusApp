using System;
using System.Windows;

namespace NexusApp.Services;

/// <summary>
/// Theme selection. "luxury" = v5 gold, "classic" = v4 slate/teal/amber.
/// The chosen palette is applied once at startup (App.OnStartup) by swapping the
/// merged palette dictionary BEFORE any window is created - that resolves cleanly
/// through every DynamicResource consumer. We deliberately do NOT swap themes live
/// at runtime (mutating shared brushes in place was unreliable); instead the picker
/// saves the choice and prompts the user to restart.
/// </summary>
public static class ThemeService
{
    public static string Current { get; private set; } = "luxury";

    /// <summary>Theme saved for next launch when it differs from the running one.</summary>
    public static string? Pending { get; private set; }

    public static event Action? ThemeChanged;

    public static bool IsClassic => Current == "classic";

    private static string Normalize(string name) => name == "classic" ? "classic" : "luxury";

    /// <summary>
    /// Boot-time apply: swap the merged palette dictionary. Must run before the
    /// main window is created. Not for live runtime switching.
    /// </summary>
    public static void Apply(string name, bool save = true)
    {
        name = Normalize(name);
        Current = name;
        var file = name == "classic" ? "Palette.Classic.xaml" : "Palette.Luxury.xaml";
        var palette = new ResourceDictionary { Source = new Uri($"Themes/{file}", UriKind.Relative) };

        var dicts = Application.Current.Resources.MergedDictionaries;
        for (int i = dicts.Count - 1; i >= 0; i--)
        {
            var src = dicts[i].Source?.OriginalString ?? "";
            if (src.Contains("Palette.")) dicts.RemoveAt(i);
        }
        dicts.Insert(0, palette);

        if (save)
        {
            App.Settings.Current.Theme = name;
            App.Settings.Save();
        }
        ThemeChanged?.Invoke();
    }

    /// <summary>
    /// Persist the user's theme choice for the next launch. Returns true when a
    /// restart is needed to see it (i.e. the choice differs from the running theme).
    /// </summary>
    public static bool SelectForRestart(string name)
    {
        name = Normalize(name);
        App.Settings.Current.Theme = name;
        App.Settings.Save();
        Pending = name == Current ? null : name;
        return Pending != null;
    }

    /// <summary>Relaunch the app so the saved theme takes effect.</summary>
    public static void RestartApp()
    {
        var path = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(path))
            System.Diagnostics.Process.Start(path);
        Application.Current.Shutdown();
    }

    // UI overhaul: the default palette is now the teal command-center identity, so both themes use
    // the teal logo/icon assets (the old gold marks would clash with teal). Kept as one expression
    // per asset so a distinct overhaul logo can be slotted in later without touching call sites.
    public static string LogoUri => "pack://application:,,,/Assets/nexus_logo_classic.png";

    public static string IconUri => "pack://application:,,,/Assets/nexus_icon_classic.png";
}
