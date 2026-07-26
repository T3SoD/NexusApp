using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using NexusApp.Services;
using Path = System.Windows.Shapes.Path;   // disambiguate from System.IO.Path (implicit usings)

namespace NexusApp.Views;

/// <summary>
/// Decode sizing for the guide images. Kept separate from the viewer (and free of WPF types)
/// because it is the one part of the memory strategy worth proving in tests: the guides are up
/// to 5.6k pixels wide, so a native decode of the largest costs about 116 MB.
///
/// Tiers: while the image sits at (or near) fit, decode at about twice the viewport width, which
/// is plenty for a downscaled view. Once the zoom would ask for more pixels than that decode has,
/// upgrade once to native. Nothing ever exceeds native, and the overlay passes a hard cap.
/// </summary>
public static class GuideDecode
{
    // Decode headroom over the viewport: enough for a crisp fit view plus a notch or two of zoom.
    private const double ViewportFactor = 2.0;
    // Anything within 5% of fit still counts as "parked at fit", so a rounding wobble in the fit
    // computation cannot trigger a full-resolution decode on open.
    private const double FitSlack = 1.05;

    /// <summary>
    /// The DecodePixelWidth to load this guide at. Always an explicit width, never the 0 sentinel,
    /// so the caller can compare it against the width it already decoded and re-decode only on a change.
    /// </summary>
    /// <param name="nativeW">The guide's real pixel width.</param>
    /// <param name="viewportW">Width of the image viewport, or 0 before layout has measured it.</param>
    /// <param name="scale">Current zoom, where 1.0 is native pixels.</param>
    /// <param name="fitScale">The zoom at which the whole image fits the viewport.</param>
    /// <param name="cap">Hard ceiling on the decode (the overlay caps at 4096); 0 or less means no cap.</param>
    public static int PickDecodeWidth(int nativeW, double viewportW, double scale, double fitScale, int cap)
    {
        if (nativeW <= 0) return 0;

        // Never decode above native (upsampling a PNG buys nothing) and never above the caller's cap.
        int ceiling = cap > 0 ? Math.Min(nativeW, cap) : nativeW;

        // No measured viewport means no basis for subsampling, so fall back to the honest full decode.
        // The viewer never asks until its canvas has a size; this only guards a mis-ordered call.
        if (viewportW <= 0) return ceiling;

        int baseline = (int)Math.Min(ceiling, Math.Max(1, Math.Ceiling(viewportW * ViewportFactor)));
        if (fitScale <= 0 || scale <= fitScale * FitSlack) return baseline;

        // Zoomed past what the baseline decode can serve one-to-one: go to native (capped) once.
        return scale * nativeW > baseline ? ceiling : baseline;
    }
}

/// <summary>
/// The shared Mission Guides image viewer: a HUD rail row above a zoom and pan canvas, used by
/// the Mission Guides page and by the overlay's GUIDES tab (compact mode shrinks the rail and
/// caps the decode). Mouse only, as the app is throughout: wheel zooms on the cursor, left drag
/// pans, double click returns to fit, and the rail carries Back / zoom out / fit / zoom in.
///
/// All of the fiddly math lives in <see cref="GuideViewMath"/> and <see cref="GuideDecode"/>, so
/// this class is layout, input plumbing and bitmap lifetime. Layout values come from the approved
/// framer mock (nexus-design-lab\guides\index.html) and its value manifest.
/// </summary>
public sealed class GuideViewer : UserControl
{
    // One wheel notch or one button press. Invented in the mock: the app had no zoom step before.
    private const double ZoomStep = 1.2;
    // Overlay decodes are capped on the long edge (spec "Memory and decode strategy").
    private const int OverlayDecodeCap = 4096;
    // Entrance rise of the rail, mock "Rail in".
    private const double RailDropPx = 8;
    private const double OpenScaleFrom = 0.98;   // DialogMotion.OpenScaleFrom

    private readonly bool _compact;
    private readonly int _decodeCap;

    private readonly DockPanel _root = new();
    private readonly ScaleTransform _rootScale = new(1, 1);
    private readonly Grid _rail;
    private readonly TranslateTransform _railDrop = new();
    private readonly Grid _canvasHost = new();
    private readonly Image _image = new();
    private readonly MatrixTransform _imageTransform = new();
    private readonly TextBlock _categoryText = new();
    private readonly TextBlock _titleText = new();
    private readonly Run _zoomPercent = new();
    private readonly Run _zoomTag = new();
    private readonly TextBlock _unavailable;

    private GuideEntry? _guide;
    private double _scale;      // 0 until the canvas has been measured and a guide is showing
    private double _panX, _panY;
    private bool _atFit;
    private int _decodedWidth;
    private bool _decodeFailed;

