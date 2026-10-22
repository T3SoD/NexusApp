using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using NexusApp.Models;
using NexusApp.Services;

namespace NexusApp.Views;

/// <summary>
/// The quality bands popup of a Blueprint Library quality control: one ore's step function
/// (refined quality held to the value it crafts as), drawn as a chart over a band table.
/// Values follow the crafting-quality mock (BandGraph, .scrim, .dlg, .chart, .tip, .bandTable).
/// The window covers its owner; a 55% void scrim sits under a 640 px chamfered panel.
/// Mouse-only by rule: the close button and a scrim click close it, no key handler.
/// A band pick raises BandPicked and moves the marker; the popup stays open.
/// </summary>
public sealed class QualityBandsDialog : Window
{
    // Chart frame (mock: W 596, H 250, L 44, R 14, T 18, B 34).
    private const double W = 596, H = 250, L = 44, R = 14, T = 18, B = 34;
    private const int MaxQuality = 1000;
    private const double PanelWidth = 640;
    private const double MarkerLabelThreshold = 850;
    private static readonly int[] Ticks = [0, 200, 400, 600, 800, 1000];

    private readonly string _resource;
    private readonly IReadOnlyList<QualityBand> _bands;
    private readonly Border _scrim;
    private readonly Grid _panel;
    private readonly ScaleTransform _panelScale = new(1, 1);
    private readonly TranslateTransform _panelRise = new();
    private readonly List<(Rectangle Hit, Rectangle Tint)> _bandTints = [];
    private readonly List<(Border Row, TextBlock[] Cells)> _rows = [];
    private readonly Line _markerLine;
    private readonly Ellipse _markerDot;
    private readonly TextBlock _markerLabel;
    private readonly Brush _rowBrush;   // the mock's lab-fg #B9C4D0 has no app key: FgBrush at 0.75

    private int _current;
    private bool _closing;

    /// <summary>Raised with the start of the band the user clicked, on the chart or in the table.</summary>
    public event Action<int>? BandPicked;

    public QualityBandsDialog(string resource, string slotName, string blueprintName,
                              IReadOnlyList<QualityBand> bands, int currentQuality)
    {
        _resource = resource;
        _bands = bands;
        _current = Math.Clamp(currentQuality, 0, MaxQuality);
        _rowBrush = Hud.WithOpacity(Hud.Br("FgBrush"), 0.75);

        Title = $"{resource} quality bands";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var mono = Hud.Font("MonoFont");

        // ── Header: eyebrow + title left, close right ──
        var head = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titles = new StackPanel();
        titles.Children.Add(new TextBlock
        {
            Text = "QUALITY BANDS", FontFamily = mono, FontSize = 11, Foreground = Hud.Br("AccentBrush"),
            Margin = new Thickness(0, 0, 0, 4),
        });
        var title = new TextBlock
        {
            FontFamily = Hud.Font("DisplayFont"), FontSize = 22, FontWeight = FontWeights.Bold,
            Foreground = Hud.Br("FgBrush"), TextTrimming = TextTrimming.CharacterEllipsis,
        };
        title.Inlines.Add(new Run(resource + " "));
        title.Inlines.Add(new Run("crafts as") { Foreground = Hud.Br("CyanBrush") });
        titles.Children.Add(title);
        head.Children.Add(titles);
        var close = BuildCloseButton();
        Grid.SetColumn(close, 1);
        head.Children.Add(close);

        // ── Chart ──
        var canvas = new Canvas { Width = W, Height = H, ClipToBounds = false };
        _markerLine = new Line { Stroke = Hud.Br("AccentBrush"), StrokeThickness = 1, IsHitTestVisible = false };
        // Mock dot: r 5 with a 2 px stroke centred on the edge, so 12 px across with an 8 px amber core.
        _markerDot = new Ellipse
        {
            Width = 12, Height = 12, Fill = Hud.Br("AccentBrush"), Stroke = Hud.Br("Bg2NavBrush"),
            StrokeThickness = 2, IsHitTestVisible = false,
        };
        _markerLabel = new TextBlock
        {
            FontFamily = mono, FontSize = 10, FontWeight = FontWeights.Medium,
            Foreground = Hud.Br("AccentBrush"), IsHitTestVisible = false,
        };
        BuildChart(canvas, mono);
        var chart = new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both, Child = canvas };

        // ── Table ──
        var table = BuildTable(mono);

