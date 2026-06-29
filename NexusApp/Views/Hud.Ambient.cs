using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace NexusApp.Views;

/// <summary>
/// Ambient HUD glyphs: each surface has one signature looping animation (chosen from the framer
/// design lab). Built in 100-unit space on a Canvas and scaled to the requested size via a Viewbox.
/// Every builder takes (size, animate): animate=true loops forever (page headers / empty states /
/// live "working" states); animate=false renders a clean static resting pose (the default nav icon).
/// Nav-rail icons animate only on hover or when their tab is selected (see AttachNavGlyph).
/// </summary>
public static partial class Hud
{
    public enum Ambient
    {
        StatusBoard,     // Operations  (OPS // 10)
        AcquisitionPing, // RS Decoder  (RS // 06)
        SpectralAssay,   // Mining Codex(CODEX // 02)
        OreConveyor,     // Refinery    (REFINE // 08)
        Hologram,        // Blueprints  (BLUE // 01)
        Mesh,            // Network     (NET // 01)
        RouteConvoy,     // Cargo Hauling (HAUL // 01)
    }

    /// <summary>Build a surface's signature glyph. animate=false gives the static resting pose.
    /// When animate=true the looping glyph is wrapped in a host that only animates while on-screen:
    /// switching tabs (Visibility=Collapsed) swaps it to the static pose, disposing the animation
    /// clocks, so hidden pages cost no CPU. It re-animates when its tab is shown again.</summary>
    public static FrameworkElement AmbientGlyph(Ambient kind, double size = 64, bool animate = true)
    {
        if (!animate) return Build(kind, size, false);

        var host = new ContentControl { Width = size, Height = size, Focusable = false, Content = Build(kind, size, false) };
        host.IsVisibleChanged += (_, e) => host.Content = Build(kind, size, (bool)e.NewValue);
        return host;
    }

    private static FrameworkElement Build(Ambient kind, double size, bool animate) => kind switch
    {
        Ambient.StatusBoard     => StatusBoard(size, animate),
        Ambient.AcquisitionPing => AcquisitionPing(size, animate),
        Ambient.SpectralAssay   => SpectralAssay(size, animate),
        Ambient.OreConveyor     => OreConveyor(size, animate),
        Ambient.Hologram        => Hologram(size, animate),
        Ambient.Mesh            => Mesh(size, animate),
        _                       => RouteConvoy(size, animate),
    };

    /// <summary>Wire a nav-rail glyph into a ContentControl host: static by default, animating while the
    /// nav button is hovered or selected (checked). Rebuilds the small glyph on each state change.</summary>
    public static void AttachNavGlyph(System.Windows.Controls.Primitives.ToggleButton btn, ContentControl host, Ambient kind, double size = 42)
    {
        // Nav glyphs already gate on hover/checked (and the rail is always visible), so build the raw
        // glyph directly instead of the IsVisible-gated wrapper.
        void Refresh() => host.Content = Build(kind, size, btn.IsMouseOver || btn.IsChecked == true);
        Refresh();
        btn.MouseEnter += (_, _) => Refresh();
        btn.MouseLeave += (_, _) => Refresh();
        btn.Checked    += (_, _) => Refresh();
        btn.Unchecked  += (_, _) => Refresh();
    }

    private static DropShadowEffect Glow(string key, double blur) =>
        new() { Color = Col(key), BlurRadius = blur, ShadowDepth = 0, Opacity = 0.85 };

