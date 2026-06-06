using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Nexus_v4.Models;
using Nexus_v4.Services;
using Nexus_v4.ViewModels;

namespace Nexus_v4.Views;

public partial class OverlayWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _boxVisible = false;
    private string _activeTab = "scan";
    private WorkOrderFlyoutWindow? _woFlyout;

    public event Action<Nexus_v4.Models.ScanRegion>? ScanRegionSelected;
    public event Action<bool>? BoxVisibilityToggled;
    public event Action? Hidden;
    public event Action? Shown;

    // ── Welcome-tour targets ───────────────────────────────────────────────────
    public FrameworkElement SetRegionTarget   => SetRegionBtn;
    public FrameworkElement BoxToggleTarget   => BoxToggleBtn;
    public FrameworkElement ScanToggleTarget  => ScanToggleBtn;
    public FrameworkElement TabStripTarget    => TabOrdersBtn;    // points at the SCAN/ORDERS/SHOPPING strip
    public FrameworkElement ShoppingTabTarget => TabShoppingBtn;

    /// <summary>Force the SCAN tab visible so the tour can point at the scan controls.</summary>
    public void ShowScanTabForTutorial() => SwitchTab("scan");

    /// <summary>Force the SHOPPING tab visible so the tour can point at the cart.</summary>
    public void ShowShoppingTabForTutorial() => SwitchTab("shopping");

    public OverlayWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;

        var s = App.Settings.Current;
        Left = s.OverlayLeft; Top = s.OverlayTop;
        Width = s.OverlayWidth; Height = s.OverlayHeight;

        OpacitySlider.ValueChanged -= OpacitySlider_ValueChanged;
        OpacitySlider.Value = 1.0;
        OpacitySlider.ValueChanged += OpacitySlider_ValueChanged;
        this.Opacity = 1.0;
        OpacityLabel.Text = "100%";

        _vm.FilteredScanHistory.CollectionChanged += (s, e) => RebuildHistory();
        RebuildHistory();

        BuildOverlayHistoryFilterPills();
        _vm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.HistoryFilter))
                BuildOverlayHistoryFilterPills();
        };

        SwitchTab("scan");

        WorkOrderEditorPanel.OrderReadyToCollect += label => PulseWorkOrderButton();

        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue) Shown?.Invoke();
            else Hidden?.Invoke();
        };
    }

    public void ReceiveOcrValue(int value)
    {
        OverlayRsInput.Text = value.ToString("N0");
        OverlayScanStatus.Text = $"◎  Auto-scanned: {value:N0}";
        OverlayScanStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0xC9, 0xA7));
        RunScan(value);
    }

    public void ReceiveScanPhase(ScanPhase phase)
    {
        switch (phase)
        {
            case ScanPhase.Watching:
                OverlayScanStatus.Text = "◎  Scanning…";
                OverlayScanStatus.Foreground = (Brush)FindResource("FgDimBrush");
                break;
            case ScanPhase.PinFound:
                OverlayScanStatus.Text = "◉  Reading…";
                OverlayScanStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
                break;
            case ScanPhase.NoRegion:
                OverlayScanStatus.Text = "⊕  Draw region to scan";
                OverlayScanStatus.Foreground = (Brush)FindResource("FgDimBrush");
                break;
        }
    }

    public void ReceiveScanProgress(int count)
    {
        OverlayScanStatus.Text = $"◉  Reading… {count}/2";
        OverlayScanStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
    }

    private void BoxToggle_Click(object sender, RoutedEventArgs e)
    {
        _boxVisible = !_boxVisible;
        BoxToggleBtn.Content = _boxVisible ? "⊡" : "⊠";
        BoxToggleBtn.ToolTip = _boxVisible ? "Hide scan box" : "Show scan box";
        BoxVisibilityToggled?.Invoke(_boxVisible);
    }

    private void SetRegion_Click(object sender, RoutedEventArgs e)
    {
        var selector = new RegionSelectorWindow();
        selector.RegionSelected += r => ScanRegionSelected?.Invoke(r);
        selector.Show();
    }

    private void ScanToggle_Click(object sender, RoutedEventArgs e)
    {
        _vm.ToggleScanCommand.Execute(null);
        if (_vm.IsScanActive)
        {
            ScanToggleBtn.Content = "■";
            ScanToggleBtn.ToolTip = "Stop auto-scan";
            OverlayScanStatus.Text = "◎  Scanning…";
            OverlayScanStatus.Foreground = (Brush)FindResource("FgDimBrush");
        }
        else
        {
            ScanToggleBtn.Content = "▶";
            ScanToggleBtn.ToolTip = "Start auto-scan";
            OverlayScanStatus.Text = "◎  Scan stopped";
            OverlayScanStatus.Foreground = (Brush)FindResource("FgDimBrush");
        }
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        this.Opacity = e.NewValue;
        if (OpacityLabel != null) OpacityLabel.Text = $"{(int)(e.NewValue * 100)}%";
        App.Settings.Current.OverlayOpacity = e.NewValue;
        App.Settings.Save();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.Current.OverlayLeft = Left; App.Settings.Current.OverlayTop = Top;
        App.Settings.Current.OverlayWidth = Width; App.Settings.Current.OverlayHeight = Height;
        App.Settings.Save();
        _woFlyout?.Hide();
        Hide();
    }

    private void OverlayRsInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { RunScanFromInput(); OverlayRsInput.Clear(); }
        if (e.Key == Key.Escape) Hide();
    }

    private void OverlayScan_Click(object sender, RoutedEventArgs e) => RunScanFromInput();

    private void RunScanFromInput()
    {
        var text = OverlayRsInput.Text.Replace(",", "").Trim();
        if (int.TryParse(text, out var rs)) RunScan(rs);
    }

    private void RunScan(int rs)
    {
        _vm.RsInput = rs.ToString();
        _vm.LookupCommand.Execute(null);
        OverlayResults.ItemsSource = _vm.ScanResults;
    }

    private void BuildOverlayHistoryFilterPills()
    {
        OverlayHistoryFilterPanel.Children.Clear();
        (string Label, HistoryFilter Filter)[] options =
        [
            ("All",          HistoryFilter.All),
            ("Exact+Close",  HistoryFilter.ExactAndClose),
            ("Exact",        HistoryFilter.Exact),
        ];
        foreach (var (label, filter) in options)
        {
            var f = filter;
            var btn = new Button
            {
                Content = label,
                Style = (Style)FindResource(_vm.HistoryFilter == f ? "AccentButton" : "NexusButton"),
                Padding = new Thickness(5, 1, 5, 1),
                Margin = new Thickness(0, 0, 3, 0),
                FontSize = 8,
                Height = 18,
            };
            btn.Click += (_, __) => { _vm.HistoryFilter = f; };
            OverlayHistoryFilterPanel.Children.Add(btn);
        }
    }

    private void RebuildHistory()
    {
        HistoryStrip.Children.Clear();
        var hoverBg  = (Brush)FindResource("HighlightBrush");
        var cartTeal = new SolidColorBrush(Color.FromArgb(0x26, 0x00, 0xC9, 0xA7));

        foreach (var entry in _vm.FilteredScanHistory)
        {
            var rsColor    = new SolidColorBrush((Color)ColorConverter.ConvertFromString(entry.RsColor));
            var defaultBg  = entry.IsInCart ? cartTeal : Brushes.Transparent;

            var diamond = new TextBlock
            {
                Text = "◆", FontSize = 9, Foreground = rsColor,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
            };

            var nameBlock = new TextBlock
            {
                Text = entry.TopResource, FontSize = 10,
                Foreground = (Brush)FindResource("FgBrush"),
                Opacity = entry.NameOpacity,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            var cartBadge = new Border
            {
                Padding = new Thickness(3, 1, 3, 1),
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(6, 0, 0, 0),
                Background = new SolidColorBrush(Color.FromRgb(0x00, 0xC9, 0xA7)),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = entry.IsInCart ? Visibility.Visible : Visibility.Collapsed,
                Child = new TextBlock
                {
                    Text = "CART", FontSize = 8, FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x07, 0x0B, 0x12)),
                },
            };

            var rsBlock = new TextBlock
            {
                Text = $"RS {entry.Rs:N0}", FontSize = 10, Foreground = rsColor,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var namePanel = new StackPanel { Orientation = Orientation.Horizontal };
            namePanel.Children.Add(nameBlock);
            namePanel.Children.Add(cartBadge);

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(diamond, 0);
            Grid.SetColumn(namePanel, 1);
            Grid.SetColumn(rsBlock, 2);
            row.Children.Add(diamond);
            row.Children.Add(namePanel);
            row.Children.Add(rsBlock);

            var rowBorder = new Border
            {
                Child = row,
                Padding = new Thickness(10, 4, 10, 4),
                Background = defaultBg,
                Cursor = Cursors.Hand,
                Tag = entry,
            };
            rowBorder.MouseEnter  += (s, _) => ((Border)s).Background = hoverBg;
            rowBorder.MouseLeave  += (s, _) => ((Border)s).Background = defaultBg;
            rowBorder.MouseLeftButtonDown += (s, _) =>
            {
                var e2 = (ScanHistoryEntry)((Border)s).Tag!;
                OverlayRsInput.Text = e2.Rs.ToString("N0");
                _vm.RunScanNoHistory(e2.Rs);
                OverlayResults.ItemsSource = _vm.ScanResults;
            };
            HistoryStrip.Children.Add(rowBorder);
        }
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        _vm.ScanHistory.Clear();
    }

    private void ResultCard_Click(object sender, MouseButtonEventArgs e)
    {
        // Mirror click to main window lookup — already synced via shared vm
    }

    private void PulseWorkOrderButton()
    {
        var anim = new System.Windows.Media.Animation.DoubleAnimation(1.0, 0.25, TimeSpan.FromMilliseconds(300))
        {
            AutoReverse = true,
            RepeatBehavior = new System.Windows.Media.Animation.RepeatBehavior(4),
        };
        WorkOrderBtn.BeginAnimation(OpacityProperty, anim);
    }

    private void WorkOrderToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_woFlyout == null || !_woFlyout.IsVisible)
        {
            if (_woFlyout == null)
            {
                _woFlyout = new WorkOrderFlyoutWindow(_vm);
                _woFlyout.AnchorTo(this);
            }
            else
            {
                _woFlyout.Rebuild();
            }
            _woFlyout.ShowWithAnimation();
        }
        else
        {
            _woFlyout.HideWithAnimation();
        }
    }

    private void TabScan_Click(object sender, RoutedEventArgs e) => SwitchTab("scan");
    private void TabOrders_Click(object sender, RoutedEventArgs e) => SwitchTab("orders");
    private void TabShopping_Click(object sender, RoutedEventArgs e) => SwitchTab("shopping");

    private void SwitchTab(string tab)
    {
        _activeTab = tab;
        ScanTabContent.Visibility     = tab == "scan"     ? Visibility.Visible : Visibility.Collapsed;
        OrdersTabContent.Visibility   = tab == "orders"   ? Visibility.Visible : Visibility.Collapsed;
        ShoppingTabContent.Visibility = tab == "shopping" ? Visibility.Visible : Visibility.Collapsed;

        var accent = new SolidColorBrush(Color.FromRgb(0x00, 0xC9, 0xA7));
        var none   = Brushes.Transparent;
        TabScanIndicator.Background     = tab == "scan"     ? accent : none;
        TabOrdersIndicator.Background   = tab == "orders"   ? accent : none;
        TabShoppingIndicator.Background = tab == "shopping" ? accent : none;

        if (tab == "shopping") RebuildShoppingPanel();
    }

    private void RebuildShoppingPanel()
    {
        ShoppingPanelItems.Children.Clear();
        foreach (var item in _vm.ShoppingList)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            row.Children.Add(new TextBlock
            {
                Text = $"{item.ResourceName}  ×{item.Quantity:0.##} {item.Unit}",
                Foreground = (System.Windows.Media.Brush)FindResource("FgBrush"),
                FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            });

            var removeBtn = new Button
            {
                Content = "−", Style = (Style)FindResource("NexusButton"),
                Padding = new Thickness(5, 2, 5, 2), Tag = item.ResourceName,
            };
            removeBtn.Click += (s, e) =>
            {
                _vm.RemoveFromShoppingCommand.Execute(((Button)s).Tag);
                RebuildShoppingPanel();
            };
            Grid.SetColumn(removeBtn, 1);
            row.Children.Add(removeBtn);

            ShoppingPanelItems.Children.Add(row);
        }

        if (_vm.ShoppingList.Count == 0)
            ShoppingPanelItems.Children.Add(new TextBlock
            {
                Text = "Shopping list is empty", FontSize = 12,
                Foreground = (System.Windows.Media.Brush)FindResource("FgDimBrush"),
            });
    }
}
