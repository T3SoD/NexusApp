using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using Nexus_v4.Models;

namespace Nexus_v4.Views;

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

    private readonly System.Windows.Controls.Border _border;

    public ScanIndicatorWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
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
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
    }

    public void SetRegion(ScanRegion region, DpiScale dpi)
    {
        Left   = region.X      / dpi.DpiScaleX;
        Top    = region.Y      / dpi.DpiScaleY;
        Width  = region.Width  / dpi.DpiScaleX;
        Height = region.Height / dpi.DpiScaleY;
    }
}
