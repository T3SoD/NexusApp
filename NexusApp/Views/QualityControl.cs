using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using NexusApp.Models;
using NexusApp.Services;

namespace NexusApp.Views;

/// <summary>
/// One ore's held-quality control on a Blueprint Library bill-of-materials row: a typed
/// Q field and band readout above a 0-1000 slider with ticks at the ore's band starts.
/// Values and layout follow library-redesign direction 2 (.qctl, .qread, .gbtn, .sl).
/// The control only reports changes; persistence and the forecast refresh are the caller's.
/// </summary>
public sealed class QualityControl : Grid
{
    private const int MaxQuality = 1000;
    private const double SliderHeight = 22;
    private const double KnobSize = 14;
    private const double HaloSize = 22;   // 14 px knob + the mock's 4 px box-shadow ring

    private readonly IReadOnlyList<QualityBand> _bands;
    private readonly TextBox _field;
    private readonly TextBlock _bandText;
    private readonly Button _graphBtn;
    private readonly Canvas _slider;
    private readonly Rectangle _rail;
    private readonly Rectangle _fill;
    private readonly ScaleTransform _fillScale = new(0, 1);
    private readonly Grid _knobHost;
    private readonly TranslateTransform _knobShift = new();
    private readonly Border _knob;
    private readonly Border _halo;
    private readonly ScaleTransform _haloScale = new(1, 1);
    private readonly List<Rectangle> _ticks = [];
    private readonly Brush _rangeBrush;   // the mock's lab-fg #B9C4D0 has no app key: FgBrush at 0.75

    private int _value;
    private int _committed;      // last value reported through QualityChanged
    private bool _dragging;

    /// <summary>Raised with the new value and its source: "typed", "drag", "preset" or "bands".</summary>
    public event Action<int, string>? QualityChanged;
    public event Action? GraphRequested;
    /// <summary>Raised on every value step while the knob is dragged, so a preview can follow the mouse. Not a commit.</summary>
    public event Action<int>? ValueChanging;

    public int Value => _value;

