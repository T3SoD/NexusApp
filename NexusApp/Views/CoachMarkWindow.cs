using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace NexusApp.Views;

/// <summary>
/// A compact, modeless caption bubble for the welcome tour. It docks beside the
/// control being explained (never over it) with a small beak pointing at it, and
/// carries the step title, caption, progress, and Skip / Back / Next controls.
///
/// Topmost + no-activate (WS_EX_NOACTIVATE) so it floats above the app and the
/// overlay without stealing focus from the game; its buttons stay clickable.
/// Styled from the app's shared theme resources.
/// </summary>
public sealed class CoachMarkWindow : Window
{
    public event Action? BackClicked;
    public event Action? NextClicked;
    public event Action? SkipClicked;

    private const double CardWidth = 340;
    private const double Beak      = 11;   // beak depth; also the uniform card margin
    private const double Gap       = 18;   // space between target and bubble

    private readonly TextBlock  _title   = new() { FontSize = 15, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock  _caption = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, LineHeight = 18 };
    private readonly TextBlock  _stepLbl = new() { FontSize = 10.5, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _dots    = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button     _back;
    private readonly Button     _next;
    private readonly Button     _skip;
    private readonly Polygon    _beak;
    private int _stepCount;

    private static Brush B(string key) => (Brush)Application.Current.FindResource(key);

    public CoachMarkWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;

        _title.Foreground   = B("AccentBrush");
        _title.FontFamily   = (FontFamily)Application.Current.FindResource("HeadFont");
        _caption.Foreground = B("FgBrush");
        _stepLbl.Foreground = B("FgDimBrush");

        _back = TextButton("Back",  B("FgDimBrush"));
        _skip = TextButton("Skip",  B("FgDimBrush"));
        _next = new Button
        {
            Style = (Style)Application.Current.FindResource("AccentButton"),
            Padding = new Thickness(18, 7, 18, 7),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        _back.Click += (_, __) => BackClicked?.Invoke();
        _next.Click += (_, __) => NextClicked?.Invoke();
        _skip.Click += (_, __) => SkipClicked?.Invoke();

        // ── Footer: progress (left) + Back / Next (right) ──
        var footer = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var progress = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        progress.Children.Add(_dots);
        progress.Children.Add(_stepLbl);
        Grid.SetColumn(progress, 0);

        var nav = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _back.Margin = new Thickness(0, 0, 8, 0);
        nav.Children.Add(_back);
        nav.Children.Add(_next);
        Grid.SetColumn(nav, 1);

        footer.Children.Add(progress);
        footer.Children.Add(nav);

        // ── Title row: title (left) + Skip (right) ──
        var titleRow = new Grid();
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_title, 0);
        _skip.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(_skip, 1);
        titleRow.Children.Add(_title);
        titleRow.Children.Add(_skip);

        var stack = new StackPanel();
        stack.Children.Add(titleRow);
        _caption.Margin = new Thickness(0, 8, 0, 0);
        stack.Children.Add(_caption);
        stack.Children.Add(footer);

        var card = new Border
        {
            Width = CardWidth,
            Background = B("BgBrush"),
            BorderBrush = B("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(18),
            Margin = new Thickness(Beak),
            Child = stack,
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 24, ShadowDepth = 4, Opacity = 0.55 },
        };

        _beak = new Polygon { Fill = B("BgBrush"), Visibility = Visibility.Collapsed };

        var root = new Grid();
        root.Children.Add(card);
        root.Children.Add(_beak);
        Content = root;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    /// <summary>Fill the bubble with a step's content.</summary>
    public void SetContent(string title, string caption, int stepIndex, int stepCount, bool isFirst, bool isLast, string nextLabel)
    {
        _stepCount = stepCount;
        _title.Text = title;
        _caption.Text = caption;
        _stepLbl.Text = $"Step {stepIndex + 1} of {stepCount}";
        _back.Visibility = isFirst ? Visibility.Collapsed : Visibility.Visible;
        _next.Content = nextLabel;

        _dots.Children.Clear();
        for (int i = 0; i < stepCount; i++)
            _dots.Children.Add(new Border
            {
                Width = 7, Height = 7,
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(3, 0, 0, 0),
                Background = i == stepIndex ? B("AccentBrush") : B("BorderBrush"),
            });
    }

    /// <summary>Dock the bubble beside <paramref name="target"/>, on the first side with room.</summary>
    public void ShowBeside(FrameworkElement target)
    {
        if (!IsVisible) { Left = -10000; Top = -10000; Show(); }
        UpdateLayout();
        double w = ActualWidth, h = ActualHeight;

        Point tl;
        try { tl = target.PointToScreen(new Point(0, 0)); }
        catch { return; }
        var dpi = VisualTreeHelper.GetDpi(target);
        double tx = tl.X / dpi.DpiScaleX, ty = tl.Y / dpi.DpiScaleY;
        double tw = target.ActualWidth, th = target.ActualHeight;
        double tcx = tx + tw / 2, tcy = ty + th / 2;

        var wa = SystemParameters.WorkArea;
        double left, top; string side;

        if (tx + tw + Gap + w <= wa.Right)            { side = "left";  left = tx + tw + Gap;     top = tcy - h / 2; }
        else if (tx - Gap - w >= wa.Left)             { side = "right"; left = tx - Gap - w;      top = tcy - h / 2; }
        else if (ty + th + Gap + h <= wa.Bottom)      { side = "top";   top  = ty + th + Gap;     left = tcx - w / 2; }
        else                                          { side = "bottom"; top = ty - Gap - h;      left = tcx - w / 2; }

        left = Math.Clamp(left, wa.Left + 4, wa.Right - w - 4);
        top  = Math.Clamp(top,  wa.Top + 4,  wa.Bottom - h - 4);
        Left = left; Top = top;

        PlaceBeak(side);
        BringToTop();
    }

    /// <summary>Center the bubble over <paramref name="owner"/> (steps with no target).</summary>
    public void ShowCentered(Window owner)
    {
        _beak.Visibility = Visibility.Collapsed;
        if (!IsVisible) { Left = -10000; Top = -10000; Show(); }
        UpdateLayout();
        double w = ActualWidth, h = ActualHeight;
        Left = owner.Left + (owner.ActualWidth - w) / 2;
        Top  = owner.Top  + (owner.ActualHeight - h) / 2;
        BringToTop();
    }

    /// <summary>Point the beak from the bubble edge nearest the target toward it.</summary>
    private void PlaceBeak(string side)
    {
        _beak.Visibility = Visibility.Visible;
        switch (side)
        {
            case "left":   // bubble is right of target → beak on bubble's left edge, pointing left
                _beak.HorizontalAlignment = HorizontalAlignment.Left;
                _beak.VerticalAlignment   = VerticalAlignment.Center;
                _beak.Points = new PointCollection { new(Beak, 0), new(Beak, 2 * Beak), new(0, Beak) };
                break;
            case "right":
                _beak.HorizontalAlignment = HorizontalAlignment.Right;
                _beak.VerticalAlignment   = VerticalAlignment.Center;
                _beak.Points = new PointCollection { new(0, 0), new(0, 2 * Beak), new(Beak, Beak) };
                break;
            case "top":    // bubble is below target → beak on top edge, pointing up
                _beak.HorizontalAlignment = HorizontalAlignment.Center;
                _beak.VerticalAlignment   = VerticalAlignment.Top;
                _beak.Points = new PointCollection { new(0, Beak), new(2 * Beak, Beak), new(Beak, 0) };
                break;
            default:       // "bottom"
                _beak.HorizontalAlignment = HorizontalAlignment.Center;
                _beak.VerticalAlignment   = VerticalAlignment.Bottom;
                _beak.Points = new PointCollection { new(0, 0), new(2 * Beak, 0), new(Beak, Beak) };
                break;
        }
    }

    public void BringToTop()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private static Button TextButton(string text, Brush fg) => new()
    {
        Content = text,
        Foreground = fg,
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        Padding = new Thickness(6, 4, 6, 4),
        FontSize = 12,
        Cursor = System.Windows.Input.Cursors.Hand,
        Template = TransparentButtonTemplate(),
    };

    // Flat template so the text buttons don't render the default OS button chrome.
    private static ControlTemplate TransparentButtonTemplate()
    {
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.SetValue(Border.PaddingProperty, new Thickness(6, 4, 6, 4));
        border.AppendChild(presenter);
        return new ControlTemplate(typeof(Button)) { VisualTree = border };
    }

    // ── P/Invoke (no-activate + z-order) ────────────────────────────────────────
    private const int GWL_EXSTYLE      = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
}
