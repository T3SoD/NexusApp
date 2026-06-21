using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

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
            _current = new ToastWindow();
            _current.Closed += (_, _) => _current = null;
            _current.Show();
        }
        _current._line.Text = message;
        _current._timer.Stop();
        _current._timer.Start();
    }
}
