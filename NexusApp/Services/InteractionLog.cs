using System.Windows;
using System.Windows.Media;

namespace NexusApp.Services;

/// <summary>
/// Records discrete user interactions to nexus.log together with WHERE they happened — the
/// window type plus the nearest named container — so a diagnostic snapshot reads like a
/// breadcrumb trail ("toggle: Auto-scan @ OverlayWindow/ScanControlBar"). User-driven UI only;
/// background/automatic work (the scan loop) is logged separately under [SCAN].
/// </summary>
public static class InteractionLog
{
    public static void Click(string? label, DependencyObject source)  => Write("click", label, source);
    public static void Toggle(string? label, DependencyObject source) => Write("toggle", label, source);
    public static void Nav(string? label, DependencyObject source)    => Write("nav", label, source);

    /// <summary>For page-level navigations (e.g. Blueprint Library drill-down) where the label
    /// already names the destination, so there's no single control to locate from.</summary>
    public static void Nav(string? label) => Write("nav", label, null);

    private static void Write(string kind, string? label, DependencyObject? source)
    {
        if (string.IsNullOrWhiteSpace(label)) return;
        Logger.Info($"[UI] {kind}: {label}{(source != null ? LocationOf(source) : "")}");
    }

    /// <summary>
    /// Best-effort "where" for a logged interaction: the window type plus the nearest named
    /// container above the control (its x:Name from XAML), so duplicate labels — e.g. the three
    /// separate "Clear" buttons — are distinguishable. Template-internal parts (PART_*) are skipped
    /// so the result names a real page/panel (PageScan, ScanHistorySection, ScanTabContent), not a
    /// control-template fragment. Returns "" if nothing useful is found.
    /// </summary>
    public static string LocationOf(DependencyObject control)
    {
        var window = Window.GetWindow(control)?.GetType().Name;

        string? region = null;
        for (var p = VisualTreeHelper.GetParent(control); p != null; p = VisualTreeHelper.GetParent(p))
        {
            if (p is FrameworkElement fe && !string.IsNullOrEmpty(fe.Name)
                && !fe.Name.StartsWith("PART_", StringComparison.Ordinal))
            {
                region = fe.Name;
                break;
            }
        }

        if (window != null && region != null) return $" @ {window}/{region}";
        if (window != null) return $" @ {window}";
        if (region != null) return $" @ {region}";
        return "";
    }
}
