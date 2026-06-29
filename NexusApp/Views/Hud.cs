using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using NexusApp.Models;

namespace NexusApp.Views;

/// <summary>
/// MOBIGLAS HUD primitives shared across every page so the app renders one
/// consistent hardlight-HUD language: chamfered panels (top-left + bottom-right
/// bevel), amber corner brackets / reticle framing, glowing color-coded status
/// chips, gradient state progress bars, toggle switches, and the page header.
/// Code-behind pages build their UI by composing these helpers.
/// </summary>
public static partial class Hud
{
    public static Brush Br(string k) => (Brush)Application.Current.FindResource(k);
    public static FontFamily Font(string k) => (FontFamily)Application.Current.FindResource(k);

    // Resolves either a SolidColorBrush key or a raw Color key to a Color, so callers
    // can pass "AccentBrush" or "AccentColor" without risking an InvalidCastException.
    public static Color Col(string k) => Application.Current.FindResource(k) switch
    {
        SolidColorBrush b => b.Color,
        Color c => c,
        _ => Colors.Transparent,
    };

    // ── Chamfered HUD panel ───────────────────────────────────────────────
    // A Grid whose background+border is a Path that bevels the TL + BR corners
    // (the MOBIGLAS panel silhouette). The geometry re-computes on resize.
    // Optional amber corner brackets and an amber outer glow.
    public static Grid Panel(UIElement content, double chamfer = 12, bool brackets = false,
                             bool glow = false, Brush? bg = null, Brush? border = null,
                             Thickness? padding = null, double bracketSize = 14, Brush? bracketBrush = null)
    {
        bg ??= Br("Bg2NavBrush");
        border ??= Br("NavBorderBrush");
        var frame = new Path { Fill = bg, Stroke = border, StrokeThickness = 1, SnapsToDevicePixels = true };
        if (glow)
            frame.Effect = new DropShadowEffect { Color = Col("AccentBrush"), BlurRadius = 22, ShadowDepth = 0, Opacity = 0.22 };

        var host = new Grid();
        host.Children.Add(frame);

        var cp = new ContentControl
        {
            Content = content, Margin = padding ?? new Thickness(16), Focusable = false,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
        };
        host.Children.Add(cp);

        if (brackets) host.Children.Add(BracketLayer(bracketSize, bracketBrush ?? Br("AccentStrongBrush")));

        host.SizeChanged += (_, _) => frame.Data = ChamferGeometry(host.ActualWidth, host.ActualHeight, chamfer);
        return host;
    }

