using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using NexusApp.Services;

namespace NexusApp.Views;

// BETA — a tiny toast that floats at the bottom-right corner to confirm an auto-marked
// blueprint while you're in-game. Critically it's NON-ACTIVATING (ShowActivated=false)
// and click-through (IsHitTestVisible=false) so it never steals focus or clicks from
// Star Citizen. Reuses a single instance; each Show resets a 3.5s auto-dismiss timer.
public sealed class ToastWindow : Window
{
    private static ToastWindow? _current;
    private readonly TextBlock _line;
    private readonly DispatcherTimer _timer;

    private ToastWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowActivated = false;        // never grab focus from the game
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        IsHitTestVisible = false;     // click-through
        Focusable = false;
        SizeToContent = SizeToContent.WidthAndHeight;

        _line = new TextBlock
        {
            Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap, MaxWidth = 380,
        };
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x10, 0x2A, 0x24)),
            BorderBrush = Brushes.MediumSpringGreen, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10, 14, 10),
            Child = _line,
        };

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
        _timer.Tick += (_, _) => { _timer.Stop(); Close(); };

        SizeChanged += (_, _) => Reposition();
        Loaded += (_, _) => Reposition();
    }

    // Apply the same no-activate / tool-window / click-through styles the other in-game overlays
    // use, so the toast NEVER pulls focus from Star Citizen. ShowActivated=false alone isn't
    // reliable against a fullscreen/borderless game — this was tabbing players out mid-session.
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT);
    }

    private const int GWL_EXSTYLE       = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW  = 0x00000080;
    private const int WS_EX_NOACTIVATE  = 0x08000000;
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private void Reposition()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - ActualWidth - 24;
        Top = wa.Bottom - ActualHeight - 24;
    }

    // Shows (or updates) the toast. Safe to call repeatedly; the latest message wins
    // and the dismiss timer restarts.
    public static void Show(string message)
    {
        if (_current is null)
        {
            // A new toast is a non-activating window appearing over the game — log its creation so
            // it can be ruled in or out as a cause of a mid-session tab-out.
            Logger.Info("[WIN] toast shown");
            _current = new ToastWindow();
            _current.Closed += (_, _) => _current = null;
            _current.Show();
        }
        _current._line.Text = message;
        _current._timer.Stop();
        _current._timer.Start();
    }
}
