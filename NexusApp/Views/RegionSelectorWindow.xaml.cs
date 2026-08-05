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
    private readonly List<RECT> _monitors = [];
    private int _monitorIndex;

    public event Action<ScanRegion>? RegionSelected;

    public RegionSelectorWindow()
    {
        InitializeComponent();
        KeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };
    }

    /// <summary>
    /// Shows the draw surface covering the single monitor that <paramref name="anchor"/> occupies
    /// (issue #6), with a NEXT MONITOR button to hop the surface to any other screen (issue #36:
    /// the overlay may sit away from the game's monitor, and the old picker stranded the user
    /// there). Sized in physical pixels via MoveWindow so it lands on the correct monitor under
    /// Per-Monitor-DPI-V2, where WPF Left/Top + Maximize does not.
    /// </summary>
    public void ShowOnMonitorOf(Window? anchor)
    {
        _anchorHwnd = anchor != null ? new WindowInteropHelper(anchor).Handle : IntPtr.Zero;
        Show();   // OnSourceInitialized enumerates monitors and covers the anchor's
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;

        var handles = new List<IntPtr>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr hMon, IntPtr _, ref RECT rc, IntPtr _) => { handles.Add(hMon); _monitors.Add(rc); return true; },
            IntPtr.Zero);

        if (_monitors.Count == 0)
        {
            Logger.Info("[WIN] region selector: monitor enumeration failed; default placement");
            return;
        }

        // Start on the anchor's monitor (the overlay's or main window's), matched by handle.
        var target = _anchorHwnd != IntPtr.Zero ? _anchorHwnd : hwnd;
        var anchorMon = MonitorFromWindow(target, MONITOR_DEFAULTTONEAREST);
        _monitorIndex = Math.Max(0, handles.IndexOf(anchorMon));

        NextMonitorBtn.Visibility = _monitors.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        ApplyMonitor(hwnd);
    }

    // Cover _monitors[_monitorIndex] in PHYSICAL pixels. Double move (the ScanIndicatorWindow
    // idiom): the first lands the window on the target monitor and fires WM_DPICHANGED so WPF
    // adopts that monitor's DPI; the second re-asserts the exact rect over WPF's auto-resize.
    // PointToScreen on the canvas then yields the right physical coords for that monitor.
    private void ApplyMonitor(IntPtr hwnd)
    {
        var rc = _monitors[_monitorIndex];
        int w = rc.right - rc.left, h = rc.bottom - rc.top;
        MoveWindow(hwnd, rc.left + 1, rc.top + 1, w, h, repaint: false);
        MoveWindow(hwnd, rc.left, rc.top, w, h, repaint: true);
        NextMonitorLabel.Text = MonitorCycle.Label(_monitorIndex, _monitors.Count) + "  ·  MOVE TO NEXT";
        Logger.Info($"[WIN] region selector covering monitor {_monitorIndex + 1} of {_monitors.Count} " +
                    $"({rc.left},{rc.top}) {w}x{h} (issue #36)");
    }

    private void NextMonitor_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;   // never reaches the canvas: a hop must not start a drag
        if (_monitors.Count <= 1) return;
        _dragging = false;
        SelectRect.Visibility = Visibility.Collapsed;
        _monitorIndex = MonitorCycle.Next(_monitorIndex, _monitors.Count);
        ApplyMonitor(new WindowInteropHelper(this).Handle);
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

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