    private static Geometry ChamferGeometry(double w, double h, double c)
    {
        if (w <= 1 || h <= 1) return Geometry.Empty;
        c = Math.Min(c, Math.Min(w, h) / 2);
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(c, 0.5), true, true);
            ctx.LineTo(new Point(w - 0.5, 0.5), true, false);          // top
            ctx.LineTo(new Point(w - 0.5, h - c), true, false);        // right
            ctx.LineTo(new Point(w - c, h - 0.5), true, false);        // BR bevel
            ctx.LineTo(new Point(0.5, h - 0.5), true, false);          // bottom
            ctx.LineTo(new Point(0.5, c), true, false);                // left
            ctx.LineTo(new Point(c, 0.5), true, false);                // TL bevel
        }
        g.Freeze();
        return g;
    }

    // L-shaped corner brackets as a non-interactive overlay. The panel chamfers the
    // top-left + bottom-right corners, so by default brackets sit only on the two SQUARE
    // corners (top-right + bottom-left) where they read cleanly. Pass squareOnly:false for
    // a full four-corner frame on a non-chamfered surface.
    public static Grid BracketLayer(double size, Brush brush, double thick = 1.5, double inset = 2, bool squareOnly = true)
    {
        var layer = new Grid { IsHitTestVisible = false, Margin = new Thickness(inset) };
        layer.Children.Add(Bracket(size, thick, brush, HorizontalAlignment.Right, VerticalAlignment.Top, new Thickness(0, thick, thick, 0)));
        layer.Children.Add(Bracket(size, thick, brush, HorizontalAlignment.Left, VerticalAlignment.Bottom, new Thickness(thick, 0, 0, thick)));
        if (!squareOnly)
        {
            layer.Children.Add(Bracket(size, thick, brush, HorizontalAlignment.Left, VerticalAlignment.Top, new Thickness(thick, thick, 0, 0)));
            layer.Children.Add(Bracket(size, thick, brush, HorizontalAlignment.Right, VerticalAlignment.Bottom, new Thickness(0, 0, thick, thick)));
        }
        return layer;
    }

    private static Border Bracket(double size, double thick, Brush brush, HorizontalAlignment h, VerticalAlignment v, Thickness t) => new()
    {
        Width = size, Height = size, HorizontalAlignment = h, VerticalAlignment = v,
        BorderBrush = brush, BorderThickness = t,
    };

    // ── Interactive chamfered card ────────────────────────────────────────
    // Like Panel, but built for clickable list rows: the chamfer Path is returned via `frame` so the
    // caller can recolor Fill/Stroke for hover/select, and a bracket layer is returned via `brackets`
    // (collapsed by default) to reveal on select. Geometry recomputes on resize.
    public static Grid CardFrame(UIElement content, out Path frame, out Grid brackets,
                                 double chamfer = 10, Thickness? padding = null)
    {
        frame = new Path { Fill = Br("Bg2NavBrush"), Stroke = Br("NavBorderBrush"), StrokeThickness = 1, SnapsToDevicePixels = true };
        var host = new Grid();
        host.Children.Add(frame);
        host.Children.Add(new ContentControl
        {
            Content = content, Margin = padding ?? new Thickness(13, 12, 12, 12), Focusable = false,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
        });
        brackets = BracketLayer(10, Br("AccentStrongBrush"));
        brackets.Visibility = Visibility.Collapsed;
        host.Children.Add(brackets);
        var f = frame;
        host.SizeChanged += (_, _) => f.Data = ChamferGeometry(host.ActualWidth, host.ActualHeight, chamfer);
        return host;
    }

    // ── Reticle: a 4-corner amber target frame (brighter/larger brackets) over a Grid ──
    public static void AttachReticle(Grid host, double size = 14)
        => host.Children.Add(BracketLayer(size, Br("AccentBrush"), 2, 2));

    // ── Status LED "alive" pulse: a slow looping opacity breathe while on, static (full) while off ──
    // Used by the GAME SESSION / BLUEPRINTS pills so a green (live) LED gently flashes; off LEDs stay solid.
    public static void PulseDot(UIElement dot, bool on)
    {
        if (on)
        {
            var anim = new System.Windows.Media.Animation.DoubleAnimation(1.0, 0.3, new Duration(TimeSpan.FromSeconds(1.1)))
            {
                AutoReverse = true,
                RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
            };
            dot.BeginAnimation(UIElement.OpacityProperty, anim);
        }
        else
        {
            dot.BeginAnimation(UIElement.OpacityProperty, null);
            dot.Opacity = 1.0;
        }
    }

    // ── Status chip: tinted bg + colored border + glowing leading dot + mono label ──
    public static Border StatusChip(WorkOrderStatus status)
    {
        var (c, label) = status switch
        {
            WorkOrderStatus.Mining         => (Color.FromRgb(0x3B, 0x82, 0xF6), "MINING"),
            WorkOrderStatus.Refining       => (Color.FromRgb(0xFF, 0x9D, 0x4D), "REFINING"),
            WorkOrderStatus.ReadyToCollect => (Color.FromRgb(0x66, 0xE6, 0xA6), "READY"),
            _                              => (Color.FromRgb(0x7F, 0x8C, 0x8D), "COMPLETE"),
        };
        return Chip(c, label);
    }

    // Generic color-coded chip with a glowing dot.
    public static Border Chip(Color c, string label)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var dot = new Ellipse
        {
            Width = 6, Height = 6, Fill = new SolidColorBrush(c), Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Effect = new DropShadowEffect { Color = c, BlurRadius = 7, ShadowDepth = 0, Opacity = 0.9 },
        };
        row.Children.Add(dot);
        row.Children.Add(new TextBlock { Text = label, FontFamily = Font("MonoFont"), FontSize = 9, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(c), VerticalAlignment = VerticalAlignment.Center });
        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x1F, c.R, c.G, c.B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, c.R, c.G, c.B)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3),
            Padding = new Thickness(7, 2, 8, 2), HorizontalAlignment = HorizontalAlignment.Left, Child = row,
        };
    }

    // ── State progress bar: faint track + gradient fill color-coded by state + glow ──
    public enum BarState { Cyan, Green, Amber, Blue }

    public static UIElement StateBar(double frac, BarState state = BarState.Cyan, double height = 6)
    {
        frac = Math.Max(0, Math.Min(1, frac));
        var c = state switch
        {
            BarState.Green => Color.FromRgb(0x66, 0xE6, 0xA6),
            BarState.Amber => Color.FromRgb(0xFF, 0xB2, 0x3E),
            BarState.Blue  => Color.FromRgb(0x5F, 0xA8, 0xFF),
            _              => Color.FromRgb(0x7F, 0xE9, 0xE0),
        };
        var track = new Border { Height = height, CornerRadius = new CornerRadius(height / 2), Background = new SolidColorBrush(Color.FromArgb(0x1C, c.R, c.G, c.B)) };
        var fillBrush = new LinearGradientBrush(Color.FromArgb(0xCC, c.R, c.G, c.B), c, 0);
        var fill = new Border
        {
            Height = height, CornerRadius = new CornerRadius(height / 2), Background = fillBrush,
            HorizontalAlignment = HorizontalAlignment.Left,
            Effect = new DropShadowEffect { Color = c, BlurRadius = 8, ShadowDepth = 0, Opacity = 0.55 },
        };
        var grid = new Grid();
        grid.Children.Add(track);
        var host = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0001, frac), GridUnitType.Star) });
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0001, 1 - frac), GridUnitType.Star) });
        Grid.SetColumn(fill, 0); host.Children.Add(fill);
        grid.Children.Add(host);
        return grid;
    }

    // ── Page header: leading glow dash + eyebrow + display title + subtitle + right action slot ──
    public static UIElement Header(string eyebrow, string title, string subtitle, UIElement? action = null)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 18) };
        var left = new StackPanel();

        var eb = new StackPanel { Orientation = Orientation.Horizontal };
        eb.Children.Add(new Border
        {
            Width = 16, Height = 2, Background = Br("AccentBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
            Effect = new DropShadowEffect { Color = Col("AccentBrush"), BlurRadius = 7, ShadowDepth = 0, Opacity = 0.8 },
        });
        eb.Children.Add(new TextBlock { Text = eyebrow, FontFamily = Font("UiFont"), FontSize = 10.5, FontWeight = FontWeights.Bold, Foreground = Br("AccentBrush"), VerticalAlignment = VerticalAlignment.Center });
        left.Children.Add(eb);

        left.Children.Add(new TextBlock { Text = title, FontFamily = Font("DisplayFont"), FontSize = 26, FontWeight = FontWeights.Bold, Foreground = Br("FgBrush"), Margin = new Thickness(0, 4, 0, 0) });
        if (!string.IsNullOrEmpty(subtitle))
            left.Children.Add(new TextBlock { Text = subtitle, FontFamily = Font("UiFont"), FontSize = 12.5, Foreground = Br("FgDimBrush"), Margin = new Thickness(0, 4, 0, 0) });
        g.Children.Add(left);

        if (action != null)
        {
            if (action is FrameworkElement fe) { fe.HorizontalAlignment = HorizontalAlignment.Right; fe.VerticalAlignment = VerticalAlignment.Top; }
            g.Children.Add(action);
        }
        return g;
    }

    // ── Toggle switch: amber slider track + glowing knob (click flips IsOn) ──
    public sealed class ToggleSwitch : Button
    {
        private readonly Border _track;
        private readonly Border _knob;
        private bool _isOn;

        public bool IsOn
        {
            get => _isOn;
            set { if (_isOn == value) return; _isOn = value; Apply(); OnToggled?.Invoke(value); }
        }
        public Action<bool>? OnToggled;

        /// <summary>Set the visual state WITHOUT firing OnToggled - for re-syncing a mirrored toggle
        /// from shared state, so it never re-triggers the underlying start/stop action.</summary>
        public void SetOnSilently(bool on) { _isOn = on; Apply(); }

        public ToggleSwitch(bool isOn = false)
        {
            _isOn = isOn;
            Background = Brushes.Transparent; BorderThickness = new Thickness(0); Cursor = System.Windows.Input.Cursors.Hand;
            Focusable = true; Padding = new Thickness(0);

            _track = new Border { Width = 38, Height = 20, CornerRadius = new CornerRadius(10) };
            _knob = new Border
            {
                Width = 14, Height = 14, CornerRadius = new CornerRadius(7), Margin = new Thickness(3, 0, 3, 0),
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left,
            };
            var g = new Grid();
            g.Children.Add(_track);
            g.Children.Add(_knob);

            Template = null;
            Content = g;
            // Buttons keep their chrome template; strip it to just the content.
            var tmpl = new ControlTemplate(typeof(Button));
            var presenter = new System.Windows.FrameworkElementFactory(typeof(ContentPresenter));
            tmpl.VisualTree = presenter;
            Template = tmpl;

            Click += (_, _) => IsOn = !_isOn;
            Apply();
        }

        private void Apply()
        {
            _track.Background = _isOn ? Br("AccentBrush") : Br("Bg3Brush");
            _track.BorderBrush = _isOn ? Br("AccentBrush") : Br("NavBorderBrush");
            _track.BorderThickness = new Thickness(1);
            _knob.HorizontalAlignment = _isOn ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            _knob.Background = _isOn ? Br("OnAccentBrush") : Br("FgDimBrush");
            _track.Effect = _isOn ? new DropShadowEffect { Color = Col("AccentBrush"), BlurRadius = 10, ShadowDepth = 0, Opacity = 0.6 } : null;
        }
    }
}
