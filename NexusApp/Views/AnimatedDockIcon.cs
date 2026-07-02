using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;   // disambiguate from System.IO.Path (implicit usings)

namespace NexusApp.Views;

/// <summary>
/// Renders one of the chosen dock nav icons (from <see cref="DockIconSpecs"/>) as a multi-part
/// line glyph and plays its hover / activate animations off the parent RadioButton's state.
/// Monochrome: every part follows the tile colour (dim at rest, amber on hover, ice-cyan when active).
/// Animations are built from the same little spec the design gallery used: per-part rotate / scale /
/// translate / opacity keyframes plus a stroke "draw-on" (StrokeDashOffset) reveal.
/// </summary>
public class AnimatedDockIcon : Viewbox
{
    /// <summary>Key into DockIconSpecs.Json (e.g. "operations", "rs", "settings").</summary>
    public string IconKey { get; set; } = "";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Global pacing multiplier applied to every animation duration and delay.
    /// 1.0 = the gallery timing; higher = slower. Tune this one number to taste.</summary>
    private const double Speed = 1.8;

    private readonly List<PartRt> _parts = new();
    private readonly ScaleTransform _hoverScale = new(1, 1);
    private Storyboard? _hover;
    private Storyboard? _selected;
    private RadioButton? _host;
    private bool _built;
    private Brush? _staticColor;

    /// <summary>Fixed colour override for static hosts (e.g. the Help topic list) that have no
    /// RadioButton driving the rest / hover / selected colours. Safe to call before load.</summary>
    public void SetStaticColor(Brush b)
    {
        _staticColor = b;
        if (_built) SetColor(b);
    }

    private sealed class PartRt
    {
        public string Id = "";
        public Path Path = null!;
        public ScaleTransform Scale = null!;
        public RotateTransform Rotate = null!;
        public TranslateTransform Translate = null!;
        public bool Filled;
        public double DashUnits; // StrokeDashOffset start for draw-on (0 = not drawn)
    }

    public AnimatedDockIcon()
    {
        Stretch = Stretch.Uniform;
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = _hoverScale;   // whole-icon zoom for a clearly-visible hover
        Loaded += OnLoaded;
        Unloaded += (_, __) => Detach();
    }