    private bool _dragging;
    private Point _dragFrom;
    private double _dragPanX, _dragPanY;

    /// <summary>Raised by the rail's Back button. The host page decides what to show instead.</summary>
    public event EventHandler? BackRequested;

    public GuideViewer(bool compact)
    {
        _compact = compact;
        _decodeCap = compact ? OverlayDecodeCap : 0;

        _rail = BuildRail();
        _rail.RenderTransform = _railDrop;
        DockPanel.SetDock(_rail, Dock.Top);
        _root.Children.Add(_rail);

        _root.Children.Add(BuildCanvas());
        _root.LastChildFill = true;
        _root.RenderTransform = _rootScale;
        _root.RenderTransformOrigin = new Point(0.5, 0.5);

        _unavailable = new TextBlock
        {
            Text = "Guide image unavailable.",
            FontFamily = Hud.Font("UiFont"), FontSize = 12.5, Foreground = Hud.Br("FgDimBrush"),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        _canvasHost.Children.Add(_unavailable);

        Content = _root;
    }

    // -- public surface ------------------------------------------------------------

    /// <summary>Decode this guide (at the tier its size and the viewport call for) and park it at fit.</summary>
    public void Show(GuideEntry guide)
    {
        _guide = guide;
        _categoryText.Text = guide.Category.ToUpperInvariant();
        _titleText.Text = guide.Title;

        _scale = 0; _panX = 0; _panY = 0; _atFit = false;
        _decodedWidth = 0; _decodeFailed = false;
        _image.Source = null;
        _image.Width = guide.NativeWidth;
        _image.Height = guide.NativeHeight;
        _unavailable.Visibility = Visibility.Collapsed;

        // Before the first layout pass the canvas has no size; SizeChanged fits it the moment it does.
        ResetToFit();
        PlayEntrance();
    }

    /// <summary>Release the decoded bitmap and reset the view state. Called when the host navigates away.</summary>
    public void Clear()
    {
        if (_dragging) EndDrag();
        _guide = null;
        _image.Source = null;          // the only reference to the full-size bitmap
        _image.Width = 0; _image.Height = 0;
        _imageTransform.Matrix = Matrix.Identity;
        _scale = 0; _panX = 0; _panY = 0; _atFit = false;
        _decodedWidth = 0; _decodeFailed = false;
        _unavailable.Visibility = Visibility.Collapsed;
        _zoomPercent.Text = string.Empty;
        _zoomTag.Text = string.Empty;
    }

    // -- view state ----------------------------------------------------------------

    private double CurrentFit() => _guide is null
        ? 1.0
        : GuideViewMath.FitScale(_guide.NativeWidth, _guide.NativeHeight, _canvasHost.ActualWidth, _canvasHost.ActualHeight);

    // Single write path for scale and pan: clamp both, push the transform, keep the decode tier
    // and the readout in step. Direct manipulation, never animated (spec "Viewer behavior").
    private void Commit(double scale, double panX, double panY)
    {
        if (_guide is null) return;
        double boxW = _canvasHost.ActualWidth, boxH = _canvasHost.ActualHeight;
        if (boxW < 2 || boxH < 2) return;

        double fit = GuideViewMath.FitScale(_guide.NativeWidth, _guide.NativeHeight, boxW, boxH);
        _scale = GuideViewMath.ClampScale(scale, fit);
        var (x, y) = GuideViewMath.ClampPan(panX, panY,
            _guide.NativeWidth * _scale, _guide.NativeHeight * _scale, boxW, boxH);
        _panX = x; _panY = y;
        _imageTransform.Matrix = new Matrix(_scale, 0, 0, _scale, _panX, _panY);
        _atFit = Math.Abs(_scale - fit) < 0.0001;

        EnsureDecode(boxW, fit);
        UpdateReadout();
    }

    private void ResetToFit()
    {
        if (_guide is null) return;
        Commit(CurrentFit(), 0, 0);   // ClampPan centers a fitted image on both axes
    }

    private void ZoomBy(double notches, Point anchor)
    {
        if (_guide is null || _scale <= 0) return;
        double target = _scale * Math.Pow(ZoomStep, notches);
        var (s, x, y) = GuideViewMath.ZoomAt(anchor.X, anchor.Y, _scale, target, _panX, _panY, CurrentFit());
        Commit(s, x, y);
    }

    private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_guide is null) return;
        // A resize changes fit, so an image parked at fit follows the new fit; a zoomed-in view keeps
        // its zoom and is only re-clamped back into the new bounds.
        if (_scale <= 0 || _atFit) ResetToFit();
        else Commit(_scale, _panX, _panY);
    }

    // -- bitmap lifetime -----------------------------------------------------------

    // At most ONE decoded bitmap is alive: assigning a new Source drops the previous one. The
    // decode is skipped entirely when the chosen width has not changed, so panning and small zoom
    // steps never re-decode.
    private void EnsureDecode(double viewportW, double fit)
    {
        if (_guide is null || _decodeFailed) return;
        int want = GuideDecode.PickDecodeWidth(_guide.NativeWidth, viewportW, _scale, fit, _decodeCap);
        if (want <= 0 || (want == _decodedWidth && _image.Source != null)) return;

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(_guide.PackUri, UriKind.Absolute);
            // IgnoreImageCache: WPF caches decoded images per URI, and the cached copy would come
            // back at the OLD DecodePixelWidth, defeating the upgrade to native.
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            if (want < _guide.NativeWidth) bmp.DecodePixelWidth = want;
            bmp.EndInit();
            bmp.Freeze();
            _image.Source = bmp;
            _decodedWidth = want;
            _unavailable.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            // Never crash on a bad or missing resource: show the placeholder and log it. Guide ids
            // only in log strings, never titles or creator names.
            _decodeFailed = true;
            _image.Source = null;
            _unavailable.Visibility = Visibility.Visible;
            Logger.Error($"[UI] guide image unavailable: {_guide.Id}", ex);
        }
    }

    // -- rail --------------------------------------------------------------------

    private Grid BuildRail()
    {
        double gap = _compact ? 6 : 10;
        var row = new Grid { VerticalAlignment = VerticalAlignment.Stretch };
        for (int i = 0; i < 7; i++)
            row.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = i == 2 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto,
            });

        var backBtn = AccentGlyphButton("M15,5 L8,12 L15,19", _compact ? 11 : 13, "Back", "Back to the guide list");
        backBtn.Click += (_, _) =>
        {
            InteractionLog.Click("Back", backBtn);
            BackRequested?.Invoke(this, EventArgs.Empty);
        };
        Grid.SetColumn(backBtn, 0);
        row.Children.Add(backBtn);

        if (!_compact)
        {
            var sep = new Border
            {
                Width = 1, Height = 22, Background = Hud.Br("NavBorderBrush"),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(gap, 0, 0, 0),
            };
            Grid.SetColumn(sep, 1);
            row.Children.Add(sep);
        }

        var titleStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(gap, 0, 0, 0),
        };
        _categoryText.FontFamily = Hud.Font("MonoFont");
        _categoryText.FontSize = 9;
        _categoryText.Foreground = Hud.Br("CyanBrush");
        _categoryText.TextTrimming = TextTrimming.CharacterEllipsis;
        _categoryText.Visibility = _compact ? Visibility.Collapsed : Visibility.Visible;
        _titleText.FontFamily = Hud.Font("DisplayFont");
        _titleText.FontSize = _compact ? 12 : 15;
        _titleText.FontWeight = FontWeights.Bold;
        _titleText.Foreground = Hud.Br("FgBrush");
        _titleText.TextTrimming = TextTrimming.CharacterEllipsis;
        titleStack.Children.Add(_categoryText);
        titleStack.Children.Add(_titleText);
        Grid.SetColumn(titleStack, 2);
        row.Children.Add(titleStack);

        var zoomOut = GhostGlyphButton("M5,12 L19,12", _compact ? 11 : 14, null, "Zoom out");
        zoomOut.Margin = new Thickness(gap, 0, 0, 0);
        zoomOut.Click += (_, _) => { InteractionLog.Click("Zoom out", zoomOut); ZoomBy(-1, CanvasCenter()); };
        Grid.SetColumn(zoomOut, 3);
        row.Children.Add(zoomOut);

        var fitBtn = GhostGlyphButton("M4,9 L4,4 L9,4 M15,4 L20,4 L20,9 M20,15 L20,20 L15,20 M9,20 L4,20 L4,15",
                                      _compact ? 11 : 13, _compact ? null : "Fit", "Reset to fit");
        fitBtn.Margin = new Thickness(gap, 0, 0, 0);
        fitBtn.Click += (_, _) => { InteractionLog.Click("Fit", fitBtn); ResetToFit(); };
        Grid.SetColumn(fitBtn, 4);
        row.Children.Add(fitBtn);

        var zoomIn = GhostGlyphButton("M12,5 L12,19 M5,12 L19,12", _compact ? 11 : 14, null, "Zoom in");
        zoomIn.Margin = new Thickness(gap, 0, 0, 0);
        zoomIn.Click += (_, _) => { InteractionLog.Click("Zoom in", zoomIn); ZoomBy(1, CanvasCenter()); };
        Grid.SetColumn(zoomIn, 5);
        row.Children.Add(zoomIn);

        var readout = new TextBlock
        {
            FontFamily = Hud.Font("MonoFont"), FontSize = _compact ? 9 : 10,
            Foreground = Hud.Br("FgDimBrush"), MinWidth = _compact ? 46 : 64,
            TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(gap, 0, 0, 0),
        };
        _zoomPercent.Foreground = Hud.Br("FgBrush");
        _zoomPercent.FontWeight = FontWeights.Medium;
        _zoomTag.Foreground = Hud.Br("AccentBrush");
        readout.Inlines.Add(_zoomPercent);
        readout.Inlines.Add(_zoomTag);
        Grid.SetColumn(readout, 6);
        row.Children.Add(readout);

        // Overlay-panel fill + BorderBrush line + the shared panel glow, chamfered like the rest of the HUD.
        var rail = Hud.Panel(row, chamfer: _compact ? 7 : 10, glow: true,
                             bg: Hud.Br("OverlayBgTopBrush"), border: Hud.Br("BorderBrush"),
                             padding: new Thickness(_compact ? 6 : 10, 0, _compact ? 6 : 10, 0));
        rail.Name = "GuideViewerRail";   // gives InteractionLog a region name for the rail buttons
        rail.Height = _compact ? 34 : 44;
        rail.Margin = _compact ? new Thickness(8, 8, 8, 6) : new Thickness(12, 12, 12, 10);
        return rail;
    }

    // Percent of NATIVE pixels (scale is native-relative), with the bound the view is sitting on
    // tagged in amber. Fit wins the tag when a tiny viewport puts fit above the 2x ceiling.
    private void UpdateReadout()
    {
        if (_guide is null || _scale <= 0) { _zoomPercent.Text = string.Empty; _zoomTag.Text = string.Empty; return; }
        _zoomPercent.Text = $"{Math.Round(_scale * 100)}%";
        _zoomTag.Text = _atFit ? " fit"
                      : Math.Abs(_scale - GuideViewMath.MaxScale()) < 0.0001 ? " max"
                      : string.Empty;
    }

    // -- canvas ------------------------------------------------------------------

    private UIElement BuildCanvas()
    {
        _canvasHost.Background = Hud.Br("BgBrush");   // the void backdrop the mock viewer sits on
        _canvasHost.ClipToBounds = true;
        _canvasHost.Cursor = Cursors.Hand;

        _image.Stretch = Stretch.Fill;                // layout size is the native size; the transform scales it
        _image.SnapsToDevicePixels = true;
        _image.RenderTransform = _imageTransform;
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);

        // A Canvas reports no desired size, so a 5500px-wide image never inflates the layout of
        // whatever hosts the viewer; the host Grid stays sized by its container.
        var canvas = new Canvas();
        Canvas.SetLeft(_image, 0);
        Canvas.SetTop(_image, 0);
        canvas.Children.Add(_image);
        _canvasHost.Children.Add(canvas);

        _canvasHost.SizeChanged += OnCanvasSizeChanged;
        _canvasHost.MouseWheel += OnCanvasWheel;
        _canvasHost.MouseLeftButtonDown += OnCanvasLeftDown;
        _canvasHost.MouseMove += OnCanvasMouseMove;
        _canvasHost.MouseLeftButtonUp += (_, _) => EndDrag();
        _canvasHost.LostMouseCapture += (_, _) => EndDrag();
        return _canvasHost;
    }

    private Point CanvasCenter() => new(_canvasHost.ActualWidth / 2, _canvasHost.ActualHeight / 2);

    private void OnCanvasWheel(object sender, MouseWheelEventArgs e)
    {
        if (_guide is null || _scale <= 0 || e.Delta == 0) return;
        ZoomBy(e.Delta > 0 ? 1 : -1, e.GetPosition(_canvasHost));
        e.Handled = true;
    }

    private void OnCanvasLeftDown(object sender, MouseButtonEventArgs e)
    {
        if (_guide is null || _scale <= 0) return;
        if (e.ClickCount == 2) { ResetToFit(); e.Handled = true; return; }   // double click resets to fit

        _dragging = true;
        _dragFrom = e.GetPosition(_canvasHost);
        _dragPanX = _panX; _dragPanY = _panY;
        _canvasHost.Cursor = Cursors.SizeAll;
        _canvasHost.CaptureMouse();
        e.Handled = true;
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var p = e.GetPosition(_canvasHost);
        Commit(_scale, _dragPanX + (p.X - _dragFrom.X), _dragPanY + (p.Y - _dragFrom.Y));
    }

    private void EndDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        _canvasHost.Cursor = Cursors.Hand;
        if (_canvasHost.IsMouseCaptured) _canvasHost.ReleaseMouseCapture();
    }

    // -- entrance ------------------------------------------------------------------

    // The dialog entrance signature (180ms opacity + 0.98 scale, Settle) with the rail dropping in
    // behind it (240ms, 8px, Reveal). Reduce animations snaps straight to the final state.
    private void PlayEntrance()
    {
        _root.BeginAnimation(OpacityProperty, null);
        _rootScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _rootScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _rail.BeginAnimation(OpacityProperty, null);
        _railDrop.BeginAnimation(TranslateTransform.YProperty, null);

        if (Motion.Reduced)
        {
            _root.Opacity = 1; _rootScale.ScaleX = 1; _rootScale.ScaleY = 1;
            _rail.Opacity = 1; _railDrop.Y = 0;
            return;
        }

        _root.Opacity = 0;
        _rail.Opacity = 0;
        _railDrop.Y = -RailDropPx;

        var open = new Duration(TimeSpan.FromMilliseconds(Motion.DialogOpenMs));
        _root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, open) { EasingFunction = Motion.Settle });
        var grow = new DoubleAnimation(OpenScaleFrom, 1, open) { EasingFunction = Motion.Settle };
        _rootScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        _rootScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);

        var railIn = new Duration(TimeSpan.FromMilliseconds(Motion.DrillMs));
        var after = TimeSpan.FromMilliseconds(Motion.DialogOpenMs);
        _rail.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, railIn) { BeginTime = after, EasingFunction = Motion.Reveal });
        _railDrop.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(-RailDropPx, 0, railIn) { BeginTime = after, EasingFunction = Motion.Reveal });
    }

    // -- small builders ------------------------------------------------------------

    // A 24x24 stroke-only line glyph in the dock's glyph language (stroke 1.6, round caps and joins),
    // scaled to size by a Viewbox exactly as the mock's SVGs are.
    private static (Viewbox Box, Path Stroke) Glyph(string data, double size)
    {
        var path = new Path
        {
            Data = Geometry.Parse(data),
            StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        };
        var box = new Canvas { Width = 24, Height = 24 };
        box.Children.Add(path);
        return (new Viewbox { Width = size, Height = size, Stretch = Stretch.Uniform, Child = box }, path);
    }

    private static StackPanel GlyphContent(FrameworkElement glyph, string? text, double gap)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        glyph.VerticalAlignment = VerticalAlignment.Center;
        content.Children.Add(glyph);
        if (!string.IsNullOrEmpty(text))
            content.Children.Add(new TextBlock { Text = text, Margin = new Thickness(gap, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        return content;
    }

    // Ghost rail button (ChamferButton): dim glyph and label that both go Fg on hover. The glyph
    // stroke is set from the same hover handlers the template uses for the text, so the two match.
    private Button GhostGlyphButton(string glyphData, double glyphSize, string? text, string tip)
    {
        var (box, stroke) = Glyph(glyphData, glyphSize);
        stroke.Stroke = Hud.Br("FgDimBrush");
        var btn = new Button
        {
            Style = (Style)Application.Current.FindResource("ChamferButton"),
            Content = GlyphContent(box, text, 6),
            Height = _compact ? 24 : 28,
            Padding = new Thickness(text is null ? 0 : (_compact ? 9 : 12), 0, text is null ? 0 : (_compact ? 9 : 12), 0),
            FontSize = _compact ? 11 : 13,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = tip,
        };
        if (text is null) btn.Width = _compact ? 24 : 28;
        btn.MouseEnter += (_, _) => stroke.Stroke = Hud.Br("FgBrush");
        btn.MouseLeave += (_, _) => stroke.Stroke = Hud.Br("FgDimBrush");
        return btn;
    }

    // Primary rail button (ChamferAccentButton): amber fill, on-accent glyph and label.
    private Button AccentGlyphButton(string glyphData, double glyphSize, string text, string tip)
    {
        var (box, stroke) = Glyph(glyphData, glyphSize);
        stroke.Stroke = Hud.Br("OnAccentBrush");
        return new Button
        {
            Style = (Style)Application.Current.FindResource("ChamferAccentButton"),
            Content = GlyphContent(box, text, 6),
            Height = _compact ? 24 : 28,
            Padding = new Thickness(_compact ? 10 : 13, 0, _compact ? 10 : 13, 0),
            FontSize = _compact ? 11 : 13,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = tip,
        };
    }
}
