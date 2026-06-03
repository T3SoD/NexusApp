using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Nexus_v4.Models;
using Nexus_v4.ViewModels;

namespace Nexus_v4.Views;

public class WorkOrderFlyoutWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly StackPanel _listPanel;
    private System.Windows.Threading.DispatcherTimer? _ticker;
    private readonly Dictionary<string, (ScaleTransform? Scale, TextBlock? Subtitle)> _refs = new();

    private readonly TranslateTransform _slideTransform = new();
    private bool _dockRight;
    private bool _hideCompleted;
    private double _ownerLeft, _ownerTop, _ownerHeight, _ownerWidth;
    private readonly TextBlock _sideBtnText;
    private readonly TextBlock _hideBtnText;

    public WorkOrderFlyoutWindow(MainViewModel vm)
    {
        _vm = vm;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Width = 260;

        _listPanel = new StackPanel { Margin = new Thickness(8, 4, 8, 8) };

        // Header
        var header = new Grid
        {
            Height = 36,
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x0A, 0x10, 0x20)),
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        header.Children.Add(new TextBlock
        {
            Text = "REFINERY TRACKER",
            FontSize = 11, FontWeight = FontWeights.Bold,
            Foreground = BrushFromHex("#00C9A7"),
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });

        // Hide-completed toggle
        _hideBtnText = new TextBlock
        {
            Text = "☐",
            FontSize = 11,
            Foreground = BrushFromHex("#8B949E"),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Hide completed",
        };
        var hideBtn = new Border
        {
            Child = _hideBtnText,
            Padding = new Thickness(6, 0, 2, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        hideBtn.MouseEnter  += (s, _) => _hideBtnText.Foreground = BrushFromHex("#00C9A7");
        hideBtn.MouseLeave  += (s, _) => _hideBtnText.Foreground = _hideCompleted ? BrushFromHex("#00C9A7") : BrushFromHex("#8B949E");
        hideBtn.MouseLeftButtonDown += (s, _) => ToggleHideCompleted();

        // Side-toggle button
        _sideBtnText = new TextBlock
        {
            Text = "⇄",
            FontSize = 10,
            Foreground = BrushFromHex("#8B949E"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var sideBtn = new Border
        {
            Child = _sideBtnText,
            Padding = new Thickness(6, 0, 10, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "Switch side",
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        sideBtn.MouseEnter  += (s, _) => _sideBtnText.Foreground = BrushFromHex("#00C9A7");
        sideBtn.MouseLeave  += (s, _) => _sideBtnText.Foreground = BrushFromHex("#8B949E");
        sideBtn.MouseLeftButtonDown += (s, _) => ToggleSide();

        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal };
        btnPanel.Children.Add(hideBtn);
        btnPanel.Children.Add(sideBtn);
        Grid.SetColumn(btnPanel, 1);
        header.Children.Add(btnPanel);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _listPanel,
        };

        var inner = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        inner.Children.Add(header);
        inner.Children.Add(scroll);

        Content = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = BrushFromHex("#E0070B12"),
            BorderBrush = BrushFromHex("#FF30363D"),
            BorderThickness = new Thickness(1),
            Child = inner,
            RenderTransform = _slideTransform,
        };

        _vm.WorkOrders.CollectionChanged += (s, e) => Rebuild();
        Rebuild();
    }

    // ── Animation ────────────────────────────────────────────────────────────

    public void ShowWithAnimation()
    {
        // Reset any in-progress hide animation before showing
        _slideTransform.BeginAnimation(TranslateTransform.XProperty, null);
        _slideTransform.X = _dockRight ? -Width : Width;
        Show();

        var anim = new DoubleAnimation(
            _dockRight ? -Width : Width, 0,
            new Duration(TimeSpan.FromMilliseconds(220)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        _slideTransform.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    public void HideWithAnimation()
    {
        var anim = new DoubleAnimation(
            0, _dockRight ? -Width : Width,
            new Duration(TimeSpan.FromMilliseconds(180)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        anim.Completed += (_, _) => Hide();
        _slideTransform.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    // ── Side toggle ──────────────────────────────────────────────────────────

    private void ToggleHideCompleted()
    {
        _hideCompleted = !_hideCompleted;
        _hideBtnText.Text = _hideCompleted ? "☑" : "☐";
        _hideBtnText.Foreground = BrushFromHex(_hideCompleted ? "#00C9A7" : "#8B949E");
        Rebuild();
    }

    private void ToggleSide()
    {
        _dockRight = !_dockRight;
        Reposition();
    }

    // ── Positioning ──────────────────────────────────────────────────────────

    public void AnchorTo(Window owner)
    {
        _ownerLeft   = owner.Left;
        _ownerTop    = owner.Top;
        _ownerWidth  = owner.ActualWidth;
        _ownerHeight = owner.ActualHeight;
        Reposition();

        owner.LocationChanged += (_, _) =>
        {
            _ownerLeft = owner.Left;
            _ownerTop  = owner.Top;
            Reposition();
        };
        owner.SizeChanged += (_, _) =>
        {
            _ownerWidth  = owner.ActualWidth;
            _ownerHeight = owner.ActualHeight;
            Reposition();
        };
    }

    private void Reposition()
    {
        Left   = _dockRight ? _ownerLeft + _ownerWidth + 4 : _ownerLeft - Width - 4;
        Top    = _ownerTop;
        Height = _ownerHeight;
    }

    // ── Work order list ──────────────────────────────────────────────────────

    public void Rebuild()
    {
        _refs.Clear();
        _listPanel.Children.Clear();
        _ticker?.Stop();
        _ticker = null;

        if (_vm.WorkOrders.Count == 0)
        {
            _listPanel.Children.Add(new TextBlock
            {
                Text = "No work orders",
                FontSize = 12,
                Foreground = BrushFromHex("#8B949E"),
                Margin = new Thickness(4, 8, 0, 0),
            });
            return;
        }

        var visible = _hideCompleted
            ? _vm.WorkOrders.Where(w => w.Status != WorkOrderStatus.Complete).ToList()
            : _vm.WorkOrders.ToList();

        if (visible.Count == 0 && _hideCompleted)
        {
            _listPanel.Children.Add(new TextBlock
            {
                Text = "No active work orders",
                FontSize = 12,
                Foreground = BrushFromHex("#8B949E"),
                Margin = new Thickness(4, 8, 0, 0),
            });
            return;
        }

        foreach (var wo in visible)
            _listPanel.Children.Add(BuildRow(wo));

        if (_vm.WorkOrders.Any(w => w.HasActiveTimer))
        {
            _ticker = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _ticker.Tick += Ticker_Tick;
            _ticker.Start();
        }
    }

    private UIElement BuildRow(WorkOrder wo)
    {
        var container = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new Border { Background = BrushFromHex(wo.StatusColorHex) });

        var content = new Grid { Margin = new Thickness(8, 4, 8, 4) };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var textStack = new StackPanel();
        textStack.Children.Add(new TextBlock
        {
            Text = wo.Label, FontSize = 11, FontWeight = FontWeights.SemiBold,
            Foreground = BrushFromHex("#E6EDF3"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        TextBlock? subtitleTb = null;
        if (wo.HasActiveTimer)
        {
            subtitleTb = new TextBlock
            {
                Text = wo.SubtitleText, FontSize = 9,
                Foreground = BrushFromHex(wo.SubtitleForeground),
                Margin = new Thickness(0, 1, 0, 0),
            };
            textStack.Children.Add(subtitleTb);
        }
        content.Children.Add(textStack);

        var dot = new TextBlock
        {
            Text = "●", FontSize = 9, VerticalAlignment = VerticalAlignment.Center,
            Foreground = BrushFromHex(wo.StatusColorHex), ToolTip = wo.StatusLabel,
        };
        Grid.SetColumn(dot, 1);
        content.Children.Add(dot);

        Grid.SetColumn(content, 1);
        row.Children.Add(content);
        container.Children.Add(row);

        ScaleTransform? scale = null;
        if (wo.HasActiveTimer)
        {
            scale = new ScaleTransform(wo.TimerFraction, 1);
            var barContainer = new Grid { Height = 2, Margin = new Thickness(0, 2, 0, 0) };
            barContainer.Children.Add(new Border
            {
                Background = BrushFromHex(wo.StatusColorHex),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                RenderTransform = scale,
                RenderTransformOrigin = new Point(0, 0.5),
            });
            container.Children.Add(barContainer);

            var remaining = wo.TimerEnd!.Value - DateTime.UtcNow;
            if (remaining > TimeSpan.Zero)
            {
                var anim = new DoubleAnimation
                {
                    From = wo.TimerFraction, To = 1.0,
                    Duration = remaining,
                    FillBehavior = FillBehavior.HoldEnd,
                };
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            }
        }

        _refs[wo.Id] = (scale, subtitleTb);
        return container;
    }

    private void Ticker_Tick(object? sender, EventArgs e)
    {
        bool anyActive = false;
        foreach (var wo in _vm.WorkOrders)
        {
            if (!_refs.TryGetValue(wo.Id, out var refs)) continue;
            if (!wo.HasActiveTimer) continue;
            anyActive = true;
            if (refs.Subtitle != null) refs.Subtitle.Text = wo.SubtitleText;
        }
        if (!anyActive) { _ticker?.Stop(); _ticker = null; }
    }

    private static SolidColorBrush BrushFromHex(string hex)
        => new((Color)ColorConverter.ConvertFromString(hex));
}
