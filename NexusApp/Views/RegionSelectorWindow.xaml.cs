using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using NexusApp.Models;
using NexusApp.Services;

namespace NexusApp.Views;

public partial class RegionSelectorWindow : Window
{
    private Point _start;
    private bool _dragging;
    private IntPtr _anchorHwnd;

    public event Action<ScanRegion>? RegionSelected;

    public RegionSelectorWindow()
    {
        InitializeComponent();
        KeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };
    }

    /// <summary>
    /// Shows the draw surface covering the single monitor that <paramref name="anchor"/> occupies
    /// (the overlay / main window - i.e. where the user is working, usually the game's monitor)
    /// instead of always the primary (issue #6). Sized in physical pixels via MoveWindow so it lands
    /// on the correct monitor under Per-Monitor-DPI-V2, where WPF Left/Top + Maximize does not.
    /// </summary>
    public void ShowOnMonitorOf(Window? anchor)
    {
        _anchorHwnd = anchor != null ? new WindowInteropHelper(anchor).Handle : IntPtr.Zero;
        Show();   // OnSourceInitialized sizes us to the anchor's monitor in physical pixels
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        // Cover the monitor the anchor (overlay / main window - usually the game's monitor) is on, in
        // PHYSICAL pixels. Under Per-Monitor-DPI-V2, positioning via WPF Left/Top + Maximize lands on
        // the wrong monitor across a DPI boundary (issue #6); MonitorFromWindow + MoveWindow is exact,
        // and PointToScreen on the canvas then yields the right physical coords for that monitor.
        var target = _anchorHwnd != IntPtr.Zero ? _anchorHwnd : hwnd;
        var mon = MonitorFromWindow(target, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (mon != IntPtr.Zero && GetMonitorInfo(mon, ref mi))
        {
            var rc = mi.rcMonitor;
            MoveWindow(hwnd, rc.left, rc.top, rc.right - rc.left, rc.bottom - rc.top, repaint: true);
            Logger.Info($"[WIN] region selector covering monitor ({rc.left},{rc.top}) {rc.right - rc.left}x{rc.bottom - rc.top} (issue #6)");
        }
        else
        {
            Logger.Info("[WIN] region selector: could not resolve anchor monitor; default placement (issue #6)");
        }
    }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO mi);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    private void Canvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(DrawCanvas);
        _dragging = true;
        DrawCanvas.CaptureMouse();
        SelectRect.Visibility = Visibility.Visible;
        Canvas.SetLeft(SelectRect, _start.X);
        Canvas.SetTop(SelectRect, _start.Y);
        SelectRect.Width = 0;
        SelectRect.Height = 0;
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var pos = e.GetPosition(DrawCanvas);
        var x = Math.Min(_start.X, pos.X);
        var y = Math.Min(_start.Y, pos.Y);
        var w = Math.Abs(pos.X - _start.X);
        var h = Math.Abs(pos.Y - _start.Y);
        Canvas.SetLeft(SelectRect, x);
        Canvas.SetTop(SelectRect, y);
        SelectRect.Width = w;
        SelectRect.Height = h;
    }

    private void Canvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        DrawCanvas.ReleaseMouseCapture();

        var pos = e.GetPosition(DrawCanvas);
        var x = (int)Math.Min(_start.X, pos.X);
        var y = (int)Math.Min(_start.Y, pos.Y);
        var w = (int)Math.Abs(pos.X - _start.X);
        var h = (int)Math.Abs(pos.Y - _start.Y);

        if (w > 5 && h > 5)
        {
            // PointToScreen converts WPF DIPs → physical screen pixels and accounts
            // for window position and DPI scaling in one step.
            var topLeft     = DrawCanvas.PointToScreen(new Point(x,     y));
            var bottomRight = DrawCanvas.PointToScreen(new Point(x + w, y + h));
            var region = new ScanRegion
            {
                X      = (int)topLeft.X,
                Y      = (int)topLeft.Y,
                Width  = (int)(bottomRight.X - topLeft.X),
                Height = (int)(bottomRight.Y - topLeft.Y),
            };
            RegionSelected?.Invoke(region);
        }

        Close();
    }
}