    // -- Operations: module status board (OPS // 10) ------------------------------
    private static FrameworkElement StatusBoard(double size, bool animate)
    {
        var c = new Canvas { Width = 100, Height = 100 };

        var board = new Rectangle { Width = 64, Height = 56, RadiusX = 2, RadiusY = 2, Fill = System.Windows.Media.Brushes.Transparent, Stroke = Br("CyanDimBrush"), StrokeThickness = 1 };
        Canvas.SetLeft(board, 18); Canvas.SetTop(board, 22);
        c.Children.Add(board);

        for (int i = 0; i < 16; i++)
        {
            int row = i / 4;
            int col = i % 4;
            string key = i % 7 == 3 ? "AccentBrush" : (i % 5 == 2 ? "GoldBrush" : "OkBrush");
            var chip = new Rectangle { Width = 9, Height = 7, RadiusX = 1, RadiusY = 1, Fill = Br(key) };
            Canvas.SetLeft(chip, 24 + col * 14); Canvas.SetTop(chip, 28 + row * 12);
            c.Children.Add(chip);
            if (animate)
            {
                var dur = new Duration(TimeSpan.FromSeconds(1.6 + (i % 5) * 0.5));
                chip.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.18, 1, dur) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromSeconds((i % 4) * 0.45) });
            }
            else chip.Opacity = 0.6;
        }

        return new Viewbox { Width = size, Height = size, Child = c };
    }

    // -- RS Decoder: acquisition ping (RS // 06) ----------------------------------
    private static FrameworkElement AcquisitionPing(double size, bool animate)
    {
        var c = new Canvas { Width = 100, Height = 100 };

        var outer = new Ellipse { Width = 66, Height = 66, Stroke = Br("CyanDimBrush"), StrokeThickness = 0.7, StrokeDashArray = new DoubleCollection { 2, 4 } };
        Canvas.SetLeft(outer, 17); Canvas.SetTop(outer, 17);
        c.Children.Add(outer);

        for (int i = 0; i < 3; i++)
        {
            var ring = new Ellipse { Width = 8, Height = 8, Stroke = Br("CyanBrush"), StrokeThickness = 1.4, Fill = System.Windows.Media.Brushes.Transparent };
            Canvas.SetLeft(ring, 46); Canvas.SetTop(ring, 46);
            var st = new ScaleTransform(1, 1, 4, 4);
            ring.RenderTransform = st;
            c.Children.Add(ring);
            if (animate)
            {
                var dur = new Duration(TimeSpan.FromSeconds(2.4));
                var begin = TimeSpan.FromSeconds(i * 0.8);
                var grow = new DoubleAnimation(1, 8, dur) { RepeatBehavior = RepeatBehavior.Forever, BeginTime = begin };
                st.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
                st.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
                ring.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.9, 0, dur) { RepeatBehavior = RepeatBehavior.Forever, BeginTime = begin });
            }
            else ring.Opacity = i == 0 ? 0.5 : 0;
        }

        var dot = new Ellipse { Width = 5, Height = 5, Fill = Br("AccentBrush"), Effect = Glow("AccentBrush", 5) };
        Canvas.SetLeft(dot, 47.5); Canvas.SetTop(dot, 47.5);
        c.Children.Add(dot);

        return new Viewbox { Width = size, Height = size, Child = c };
    }

    // -- Mining Codex: spectral assay (CODEX // 02) -------------------------------
    private static FrameworkElement SpectralAssay(double size, bool animate)
    {
        var c = new Canvas { Width = 100, Height = 100 };

        var gem = new Polygon
        {
            Points = new PointCollection { new Point(50, 18), new Point(70, 40), new Point(50, 70), new Point(30, 40) },
            Fill = System.Windows.Media.Brushes.Transparent, Stroke = Br("CyanBrush"), StrokeThickness = 1.4,
        };
        c.Children.Add(gem);

        var scan = new Line { X1 = 26, X2 = 74, Stroke = Br("AccentBrush"), StrokeThickness = 1.6, Effect = Glow("AccentBrush", 6) };
        if (animate)
        {
            scan.Y1 = 22; scan.Y2 = 22;
            var sdur = new Duration(TimeSpan.FromSeconds(1.3));
            scan.BeginAnimation(Line.Y1Property, new DoubleAnimation(22, 68, sdur) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
            scan.BeginAnimation(Line.Y2Property, new DoubleAnimation(22, 68, sdur) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        }
        else { scan.Y1 = 45; scan.Y2 = 45; }
        c.Children.Add(scan);

        for (int i = 0; i < 5; i++)
        {
            double a = 4 + 18 * Math.Abs(Math.Sin(i * 1.3 + 1));
            var bar = new Rectangle { Width = 6, RadiusX = 1, RadiusY = 1, Fill = (i % 2 == 1) ? Br("CyanBrush") : Br("AccentBrush") };
            Canvas.SetLeft(bar, 29 + i * 9);
            if (animate)
            {
                bar.Height = 4; Canvas.SetTop(bar, 82);
                var dur = new Duration(TimeSpan.FromSeconds((1.5 + i * 0.25) / 2));
                bar.BeginAnimation(FrameworkElement.HeightProperty, new DoubleAnimation(4, a, dur) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
                bar.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(82, 86 - a, dur) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
            }
            else { bar.Height = a; Canvas.SetTop(bar, 86 - a); }
            c.Children.Add(bar);
        }

        return new Viewbox { Width = size, Height = size, Child = c };
    }

    // -- Refinery: ore conveyor (REFINE // 08) -----------------------------------
    private static FrameworkElement OreConveyor(double size, bool animate)
    {
        var c = new Canvas { Width = 100, Height = 100 };

        var belt = new Line { X1 = 16, Y1 = 64, X2 = 72, Y2 = 64, Stroke = Br("CyanBrush"), StrokeThickness = 1.3 };
        c.Children.Add(belt);

        for (int i = 0; i < 4; i++)
        {
            var roller = new Ellipse { Width = 5, Height = 5, Stroke = Br("CyanDimBrush"), StrokeThickness = 1, Fill = System.Windows.Media.Brushes.Transparent };
            Canvas.SetLeft(roller, 22 + i * 16 - 2.5);
            Canvas.SetTop(roller, 68 - 2.5);
            c.Children.Add(roller);
        }

        var furnace = new Path { Data = Geometry.Parse("M72,48 L86,44 L86,76 L72,72 Z"), Stroke = Br("CyanBrush"), StrokeThickness = 1.2, Fill = System.Windows.Media.Brushes.Transparent };
        c.Children.Add(furnace);

        var glow = new Ellipse { Width = 12, Height = 12, Fill = Br("AccentBrush"), Effect = Glow("AccentBrush", 6) };
        Canvas.SetLeft(glow, 72); Canvas.SetTop(glow, 54);
        var gst = new ScaleTransform(1, 1, 6, 6);
        glow.RenderTransform = gst;
        c.Children.Add(glow);
        if (animate)
        {
            var gdur = new Duration(TimeSpan.FromSeconds(0.9));
            var gscale = new DoubleAnimation(5.0 / 6.0, 7.0 / 6.0, gdur) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
            gst.BeginAnimation(ScaleTransform.ScaleXProperty, gscale);
            gst.BeginAnimation(ScaleTransform.ScaleYProperty, gscale);
            glow.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.4, 0.9, gdur) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        }
        else glow.Opacity = 0.7;

        for (int i = 0; i < 4; i++)
        {
            var ore = new Rectangle { Width = 8, Height = 8, RadiusX = 1.5, RadiusY = 1.5, Fill = i % 2 == 1 ? Br("GoldBrush") : Br("AccentBrush"), Effect = Glow("AccentBrush", 4) };
            Canvas.SetTop(ore, 50);
            c.Children.Add(ore);
            if (animate)
            {
                var dur = new Duration(TimeSpan.FromSeconds(2.6 + i * 0.5));
                Canvas.SetLeft(ore, 14);
                ore.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(14, 68, dur) { RepeatBehavior = RepeatBehavior.Forever });
                var op = new DoubleAnimationUsingKeyFrames { Duration = dur, RepeatBehavior = RepeatBehavior.Forever };
                op.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
                op.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.1)));
                op.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.85)));
                op.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
                ore.BeginAnimation(UIElement.OpacityProperty, op);
            }
            else { Canvas.SetLeft(ore, 14 + i * 14); ore.Opacity = 1; }
        }

        return new Viewbox { Width = size, Height = size, Child = c };
    }

    // -- Blueprints: holo-fabrication (BLUE // 01) -------------------------------
    private static FrameworkElement Hologram(double size, bool animate)
    {
        var c = new Canvas { Width = 100, Height = 100 };

        // Holographic cyan face tint with a top-down gradient, behind the wireframe (matches BLUE // 01).
        var grad = new LinearGradientBrush(Color.FromArgb(0x4D, 0x7F, 0xE9, 0xE0), Color.FromArgb(0x0A, 0x7F, 0xE9, 0xE0), new Point(0.5, 0), new Point(0.5, 1));
        c.Children.Add(new Polygon { Points = new PointCollection { new Point(34, 40), new Point(50, 48), new Point(50, 72), new Point(34, 64) }, Fill = grad });
        c.Children.Add(new Polygon { Points = new PointCollection { new Point(66, 40), new Point(50, 48), new Point(50, 72), new Point(66, 64) }, Fill = grad });

        var path = new Path
        {
            Data = Geometry.Parse("M34,64 L34,40 L50,32 L66,40 L66,64 L50,72 Z M34,40 L50,48 L66,40 M50,48 L50,72"),
            Stroke = Br("AccentBrush"), StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            Effect = Glow("AccentBrush", 6),
        };
        c.Children.Add(path);   // solid wireframe at rest; dash + offset only while animating
        if (animate)
        {
            // StrokeDashArray is in stroke-thickness units; 120 covers each subpath, so running the offset
            // 120 -> 0 reveals the whole component (trace-in), then it holds, erases and rebuilds.
            path.StrokeDashArray = new DoubleCollection { 120, 120 };
            var off = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, Duration = new Duration(TimeSpan.FromSeconds(3.4)) };
            off.KeyFrames.Add(new LinearDoubleKeyFrame(120, KeyTime.FromPercent(0)));
            off.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0.5)));
            off.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0.8)));
            off.KeyFrames.Add(new LinearDoubleKeyFrame(120, KeyTime.FromPercent(1)));
            path.BeginAnimation(Shape.StrokeDashOffsetProperty, off);
        }

        var node = new Ellipse { Width = 5, Height = 5, Fill = Br("CyanBrush"), Effect = Glow("CyanBrush", 6), Opacity = 0.85 };
        Canvas.SetLeft(node, 63.5); Canvas.SetTop(node, 37.5);
        c.Children.Add(node);
        if (animate)
            node.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.3, 1, new Duration(TimeSpan.FromSeconds(1.3))) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });

        return new Viewbox { Width = size, Height = size, Child = c };
    }

    // -- Network: constellation mesh (NET // 01) ---------------------------------
    private static FrameworkElement Mesh(double size, bool animate)
    {
        var c = new Canvas { Width = 100, Height = 100 };

        double[][] n =
        {
            new[] { 50.0, 24.0 }, new[] { 74.0, 42.0 }, new[] { 66.0, 72.0 },
            new[] { 34.0, 72.0 }, new[] { 26.0, 42.0 },
        };
        int[][] edges =
        {
            new[] { 0, 1 }, new[] { 1, 2 }, new[] { 2, 3 }, new[] { 3, 4 },
            new[] { 4, 0 }, new[] { 0, 2 }, new[] { 1, 3 },
        };

        foreach (var p in edges)
            c.Children.Add(new Line { X1 = n[p[0]][0], Y1 = n[p[0]][1], X2 = n[p[1]][0], Y2 = n[p[1]][1], Stroke = Br("CyanDimBrush"), StrokeThickness = 1 });

        for (int i = 0; i < n.Length; i++)
        {
            var node = new Ellipse { Width = 6.8, Height = 6.8, Fill = Br("AccentBrush"), Opacity = 0.7 };
            Canvas.SetLeft(node, n[i][0] - 3.4);
            Canvas.SetTop(node, n[i][1] - 3.4);
            c.Children.Add(node);
            if (animate)
                node.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.4, 1.0, new Duration(TimeSpan.FromSeconds(2)))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromSeconds(i * 0.3) });
        }

        var packet = new Ellipse { Width = 4.8, Height = 4.8, Fill = Br("CyanBrush"), Effect = Glow("CyanBrush", 6) };
        Canvas.SetLeft(packet, n[0][0] - 2.4);
        Canvas.SetTop(packet, n[0][1] - 2.4);
        c.Children.Add(packet);
        if (animate)
        {
            var dur = new Duration(TimeSpan.FromSeconds(5));
            double[] cx = { 50, 74, 66, 34, 26, 50 };
            double[] cy = { 24, 42, 72, 72, 42, 24 };
            var leftAnim = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, Duration = dur };
            var topAnim = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, Duration = dur };
            for (int k = 0; k < cx.Length; k++)
            {
                double pct = (double)k / (cx.Length - 1);
                leftAnim.KeyFrames.Add(new LinearDoubleKeyFrame(cx[k] - 2.4, KeyTime.FromPercent(pct)));
                topAnim.KeyFrames.Add(new LinearDoubleKeyFrame(cy[k] - 2.4, KeyTime.FromPercent(pct)));
            }
            packet.BeginAnimation(Canvas.LeftProperty, leftAnim);
            packet.BeginAnimation(Canvas.TopProperty, topAnim);
        }

        return new Viewbox { Width = size, Height = size, Child = c };
    }

    // -- Cargo Hauling: route convoy (HAUL // 01) --------------------------------
    private static FrameworkElement RouteConvoy(double size, bool animate)
    {
        var c = new Canvas { Width = 100, Height = 100 };

        double[] sx = { 20, 40, 62, 80 };
        double[] sy = { 72, 48, 60, 30 };

        c.Children.Add(new Path
        {
            Data = Geometry.Parse("M20,72 L40,48 L62,60 L80,30"),
            Fill = System.Windows.Media.Brushes.Transparent, Stroke = Br("CyanDimBrush"),
            StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 2, 3 },
        });

        for (int i = 0; i < 4; i++)
        {
            var stop = new Ellipse { Width = 6, Height = 6, Stroke = Br("CyanBrush"), StrokeThickness = 1.4, Fill = System.Windows.Media.Brushes.Transparent };
            Canvas.SetLeft(stop, sx[i] - 3);
            Canvas.SetTop(stop, sy[i] - 3);
            c.Children.Add(stop);
        }

        var ping = new Ellipse { Width = 6, Height = 6, Stroke = Br("AccentBrush"), StrokeThickness = 1.5, Fill = System.Windows.Media.Brushes.Transparent };
        Canvas.SetLeft(ping, 77); Canvas.SetTop(ping, 27);
        var pst = new ScaleTransform(1, 1, 3, 3);
        ping.RenderTransform = pst;
        c.Children.Add(ping);
        if (animate)
        {
            var pdur = new Duration(TimeSpan.FromSeconds(1.6));
            var pbegin = TimeSpan.FromSeconds(2.4);
            var grow = new DoubleAnimation(1, 3, pdur) { RepeatBehavior = RepeatBehavior.Forever, BeginTime = pbegin };
            pst.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            pst.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            ping.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.9, 0, pdur) { RepeatBehavior = RepeatBehavior.Forever, BeginTime = pbegin });
        }
        else ping.Opacity = 0;

        double[] begins = { 0, 1.7 };
        int[] rest = { 0, 2 };
        for (int d = 0; d < 2; d++)
        {
            var dot = new Ellipse { Width = 5.2, Height = 5.2, Fill = Br("AccentBrush"), Effect = Glow("AccentBrush", 5) };
            c.Children.Add(dot);
            if (animate)
            {
                Canvas.SetLeft(dot, sx[0] - 2.6);
                Canvas.SetTop(dot, sy[0] - 2.6);
                var begin = TimeSpan.FromSeconds(begins[d]);
                var lx = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, BeginTime = begin };
                var ty = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, BeginTime = begin };
                for (int k = 0; k < 4; k++)
                {
                    var pct = KeyTime.FromPercent(k / 3.0);
                    lx.KeyFrames.Add(new LinearDoubleKeyFrame(sx[k] - 2.6, pct));
                    ty.KeyFrames.Add(new LinearDoubleKeyFrame(sy[k] - 2.6, pct));
                }
                dot.BeginAnimation(Canvas.LeftProperty, lx);
                dot.BeginAnimation(Canvas.TopProperty, ty);
            }
            else
            {
                Canvas.SetLeft(dot, sx[rest[d]] - 2.6);
                Canvas.SetTop(dot, sy[rest[d]] - 2.6);
            }
        }

        return new Viewbox { Width = size, Height = size, Child = c };
    }
}