    public QualityControl(string resource, IReadOnlyList<QualityBand> bands, int initial)
    {
        _bands = bands;
        var range = Hud.Br("FgBrush").Clone();
        range.Opacity = 0.75;
        range.Freeze();
        _rangeBrush = range;
        _value = _committed = Math.Clamp(initial, 0, MaxQuality);

        var mono = (FontFamily)Application.Current.FindResource("MonoFont");
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // ── Readout: "Q [780]" left, "band 700-799, crafts as 710 [graph]" right ──
        var read = new Grid { Margin = new Thickness(0, 0, 0, 5) };
        read.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        read.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var q = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        q.Children.Add(new TextBlock
        {
            Text = "Q", FontFamily = mono, FontSize = 13, Foreground = Hud.Br("FgBrush"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0),
        });
        _field = BuildField(mono);
        q.Children.Add(_field);
        read.Children.Add(q);

        var band = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _bandText = new TextBlock { FontFamily = mono, FontSize = 10, Foreground = Hud.Br("FgDimBrush"), VerticalAlignment = VerticalAlignment.Center };
        band.Children.Add(_bandText);
        _graphBtn = BuildGraphButton(resource);
        // An ore without a band table has nothing for the bands popup to draw.
        if (bands.Count == 0) _graphBtn.Visibility = Visibility.Collapsed;
        band.Children.Add(_graphBtn);
        Grid.SetColumn(band, 1);
        read.Children.Add(band);
        SetRow(read, 0);
        Children.Add(read);

        // ── Slider: rail, amber fill, band-start ticks, knob ──
        _slider = new Canvas
        {
            Height = SliderHeight, Background = Brushes.Transparent, Cursor = Cursors.Hand,
            ClipToBounds = false, ToolTip = $"{resource} quality, 0 to 1000",
        };
        _rail = new Rectangle { Height = 2, Fill = Hud.Br("BorderBrush") };
        Canvas.SetTop(_rail, 10);
        _slider.Children.Add(_rail);
        _fill = new Rectangle { Height = 2, Fill = Hud.Br("AccentBrush"), RenderTransform = _fillScale };
        Canvas.SetTop(_fill, 10);
        _slider.Children.Add(_fill);
        // The first band starts at 0, where the rail itself begins, so it gets no tick (mock: slice(1)).
        for (int i = 1; i < bands.Count; i++)
        {
            var t = new Rectangle { Width = 1, Height = 8, Fill = Hud.Br("BorderBrush"), Tag = bands[i].Start };
            Canvas.SetTop(t, 7);
            _ticks.Add(t);
            _slider.Children.Add(t);
        }

        _halo = new Border
        {
            Width = HaloSize, Height = HaloSize, CornerRadius = new CornerRadius(7),
            Background = Hud.Br("AccentFaintBrush"), Opacity = 0,
            RenderTransform = _haloScale, RenderTransformOrigin = new Point(0.5, 0.5),
        };
        _knob = new Border
        {
            Width = KnobSize, Height = KnobSize, CornerRadius = new CornerRadius(3),
            Background = Hud.Br("Bg3Brush"), BorderBrush = Hud.Br("AccentBrush"), BorderThickness = new Thickness(1),
        };
        _knobHost = new Grid { Width = HaloSize, Height = HaloSize, RenderTransform = _knobShift, IsHitTestVisible = false };
        _knobHost.Children.Add(_halo);
        _knobHost.Children.Add(_knob);
        // Knob centre sits on the rail centre (top 4 + 7 = 11), so the 22 px host starts at 0.
        Canvas.SetTop(_knobHost, SliderHeight / 2 - HaloSize / 2);
        _slider.Children.Add(_knobHost);

        _slider.SizeChanged += (_, _) => Layout(animate: false);
        _slider.MouseEnter += (_, _) => { if (!_dragging) FadeHalo(1); };
        _slider.MouseLeave += (_, _) => { if (!_dragging) FadeHalo(0); };
        _slider.MouseLeftButtonDown += Slider_Down;
        _slider.MouseMove += Slider_Move;
        _slider.MouseLeftButtonUp += (_, e) => { EndDrag(); e.Handled = true; };
        _slider.LostMouseCapture += (_, _) => EndDrag();
        SetRow(_slider, 1);
        Children.Add(_slider);

        // The bill-of-materials row opens the Mining Codex on click; a press inside the control
        // is an edit, never a navigation.
        MouseLeftButtonDown += (_, e) => e.Handled = true;

        ShowValue(animate: false);
    }

    /// <summary>Sets the value from outside the control (a preset or a band pick) and reports it when it changed.</summary>
    public void SetValue(int q, string source)
    {
        _value = Math.Clamp(q, 0, MaxQuality);
        ShowValue(animate: true);
        Commit(source);
    }

