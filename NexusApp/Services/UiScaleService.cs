using System;
using System.Windows;
using System.Windows.Media;

namespace NexusApp.Services;

// App-wide UI scale (issue #20). Two persisted factors: AppUiScale (main window plus dialogs)
// and OverlayUiScale (in-game overlay plus work-order flyout), each applied as a LayoutTransform
// on a window's root content element. Windows that translate between screen pixels and layout
// coordinates (region selector, scan indicators, tour marks) are deliberately never scaled:
// a content transform would corrupt their PointToScreen math.
public static class UiScaleService
{
    public const double Min = 1.0;
    public const double Max = 1.5;
    public const double Step = 0.05;

    // Raised after a scale setting has been persisted. Long-lived windows (main, overlay)
    // subscribe to re-apply live; transient dialogs just read the value at construction.
    public static event Action? Changed;

    // Defends against hand-edited settings.json: NaN and non-positive values fall back to 1.0
    // (Math.Clamp would propagate NaN), everything else clamps into [Min, Max].
    public static double ClampScale(double value)
        => double.IsNaN(value) || value <= 0 ? 1.0 : Math.Clamp(value, Min, Max);

    public static double AppScale => ClampScale(App.Settings.Current.AppUiScale);
    public static double OverlayScale => ClampScale(App.Settings.Current.OverlayUiScale);

    public static void SetAppScale(double value)
    {
        var v = ClampScale(value);
        if (v == ClampScale(App.Settings.Current.AppUiScale)) return;
        App.Settings.Current.AppUiScale = v;
        App.Settings.Save();
        Changed?.Invoke();
    }

    public static void SetOverlayScale(double value)
    {
        var v = ClampScale(value);
        if (v == ClampScale(App.Settings.Current.OverlayUiScale)) return;
        App.Settings.Current.OverlayUiScale = v;
        App.Settings.Save();
        Changed?.Invoke();
    }

    // Sets or clears the scale transform on a window's root content element. Identity scale
    // resets to Transform.Identity (the property default) so the unscaled path behaves
    // exactly as it does today.
    public static void ApplyTransform(FrameworkElement root, double scale)
    {
        if (scale == 1.0) { root.LayoutTransform = Transform.Identity; return; }
        var t = new ScaleTransform(scale, scale);
        t.Freeze();
        root.LayoutTransform = t;
    }

    // One-shot scaling for fixed-size dialogs and tool windows: scales the content AND the
    // window dimensions together, so the layout keeps its designed logical size and simply
    // renders larger. Reads the current AppScale; call from the ctor after content is built.
    public static void ApplyToDialog(Window window, FrameworkElement root)
    {
        var k = AppScale;
        if (k == 1.0) return;
        ApplyTransform(root, k);
        if (!double.IsNaN(window.Width)) window.Width *= k;
        if (!double.IsNaN(window.Height)) window.Height *= k;
        if (window.MinWidth > 0) window.MinWidth *= k;
        if (window.MinHeight > 0) window.MinHeight *= k;
        if (!double.IsInfinity(window.MaxWidth)) window.MaxWidth *= k;
        if (!double.IsInfinity(window.MaxHeight)) window.MaxHeight *= k;
    }
}
