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
/// hand-built reticle and the Network page keeps its coverage donut; the rest pull a distinct,
/// domain-fitting glyph from here so no two tabs animate the same way. All motion is lightweight
/// transforms / dash offsets started immediately and repeated forever.
/// </summary>
public static partial class Hud
{
    public enum Ambient { RadarSweep, Crystal, Crucible, Hologram, RoutePing }

    /// <summary>Build a self-animating ambient glyph for a tab's idle state. size = glyph box in DIPs.</summary>
    public static FrameworkElement AmbientGlyph(Ambient kind, double size = 64) => kind switch
    {
        Ambient.RadarSweep => RadarSweep(size),   // Operations: a sweep arm rotating over static rings
        Ambient.Crystal    => Crystal(size),      // Mining Codex: a faceted gem rocking + shimmering
        Ambient.Crucible   => Crucible(size),     // Refinery: a crucible whose molten level rises and falls
        Ambient.Hologram   => Hologram(size),     // Blueprints: a wireframe component tracing itself in
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

    // -- Mining Codex: faceted crystal (gentle rock + facet shimmer) --------------

    private static FrameworkElement Crystal(double size)
    {
        var layer = new Grid { Width = size, Height = size };

        var t1 = new Point(size * 0.35, size * 0.30);   // table, left
        var t2 = new Point(size * 0.65, size * 0.30);   // table, right
        var ur = new Point(size * 0.80, size * 0.46);   // upper right
        var b  = new Point(size * 0.50, size * 0.80);   // bottom tip
        var ul = new Point(size * 0.20, size * 0.46);   // upper left

        // Two interior facets that shimmer out of phase, like the gem catching light.
        var leftFacet = new Polygon { Points = new PointCollection { t1, b, ul }, Fill = Br("AccentBrush"), Opacity = 0.12 };
        var rightFacet = new Polygon { Points = new PointCollection { t2, ur, b }, Fill = Br("CyanBrush"), Opacity = 0.12 };
        leftFacet.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.06, 0.38, new Duration(TimeSpan.FromSeconds(1.8))) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        rightFacet.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.38, 0.06, new Duration(TimeSpan.FromSeconds(2.3))) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        layer.Children.Add(leftFacet);
        layer.Children.Add(rightFacet);

        // Outline + interior facet lines.
        layer.Children.Add(new Polygon { Points = new PointCollection { t1, t2, ur, b, ul }, Stroke = Br("CyanBrush"), StrokeThickness = 1.5, Fill = Brushes.Transparent });
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(t1, false, false); ctx.LineTo(b, true, false);
            ctx.BeginFigure(t2, false, false); ctx.LineTo(b, true, false);
            ctx.BeginFigure(ul, false, false); ctx.LineTo(ur, true, false);
        }
        geo.Freeze();
        layer.Children.Add(new Path { Data = geo, Stroke = Br("CyanDimBrush"), StrokeThickness = 1 });

        // Gentle rock (catching the light) rather than a full tumble.
        var rt = new RotateTransform(-10);
        layer.RenderTransform = rt;
        layer.RenderTransformOrigin = new Point(0.5, 0.5);
        rt.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(-10, 10, new Duration(TimeSpan.FromSeconds(3.4))) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        return layer;
    }

    // -- Refinery: molten crucible (level rises and falls) ------------------------

    private static FrameworkElement Crucible(double size)
    {
        var c = new Canvas { Width = size, Height = size };

        double topY = size * 0.34, botY = size * 0.80;
        double topL = size * 0.24, topR = size * 0.76;   // open top (wider)
        double botL = size * 0.34, botR = size * 0.66;   // narrow base

        // Molten fill, placed in the lower vessel; rises and falls via ScaleY anchored at the base.
        double fillLeft = botL + 1, fillRight = botR - 1;
        double fillTop = topY + (botY - topY) * 0.12;
        var fill = new Rectangle
        {
            Width = Math.Max(2, fillRight - fillLeft),
            Height = Math.Max(2, botY - fillTop - 1),
            RadiusX = 2, RadiusY = 2,
            Fill = new LinearGradientBrush(
                Color.FromArgb(0xFF, 0xFF, 0xD0, 0x60),
                Color.FromArgb(0xFF, 0xFF, 0x88, 0x1E),
                new Point(0.5, 0), new Point(0.5, 1)),
            Effect = Glow("AccentBrush", 10),
            RenderTransformOrigin = new Point(0.5, 1),
        };
        var sy = new ScaleTransform(1, 0.3);
        fill.RenderTransform = sy;
        Canvas.SetLeft(fill, fillLeft);
        Canvas.SetTop(fill, fillTop);
        sy.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.22, 1.0, new Duration(TimeSpan.FromSeconds(2.6))) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        c.Children.Add(fill);

        // Vessel walls + base (open-top trapezoid), drawn over the molten fill.
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(topL, topY), false, false);
            ctx.LineTo(new Point(botL, botY), true, false);
            ctx.LineTo(new Point(botR, botY), true, false);
            ctx.LineTo(new Point(topR, topY), true, false);
        }
        geo.Freeze();
        c.Children.Add(new Path { Data = geo, Stroke = Br("CyanBrush"), StrokeThickness = 1.6 });
        return c;
    }

    // -- Blueprints: hologram component (wireframe tracing itself in) -------------

    private static FrameworkElement Hologram(double size)
    {
        var g = new Grid { Width = size, Height = size };

        double m = size * 0.20, w = size * 0.40, d = size * 0.16;
        var fbl = new Point(m, size - m);
        var fbr = new Point(m + w, size - m);
        var ftl = new Point(m, size - m - w);
        var ftr = new Point(m + w, size - m - w);
        var btl = new Point(ftl.X + d, ftl.Y - d);
        var btr = new Point(ftr.X + d, ftr.Y - d);
        var bbr = new Point(fbr.X + d, fbr.Y - d);

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            // front face
            ctx.BeginFigure(fbl, false, false);
            ctx.LineTo(fbr, true, false); ctx.LineTo(ftr, true, false); ctx.LineTo(ftl, true, false); ctx.LineTo(fbl, true, false);
            // top face connectors
            ctx.BeginFigure(ftl, false, false);
            ctx.LineTo(btl, true, false); ctx.LineTo(btr, true, false); ctx.LineTo(ftr, true, false);
            // right face connectors
            ctx.BeginFigure(fbr, false, false);
            ctx.LineTo(bbr, true, false); ctx.LineTo(btr, true, false);
        }
        geo.Freeze();

        var path = new Path
        {
            Data = geo, Stroke = Br("AccentBrush"), StrokeThickness = 1.4,
            StrokeDashArray = new DoubleCollection { 3, 2 }, Effect = Glow("AccentBrush", 6),
        };
        path.BeginAnimation(Shape.StrokeDashOffsetProperty,
            new DoubleAnimation(20, 0, new Duration(TimeSpan.FromSeconds(2.4))) { RepeatBehavior = RepeatBehavior.Forever });
        g.Children.Add(path);

        // A cyan vertex node that pulses, like a live projection point.
        var node = new Ellipse
        {
            Width = 5, Height = 5, Fill = Br("CyanBrush"), Effect = Glow("CyanBrush", 6),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(ftr.X - 2.5, ftr.Y - 2.5, 0, 0),
        };
        node.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.3, 1.0, new Duration(TimeSpan.FromSeconds(1.3))) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        g.Children.Add(node);
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
