using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using NexusApp.Models;

namespace NexusApp.Views;

// Persistent transparent overlay that shows where the OCR scanner is watching.
// WS_EX_TRANSPARENT makes it fully click-through so it never blocks the game.
public class ScanIndicatorWindow : Window
{
    private const int GWL_EXSTYLE      = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW  = 0x00000080;
    private const int WS_EX_NOACTIVATE  = 0x08000000;

    private static readonly SolidColorBrush MagentaBrush = new(Color.FromArgb(255, 255, 0, 255));
    private static readonly SolidColorBrush GreenBrush   = new(Color.FromArgb(255, 0, 210, 120));

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int idx);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int idx, int val);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int width, int height, bool repaint);

    // Indicator bounds are stored in true PHYSICAL pixels and applied via MoveWindow (see SetRegion).
    private IntPtr _hwnd;
    private int _px, _py, _pw, _ph;

    private readonly System.Windows.Controls.Border _border;

    public ScanIndicatorWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowActivated = false;   // never grab focus from the game (defense-in-depth with WS_EX_NOACTIVATE)
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        IsHitTestVisible = false;

        _border = new System.Windows.Controls.Border
        {
            BorderBrush = MagentaBrush,
            BorderThickness = new Thickness(3),
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(2),
        };
        Content = _border;
    }

    public void FlashGreen()
    {
        if (!IsVisible) return;
        _border.BorderBrush = GreenBrush;
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        t.Tick += (_, _) => { t.Stop(); _border.BorderBrush = MagentaBrush; };
        t.Start();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        ApplyPhysicalBounds();   // apply any region set before the handle existed
    }

    public void SetRegion(ScanRegion region)
    {
        _px = region.X; _py = region.Y; _pw = region.Width; _ph = region.Height;
        ApplyPhysicalBounds();
    }

    // Position the borderless click-through indicator directly in PHYSICAL pixels. MoveWindow always
    // operates in device pixels regardless of any monitor's per-monitor DPI, so the magenta box lands
    // exactly over the captured region on ANY-scale monitor (issue #6) — unlike the old code, which
    // divided by the main window's single DPI and mis-placed the box on a different-DPI secondary.
    // First (offset) move lands the window on the region's monitor and fires WM_DPICHANGED so WPF
    // adopts that monitor's DPI; the second re-asserts the exact rect, overriding WPF's auto-resize.
    private void ApplyPhysicalBounds()
    {
        if (_hwnd == IntPtr.Zero || _pw <= 0 || _ph <= 0) return;
        MoveWindow(_hwnd, _px + 1, _py + 1, _pw, _ph, false);
        MoveWindow(_hwnd, _px,     _py,     _pw, _ph, true);
    }
}
