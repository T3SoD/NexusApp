using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace NexusApp.Views;

/// <summary>
/// Ambient HUD glyphs: each main tab gets its own signature perpetually-looping animation for its
/// idle / empty state, in the spirit of the RS Decoder's spinning reticle. The RS Decoder keeps its
/// hand-built reticle and the Network page keeps its coverage donut; the rest pull a distinct glyph
/// from here so no two tabs animate the same way. All motion is lightweight transforms / dash offsets
/// started immediately and repeated forever.
/// </summary>
public static partial class Hud
{
    public enum Ambient { RadarSweep, Orbit, ScanLine, Schematic, RoutePing }

    /// <summary>Build a self-animating ambient glyph for a tab's idle state. size = glyph box in DIPs.</summary>
    public static FrameworkElement AmbientGlyph(Ambient kind, double size = 64) => kind switch
    {
        Ambient.RadarSweep => RadarSweep(size),   // Operations: a sweep arm rotating over static rings
        Ambient.Orbit      => Orbit(size),        // Mining Codex: satellites orbiting a central node
        Ambient.ScanLine   => ScanLine(size),     // Refinery: a bright scan bar travelling over grid lines
        Ambient.Schematic  => Schematic(size),    // Blueprints: a dashed 2x2 schematic drawing itself in
        _                  => RoutePing(size),    // Cargo Hauling: a cargo dot travelling a pickup->dropoff route
    };

    // -- shared helpers -----------------------------------------------------------

    private static DropShadowEffect Glow(string key, double blur) =>
        new() { Color = Col(key), BlurRadius = blur, ShadowDepth = 0, Opacity = 0.85 };

    // Spin an element about its own centre forever (linear, seamless). reverse flips the direction.
    private static void SpinForever(UIElement el, double seconds, bool reverse = false)
    {
        var rt = new RotateTransform();
        el.RenderTransform = rt;
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        var a = new DoubleAnimation(reverse ? 360 : 0, reverse ? 0 : 360, new Duration(TimeSpan.FromSeconds(seconds)))
        {
            RepeatBehavior = RepeatBehavior.Forever,
        };
        rt.BeginAnimation(RotateTransform.AngleProperty, a);
    }

    private static Ellipse Ring(double d) => new()
    {
        Width = d, Height = d, Stroke = Br("CyanDimBrush"), StrokeThickness = 1,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
    };

    // -- Operations: radar sweep --------------------------------------------------

