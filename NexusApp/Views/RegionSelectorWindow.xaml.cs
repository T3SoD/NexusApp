using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using NexusApp.Models;

namespace NexusApp.Views;

public partial class RegionSelectorWindow : Window
{
    private Point _start;
    private bool _dragging;

    public event Action<ScanRegion>? RegionSelected;

    public RegionSelectorWindow()
    {
        InitializeComponent();
        KeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };
    }

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
