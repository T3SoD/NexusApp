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

    private static Brush Res(string key) => (Brush)System.Windows.Application.Current.FindResource(key);

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

        _listPanel = new StackPanel { Margin = new Thickness(10, 4, 10, 10) };

        // ── Header ──
        var header = new Grid { Height = 38, Background = Brushes.Transparent };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        header.Children.Add(new TextBlock
        {
            Text = "REFINERY TRACKER",
            FontSize = 12, FontWeight = FontWeights.Bold,
            FontFamily = (FontFamily)System.Windows.Application.Current.FindResource("HeadFont"),
            Foreground = Res("GoldBrush"),
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });

        // Hide-completed toggle
        _hideBtnText = new TextBlock
        {
            Text = "☐", FontSize = 11, Foreground = Res("FgDimBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Hide completed",
        };
        var hideBtn = MakeToolButton(_hideBtnText);
        hideBtn.MouseEnter += (s, _) => _hideBtnText.Foreground = Res("GoldBrush");
        hideBtn.MouseLeave += (s, _) => _hideBtnText.Foreground = _hideCompleted ? Res("GoldBrush") : Res("FgDimBrush");
        hideBtn.MouseLeftButtonDown += (s, _) => ToggleHideCompleted();

        // Side-toggle button
        _sideBtnText = new TextBlock
        {
            Text = "⇄", FontSize = 11, Foreground = Res("FgDimBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var sideBtn = MakeToolButton(_sideBtnText);
        sideBtn.ToolTip = "Switch side";
        sideBtn.Margin = new Thickness(0, 0, 10, 0);
        sideBtn.MouseEnter += (s, _) => _sideBtnText.Foreground = Res("GoldBrush");
        sideBtn.MouseLeave += (s, _) => _sideBtnText.Foreground = Res("FgDimBrush");
        sideBtn.MouseLeftButtonDown += (s, _) => ToggleSide();

        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        btnPanel.Children.Add(hideBtn);
        btnPanel.Children.Add(sideBtn);
        Grid.SetColumn(btnPanel, 1);
        header.Children.Add(btnPanel);

        // accent hairline under header
        var hairline = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(12, 0, 12, 0) };
        var hlBrush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        hlBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xC9, 0xA2, 0x4B), 0));
        hlBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0x66, 0xC9, 0xA2, 0x4B), 0.5));
        hlBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xC9, 0xA2, 0x4B), 1));
        hairline.Background = hlBrush;
        Grid.SetColumnSpan(hairline, 2);
        header.Children.Add(hairline);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _listPanel,
        };

        var inner = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        inner.Children.Add(header);
        inner.Children.Add(scroll);

        // ── Panel: luxury-dark gradient + gold-tinted hairline ──
        var panelBg = new LinearGradientBrush(
            Color.FromArgb(0xF2, 0x15, 0x14, 0x1C),
            Color.FromArgb(0xF2, 0x0C, 0x0C, 0x11),
            new Point(0, 0), new Point(0, 1));

        Content = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = panelBg,
            BorderBrush = BrushFromHex("#FF332F3D"),
            BorderThickness = new Thickness(1),
            Child = inner,
            RenderTransform = _slideTransform,
        };

        _vm.WorkOrders.CollectionChanged += (s, e) => Rebuild();
        Rebuild();
    }

    private static Border MakeToolButton(UIElement child) => new()
    {
        Child = child,
        Width = 24, Height = 22,
        Background = Res("Bg3Brush"),
        BorderBrush = Res("BorderBrush"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
        Margin = new Thickness(0, 0, 6, 0),
        Cursor = System.Windows.Input.Cursors.Hand,
        VerticalAlignment = VerticalAlignment.Center,
    };

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
        _hideBtnText.Foreground = _hideCompleted ? Res("GoldBrush") : Res("FgDimBrush");
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
            _listPanel.Children.Add(EmptyText("No work orders"));
            return;
        }

        var visible = _hideCompleted
            ? _vm.WorkOrders.Where(w => w.Status != WorkOrderStatus.Complete).ToList()
            : _vm.WorkOrders.ToList();

        if (visible.Count == 0 && _hideCompleted)
        {
            _listPanel.Children.Add(EmptyText("No active work orders"));
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

    private static TextBlock EmptyText(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = Res("FgDimBrush"),
        Margin = new Thickness(4, 10, 0, 0),
    };

    private UIElement BuildRow(WorkOrder wo)
    {
        var cardBg   = Res("Bg2NavBrush");
        var navB     = Res("NavBorderBrush");
        var fg       = Res("FgBrush");
        var dim      = Res("FgDimBrush");
        var chipBg   = Res("Bg3Brush");
        var trackBg  = Res("BorderBrush");
        var headFont = (FontFamily)System.Windows.Application.Current.FindResource("HeadFont");
        var statusBrush = BrushFromHex(wo.StatusColorHex);

        var outer = new Border { Background = cardBg, BorderBrush = navB, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 8, 0, 0) };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new Border { Background = statusBrush, CornerRadius = new CornerRadius(8, 0, 0, 8) });

        var stack = new StackPanel { Margin = new Thickness(12, 9, 10, 9) };
        Grid.SetColumn(stack, 1);

        // top: label | status pill
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(wo.Label) ? wo.Resources : wo.Label,
            FontFamily = headFont, FontSize = 12, Foreground = fg,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var chip = new Border
        {
            Background = chipBg, CornerRadius = new CornerRadius(8),
            Padding = new Thickness(7, 1, 7, 1), Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = wo.StatusLabel.ToUpperInvariant(), FontSize = 8, FontWeight = FontWeights.Bold, Foreground = statusBrush },
        };
        Grid.SetColumn(chip, 1);
        top.Children.Add(chip);
        stack.Children.Add(top);

        // meta: resources ◆ location
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(wo.Resources)) parts.Add(wo.Resources);
        if (!string.IsNullOrWhiteSpace(wo.Location))  parts.Add("◆ " + wo.Location);
        if (parts.Count > 0)
            stack.Children.Add(new TextBlock { Text = string.Join("    ", parts), Margin = new Thickness(0, 5, 0, 0), FontSize = 9, Foreground = dim, TextTrimming = TextTrimming.CharacterEllipsis });

        // timer + animated progress bar
        TextBlock? subtitleTb = null;
        ScaleTransform? scale = null;
        if (wo.HasActiveTimer)
        {
            var timerRow = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            timerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            timerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            subtitleTb = new TextBlock
            {
                Text = wo.SubtitleText, FontSize = 9, FontWeight = FontWeights.Bold,
                Foreground = BrushFromHex(wo.SubtitleForeground),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
            };
            timerRow.Children.Add(subtitleTb);

            scale = new ScaleTransform(wo.TimerFraction, 1);
            var barGrid = new Grid { Height = 5, VerticalAlignment = VerticalAlignment.Center };
            barGrid.Children.Add(new Border { Background = trackBg, CornerRadius = new CornerRadius(2) });
            barGrid.Children.Add(new Border
            {
                Background = statusBrush, CornerRadius = new CornerRadius(2),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                RenderTransform = scale,
                RenderTransformOrigin = new Point(0, 0.5),
            });
            Grid.SetColumn(barGrid, 1);
            timerRow.Children.Add(barGrid);
            stack.Children.Add(timerRow);

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
        grid.Children.Add(stack);
        outer.Child = grid;
        return outer;
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
