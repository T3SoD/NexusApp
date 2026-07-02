using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using NexusApp.Services;

namespace NexusApp.Views;

/// <summary>
/// The animated Nexus mark: the teal hex icon deconstructed into vector parts with a live
/// mining-laser loop - beam draws down from under the top vertex, impact flare + sparks,
/// strata chevrons ripple, embers rise during the hold, then the beam shuts off from the
/// source. Ported 1:1 from the approved design-lab mock (icons-animated/header-icon-
/// deconstructed.html); geometry lives in the same 64x64 unit space. The tapered glow is
/// hard-clipped below the hex vertex so it can never bleed into the border. Honors
/// Reduce animations (renders the static fully-fired mark) and pauses off-screen.
/// </summary>
public sealed class NexusBeamIcon : Viewbox
{
    private const double Cycle = 5.2;   // master loop seconds, matching the mock

    private readonly Canvas _canvas = new() { Width = 64, Height = 64 };
    private Storyboard? _master;        // all Cycle-long tracks
    private Storyboard? _hum;           // fast core shimmer
    private Storyboard? _breathe;       // impact disc
    private Storyboard? _flicker;       // impact rays
    private bool _built;
    private static bool _loggedOnce;

    public NexusBeamIcon()
    {
        Stretch = Stretch.Uniform;
        Child = _canvas;
        Loaded += (_, _) => { Build(); Start(); };
        Unloaded += (_, _) => Stop();
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) Start(); else Stop(); };
    }

    // ── construction (all values verbatim from the mock) ─────────────────────

    private void Build()
    {
        if (_built) return;
        _built = true;

        _master = new Storyboard { Duration = TimeSpan.FromSeconds(Cycle), RepeatBehavior = RepeatBehavior.Forever };

        // hex frame (static)
        var hexStroke = new LinearGradientBrush(
            Color.FromRgb(0x3F, 0xD9, 0xEC), Color.FromRgb(0x14, 0x78, 0xA0), 90);
        _canvas.Children.Add(new Path
        {
            Data = Geometry.Parse("M32,4.5 L55,17.5 L55,46.5 L32,59.5 L9,46.5 L9,17.5 Z"),
            Stroke = hexStroke, StrokeThickness = 2.6, StrokeLineJoin = PenLineJoin.Round,
        });

        // strata chevrons: base .55, pulse bright as the strike energy transfers down
        AddChevron("M22,44 L32,48.5 L42,44", 0x2F, 0xBB, 0xD9, 2.4, 0.15);
        AddChevron("M25,49 L32,52.5 L39,49", 0x23, 0xA0, 0xC4, 2.2, 0.30);
        AddChevron("M28,53.5 L32,55.8 L36,53.5", 0x1B, 0x84, 0xAC, 2.0, 0.45);

        // tapered glow: narrow at muzzle, blooming at impact; clipped so it can never
        // cross above y=8.5 (hex vertex stroke ends ~y5.8)
        var glow = new Polygon
        {
            Points = new PointCollection { new(30.9, 10), new(33.1, 10), new(34.8, 38), new(29.2, 38) },
            Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0xB2, 0x3E)),
            Effect = new BlurEffect { Radius = 2.0 },
            Clip = new RectangleGeometry(new Rect(20, 8.5, 24, 52)),
            Opacity = 0,
        };
        _canvas.Children.Add(glow);
        AddOpacityTrack(glow, (0, 0), (.05, 0), (.08, .8), (.20, .55), (.50, .75), (.80, .55), (.88, 0), (1, 0));

        // beam body: amber gradient, draws top-to-bottom via dash offset (AnimatedDockIcon technique)
        var bodyBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0), EndPoint = new Point(0, 1),
            GradientStops =
            {
                new GradientStop(Color.FromRgb(0xE8, 0x9A, 0x2E), 0),
                new GradientStop(Color.FromRgb(0xFF, 0xC2, 0x4F), .6),
                new GradientStop(Color.FromRgb(0xFF, 0xE7, 0xB0), 1),
            },
        };
        var body = BeamLine(bodyBrush, 3.4);
        var core = BeamLine(new SolidColorBrush(Color.FromRgb(0xFF, 0xF6, 0xE0)), 1.6);
        AddDrawTrack(body, 28 / 3.4);
        AddDrawTrack(core, 28 / 1.6);

        // fast energy hum on the core only
        _hum = MicroLoop(core, UIElement.OpacityProperty, 1, .82, 0.16);

        // muzzle emitter glint: anticipates the fire, idles warm through the hold
        var muzzle = new Rectangle
        {
            Width = 2.2, Height = 1.4,
            Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0xF6, 0xE0)), Opacity = 0,
        };
        Canvas.SetLeft(muzzle, 30.9); Canvas.SetTop(muzzle, 9.4);
        _canvas.Children.Add(muzzle);
        AddOpacityTrack(muzzle, (0, 0), (.02, 0), (.04, 1), (.07, .9), (.20, .75), (.80, .75), (.88, 0), (1, 0));

        // impact flare: breathing disc + diamond + 4 micro-flickering rays, group scales in
        var flare = BuildFlare();
        _canvas.Children.Add(flare);

        // sparks: primary burst on strike + softer mid-hold burst, each on its own trajectory
        AddSpark(29, 37, 26.5, 34.8, 0.00, .08, .12, .26, -11, -9, 1);
        AddSpark(28, 38.5, 25.2, 37.9, 0.08, .08, .13, .30, -13, -3, 1);
        AddSpark(30, 35.5, 28.6, 33, 0.16, .08, .12, .24, -8, -13, 1);
        AddSpark(35, 37, 37.5, 34.8, 0.04, .08, .12, .26, 11, -9, 1);
        AddSpark(36, 38.5, 38.8, 37.9, 0.12, .08, .13, .30, 13, -3, 1);
        AddSpark(34, 35.5, 35.4, 33, 0.20, .08, .12, .24, 8, -13, 1);
        AddSpark(29, 37, 26.8, 35.2, 0.00, .46, .50, .64, -10, -8, .9);
        AddSpark(35, 37, 37.2, 35.2, 0.00, .48, .52, .66, 10, -8, .9);

        // embers: tiny motes rising from the melt pool during the hold
        AddEmber(30.6, 36.6, .7, 0xFF, 0xD0, 0x89, 0.6);
        AddEmber(33.5, 36.2, .6, 0xFF, 0xC7, 0x66, 1.9);
        AddEmber(31.8, 36.9, .55, 0xFF, 0xE0, 0xA3, 3.1);

        if (Motion.Reduced) ApplyStaticState(glow, body, core, muzzle, flare);

        if (!_loggedOnce) { _loggedOnce = true; Logger.Info("[UI] beam icon: animated Nexus mark active"); }
    }

    private Line BeamLine(Brush stroke, double thickness)
    {
        var l = new Line
        {
            X1 = 32, Y1 = 10, X2 = 32, Y2 = 38,
            Stroke = stroke, StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Flat, StrokeEndLineCap = PenLineCap.Flat,
        };
        _canvas.Children.Add(l);
        return l;
    }

    private void AddChevron(string data, byte r, byte g, byte b, double thickness, double delayFrac)
    {
        var p = new Path
        {
            Data = Geometry.Parse(data),
            Stroke = new SolidColorBrush(Color.FromRgb(r, g, b)), StrokeThickness = thickness,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            Opacity = .55,
        };
        _canvas.Children.Add(p);
        // pulse shifted by the per-chevron delay so the energy visibly travels downward
        double d = delayFrac / Cycle;
        AddOpacityTrack(p, (0, .55), (.08 + d, .55), (.12 + d, 1), (.22 + d, .62), (.52, .9), (.62, .55), (1, .55));
    }

    private Grid BuildFlare()
    {
        var g = new Grid { Width = 64, Height = 64, RenderTransformOrigin = new Point(32.0 / 64, 39.0 / 64) };
        var scale = new ScaleTransform(1, 1);
        g.RenderTransform = scale;

        var disc = new Ellipse
        {
            Width = 8, Height = 8, Opacity = .5,
            Fill = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(Color.FromRgb(0xFF, 0xE0, 0xA3), 0),
                    new GradientStop(Color.FromRgb(0xFF, 0xC7, 0x66), .55),
                    new GradientStop(Color.FromArgb(0, 0xFF, 0xC7, 0x66), 1),
                },
            },
            Margin = new Thickness(28, 35, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
        };
        g.Children.Add(disc);
        _breathe = MicroLoop(disc, UIElement.OpacityProperty, .4, .62, 1.4);

        var diamond = new Path
        {
            Data = Geometry.Parse("M32,33.6 L34.4,39 L32,41.6 L29.6,39 Z"),
            Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0xD0, 0x89)),
        };
        g.Children.Add(diamond);

        _flicker = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        AddRay(g, 27.6, 36.4, 25.4, 34.6, 0.0);
        AddRay(g, 36.4, 36.4, 38.6, 34.6, 0.3);
        AddRay(g, 28.2, 41, 26.2, 42.2, 0.55);
        AddRay(g, 35.8, 41, 37.8, 42.2, 0.8);

        // group entrance/exit synced to the strike
        AddOpacityTrack(g, (0, 0), (.06, 0), (.09, 1), (.13, .9), (.55, .95), (.84, .7), (.90, 0), (1, 0));
        AddTrack(g, scale, ScaleTransform.ScaleXProperty, (0, .2), (.06, .2), (.09, 1.25), (.13, 1), (.55, 1.08), (.84, .95), (.90, .3), (1, .3));
        AddTrack(g, scale, ScaleTransform.ScaleYProperty, (0, .2), (.06, .2), (.09, 1.25), (.13, 1), (.55, 1.08), (.84, .95), (.90, .3), (1, .3));
        return g;
    }

    private void AddRay(Grid host, double x1, double y1, double x2, double y2, double delaySec)
    {
        var ray = new Line
        {
            X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
            Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0xD9, 0x8F)), StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Flat, StrokeEndLineCap = PenLineCap.Flat,
        };
        host.Children.Add(ray);
        var anim = new DoubleAnimation(1, .78, TimeSpan.FromSeconds(0.55))
        {
            BeginTime = TimeSpan.FromSeconds(delaySec),
            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(anim, ray);
        Storyboard.SetTargetProperty(anim, new PropertyPath(UIElement.OpacityProperty));
        _flicker!.Children.Add(anim);
    }

    private void AddSpark(double x1, double y1, double x2, double y2, double delaySec,
                          double tHide, double tShow, double tGone, double dx, double dy, double peak)
    {
        var tr = new TranslateTransform();
        var s = new Line
        {
            X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
            Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0xB2, 0x3E)), StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            Opacity = 0, RenderTransform = tr,
        };
        _canvas.Children.Add(s);
        double d = delaySec / Cycle;
        AddOpacityTrack(s, (0, 0), (tHide + d, 0), (tShow + d, peak), (tGone + d, 0), (1, 0));
        AddTrack(s, tr, TranslateTransform.XProperty, (0, 0), (tHide + d, 0), (tGone + d, dx), (1, dx));
        AddTrack(s, tr, TranslateTransform.YProperty, (0, 0), (tHide + d, 0), (tGone + d, dy), (1, dy));
    }

    private void AddEmber(double cx, double cy, double r, byte cr, byte cg, byte cb, double delaySec)
    {
        var tr = new TranslateTransform();
        var e = new Ellipse
        {
            Width = r * 2, Height = r * 2,
            Fill = new SolidColorBrush(Color.FromRgb(cr, cg, cb)),
            Opacity = 0, RenderTransform = tr,
        };
        Canvas.SetLeft(e, cx - r); Canvas.SetTop(e, cy - r);
        _canvas.Children.Add(e);
        double d = delaySec / Cycle;
        AddOpacityTrack(e, (0, 0), (.18 + d, 0), (.22 + d, .9), (.40 + d, 0), (1, 0));
        AddTrack(e, tr, TranslateTransform.YProperty, (0, 0), (.18 + d, 0), (.40 + d, -8), (1, -8));
    }

    // beam draw-down: dash offset positive-to-zero reveals from the muzzle; negative shuts
    // off from the source (dash units are multiples of stroke thickness, as in AnimatedDockIcon)
    private void AddDrawTrack(Line line, double dashUnits)
    {
        line.StrokeDashArray = new DoubleCollection { dashUnits, dashUnits };
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var anim = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(Cycle) };
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(dashUnits, KeyTime.FromPercent(0)));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(.06), ease));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(.86)));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(-dashUnits, KeyTime.FromPercent(.92), ease));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(-dashUnits, KeyTime.FromPercent(1)));
        Storyboard.SetTarget(anim, line);
        Storyboard.SetTargetProperty(anim, new PropertyPath(Shape.StrokeDashOffsetProperty));
        _master!.Children.Add(anim);
    }

    private void AddOpacityTrack(UIElement el, params (double t, double v)[] keys)
        => AddTrack(el, el, UIElement.OpacityProperty, keys);

    private void AddTrack(UIElement animTarget, DependencyObject target, DependencyProperty prop,
                          params (double t, double v)[] keys)
    {
        var anim = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(Cycle) };
        foreach (var (t, v) in keys)
            anim.KeyFrames.Add(new EasingDoubleKeyFrame(v, KeyTime.FromPercent(Math.Min(t, 1))));
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, new PropertyPath(prop));
        _master!.Children.Add(anim);
        _ = animTarget; // target association is via SetTarget; parameter kept for call-site clarity
    }

    private static Storyboard MicroLoop(UIElement el, DependencyProperty prop, double from, double to, double seconds)
    {
        var anim = new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds))
        {
            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        var sb = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        Storyboard.SetTarget(anim, el);
        Storyboard.SetTargetProperty(anim, new PropertyPath(prop));
        sb.Children.Add(anim);
        return sb;
    }

    // Reduce animations: the static, fully-fired mark - beam on, warm impact, no motion
    private void ApplyStaticState(UIElement glow, Line body, Line core, UIElement muzzle, UIElement flare)
    {
        _master = null; _hum = null; _breathe = null; _flicker = null;
        glow.Opacity = .55;
        body.StrokeDashArray = null;
        core.StrokeDashArray = null;
        muzzle.Opacity = .75;
        flare.Opacity = .95;
    }

    private void Start()
    {
        if (Motion.Reduced) return;
        _master?.Begin(this, true);
        _hum?.Begin(this, true);
        _breathe?.Begin(this, true);
        _flicker?.Begin(this, true);
    }

    private void Stop()
    {
        _master?.Stop(this);
        _hum?.Stop(this);
        _breathe?.Stop(this);
        _flicker?.Stop(this);
    }
}
