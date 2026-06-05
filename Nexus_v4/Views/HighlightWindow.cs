using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace Nexus_v4.Views;

/// <summary>
/// A transparent, click-through, topmost window that draws an attention-grabbing
/// highlight around an arbitrary on-screen control: a bright pulsing core ring plus
/// an expanding "radar ping" ring that repeatedly sweeps outward. Used by the
/// welcome tour to point at the exact button being explained on each step.
///
/// While shown it re-reads the target's live screen position every tick, so the ring
/// follows the overlay when the user drags or resizes it, and it forces itself above
/// the (also-topmost) overlay so it's never hidden behind it.
/// </summary>
public class HighlightWindow : Window
{
    // Slack around the target so the glow and the expanding ping aren't clipped by
    // the (transparency-clipped) window bounds.
    private const double Slack   = 52;
    private const double CorePad = 9;

    private readonly Border         _core;
    private readonly Border         _ping;
    private readonly ScaleTransform _pingScale = new(1, 1) { CenterX = 0, CenterY = 0 };
    private readonly DispatcherTimer _follow;
    private FrameworkElement? _target;
    private Window? _hookedWindow;
    private bool _animating;
    private bool _active;

    public HighlightWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        IsHitTestVisible = false;

        var accent = (Brush)Application.Current.FindResource("AccentBrush");
        var accentColor = (accent as SolidColorBrush)?.Color ?? Colors.Cyan;

        var grid = new Grid();

        // Expanding radar ping (scales out from the centre and fades).
        _ping = new Border
        {
            BorderBrush = accent,
            BorderThickness = new Thickness(3),
            CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _pingScale,
        };

        // Steady, bright core ring with a strong glow.
        _core = new Border
        {
            BorderBrush = accent,
            BorderThickness = new Thickness(4),
            CornerRadius = new CornerRadius(7),
            Background = new SolidColorBrush(Color.FromArgb(0x33, accentColor.R, accentColor.G, accentColor.B)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Effect = new DropShadowEffect
            {
                Color = accentColor,
                BlurRadius = 26, ShadowDepth = 0, Opacity = 1.0,
            },
        };

        grid.Children.Add(_ping);
        grid.Children.Add(_core);
        Content = grid;

        // Live tracking is driven synchronously by the host window's LocationChanged
        // (smooth even inside the OS drag loop); this timer is just a low-frequency
        // safety net that also re-asserts z-order above the overlay.
        _follow = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(120),
        };
        _follow.Tick += (_, __) => Reposition();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Click-through + never steal focus + hide from alt-tab.
        var hwnd = new WindowInteropHelper(this).Handle;
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    /// <summary>Point the highlight at <paramref name="target"/> and show it. Hides if the target isn't visible.</summary>
    public void HighlightControl(FrameworkElement? target)
    {
        _target = target;
        _active = true;
        HookHostWindow(target);
        if (!Reposition()) return;

        if (!IsVisible) Show();
        StartAnimations();
        BringToTop();
        _follow.Start();
    }

    /// <summary>Subscribe to the target's host window so the ring tracks its moves/resizes in real time.</summary>
    private void HookHostWindow(FrameworkElement? target)
    {
        var host = target is null ? null : Window.GetWindow(target);
        if (ReferenceEquals(host, _hookedWindow)) return;

        if (_hookedWindow != null)
        {
            _hookedWindow.LocationChanged -= OnHostChanged;
            _hookedWindow.SizeChanged     -= OnHostResized;
        }
        _hookedWindow = host;
        if (_hookedWindow != null)
        {
            _hookedWindow.LocationChanged += OnHostChanged;
            _hookedWindow.SizeChanged     += OnHostResized;
        }
    }

    private void OnHostChanged(object? sender, EventArgs e) => Reposition();
    private void OnHostResized(object? sender, SizeChangedEventArgs e) => Reposition();

    /// <summary>Re-reads the target's current on-screen rect and moves the ring to match.</summary>
    private bool Reposition()
    {
        if (!_active) return false;

        var target = _target;
        if (target is null || !target.IsVisible || target.ActualWidth <= 0)
        {
            HideRing();
            return false;
        }

        Point topLeftDevice;
        try { topLeftDevice = target.PointToScreen(new Point(0, 0)); }
        catch { HideRing(); return false; }   // target's window was closed mid-tour

        // PointToScreen → physical pixels; divide by DPI scale → DIPs (Window.Left/Top space).
        var dpi = VisualTreeHelper.GetDpi(target);
        double w = target.ActualWidth, h = target.ActualHeight;

        Left   = topLeftDevice.X / dpi.DpiScaleX - Slack;
        Top    = topLeftDevice.Y / dpi.DpiScaleY - Slack;
        Width  = w + Slack * 2;
        Height = h + Slack * 2;

        double cw = w + CorePad * 2, ch = h + CorePad * 2;
        if (_core.Width != cw)  { _core.Width = _ping.Width = cw; }
        if (_core.Height != ch) { _core.Height = _ping.Height = ch; }

        BringToTop();
        return true;
    }

    private void StartAnimations()
    {
        if (_animating) return;
        _animating = true;

        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };

        // Core "breathing" pulse — never fully fades so the button stays clearly marked.
        var breathe = new DoubleAnimation(1.0, 0.55, TimeSpan.FromMilliseconds(700))
        {
            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease,
        };
        _core.BeginAnimation(OpacityProperty, breathe);

        // Radar ping — scale outward and fade, repeating.
        var grow = new DoubleAnimation(1.0, 1.9, TimeSpan.FromMilliseconds(1150))
        {
            RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut },
        };
        _pingScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        _pingScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);

        var fade = new DoubleAnimation(0.85, 0.0, TimeSpan.FromMilliseconds(1150))
        {
            RepeatBehavior = RepeatBehavior.Forever,
        };
        _ping.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Force the ring above all other topmost windows (e.g. the overlay) without stealing focus.</summary>
    private void BringToTop()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    public void HideRing()
    {
        _active = false;
        _follow.Stop();
        if (IsVisible) Hide();
    }

    protected override void OnClosed(EventArgs e)
    {
        _active = false;
        _follow.Stop();
        if (_hookedWindow != null)
        {
            _hookedWindow.LocationChanged -= OnHostChanged;
            _hookedWindow.SizeChanged     -= OnHostResized;
            _hookedWindow = null;
        }
        base.OnClosed(e);
    }

    // ── P/Invoke ────────────────────────────────────────────────────────────────
    private const int GWL_EXSTYLE       = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_NOACTIVATE  = 0x08000000;
    private const int WS_EX_TOOLWINDOW  = 0x00000080;

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE     = 0x0001;
    private const uint SWP_NOMOVE     = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
}