    private static FrameworkElement RadarSweep(double size)
    {
        var g = new Grid { Width = size, Height = size };
        g.Children.Add(Ring(size));
        g.Children.Add(Ring(size * 0.66));
        g.Children.Add(Ring(size * 0.33));
        g.Children.Add(new Border { Width = 1, Height = size, Background = Br("CyanDimBrush"), Opacity = 0.5, HorizontalAlignment = HorizontalAlignment.Center });
        g.Children.Add(new Border { Height = 1, Width = size, Background = Br("CyanDimBrush"), Opacity = 0.5, VerticalAlignment = VerticalAlignment.Center });

        // A wedge from the centre to the top edge, bright at the hub and fading out, rotating like a sweep.
        var wedge = new Polygon
        {
            Points = new PointCollection
            {
                new Point(size / 2, size / 2),
                new Point(size / 2 - size * 0.16, 0),
                new Point(size / 2 + size * 0.16, 0),
            },
            Fill = new LinearGradientBrush(
                Color.FromArgb(0x99, 0xFF, 0xB2, 0x3E),
                Color.FromArgb(0x00, 0xFF, 0xB2, 0x3E),
                new Point(0.5, 1), new Point(0.5, 0)),
        };
        var sweep = new Grid { Width = size, Height = size };
        sweep.Children.Add(wedge);
        SpinForever(sweep, 5);
        g.Children.Add(sweep);

        g.Children.Add(new Ellipse { Width = 5, Height = 5, Fill = Br("AccentBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Effect = Glow("AccentBrush", 6) });
        return g;
    }

    // -- Mining Codex: orbiting satellites ---------------------------------------

    private static FrameworkElement Orbit(double size)
    {
        var g = new Grid { Width = size, Height = size };
        g.Children.Add(Ring(size * 0.5));
        g.Children.Add(Ring(size * 0.84));
        g.Children.Add(OrbitSat(size, size * 0.25, 7, false));
        g.Children.Add(OrbitSat(size, size * 0.42, 11, true));
        g.Children.Add(new Ellipse { Width = 9, Height = 9, Fill = Br("AccentBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Effect = Glow("AccentBrush", 10) });
        return g;
    }

    // A full-size layer carrying a dot at the given orbit radius (above centre); spinning the layer orbits it.
    private static FrameworkElement OrbitSat(double size, double radius, double seconds, bool reverse)
    {
        var layer = new Grid { Width = size, Height = size };
        layer.Children.Add(new Ellipse
        {
            Width = 5, Height = 5, Fill = Br("CyanBrush"), Effect = Glow("CyanBrush", 6),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, size / 2 - radius - 2.5, 0, 0),
        });
        SpinForever(layer, seconds, reverse);
        return layer;
    }

    // -- Refinery: travelling scan bar -------------------------------------------

    private static FrameworkElement ScanLine(double size)
    {
        var g = new Grid { Width = size, Height = size, ClipToBounds = true };
        for (int i = 1; i <= 5; i++)
            g.Children.Add(new Border { Height = 1, Width = size, Background = Br("CyanDimBrush"), Opacity = 0.4, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, size * i / 6.0, 0, 0) });

        var bar = new Border { Height = 2, Width = size, VerticalAlignment = VerticalAlignment.Top, Background = Br("AccentBrush"), Effect = Glow("AccentBrush", 8) };
        var tt = new TranslateTransform();
        bar.RenderTransform = tt;
        tt.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(0, size, new Duration(TimeSpan.FromSeconds(2.6))) { RepeatBehavior = RepeatBehavior.Forever });
        g.Children.Add(bar);
        return g;
    }

    // -- Blueprints: self-drawing schematic --------------------------------------

    private static FrameworkElement Schematic(double size)
    {
        var g = new Grid { Width = size, Height = size };
        double cell = size * 0.42;
        var slots = new (HorizontalAlignment h, VerticalAlignment v)[]
        {
            (HorizontalAlignment.Left,  VerticalAlignment.Top),
            (HorizontalAlignment.Right, VerticalAlignment.Top),
            (HorizontalAlignment.Left,  VerticalAlignment.Bottom),
            (HorizontalAlignment.Right, VerticalAlignment.Bottom),
        };
        for (int i = 0; i < 4; i++)
        {
            var r = new Rectangle
            {
                Width = cell, Height = cell, RadiusX = 2, RadiusY = 2,
                Stroke = (i % 2 == 0) ? Br("AccentBrush") : Br("CyanBrush"), StrokeThickness = 1.4,
                StrokeDashArray = new DoubleCollection { 3, 2 },
                HorizontalAlignment = slots[i].h, VerticalAlignment = slots[i].v,
            };
            r.BeginAnimation(Shape.StrokeDashOffsetProperty,
                new DoubleAnimation(10, 0, new Duration(TimeSpan.FromSeconds(3)))
                {
                    RepeatBehavior = RepeatBehavior.Forever,
                    BeginTime = TimeSpan.FromSeconds(i * 0.4),
                });
            g.Children.Add(r);
        }
        return g;
    }

    // -- Cargo Hauling: cargo travelling a route ---------------------------------

    private static FrameworkElement RoutePing(double size)
    {
        var g = new Grid { Width = size, Height = size * 0.5 };
        g.Children.Add(new Border { Height = 1.5, Width = size * 0.78, Background = Br("CyanDimBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        g.Children.Add(EndNode(HorizontalAlignment.Left, size));
        g.Children.Add(EndNode(HorizontalAlignment.Right, size));

        var cargo = new Ellipse
        {
            Width = 7, Height = 7, Fill = Br("AccentBrush"), Effect = Glow("AccentBrush", 8),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(size * 0.11, 0, 0, 0),
        };
        var tt = new TranslateTransform();
        cargo.RenderTransform = tt;
        tt.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(0, size * 0.78, new Duration(TimeSpan.FromSeconds(3.2))) { RepeatBehavior = RepeatBehavior.Forever });
        g.Children.Add(cargo);
        return g;
    }

    private static FrameworkElement EndNode(HorizontalAlignment h, double size) => new Ellipse
    {
        Width = 8, Height = 8, Stroke = Br("CyanBrush"), StrokeThickness = 1.5, Fill = Br("Bg2NavBrush"),
        HorizontalAlignment = h, VerticalAlignment = VerticalAlignment.Center,
        Margin = h == HorizontalAlignment.Left ? new Thickness(size * 0.08, 0, 0, 0) : new Thickness(0, 0, size * 0.08, 0),
    };
}