        // ── Footer ──
        var foot = new TextBlock
        {
            Text = FooterText(resource, slotName, blueprintName),
            FontFamily = Hud.Font("UiFont"), FontSize = 11.5, Foreground = Hud.Br("FgDimBrush"),
            TextWrapping = TextWrapping.Wrap, LineHeight = 11.5 * 1.5,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight, Margin = new Thickness(0, 12, 0, 0),
        };

        var body = new StackPanel();
        body.Children.Add(head);
        body.Children.Add(chart);
        body.Children.Add(table);
        body.Children.Add(foot);

        _panel = Hud.Panel(body, chamfer: 12, bg: Hud.Br("Bg2NavBrush"), border: Hud.Br("BorderBrush"),
                           padding: new Thickness(22, 18, 22, 20));
        _panel.Width = PanelWidth;
        _panel.HorizontalAlignment = HorizontalAlignment.Center;
        _panel.VerticalAlignment = VerticalAlignment.Center;
        _panel.Margin = new Thickness(16);
        // Mock box-shadow 0 24px 60px rgba(0,0,0,.55), on the frame only so the text keeps ClearType.
        if (_panel.Children.Count > 0 && _panel.Children[0] is Path frame)
            frame.Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 60, ShadowDepth = 24, Direction = 270, Opacity = 0.55 };
        _panel.RenderTransformOrigin = new Point(0.5, 0.5);
        _panel.RenderTransform = new TransformGroup { Children = { _panelScale, _panelRise } };
        // A press inside the panel never reaches the scrim.
        _panel.MouseLeftButtonDown += (_, e) => e.Handled = true;
        UiScaleService.ApplyTransform(_panel, UiScaleService.AppScale);

        var scrimColor = Hud.Col("BgColor");
        _scrim = new Border { Background = new SolidColorBrush(Color.FromArgb(0x8C, scrimColor.R, scrimColor.G, scrimColor.B)) };
        _scrim.MouseLeftButtonDown += (_, e) => { e.Handled = true; RequestClose(); };

        var root = new Grid();
        root.Children.Add(_scrim);
        root.Children.Add(_panel);
        Content = root;

        PlaceMarker(animate: false);
        Loaded += (_, _) => Open();
        Logger.Info($"[UI] bands open {resource}");
    }

    /// <summary>
    /// Makes the popup cover its owner so the scrim spans the whole window: maximized over a
    /// maximized owner, the owner's size otherwise (CenterOwner then lines it up). No owner:
    /// the window sizes to its content.
    /// </summary>
    public static void FitToOwner(QualityBandsDialog dlg, Window? owner)
    {
        if (owner is null)
        {
            dlg.SizeToContent = SizeToContent.WidthAndHeight;
            return;
        }
        dlg.Owner = owner;
        if (owner.WindowState == WindowState.Maximized)
        {
            dlg.WindowState = WindowState.Maximized;
            return;
        }
        dlg.Width = owner.ActualWidth;
        dlg.Height = owner.ActualHeight;
    }

    // ── Pure geometry and copy (unit tested) ────────────────────────────────

    internal static double ChartX(double q) => L + q / MaxQuality * (W - L - R);
    internal static double ChartY(double q) => T + (1 - q / MaxQuality) * (H - T - B);

    /// <summary>The step line ends where the next band starts, held to the axis on the last band.</summary>
    internal static int StepEnd(QualityBand b) => Math.Min(b.End + 1, MaxQuality);

    internal static bool ShowsValueLabel(QualityBand b) => b.End - b.Start > 60;

    internal static bool MarkerLabelLeft(int q) => q > MarkerLabelThreshold;

    internal static string MarkerText(int q, int mapped) => $"Q{q} crafts as {mapped}";

    internal static string TipText(QualityBand b) => $"{b.Start}-{b.End} crafts as {b.Mapped}";

    internal static string HoldText(QualityBand b) => $"{b.Start} to {b.End}";

    /// <summary>Mapped value against the band middle, rounded half up like the mock's Math.round; "+" only above zero.</summary>
    internal static string VsMiddleText(QualityBand b)
    {
        double d = b.Mapped - (b.Start + b.End) / 2.0;
        var n = (int)Math.Floor(d + 0.5);
        return (d > 0 ? "+" : "") + n.ToString(CultureInfo.InvariantCulture);
    }

    internal static string FooterText(string resource, string slotName, string blueprintName) =>
        $"Game record for {resource}. Every ore has its own bands. The dashed line is what an unbanded quality would craft as. Click a band to set the {slotName} slot on the {blueprintName} to its start.";

    internal static QualityBand? BandAt(IReadOnlyList<QualityBand> bands, int q)
    {
        foreach (var b in bands)
            if (b.Start <= q && q <= b.End) return b;
        return null;
    }

    // ── Chart ───────────────────────────────────────────────────────────────

    private void BuildChart(Canvas c, FontFamily mono)
    {
        var cyan = Hud.Col("CyanBrush");
        var dimC = Hud.Col("FgDimBrush");
        var grid = Fresh(cyan, 0.07);
        var riser = Fresh(cyan, 0.35);
        var ident = Fresh(dimC, 0.35);
        var tint = Fresh(cyan, 0.09);
        var axis = Hud.Br("BorderBrush");
        var dim = Hud.Br("FgDimBrush");
        var fg = Hud.Br("FgBrush");
        var cyanBr = Hud.Br("CyanBrush");

        foreach (var t in Ticks)
            c.Children.Add(Seg(L, ChartY(t), W - R, ChartY(t), grid, 1));
        c.Children.Add(Seg(L, ChartY(0), W - R, ChartY(0), axis, 1));
        c.Children.Add(Seg(L, ChartY(0), L, ChartY(MaxQuality), axis, 1));
        foreach (var t in Ticks)
        {
            var s = t.ToString(CultureInfo.InvariantCulture);
            c.Children.Add(Label(s, mono, 9, dim, ChartX(t), H - B + 13, TextAlignment.Center));
            c.Children.Add(Label(s, mono, 9, dim, L - 6, ChartY(t) + 3, TextAlignment.Right));
        }
        c.Children.Add(Label("REFINED QUALITY YOU HOLD", mono, 9, dim, (L + W - R) / 2, H - 4, TextAlignment.Center));
        c.Children.Add(VerticalLabel("CRAFTS AS", mono, 9, dim, 12, (T + H - B) / 2));

        var identity = Seg(ChartX(0), ChartY(0), ChartX(MaxQuality), ChartY(MaxQuality), ident, 1);
        identity.StrokeDashArray = [3, 4];
        c.Children.Add(identity);

        // Band hit zones and their tints (hover, and the current band).
        foreach (var b in _bands)
        {
            double x = ChartX(b.Start), w = Math.Max(ChartX(b.End + 1) - x, 2);
            var hl = new Rectangle { Width = w, Height = H - T - B, Fill = tint, Opacity = 0, IsHitTestVisible = false };
            var hit = new Rectangle
            {
                Width = w, Height = H - T - B, Fill = Brushes.Transparent, Cursor = Cursors.Hand,
                ToolTip = BuildTip(b),
            };
            // Mock .tip (translate -50%, -100% - 10px): a custom placement centres it over the band,
            // 10 px above its step, and it stays up while the pointer hovers.
            ToolTipService.SetInitialShowDelay(hit, 0);
            ToolTipService.SetShowDuration(hit, int.MaxValue);
            ToolTipService.SetPlacement(hit, System.Windows.Controls.Primitives.PlacementMode.Custom);
            Canvas.SetLeft(hit, x); Canvas.SetTop(hit, T);
            Canvas.SetLeft(hl, x); Canvas.SetTop(hl, T);
            var band = b;
            hit.MouseEnter += (_, _) => FadeTint(hl, 1);
            hit.MouseLeave += (_, _) => FadeTint(hl, IsCurrent(band) ? 1 : 0);
            PickOnRelease(hit, () => Pick(band.Start));
            c.Children.Add(hit);
            c.Children.Add(hl);
            _bandTints.Add((hit, hl));
        }

        // Step line, risers and value labels.
        for (int i = 0; i < _bands.Count; i++)
        {
            var b = _bands[i];
            var step = Seg(ChartX(b.Start), ChartY(b.Mapped), ChartX(StepEnd(b)), ChartY(b.Mapped), cyanBr, 2);
            step.StrokeStartLineCap = step.StrokeEndLineCap = PenLineCap.Round;
            c.Children.Add(step);
            if (i > 0)
            {
                var r = Seg(ChartX(b.Start), ChartY(_bands[i - 1].Mapped), ChartX(b.Start), ChartY(b.Mapped), riser, 1);
                r.StrokeDashArray = [2, 3];
                c.Children.Add(r);
            }
            if (ShowsValueLabel(b))
                c.Children.Add(Label(b.Mapped.ToString(CultureInfo.InvariantCulture), mono, 9.5, fg,
                                     (ChartX(b.Start) + ChartX(b.End + 1)) / 2, ChartY(b.Mapped) - 6, TextAlignment.Center));
        }

        c.Children.Add(_markerLine);
        c.Children.Add(_markerDot);
        c.Children.Add(_markerLabel);
    }

    private static object BuildTip(QualityBand b)
    {
        // TipText ends with the mapped value; that tail is the cyan run (mock .tip b).
        var full = TipText(b);
        var mapped = b.Mapped.ToString(CultureInfo.InvariantCulture);
        var text = new TextBlock { FontFamily = Hud.Font("MonoFont"), FontSize = 10.5, Foreground = Hud.Br("FgBrush") };
        text.Inlines.Add(new Run(full[..^mapped.Length]));
        text.Inlines.Add(new Run(mapped) { Foreground = Hud.Br("CyanBrush"), FontWeight = FontWeights.Medium });
        // Sizes arrive in the target's device units, so the step height is scaled by the same factor.
        double stepFrac = (ChartY(b.Mapped) - T) / (H - T - B);
        return new ToolTip
        {
            Content = text, Background = Hud.Br("Bg3Brush"), BorderBrush = Hud.Br("BorderBrush"),
            BorderThickness = new Thickness(1), Padding = new Thickness(8, 5, 8, 5), HasDropShadow = false,
            CustomPopupPlacementCallback = (popup, target, _) =>
            {
                double unit = target.Height / (H - T - B);
                return [new System.Windows.Controls.Primitives.CustomPopupPlacement(
                    new Point((target.Width - popup.Width) / 2, target.Height * stepFrac - popup.Height - 10 * unit),
                    System.Windows.Controls.Primitives.PopupPrimaryAxis.Horizontal)];
            },
        };
    }

    // Acts on release over the element, like a button: a press that slides off does not pick.
    private static void PickOnRelease(FrameworkElement el, Action pick)
    {
        el.MouseLeftButtonDown += (_, e) => { el.CaptureMouse(); e.Handled = true; };
        el.MouseLeftButtonUp += (_, e) =>
        {
            bool wasPressed = el.IsMouseCaptured;
            bool inside = Inside(el, e);
            el.ReleaseMouseCapture();
            if (!wasPressed) return;
            e.Handled = true;
            if (inside) pick();
        };
    }

    // Same release rule as ButtonBase: the pointer must still be within the element's own bounds.
    private static bool Inside(FrameworkElement el, MouseEventArgs e)
    {
        var p = e.GetPosition(el);
        return p.X >= 0 && p.Y >= 0 && p.X <= el.ActualWidth && p.Y <= el.ActualHeight;
    }

    private void PlaceMarker(bool animate)
    {
        var cur = BandAt(_bands, _current);
        var vis = cur is null ? Visibility.Collapsed : Visibility.Visible;
        _markerLine.Visibility = _markerDot.Visibility = _markerLabel.Visibility = vis;
        if (cur is null) return;

        double x = ChartX(_current), y = ChartY(cur.Mapped);
        _markerLine.Y1 = ChartY(0);
        Move(_markerLine, Line.X1Property, x, animate);
        Move(_markerLine, Line.X2Property, x, animate);
        Move(_markerLine, Line.Y2Property, y, animate);
        Move(_markerDot, Canvas.LeftProperty, x - _markerDot.Width / 2, animate);
        Move(_markerDot, Canvas.TopProperty, y - _markerDot.Height / 2, animate);

        _markerLabel.Text = MarkerText(_current, cur.Mapped);
        bool left = MarkerLabelLeft(_current);
        PlaceText(_markerLabel, x + (left ? -10 : 10), y - 10, left ? TextAlignment.Right : TextAlignment.Left);
    }

    private static void Move(DependencyObject target, DependencyProperty dp, double to, bool animate)
    {
        if (!animate || Motion.Reduced)
        {
            ((IAnimatable)target).BeginAnimation(dp, null);
            target.SetValue(dp, to);
            return;
        }
        var from = (double)target.GetValue(dp);
        if (double.IsNaN(from)) from = to;
        target.SetValue(dp, to);
        ((IAnimatable)target).BeginAnimation(dp,
            new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(Motion.QuickRevealMs)) { EasingFunction = Motion.Settle });
    }

    private bool IsCurrent(QualityBand b) => b.Start <= _current && _current <= b.End;

    private void FadeTint(Rectangle hl, double to)
    {
        if (Motion.Reduced)
        {
            hl.BeginAnimation(OpacityProperty, null);
            hl.Opacity = to;
            return;
        }
        hl.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(Motion.HoverMs)));
    }

    // ── Table ───────────────────────────────────────────────────────────────

    private StackPanel BuildTable(FontFamily mono)
    {
        var line = Hud.Br("NavBorderBrush");
        var table = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };

        var header = RowGrid();
        string[] heads = ["YOU HOLD", "CRAFTS AS", "VS BAND MIDDLE"];
        for (int i = 0; i < heads.Length; i++)
        {
            var th = new TextBlock { Text = heads[i], FontFamily = mono, FontSize = 10, Foreground = Hud.Br("FgDimBrush"), Margin = new Thickness(2, 0, 6, 6) };
            Grid.SetColumn(th, i);
            header.Children.Add(th);
        }
        table.Children.Add(new Border { Child = header, BorderBrush = line, BorderThickness = new Thickness(0, 0, 0, 1) });

        foreach (var b in _bands)
        {
            var g = RowGrid();
            string[] texts = [HoldText(b), b.Mapped.ToString(CultureInfo.InvariantCulture), VsMiddleText(b)];
            var cells = new TextBlock[texts.Length];
            for (int i = 0; i < texts.Length; i++)
            {
                cells[i] = new TextBlock
                {
                    Text = texts[i], FontFamily = mono, FontSize = 11, Margin = new Thickness(2, 5, 6, 5),
                    FontWeight = i == 1 ? FontWeights.Medium : FontWeights.Normal,
                };
                Grid.SetColumn(cells[i], i);
                g.Children.Add(cells[i]);
            }
            var row = new Border
            {
                Child = g, BorderBrush = line, BorderThickness = new Thickness(0, 0, 0, 1),
                Background = Brushes.Transparent, Cursor = Cursors.Hand,
            };
            var hover = Hud.Br("HighlightBrush");
            Hud.Hoverable(row, on => row.Background = on ? hover : Brushes.Transparent);
            var band = b;
            PickOnRelease(row, () => Pick(band.Start));
            _rows.Add((row, cells));
            table.Children.Add(row);
        }
        ShowActive(animate: false);
        return table;
    }

    // Column widths follow the mock table's auto layout, roughly in proportion to each column's widest text.
    private static Grid RowGrid()
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4, GridUnitType.Star) });
        return g;
    }

    /// <summary>Lights the current band: its chart tint and its table row (text FgBrush, value CyanBrush).</summary>
    private void ShowActive(bool animate)
    {
        for (int i = 0; i < _bands.Count; i++)
        {
            bool on = IsCurrent(_bands[i]);
            if (i < _bandTints.Count && !_bandTints[i].Hit.IsMouseOver)
            {
                if (animate) FadeTint(_bandTints[i].Tint, on ? 1 : 0);
                else _bandTints[i].Tint.Opacity = on ? 1 : 0;
            }
            if (i >= _rows.Count) continue;
            var cells = _rows[i].Cells;
            for (int k = 0; k < cells.Length; k++)
                cells[k].Foreground = on ? (k == 1 ? Hud.Br("CyanBrush") : Hud.Br("FgBrush")) : _rowBrush;
        }
    }

    // ── Pick, open, close ───────────────────────────────────────────────────

    private void Pick(int q)
    {
        _current = Math.Clamp(q, 0, MaxQuality);
        Logger.Info($"[UI] bands pick {_resource} {_current}");
        PlaceMarker(animate: true);
        ShowActive(animate: true);
        BandPicked?.Invoke(_current);
    }

    private void Open()
    {
        if (Motion.Reduced) return;
        var d = TimeSpan.FromMilliseconds(Motion.DialogOpenMs);
        _scrim.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, d) { EasingFunction = Motion.Settle });
        _panel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, d) { EasingFunction = Motion.Settle });
        var grow = new DoubleAnimation(0.96, 1, d) { EasingFunction = Motion.Settle };
        _panelScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        _panelScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        _panelRise.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, d) { EasingFunction = Motion.Settle });
    }

    private void RequestClose()
    {
        if (_closing) return;
        _closing = true;
        if (Motion.Reduced) { Close(); return; }
        // Mock exit: opacity 0, scale .98, y 4 over 120 ms; exits ease on SlideOut by the app convention.
        var d = TimeSpan.FromMilliseconds(Motion.DialogCloseMs);
        var fade = new DoubleAnimation(0, d) { EasingFunction = Motion.SlideOut };
        fade.Completed += (_, _) => Close();
        _scrim.BeginAnimation(OpacityProperty, new DoubleAnimation(0, d) { EasingFunction = Motion.SlideOut });
        var shrink = new DoubleAnimation(0.98, d) { EasingFunction = Motion.SlideOut };
        _panelScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        _panelScale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
        _panelRise.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(4, d) { EasingFunction = Motion.SlideOut });
        _panel.BeginAnimation(OpacityProperty, fade);
    }

    protected override void OnClosed(EventArgs e)
    {
        Logger.Info("[UI] bands close");
        base.OnClosed(e);
    }

    // Mock .close: 28 px, radius 4, line border, dim glyph; hover fg, amber-strong border, highlight fill.
    private Border BuildCloseButton()
    {
        var glyph = new TextBlock
        {
            Text = "×", FontFamily = Hud.Font("UiFont"), FontSize = 14, Foreground = Hud.Br("FgDimBrush"),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var btn = new Border
        {
            Width = 28, Height = 28, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1),
            BorderBrush = Hud.Br("NavBorderBrush"), Background = Brushes.Transparent, Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Top, Child = glyph, ToolTip = "Close",
        };
        Hud.Hoverable(btn, on =>
        {
            glyph.Foreground = Hud.Br(on ? "FgBrush" : "FgDimBrush");
            btn.BorderBrush = Hud.Br(on ? "AccentStrongBrush" : "NavBorderBrush");
            btn.Background = on ? Hud.Br("HighlightBrush") : Brushes.Transparent;
        });
        btn.MouseLeftButtonDown += (_, e) => { e.Handled = true; RequestClose(); };
        return btn;
    }

    // ── Drawing helpers ─────────────────────────────────────────────────────

    private static SolidColorBrush Fresh(Color c, double alpha)
    {
        var b = new SolidColorBrush(Color.FromArgb((byte)Math.Round(alpha * 255), c.R, c.G, c.B));
        b.Freeze();
        return b;
    }

    private static Line Seg(double x1, double y1, double x2, double y2, Brush stroke, double thick) => new()
    {
        X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = stroke, StrokeThickness = thick, IsHitTestVisible = false,
    };

    private static TextBlock Label(string text, FontFamily font, double size, Brush fg, double x, double baseline, TextAlignment anchor)
    {
        var tb = new TextBlock { Text = text, FontFamily = font, FontSize = size, Foreground = fg, IsHitTestVisible = false };
        PlaceText(tb, x, baseline, anchor);
        return tb;
    }

    /// <summary>Positions a TextBlock the way SVG text sits: x by its anchor, y on the baseline.</summary>
    private static void PlaceText(TextBlock tb, double x, double baseline, TextAlignment anchor)
    {
        tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double w = tb.DesiredSize.Width;
        double left = anchor switch { TextAlignment.Center => x - w / 2, TextAlignment.Right => x - w, _ => x };
        Canvas.SetLeft(tb, left);
        Canvas.SetTop(tb, baseline - tb.FontFamily.Baseline * tb.FontSize);
    }

    // The mock rotates the y-axis label -90 degrees about (12, middle) with the text centred there.
    private static TextBlock VerticalLabel(string text, FontFamily font, double size, Brush fg, double x, double y)
    {
        var tb = new TextBlock { Text = text, FontFamily = font, FontSize = size, Foreground = fg, IsHitTestVisible = false };
        tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double w = tb.DesiredSize.Width, h = tb.DesiredSize.Height;
        double baselineOffset = font.Baseline * size;
        // Unrotated, the text box spans [x - w/2, x + w/2] with its baseline at y; rotate about that baseline point.
        Canvas.SetLeft(tb, x - w / 2);
        Canvas.SetTop(tb, y - baselineOffset);
        tb.RenderTransformOrigin = new Point(0.5, baselineOffset / h);
        tb.RenderTransform = new RotateTransform(-90);
        return tb;
    }
}
