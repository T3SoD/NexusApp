using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NexusApp.Models;

namespace NexusApp.Views;

// Shared builders for the scan-result deposit-composition UI (segmented bar + "CAN CONTAIN" rows
// + expand motion). Used verbatim by both the in-game overlay (OverlayWindow) and the main-window
// RS Decoder (MainWindow) so the frozen visual treatment stays in lockstep across both surfaces.
// Frozen values: docs/superpowers/specs/2026-07-11-overlay-pass-values.md ("Scan result cards"
// + "Part G additions"). Resources (BorderBrush/FgBrush/MonoFont) resolve app-level via
// Application.Current.FindResource (defined in Themes/GameTheme.xaml, no window overrides).
internal static class ScanCardComposition
{
    // ── Frozen palette ──────────────────────────────────────────────────────────
    public static readonly Color CompTrack     = Color.FromRgb(0x1A, 0x20, 0x28);        // bar track
    public static readonly Color CompPrimary   = Color.FromRgb(0xFF, 0xB2, 0x3E);        // primary segment / chip text (amber)
    public static readonly Color CompCyan      = Color.FromRgb(0x7F, 0xE9, 0xE0);        // byproduct segments
    public static readonly Color CompInert     = Color.FromRgb(0x5F, 0x6B, 0x78);        // remainder / inert
    public static readonly Color CompPctBand   = Color.FromRgb(0xFF, 0xD0, 0x89);        // pct band (amber-bright)
    public static readonly Color CompAmberLine = Color.FromArgb(0x6B, 0xFF, 0xB2, 0x3E); // "primary" chip border (amber-line .42)
    private static readonly double[] ByproductOpacity = { 1.0, 0.7, 0.45 };              // stepped by descending share

    // 4px segmented bar: primary (amber) first, byproducts (cyan, stepped opacity by descending share),
    // then an inert remainder to 100%. Widths proportional to each part's (Min+Max)/2 average.
    public static Border BuildBar(IReadOnlyList<CompositionPart> parts)
    {
        static double Avg(CompositionPart p) => (p.MinPct + p.MaxPct) / 2.0;

        var byproducts = parts.Where(p => !p.IsPrimary).OrderByDescending(Avg).ToList();

        var grid = new Grid();
        void AddSegment(double weight, Color color, double opacity)
        {
            if (weight <= 0) return;
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(weight, GridUnitType.Star) });
            var seg = new Border { Background = new SolidColorBrush(color), Opacity = opacity };
            Grid.SetColumn(seg, grid.ColumnDefinitions.Count - 1);
            grid.Children.Add(seg);
        }

        foreach (var p in parts.Where(p => p.IsPrimary)) AddSegment(Avg(p), CompPrimary, 1.0);
        for (int i = 0; i < byproducts.Count; i++)
            AddSegment(Avg(byproducts[i]), CompCyan, ByproductOpacity[Math.Min(i, ByproductOpacity.Length - 1)]);

        double remainder = 100.0 - parts.Sum(Avg);
        if (remainder > 0) AddSegment(remainder, CompInert, 1.0);

        return new Border
        {
            Height = 4,
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(CompTrack),
            Margin = new Thickness(0, 6, 0, 0),
            Child = grid,
        };
    }

    // Expand rows: a "CAN CONTAIN" header (G1) above one row per part - ore name (11px), optional
    // "primary" chip, pct band (mono 10.5px). Wrapped in a 1px top border to separate the section
    // from the card body. Frozen: docs/superpowers/specs/2026-07-11-overlay-pass-values.md ("Part G additions").
    public static Border BuildExpandRows(IReadOnlyList<CompositionPart> parts)
    {
        var app = System.Windows.Application.Current;
        var line = (Brush)app.FindResource("BorderBrush");
        var fg = (Brush)app.FindResource("FgBrush");
        var mono = (FontFamily)app.FindResource("MonoFont");
        var stack = new StackPanel { Margin = new Thickness(0, 5, 0, 1) };

        // G1: "Can contain" header - matches the file's existing uppercase-label idiom
        // (ToUpperInvariant at render time; WPF has no text-transform).
        // Opacity set on the element itself, same idiom as the byproduct segments in BuildBar.
        stack.Children.Add(new TextBlock
        {
            Text = "Can contain".ToUpperInvariant(),
            FontSize = 8.5,
            Foreground = new SolidColorBrush(CompPrimary),
            Opacity = 0.85,
        });

        foreach (var p in parts)
        {
            var row = new Grid { Margin = new Thickness(0, 3, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(new TextBlock
            {
                Text = p.Ore,
                FontSize = 11,
                Foreground = fg,
                VerticalAlignment = VerticalAlignment.Center,
            });
            if (p.IsPrimary) left.Children.Add(BuildPrimaryChip());
            Grid.SetColumn(left, 0);
            row.Children.Add(left);

            var pct = new TextBlock
            {
                Text = $"{p.MinPct:0}-{p.MaxPct:0}%",
                FontFamily = mono,
                FontSize = 10.5,
                Foreground = new SolidColorBrush(CompPctBand),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(pct, 1);
            row.Children.Add(pct);

            stack.Children.Add(row);
        }

        return new Border
        {
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = line,
            Margin = new Thickness(0, 6, 0, 0),
            Child = stack,
        };
    }

    private static Border BuildPrimaryChip() => new()
    {
        Margin = new Thickness(6, 0, 0, 0),
        Padding = new Thickness(4, 0, 4, 1),
        CornerRadius = new CornerRadius(3),
        BorderThickness = new Thickness(1),
        BorderBrush = new SolidColorBrush(CompAmberLine),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = "primary", FontSize = 8.5, Foreground = new SolidColorBrush(CompPrimary) },
    };

    // Show rows: animated (200ms fade + 12px rise) or snapped open, per the caller's motion budget.
    public static void ExpandRows(FrameworkElement rows, bool animate)
    {
        rows.Visibility = Visibility.Visible;
        if (animate) { AnimateRowsIn(rows); return; }
        rows.BeginAnimation(UIElement.OpacityProperty, null);
        rows.RenderTransform = null;
        rows.Opacity = 1;
    }

    public static void CollapseRows(FrameworkElement rows)
    {
        rows.BeginAnimation(UIElement.OpacityProperty, null);
        rows.RenderTransform = null;
        rows.Opacity = 1;
        rows.Visibility = Visibility.Collapsed;
    }

    // Entrance: 200ms fade 0->1 + 12px rise, Motion.Settle, one-shot (frozen "expand motion").
    public static void AnimateRowsIn(FrameworkElement rows)
    {
        var shift = new TranslateTransform(0, 12);
        rows.RenderTransform = shift;
        rows.Opacity = 0;
        var dur = TimeSpan.FromMilliseconds(200);
        var fade = new System.Windows.Media.Animation.DoubleAnimation(0, 1, dur) { EasingFunction = Motion.Settle };
        var rise = new System.Windows.Media.Animation.DoubleAnimation(12, 0, dur) { EasingFunction = Motion.Settle };
        rows.BeginAnimation(UIElement.OpacityProperty, fade);
        shift.BeginAnimation(TranslateTransform.YProperty, rise);
    }
}
