using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NexusApp.Models;
using NexusApp.Services;
using NexusApp.ViewModels;

namespace NexusApp.Views;

public partial class OverlayWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _boxVisible = false;
    private string _activeTab = "scan";
    private WorkOrderFlyoutWindow? _woFlyout;

    public event Action<NexusApp.Models.ScanRegion>? ScanRegionSelected;
    public event Action<bool>? BoxVisibilityToggled;
    public event Action? Hidden;
    public event Action? Shown;
    /// <summary>STATS tab asked to open the advanced Game.log monitor window.</summary>
    public event Action? OpenMonitorRequested;

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

        OverlayIcon.Source = new System.Windows.Media.Imaging.BitmapImage(new System.Uri(NexusApp.Services.ThemeService.IconUri));
        NexusApp.Services.ThemeService.ThemeChanged += () =>
            OverlayIcon.Source = new System.Windows.Media.Imaging.BitmapImage(new System.Uri(NexusApp.Services.ThemeService.IconUri));

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

        _vm.WorkOrders.CollectionChanged += (s, e) => { if (_activeTab == "orders") RebuildOrdersPanel(); };

        BuildOverlayHistoryFilterPills();
        _vm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.HistoryFilter))
                BuildOverlayHistoryFilterPills();
        };

        SwitchTab("stats");

        // BETA Game.log blueprint session — drive + mirror it from the STATS tab. The
        // overlay lives for the app's lifetime (created once, hidden/shown), so these
        // never need unsubscribing.
        App.GameLog.Marked += OnGameLogMarked;
        App.GameLog.StateChanged += SyncStatsControls;
        App.GameLog.StatusChanged += OnGameLogStatus;
        App.GameLog.SessionReset += OnSessionReset;
        BuildStatsControls();

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
        OverlayScanStatus.Foreground = (System.Windows.Media.SolidColorBrush)System.Windows.Application.Current.FindResource("AccentBrush");
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
        if (_woFlyout != null) _woFlyout.Opacity = e.NewValue;
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
        _ordersTicker?.Stop();
        _ordersTicker = null;
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
        var cartTeal = new SolidColorBrush(Color.FromArgb(0x26, 0xC9, 0xA2, 0x4B));

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
                Background = (System.Windows.Media.SolidColorBrush)System.Windows.Application.Current.FindResource("AccentBrush"),
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
            _woFlyout.Opacity = this.Opacity;
            _woFlyout.ShowWithAnimation();
        }
        else
        {
            _woFlyout.HideWithAnimation();
        }
    }

    private void TabStats_Click(object sender, RoutedEventArgs e) => SwitchTab("stats");
    private void TabScan_Click(object sender, RoutedEventArgs e) => SwitchTab("scan");
    private void TabOrders_Click(object sender, RoutedEventArgs e) => SwitchTab("orders");
    private void TabShopping_Click(object sender, RoutedEventArgs e) => SwitchTab("shopping");

    private void SwitchTab(string tab)
    {
        _activeTab = tab;
        StatsTabContent.Visibility    = tab == "stats"    ? Visibility.Visible : Visibility.Collapsed;
        ScanTabContent.Visibility     = tab == "scan"     ? Visibility.Visible : Visibility.Collapsed;
        OrdersTabContent.Visibility   = tab == "orders"   ? Visibility.Visible : Visibility.Collapsed;
        ShoppingTabContent.Visibility = tab == "shopping" ? Visibility.Visible : Visibility.Collapsed;

        var accent = (System.Windows.Media.SolidColorBrush)System.Windows.Application.Current.FindResource("AccentBrush");
        var none   = Brushes.Transparent;
        TabStatsIndicator.Background    = tab == "stats"    ? accent : none;
        TabScanIndicator.Background     = tab == "scan"     ? accent : none;
        TabOrdersIndicator.Background   = tab == "orders"   ? accent : none;
        TabShoppingIndicator.Background = tab == "shopping" ? accent : none;

        // RECENT scans belong to the SCAN tab only — hide the strip on every other tab.
        SetHistoryStripVisible(tab == "scan");

        if (tab == "stats") RebuildStatsPanel();
        if (tab == "shopping") RebuildShoppingPanel();

        if (tab == "orders")
        {
            RebuildOrdersPanel();
        }
        else
        {
            _ordersTicker?.Stop();
            _ordersTicker = null;
        }
    }

    // The RECENT scan-history strip + its splitter live in the window chrome (below the
    // tabs), so they'd otherwise show on every tab. They're scan-only — collapse them and
    // reclaim their rows on the STATS tab, preserving any height the user dragged them to.
    private GridLength _savedHistoryHeight = new(120);
    private double _savedHistoryMinHeight = 50;
    private bool _historyHidden;

    private void SetHistoryStripVisible(bool show)
    {
        if (show && _historyHidden)
        {
            HistoryStripRow.Height = _savedHistoryHeight;
            HistoryStripRow.MinHeight = _savedHistoryMinHeight;
            HistorySplitterRow.Height = new GridLength(4);
            HistorySplitterRow.MinHeight = 4;
            HistoryStrip_Container.Visibility = Visibility.Visible;
            HistorySplitter.Visibility = Visibility.Visible;
            _historyHidden = false;
        }
        else if (!show && !_historyHidden)
        {
            _savedHistoryHeight = HistoryStripRow.Height;
            _savedHistoryMinHeight = HistoryStripRow.MinHeight;
            HistoryStripRow.MinHeight = 0;
            HistoryStripRow.Height = new GridLength(0);
            HistorySplitterRow.MinHeight = 0;
            HistorySplitterRow.Height = new GridLength(0);
            HistoryStrip_Container.Visibility = Visibility.Collapsed;
            HistorySplitter.Visibility = Visibility.Collapsed;
            _historyHidden = true;
        }
    }

    // ── STATS tab (Beta Game.log session) ──────────────────────────────────────
    private Border? _trackSwTrack, _trackSwKnob, _autoSwTrack, _autoSwKnob;

    // Builds the two compact on/off switches (Track Session, Auto-Track Blueprints) once.
    private void BuildStatsControls()
    {
        StatsControlBar.Children.Clear();

        _trackSwTrack = NewSwitchTrack();
        _trackSwKnob  = NewSwitchKnob();
        _trackSwTrack.Child = _trackSwKnob;
        StatsControlBar.Children.Add(SwitchPair(_trackSwTrack, "Track Session", ToggleTrackSession));

        _autoSwTrack = NewSwitchTrack();
        _autoSwKnob  = NewSwitchKnob();
        _autoSwTrack.Child = _autoSwKnob;
        StatsControlBar.Children.Add(SwitchPair(_autoSwTrack, "Auto-Track Blueprints", ToggleAutoTrack));

        SyncStatsControls();
    }

    private void ToggleTrackSession()
    {
        if (App.GameLog.IsRunning) App.GameLog.Stop();
        else App.GameLog.Start(App.GameLog.StartPath());   // probes common installs if no path yet
    }

    private void OpenMonitorFromStats_Click(object sender, MouseButtonEventArgs e) => OpenMonitorRequested?.Invoke();

    // Reset() raises SessionReset, which OnSessionReset handles (clears the note + refreshes).
    private void ResetSession_Click(object sender, MouseButtonEventArgs e) => App.GameLog.Reset();

    // Feedback line under the switches: what the watcher is doing + the last blueprint collected.
    private string _lastWatcherStatus = "";
    private string _lastCollected = "";

    private void OnGameLogStatus(string s) { _lastWatcherStatus = s; UpdateStatsStatus(); }

    private void UpdateStatsStatus()
    {
        if (StatsStatus == null) return;
        if (!App.GameLog.IsRunning) { StatsStatus.Text = "Stopped"; return; }
        var s = string.IsNullOrEmpty(_lastWatcherStatus) ? "Watching" : _lastWatcherStatus;
        if (!string.IsNullOrEmpty(_lastCollected)) s += $"  ·  last: {_lastCollected}";
        StatsStatus.Text = s;
    }

    // Auto-Track on also starts the watch (handled in GameLogSession); turning it off leaves the watch running.
    private void ToggleAutoTrack() => App.GameLog.SetAutoMark(!App.GameLog.AutoMark);

    private static Border NewSwitchTrack() => new()
    {
        Width = 30, Height = 17, CornerRadius = new CornerRadius(9),
        BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center,
    };

    private static Border NewSwitchKnob() => new()
    {
        Width = 13, Height = 13, CornerRadius = new CornerRadius(7),
        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 2, 0),
    };

    private UIElement SwitchPair(Border track, string label, Action onToggle)
    {
        var pair = new StackPanel
        {
            Orientation = Orientation.Horizontal, Cursor = Cursors.Hand,
            Margin = new Thickness(0, 0, 14, 4), VerticalAlignment = VerticalAlignment.Center,
        };
        pair.Children.Add(track);
        pair.Children.Add(new TextBlock
        {
            Text = label, FontSize = 11, Foreground = (Brush)FindResource("FgBrush"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0),
        });
        pair.MouseLeftButtonUp += (_, _) => { onToggle(); SyncStatsControls(); };
        return pair;
    }

    private void SetSwitch(Border? track, Border? knob, bool on)
    {
        if (track == null || knob == null) return;
        track.Background  = on ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("Bg3Brush");
        track.BorderBrush = on ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("NavBorderBrush");
        knob.HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        knob.Background = on ? (Brush)FindResource("OnAccentBrush") : (Brush)FindResource("FgDimBrush");
    }

    private void OnGameLogMarked(BlueprintMark m)
    {
        _lastCollected = m.Name;
        UpdateStatsStatus();
        if (_activeTab == "stats") RebuildStatsPanel();
    }

    // Session tally was cleared (new SC session, or a manual reset) — drop the last-collected
    // note and refresh the visible counts.
    private void OnSessionReset()
    {
        _lastCollected = "";
        if (_activeTab == "stats") RebuildStatsPanel(); else UpdateStatsStatus();
    }

    // The overlay is app-lifetime (hidden/shown, not closed) in normal use; this only runs if
    // it's discarded — e.g. MainWindow recreates it after an error — so detach the app-lifetime
    // session handlers to avoid leaking them onto a dead window.
    protected override void OnClosed(EventArgs e)
    {
        App.GameLog.Marked -= OnGameLogMarked;
        App.GameLog.StateChanged -= SyncStatsControls;
        App.GameLog.StatusChanged -= OnGameLogStatus;
        App.GameLog.SessionReset -= OnSessionReset;
        base.OnClosed(e);
    }

    // Reflects watcher / auto-mark state onto the two switches.
    private void SyncStatsControls()
    {
        SetSwitch(_trackSwTrack, _trackSwKnob, App.GameLog.IsRunning);
        SetSwitch(_autoSwTrack, _autoSwKnob, App.GameLog.AutoMark);
        UpdateStatsStatus();
    }

    private void RebuildStatsPanel()
    {
        SyncStatsControls();

        // Count list: blueprints collected this session (header is static "THIS SESSION" from XAML).
        var accent = (Brush)FindResource("AccentBrush");
        StatsListItems.Children.Clear();
        AddStatRow("Blueprints collected", App.GameLog.Count, accent, true);

        // Blueprints-collected feed (newest first).
        StatsFeedItems.Children.Clear();
        var marks = App.GameLog.Marks;
        StatsEmptyState.Visibility = marks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var dim    = (Brush)FindResource("FgDimBrush");
        var fg     = (Brush)FindResource("FgBrush");
        var border = (Brush)FindResource("NavBorderBrush");

        for (int i = marks.Count - 1; i >= 0; i--)   // newest first
        {
            var mk = marks[i];
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var time = new TextBlock
            {
                Text = mk.At.ToString("HH:mm:ss"), FontSize = 9, Foreground = dim,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
            };
            var name = new TextBlock
            {
                Text = mk.Name, FontSize = 11, Foreground = fg,
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(name, 1);
            row.Children.Add(time);
            row.Children.Add(name);

            StatsFeedItems.Children.Add(new Border
            {
                Child = row,
                Padding = new Thickness(2, 3, 2, 3),
                BorderBrush = border,
                BorderThickness = new Thickness(0, 0, 0, 1),
            });
        }
    }

    // One "Label .............. Value" row in the session count list.
    private void AddStatRow(string label, int value, Brush valueBrush, bool last)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock
        {
            Text = label, FontSize = 12, Foreground = (Brush)FindResource("FgBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var v = new TextBlock
        {
            Text = value.ToString(), FontSize = 16, FontWeight = FontWeights.Bold,
            Foreground = valueBrush, VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(v, 1);
        row.Children.Add(v);

        StatsListItems.Children.Add(new Border
        {
            Child = row,
            Padding = new Thickness(0, 7, 0, 7),
            BorderBrush = (Brush)FindResource("NavBorderBrush"),
            BorderThickness = new Thickness(0, 0, 0, last ? 0 : 1),
        });
    }

    private System.Windows.Threading.DispatcherTimer? _ordersTicker;
    private readonly Dictionary<string, (TextBlock Txt, ColumnDefinition Fill, ColumnDefinition Remain)> _orderTimerRefs = new();

    private void RebuildOrdersPanel()
    {
        OrdersPanelItems.Children.Clear();
        OrdersSummaryPanel.Children.Clear();
        _orderTimerRefs.Clear();

        var orders = _vm.WorkOrders;
        bool any = orders.Count > 0;
        OrdersEmptyState.Visibility   = any ? Visibility.Collapsed : Visibility.Visible;
        OrdersSummaryPanel.Visibility = any ? Visibility.Visible : Visibility.Collapsed;

        if (!any)
        {
            _ordersTicker?.Stop();
            _ordersTicker = null;
            return;
        }

        BuildOrdersSummary(orders);

        foreach (var wo in orders)
            OrdersPanelItems.Children.Add(BuildOverlayOrderCard(wo));

        var hasTimers = orders.Any(w => w.HasActiveTimer);
        if (hasTimers && _ordersTicker == null)
        {
            _ordersTicker = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _ordersTicker.Tick += OrdersTicker_Tick;
            _ordersTicker.Start();
        }
        else if (!hasTimers)
        {
            _ordersTicker?.Stop();
            _ordersTicker = null;
        }
    }

    private void BuildOrdersSummary(System.Collections.Generic.IEnumerable<WorkOrder> orders)
    {
        var dim    = (Brush)FindResource("FgDimBrush");
        var accent = (Brush)FindResource("AccentBrush");
        var list   = orders.ToList();

        var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        headerRow.Children.Add(new TextBlock { Text = "ACTIVE ORDERS", FontSize = 9, FontWeight = FontWeights.Bold, Foreground = dim, VerticalAlignment = VerticalAlignment.Center });
        headerRow.Children.Add(new TextBlock { Text = list.Count.ToString(), FontSize = 9, FontWeight = FontWeights.Bold, Foreground = accent, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        OrdersSummaryPanel.Children.Add(headerRow);

        var chips = new WrapPanel();
        (WorkOrderStatus St, string Label)[] seq =
        [
            (WorkOrderStatus.Mining, "Mining"),
            (WorkOrderStatus.Refining, "Refining"),
            (WorkOrderStatus.ReadyToCollect, "Ready"),
            (WorkOrderStatus.Complete, "Complete"),
        ];
        foreach (var (st, label) in seq)
        {
            int n = list.Count(w => w.Status == st);
            if (n == 0) continue;
            chips.Children.Add(MakeStatusChip($"{n} {label}", StatusHex(st)));
        }
        OrdersSummaryPanel.Children.Add(chips);
    }

    private static string StatusHex(WorkOrderStatus s) => s switch
    {
        WorkOrderStatus.Mining         => "#3B82F6",
        WorkOrderStatus.Refining       => "#E67E22",
        WorkOrderStatus.ReadyToCollect => "#2ECC71",
        WorkOrderStatus.Complete       => "#7F8C8D",
        _                              => "#7F8C8D",
    };

    private static SolidColorBrush HexBrush(string hex) =>
        new((Color)ColorConverter.ConvertFromString(hex));

    private Border MakeStatusChip(string text, string hex)
    {
        var col  = (Color)ColorConverter.ConvertFromString(hex);
        var fill = new SolidColorBrush(col);
        var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = fill, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        sp.Children.Add(new TextBlock { Text = text, FontSize = 9, FontWeight = FontWeights.Bold, Foreground = fill, VerticalAlignment = VerticalAlignment.Center });
        return new Border
        {
            Child = sp,
            Background = new SolidColorBrush(Color.FromArgb(0x22, col.R, col.G, col.B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x88, col.R, col.G, col.B)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(0, 0, 6, 6),
        };
    }

    private Border BuildOverlayOrderCard(WorkOrder wo)
    {
        var cardBg   = (Brush)FindResource("Bg2NavBrush");
        var navB     = (Brush)FindResource("NavBorderBrush");
        var fg       = (Brush)FindResource("FgBrush");
        var dim      = (Brush)FindResource("FgDimBrush");
        var chipBg   = (Brush)FindResource("Bg3Brush");
        var trackBg  = (Brush)FindResource("BorderBrush");
        var headFont = (FontFamily)FindResource("HeadFont");
        var statusBrush = HexBrush(wo.StatusColorHex);

        var outer = new Border { Background = cardBg, BorderBrush = navB, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 0, 8) };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new Border { Background = statusBrush, CornerRadius = new CornerRadius(8, 0, 0, 8) });

        var stack = new StackPanel { Margin = new Thickness(12, 9, 10, 9) };
        Grid.SetColumn(stack, 1);

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(wo.Label) ? wo.Resources : wo.Label,
            FontFamily = headFont, FontSize = 13, Foreground = fg,
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

        var parts = new System.Collections.Generic.List<string>();
        if (!string.IsNullOrWhiteSpace(wo.Resources)) parts.Add(wo.Resources);
        if (!string.IsNullOrWhiteSpace(wo.Location))  parts.Add("◆ " + wo.Location);
        if (parts.Count > 0)
            stack.Children.Add(new TextBlock { Text = string.Join("    ", parts), Margin = new Thickness(0, 5, 0, 0), FontSize = 10, Foreground = dim, TextTrimming = TextTrimming.CharacterEllipsis });

        if (wo.HasActiveTimer)
        {
            var timerRow = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            timerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            timerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var tTxt = new TextBlock { Text = wo.TimerRemainingShort, FontSize = 9, FontWeight = FontWeights.Bold, Foreground = HexBrush("#E67E22"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            timerRow.Children.Add(tTxt);

            var frac = wo.TimerFraction;
            var fillCol = new ColumnDefinition { Width = new GridLength(frac, GridUnitType.Star) };
            var restCol = new ColumnDefinition { Width = new GridLength(1 - frac, GridUnitType.Star) };
            var barGrid = new Grid();
            barGrid.ColumnDefinitions.Add(fillCol);
            barGrid.ColumnDefinitions.Add(restCol);
            barGrid.Children.Add(new Border { Background = statusBrush, CornerRadius = new CornerRadius(2) });
            var track = new Border { Background = trackBg, CornerRadius = new CornerRadius(2), Height = 5, VerticalAlignment = VerticalAlignment.Center, Child = barGrid };
            Grid.SetColumn(track, 1);
            timerRow.Children.Add(track);
            stack.Children.Add(timerRow);

            _orderTimerRefs[wo.Id] = (tTxt, fillCol, restCol);
        }

        grid.Children.Add(stack);
        outer.Child = grid;
        return outer;
    }

    private void OrdersTicker_Tick(object? sender, EventArgs e)
    {
        bool anyActive = false;
        foreach (var wo in _vm.WorkOrders)
        {
            if (!_orderTimerRefs.TryGetValue(wo.Id, out var refs)) continue;
            if (!wo.HasActiveTimer) { RebuildOrdersPanel(); return; }
            anyActive = true;
            var frac = wo.TimerFraction;
            refs.Fill.Width = new GridLength(frac, GridUnitType.Star);
            refs.Remain.Width = new GridLength(1 - frac, GridUnitType.Star);
            refs.Txt.Text = wo.TimerRemainingShort;
        }
        if (!anyActive) { _ordersTicker?.Stop(); _ordersTicker = null; }
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