    /// <summary>Digits only, one to four characters, clamped to 0-1000; null for anything else.</summary>
    public static int? ParseTyped(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 4) return null;
        foreach (var c in text)
            if (!char.IsAsciiDigit(c)) return null;
        return Math.Clamp(int.Parse(text, System.Globalization.CultureInfo.InvariantCulture), 0, MaxQuality);
    }

    private void Commit(string source)
    {
        if (_value == _committed) return;
        _committed = _value;
        QualityChanged?.Invoke(_value, source);
    }

    // ── Typed field ─────────────────────────────────────────────────────────

    private TextBox BuildField(FontFamily mono)
    {
        // A bare template: the shared NexusTextBox chrome is a boxed field, the mock's is a
        // borderless number with a bottom hairline that only shows on hover and focus.
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
        var host = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        host.SetValue(Control.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        border.AppendChild(host);

        var tb = new TextBox
        {
            Template = new ControlTemplate(typeof(TextBox)) { VisualTree = border },
            FontFamily = mono, FontSize = 13, FontWeight = FontWeights.Medium,
            Foreground = Hud.Br("AccentBrush"), CaretBrush = Hud.Br("AccentBrush"),
            SelectionBrush = Hud.Br("AccentDimBrush"),
            Background = Brushes.Transparent, BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 0, 0, 1),
            Width = 34,   // 4.2ch at 13 px JetBrains Mono
            MaxLength = 4, VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Quality, 0 to 1000",
        };
        InputMethod.SetIsInputMethodEnabled(tb, false);
        tb.PreviewTextInput += (_, e) =>
        {
            foreach (var c in e.Text)
                if (!char.IsAsciiDigit(c)) { e.Handled = true; return; }
        };
        DataObject.AddPastingHandler(tb, (_, e) =>
        {
            var pasted = e.DataObject.GetData(DataFormats.UnicodeText) as string;
            if (ParseTyped(pasted?.Trim() ?? "") == null) e.CancelCommand();
        });
        tb.MouseEnter += (_, _) => StyleField();
        tb.MouseLeave += (_, _) => StyleField();
        tb.GotKeyboardFocus += (_, _) => { StyleField(); tb.SelectAll(); };
        // First click focuses and selects the whole number so a new value types straight over it.
        tb.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (tb.IsKeyboardFocusWithin) return;
            tb.Focus();
            e.Handled = true;
        };
        tb.LostKeyboardFocus += (_, _) => { StyleField(); CommitTyped(); };
        tb.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            CommitTyped();
            Keyboard.ClearFocus();
            e.Handled = true;
        };
        return tb;
    }

    private void StyleField()
    {
        if (_field.IsKeyboardFocusWithin)
        {
            _field.BorderBrush = Hud.Br("AccentBrush");
            _field.Background = Hud.Br("AccentFaintBrush");
        }
        else if (_field.IsMouseOver)
        {
            _field.BorderBrush = Hud.Br("AccentStrongBrush");
            _field.Background = Hud.Br("HighlightBrush");
        }
        else
        {
            _field.BorderBrush = Brushes.Transparent;
            _field.Background = Brushes.Transparent;
        }
    }

    private void CommitTyped()
    {
        var parsed = ParseTyped(_field.Text.Trim());
        if (parsed is not int q)
        {
            _field.Text = _value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return;
        }
        _value = q;
        ShowValue(animate: true);
        Commit("typed");
    }

    // ── Graph button ────────────────────────────────────────────────────────

    private Button BuildGraphButton(string resource)
    {
        var glyph = new Path
        {
            Data = Geometry.Parse("M2,13 L2,3 M2,13 L14,13 M3,11 L6,11 L6,8 L9,8 L9,5 L13,5"),
            Stroke = Hud.Br("FgDimBrush"), StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        };
        var box = new Canvas { Width = 16, Height = 16 };
        box.Children.Add(glyph);
        var face = new Border
        {
            Width = 26, Height = 22, CornerRadius = new CornerRadius(4),
            BorderBrush = Hud.Br("NavBorderBrush"), BorderThickness = new Thickness(1),
            Background = Brushes.Transparent,
            Child = new Viewbox { Width = 13, Height = 13, Child = box },
        };
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        var btn = new Button
        {
            Template = new ControlTemplate(typeof(Button)) { VisualTree = presenter },
            Content = face, Cursor = Cursors.Hand, Focusable = false,
            Margin = new Thickness(8, 0, 0, 0),   // the mock's 6 px gap plus the button's own 2 px
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = $"Quality bands for {resource}",
        };
        btn.MouseEnter += (_, _) =>
        {
            face.BorderBrush = Hud.Br("AccentStrongBrush");
            face.Background = Hud.Br("HighlightBrush");
            glyph.Stroke = Hud.Br("AccentBrush");
        };
        btn.MouseLeave += (_, _) =>
        {
            face.BorderBrush = Hud.Br("NavBorderBrush");
            face.Background = Brushes.Transparent;
            glyph.Stroke = Hud.Br("FgDimBrush");
        };
        btn.Click += (_, _) => GraphRequested?.Invoke();
        return btn;
    }

    // ── Slider ──────────────────────────────────────────────────────────────

    private void Slider_Down(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        _slider.CaptureMouse();
        _knob.Background = Hud.Br("AccentBrush");
        _halo.Background = Hud.Br("AccentDimBrush");
        _halo.BeginAnimation(OpacityProperty, null);   // a running hover fade would hold the old opacity
        _halo.Opacity = 1;
        // Drag ring is 5 px against the hover ring's 4 (mock .sl.drag .knob).
        _haloScale.ScaleX = _haloScale.ScaleY = (KnobSize + 10) / HaloSize;
        int v = ValueAt(e.GetPosition(_slider).X);
        if (v != _value)
        {
            _value = v;
            ShowValue(animate: false);
            ValueChanging?.Invoke(_value);
        }
        e.Handled = true;
    }

    private void Slider_Move(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        int v = ValueAt(e.GetPosition(_slider).X);
        if (v == _value) return;
        _value = v;
        ShowValue(animate: false);
        ValueChanging?.Invoke(_value);
    }

    private void EndDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        if (_slider.IsMouseCaptured) _slider.ReleaseMouseCapture();
        _knob.Background = Hud.Br("Bg3Brush");
        _halo.Background = Hud.Br("AccentFaintBrush");
        _haloScale.ScaleX = _haloScale.ScaleY = 1;
        FadeHalo(_slider.IsMouseOver ? 1 : 0);
        Commit("drag");
    }

    private int ValueAt(double x)
    {
        double w = _slider.ActualWidth;
        if (w <= 0) return _value;
        return (int)Math.Round(Math.Clamp(x, 0, w) / w * MaxQuality);
    }

    private void FadeHalo(double to)
    {
        if (Motion.Reduced) { _halo.BeginAnimation(OpacityProperty, null); _halo.Opacity = to; return; }
        _halo.BeginAnimation(OpacityProperty,
            new DoubleAnimation(to, TimeSpan.FromMilliseconds(Motion.HoverMs)) { EasingFunction = Motion.Settle });
    }

    // ── Rendering ───────────────────────────────────────────────────────────

    private void ShowValue(bool animate)
    {
        _field.Text = _value.ToString(System.Globalization.CultureInfo.InvariantCulture);

        _bandText.Inlines.Clear();
        var b = BandAt(_value);
        if (b != null)
        {
            _bandText.Inlines.Add(new Run("band "));
            _bandText.Inlines.Add(new Run($"{b.Start}-{b.End}") { Foreground = _rangeBrush });
            _bandText.Inlines.Add(new Run(", crafts as "));
            _bandText.Inlines.Add(new Run(b.Mapped.ToString(System.Globalization.CultureInfo.InvariantCulture))
                { Foreground = Hud.Br("CyanBrush"), FontWeight = FontWeights.Medium });
        }
        Layout(animate);
    }

    private QualityBand? BandAt(int q)
    {
        foreach (var b in _bands)
            if (b.Start <= q && q <= b.End) return b;
        return null;
    }

    private void Layout(bool animate)
    {
        double w = _slider.ActualWidth;
        if (w <= 0) return;
        _rail.Width = w;
        _fill.Width = w;
        foreach (var t in _ticks) Canvas.SetLeft(t, (int)t.Tag / (double)MaxQuality * w);

        double frac = _value / (double)MaxQuality;
        double knobX = frac * w - HaloSize / 2;
        if (!animate || _dragging || Motion.Reduced)
        {
            _fillScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _knobShift.BeginAnimation(TranslateTransform.XProperty, null);
            _fillScale.ScaleX = frac;
            _knobShift.X = knobX;
            return;
        }
        var dur = TimeSpan.FromMilliseconds(Motion.QuickRevealMs);
        _fillScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(frac, dur) { EasingFunction = Motion.Settle });
        _knobShift.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(knobX, dur) { EasingFunction = Motion.Settle });
    }
}