    // Sustained hover zoom, held while the pointer is over the tile and eased back on leave.
    private void HoverScale(double to)
    {
        if (Motion.Reduced) { _hoverScale.ScaleX = _hoverScale.ScaleY = to; return; }
        var anim = new DoubleAnimation(to, new Duration(TimeSpan.FromSeconds(0.16 * Speed)))
        {
            EasingFunction = Motion.SlideOut,
            FillBehavior = FillBehavior.HoldEnd,
        };
        _hoverScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        _hoverScale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_built) { try { Build(); } catch { /* never break the dock over an icon */ } _built = true; }
        Attach();
        ApplyInitial();
    }

    // ---------- build ----------
    private void Build()
    {
        using var doc = JsonDocument.Parse(DockIconSpecs.Json);
        if (!doc.RootElement.TryGetProperty(IconKey, out var spec)) return;

        double minX = 0, minY = 0, w = 32, h = 32;
        var ve = spec.GetProperty("view");
        if (ve.ValueKind == JsonValueKind.Number) { w = h = ve.GetDouble(); }
        else if (ve.ValueKind == JsonValueKind.Array)
        {
            var a = ve.EnumerateArray().Select(x => x.GetDouble()).ToArray();
            if (a.Length == 4) { minX = a[0]; minY = a[1]; w = a[2]; h = a[3]; }
        }
        double baseStroke = spec.TryGetProperty("stroke", out var se) ? Num(se) : 1.6;

        var canvas = new Canvas { Width = w, Height = h, RenderTransform = new TranslateTransform(-minX, -minY) };

        foreach (var p in spec.GetProperty("parts").EnumerateArray())
        {
            string id = p.GetProperty("id").GetString() ?? "";
            Geometry geom = MakeGeometry(p);
            double sw = p.TryGetProperty("sw", out var swe) ? Num(swe) : baseStroke;
            bool filled = p.TryGetProperty("fill", out var fe) && fe.ValueKind != JsonValueKind.False;
            Point pivot = ResolvePivot(p, id, spec, geom);

            var sc = new ScaleTransform(1, 1, pivot.X, pivot.Y);
            var rot = new RotateTransform(0, pivot.X, pivot.Y);
            var tr = new TranslateTransform(0, 0);
            var tg = new TransformGroup();
            tg.Children.Add(sc); tg.Children.Add(rot); tg.Children.Add(tr);

            var path = new Path
            {
                Data = geom,
                StrokeThickness = sw,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                RenderTransform = tg,
            };

            double dashUnits = 0;
            if (IsDrawn(spec, id))
            {
                double len = GeomLength(geom);
                if (len > 0 && sw > 0)
                {
                    dashUnits = len / sw; // StrokeDashArray is in stroke-thickness units
                    path.StrokeDashArray = new DoubleCollection { dashUnits, dashUnits };
                    path.StrokeDashCap = PenLineCap.Round;
                    path.StrokeDashOffset = 0; // rest = fully drawn
                }
            }

            canvas.Children.Add(path);
            _parts.Add(new PartRt { Id = id, Path = path, Scale = sc, Rotate = rot, Translate = tr, Filled = filled, DashUnits = dashUnits });
        }

        Child = canvas;
        _hover = BuildStoryboard(spec, "hover");
        _selected = BuildStoryboard(spec, "selected");
        SetColor(_staticColor ?? Res("FgDimBrush"));
    }

    private Point ResolvePivot(JsonElement part, string id, JsonElement spec, Geometry geom)
    {
        if (part.TryGetProperty("origin", out var po) && po.ValueKind == JsonValueKind.Array)
        {
            var a = po.EnumerateArray().Select(Num).ToArray();
            if (a.Length >= 2) return new Point(a[0], a[1]);
        }
        foreach (var blk in new[] { "selected", "hover" })
        {
            if (spec.TryGetProperty(blk, out var b) && b.TryGetProperty("tracks", out var tks))
                foreach (var t in tks.EnumerateArray())
                    if (t.TryGetProperty("part", out var tp) && tp.GetString() == id
                        && t.TryGetProperty("origin", out var to) && to.ValueKind == JsonValueKind.Array)
                    {
                        var a = to.EnumerateArray().Select(Num).ToArray();
                        if (a.Length >= 2) return new Point(a[0], a[1]);
                    }
        }
        var bnd = geom.Bounds;
        return new Point(bnd.X + bnd.Width / 2, bnd.Y + bnd.Height / 2);
    }

    private static bool IsDrawn(JsonElement spec, string id)
    {
        foreach (var blk in new[] { "hover", "selected" })
            if (spec.TryGetProperty(blk, out var b) && b.TryGetProperty("tracks", out var tks))
                foreach (var t in tks.EnumerateArray())
                    if (t.TryGetProperty("part", out var tp) && tp.GetString() == id
                        && t.TryGetProperty("draw", out var dv) && dv.ValueKind == JsonValueKind.True)
                        return true;
        return false;
    }

    // ---------- geometry ----------
    private static double Num(JsonElement e) =>
        e.ValueKind == JsonValueKind.String ? double.Parse(e.GetString()!, Inv) : e.GetDouble();

    private static double Prop(JsonElement p, string name) => Num(p.GetProperty(name));

    private static Geometry MakeGeometry(JsonElement p)
    {
        switch (p.GetProperty("el").GetString())
        {
            case "path": return Geometry.Parse(p.GetProperty("d").GetString() ?? "");
            case "line": return new LineGeometry(new Point(Prop(p, "x1"), Prop(p, "y1")), new Point(Prop(p, "x2"), Prop(p, "y2")));
            case "circle": { double r = Prop(p, "r"); return new EllipseGeometry(new Point(Prop(p, "cx"), Prop(p, "cy")), r, r); }
            case "ellipse": return new EllipseGeometry(new Point(Prop(p, "cx"), Prop(p, "cy")), Prop(p, "rx"), Prop(p, "ry"));
            case "rect":
            {
                double rx = p.TryGetProperty("rx", out var rxe) ? Num(rxe) : 0;
                return new RectangleGeometry(new Rect(Prop(p, "x"), Prop(p, "y"), Prop(p, "w"), Prop(p, "h")), rx, rx);
            }
            case "polyline": return Poly(p.GetProperty("points").GetString() ?? "", false);
            case "polygon": return Poly(p.GetProperty("points").GetString() ?? "", true);
        }
        return Geometry.Empty;
    }

    private static Geometry Poly(string pts, bool closed)
    {
        var n = pts.Replace(",", " ").Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        var fig = new PathFigure();
        bool first = true;
        for (int i = 0; i + 1 < n.Length; i += 2)
        {
            var pt = new Point(double.Parse(n[i], Inv), double.Parse(n[i + 1], Inv));
            if (first) { fig.StartPoint = pt; first = false; }
            else fig.Segments.Add(new LineSegment(pt, true));
        }
        fig.IsClosed = closed;
        var pg = new PathGeometry();
        pg.Figures.Add(fig);
        return pg;
    }

    private static double GeomLength(Geometry g)
    {
        PathGeometry flat;
        try { flat = g.GetFlattenedPathGeometry(); } catch { return 0; }
        double len = 0;
        foreach (var fig in flat.Figures)
        {
            Point cur = fig.StartPoint;
            foreach (var seg in fig.Segments)
            {
                if (seg is PolyLineSegment pls)
                    foreach (var pt in pls.Points) { len += (pt - cur).Length; cur = pt; }
                else if (seg is LineSegment ls) { len += (ls.Point - cur).Length; cur = ls.Point; }
            }
            if (fig.IsClosed) len += (fig.StartPoint - cur).Length;
        }
        return len;
    }

    // ---------- storyboards ----------
    private Storyboard? BuildStoryboard(JsonElement spec, string blockName)
    {
        if (!spec.TryGetProperty(blockName, out var blk) || !blk.TryGetProperty("tracks", out var tks)) return null;
        double dur = blk.TryGetProperty("duration", out var de) ? Num(de) : 0.3;
        string ease = blk.TryGetProperty("ease", out var ee) ? (ee.GetString() ?? "settle") : "settle";

        var sb = new Storyboard();
        foreach (var t in tks.EnumerateArray())
        {
            string pid = t.TryGetProperty("part", out var tp) ? (tp.GetString() ?? "") : "";
            var pr = _parts.FirstOrDefault(x => x.Id == pid);
            if (pr == null) continue;

            double d = t.TryGetProperty("duration", out var td) ? Num(td) : dur;
            string ez = t.TryGetProperty("ease", out var te) ? (te.GetString() ?? ease) : ease;
            double delay = t.TryGetProperty("delay", out var dl) ? Num(dl) : 0;
            var easing = Ease(ez);
            var durTs = new Duration(TimeSpan.FromSeconds(d * Speed));
            var begin = TimeSpan.FromSeconds(delay * Speed);

            if (t.TryGetProperty("scale", out var sv))
            {
                var vals = ReadArr(sv);
                AddKeys(sb, pr.Scale, ScaleTransform.ScaleXProperty, vals, durTs, easing, begin);
                AddKeys(sb, pr.Scale, ScaleTransform.ScaleYProperty, vals, durTs, easing, begin);
            }
            if (t.TryGetProperty("rotate", out var rv)) AddKeys(sb, pr.Rotate, RotateTransform.AngleProperty, ReadArr(rv), durTs, easing, begin);
            if (t.TryGetProperty("x", out var xv)) AddKeys(sb, pr.Translate, TranslateTransform.XProperty, ReadArr(xv), durTs, easing, begin);
            if (t.TryGetProperty("y", out var yv)) AddKeys(sb, pr.Translate, TranslateTransform.YProperty, ReadArr(yv), durTs, easing, begin);
            if (t.TryGetProperty("opacity", out var ov)) AddKeys(sb, pr.Path, UIElement.OpacityProperty, ReadArr(ov), durTs, easing, begin);
            if (t.TryGetProperty("draw", out var dv) && dv.ValueKind == JsonValueKind.True && pr.DashUnits > 0)
            {
                var da = new DoubleAnimation(pr.DashUnits, 0, durTs) { BeginTime = begin, EasingFunction = easing, FillBehavior = FillBehavior.Stop };
                Storyboard.SetTarget(da, pr.Path);
                Storyboard.SetTargetProperty(da, new PropertyPath(Shape.StrokeDashOffsetProperty));
                sb.Children.Add(da);
            }
        }
        return sb.Children.Count > 0 ? sb : null;
    }

    private static double[] ReadArr(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Array) return new[] { Num(e) };
        var list = new List<double>();
        foreach (var x in e.EnumerateArray())
        {
            if (x.ValueKind == JsonValueKind.Array) { foreach (var y in x.EnumerateArray()) { list.Add(Num(y)); break; } }
            else list.Add(Num(x));
        }
        return list.ToArray();
    }

    private static void AddKeys(Storyboard sb, DependencyObject target, DependencyProperty prop,
                                double[] vals, Duration dur, IEasingFunction? ease, TimeSpan begin)
    {
        if (vals.Length == 0) return;
        var anim = new DoubleAnimationUsingKeyFrames { Duration = dur, BeginTime = begin, FillBehavior = FillBehavior.Stop };
        int n = vals.Length;
        for (int i = 0; i < n; i++)
        {
            double frac = n == 1 ? 1.0 : (double)i / (n - 1);
            anim.KeyFrames.Add(new EasingDoubleKeyFrame(vals[i], KeyTime.FromPercent(frac), ease));
        }
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, new PropertyPath(prop));
        sb.Children.Add(anim);
    }

    private static IEasingFunction? Ease(string name) => name switch
    {
        "settle" => Motion.Settle,
        "reveal" => Motion.Reveal,
        "easeOut" => new CubicEase { EasingMode = EasingMode.EaseOut },
        "easeInOut" => new CubicEase { EasingMode = EasingMode.EaseInOut },
        "back" => new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 },
        "linear" => null,
        _ => Motion.Settle,
    };

    // ---------- state / colour ----------
    private Brush Res(string key) => (TryFindResource(key) as Brush) ?? Brushes.Goldenrod;

    private void SetColor(Brush b)
    {
        foreach (var pr in _parts)
        {
            if (pr.Filled) { pr.Path.Fill = b; pr.Path.Stroke = null; }
            else { pr.Path.Stroke = b; pr.Path.Fill = null; }
        }
    }

    private void Attach()
    {
        if (_host != null) return;
        _host = FindHost();
        if (_host == null) return;
        _host.MouseEnter += Host_Enter;
        _host.MouseLeave += Host_Leave;
        _host.Checked += Host_Checked;
        _host.Unchecked += Host_Unchecked;
    }

    private void Detach()
    {
        if (_host == null) return;
        _host.MouseEnter -= Host_Enter;
        _host.MouseLeave -= Host_Leave;
        _host.Checked -= Host_Checked;
        _host.Unchecked -= Host_Unchecked;
        _host = null;
    }

    private RadioButton? FindHost()
    {
        DependencyObject? d = this;
        while (d != null)
        {
            d = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d);
            if (d is RadioButton rb) return rb;
        }
        return null;
    }

    private void ApplyInitial() => SetColor(_staticColor ?? Res(_host?.IsChecked == true ? "CyanBrush" : "FgDimBrush"));

    private void Host_Enter(object sender, MouseEventArgs e)
    {
        if (_host?.IsChecked == true) return;
        SetColor(Res("AccentBrush"));
        HoverScale(1.1);
        Play(_selected);   // run the full icon animation on mouseover
    }

    private void Host_Leave(object sender, MouseEventArgs e)
    {
        if (_host?.IsChecked == true) return;
        SetColor(Res("FgDimBrush"));
        HoverScale(1.0);
    }

    private void Host_Checked(object sender, RoutedEventArgs e)
    {
        HoverScale(1.0);
        SetColor(Res("CyanBrush"));   // selected glyph goes ice-cyan (mock: dock-schemes/cyan.html)
        Play(_selected);
    }

    private void Host_Unchecked(object sender, RoutedEventArgs e) => SetColor(Res("FgDimBrush"));

    private void Play(Storyboard? sb)
    {
        if (sb == null || Motion.Reduced) return;
        try { sb.Begin(); } catch { /* animation is best-effort */ }
    }
}
