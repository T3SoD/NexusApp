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
    private RegionSelectorWindow? _regionSelector;   // single live draw-region overlay (issue #8)
    private RegionSelectorWindow? _contractRegionSelector;   // independent draw overlay for the contract region
    private bool _contractBoxVisible;

    public event Action<NexusApp.Models.ScanRegion>? ScanRegionSelected;
    public event Action<bool>? BoxVisibilityToggled;
    // Independent cargo-contract region/box events (mirror the RS pair above; MainWindow owns the yellow indicator).
    public event Action<NexusApp.Models.ScanRegion>? ContractRegionSelected;
    public event Action<bool>? ContractBoxVisibilityToggled;
    public event Action? Hidden;
    public event Action? Shown;

    // ── Welcome-tour targets ───────────────────────────────────────────────────
    public FrameworkElement ScanToggleTarget  => _scanSwitchPair ?? SetRegionBtn;
    public FrameworkElement HubTarget         => HubScanBar;      // the HUB's SCAN STATUS light rows
    public FrameworkElement ContractRegionTarget => SetContractRegionBtn;   // HAULING tab's set-region link

    /// <summary>Force the SCAN tab visible so the tour can point at the scan controls.</summary>
    public void ShowScanTabForTutorial() => SwitchTab("scan");

    /// <summary>Force the HUB tab visible so the tour can point at the status lights.</summary>
    public void ShowHubTabForTutorial() => SwitchTab("stats");

    /// <summary>Force the HAULING tab visible so the tour can point at the contract scan controls.</summary>
    public void ShowHaulingTabForTutorial() => SwitchTab("hauling");

    // Static-event handlers held as fields so OnClosed can detach them (a recreated overlay must not leak).
    private readonly Action<string> _onOrderReady;

    public OverlayWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;

        // Chamfered shell: recompute the frame Path + the content clip whenever the overlay is resized
        // (CanResizeWithGrip), so the MOBIGLAS bevel tracks the window. Fires on first layout too.
        SizeChanged += (_, _) => UpdateChamfer();

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
            else if (e.PropertyName == nameof(MainViewModel.IsScanActive))
                SyncScanControls();
        };

        SwitchTab("stats");

        // BETA Game.log blueprint session - drive + mirror it from the STATS tab. The
        // overlay lives for the app's lifetime (created once, hidden/shown), so these
        // never need unsubscribing.
        App.GameLog.Marked += OnGameLogMarked;
        App.GameLog.SessionReset += OnSessionReset;
        App.GameLog.StateChanged += RefreshSessionLed;        // keep the HUB SESSION LED live (start/stop)
        App.GameLog.StatusChanged += OnGameLogStatusChanged;  // and as SC opens / closes (session liveness)

        // Cargo Hauling glance tab: refresh the list when the tracker changes, but only while
        // the HAULING tab is the one on screen (mirrors how OnGameLogMarked guards the STATS tab).
        App.Hauls.Changed += OnHaulsChanged;

        // Server / Shard section (top of the STATS tab): refresh when the shard history changes,
        // but only while the STATS tab is on screen (same guard pattern as OnHaulsChanged).
        App.Shards.Changed += OnShardsChanged;

        // Foreground gating: when neither Nexus nor Star Citizen is in front, OCR auto-scans pause.
        // Re-sync the HUB scan LEDs so they flip to/from the yellow paused state as that happens.
        App.ForegroundRelevanceChanged += OnForegroundRelevanceChanged;

        // Contract scan/box are shared state: re-sync the contract toggle + box switch/LED whenever the
        // main Cargo Hauling page (or anything else) flips them, so the two surfaces never drift.
        App.ContractScan.RunningChanged += SyncContractFromShared;
        App.ContractBoxVisibilityChanged += OnContractBoxShared;
        _contractBoxVisible = App.ContractBoxVisible;   // seed from shared state so the switch isn't stale on first open

        UpdateHaulingTabLabel();   // initial overlay tab count (updates as hauls stream in)

        BuildScanControls();
        BuildHaulingControls();
        BuildHubScanControls();

        _onOrderReady = label => PulseWorkOrderButton();
        WorkOrderEditorPanel.OrderReadyToCollect += _onOrderReady;

        IsVisibleChanged += (_, e) =>
        {
            bool visible = (bool)e.NewValue;
            Logger.Info($"[WIN] overlay {(visible ? "shown" : "hidden")}");
            if (visible) Shown?.Invoke();
            else Hidden?.Invoke();
        };
    }

    // Paint the chamfered shell: the FramePath silhouette (bevelled fill + 1px border) and the matching
    // clip on ContentRoot so inner content stays inside the TL + BR bevels. 16px chamfer = the mock frame.
    private void UpdateChamfer()
    {
        var geo = Hud.ChamferGeometry(ActualWidth, ActualHeight, 16);
        FramePath.Data = geo;
        ContentRoot.Clip = geo;
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

    private void SetRegion_Click(object sender, MouseButtonEventArgs e)
    {
        InteractionLog.Click("Set RS detection region", (System.Windows.DependencyObject)sender);

        // Toggle: a second click while the draw overlay is up closes it instead of stacking
        // another full-screen tint, which would progressively black out the screen (issue #8).
        if (_regionSelector != null) { _regionSelector.Close(); return; }

        var selector = new RegionSelectorWindow();
        _regionSelector = selector;
        selector.RegionSelected += r => ScanRegionSelected?.Invoke(r);
        selector.Closed += (_, _) => { if (ReferenceEquals(_regionSelector, selector)) _regionSelector = null; };
        // Open the draw surface on the monitor this overlay sits on (the user drags it onto the
        // game's monitor), not always the primary - issue #6.
        selector.ShowOnMonitorOf(this);
    }

    // Draw the cargo-contract scan region. Independent of the RS region above (its own single-instance
    // guard); MainWindow handles saving + positioning the yellow indicator on RegionSelected.
    private void SetContractRegion_Click(object sender, MouseButtonEventArgs e)
    {
        InteractionLog.Click("Set contract detection region", (System.Windows.DependencyObject)sender);

        // Toggle: a second click closes the live draw overlay instead of stacking another tint (issue #8).
        if (_contractRegionSelector != null) { _contractRegionSelector.Close(); return; }

        var selector = new RegionSelectorWindow();
        _contractRegionSelector = selector;
        selector.RegionSelected += r => ContractRegionSelected?.Invoke(r);
        selector.Closed += (_, _) => { if (ReferenceEquals(_contractRegionSelector, selector)) _contractRegionSelector = null; };
        selector.ShowOnMonitorOf(this);
    }

    // ── SCAN tab controls (toggle switches matching the STATS tab) ──────────────
    private Border? _scanSwTrack, _scanSwKnob, _boxSwTrack, _boxSwKnob;
    private FrameworkElement? _scanSwitchPair, _boxSwitchPair;

    // Builds the two compact on/off switches (Auto-scan, Scan box) once, like the STATS tab.
    private void BuildScanControls()
    {
        ScanControlBar.Children.Clear();

        _scanSwTrack = NewSwitchTrack();
        _scanSwKnob  = NewSwitchKnob();
        _scanSwTrack.Child = _scanSwKnob;
        _scanSwitchPair = SwitchPair(_scanSwTrack, "Auto-scan RS", ToggleScanSwitch, SyncScanControls);
        ScanControlBar.Children.Add(_scanSwitchPair);

        _boxSwTrack = NewSwitchTrack();
        _boxSwKnob  = NewSwitchKnob();
        _boxSwTrack.Child = _boxSwKnob;
        _boxSwitchPair = SwitchPair(_boxSwTrack, "Show/Hide RS detection box", ToggleBoxSwitch, SyncScanControls);
        ScanControlBar.Children.Add(_boxSwitchPair);

        SyncScanControls();
    }

    // Auto-scan is opt-in; flipping the switch starts/stops the screen scanner.
    private void ToggleScanSwitch() => _vm.ToggleScanCommand.Execute(null);

    private void ToggleBoxSwitch()
    {
        _boxVisible = !_boxVisible;
        BoxVisibilityToggled?.Invoke(_boxVisible);
    }

    // Reflects scanner-running / box-visible state onto the two switches + the status chip.
    private void SyncScanControls()
    {
        SetSwitch(_scanSwTrack, _scanSwKnob, _vm.RsScanState);   // amber on / yellow paused / grey off
        SetSwitch(_boxSwTrack, _boxSwKnob, _boxVisible);
        SetHubLed(_hubScanLed, _vm.RsScanState);   // Hub status LED (green on / yellow paused / red off)
        if (OverlayScanStatus == null) return;
        OverlayScanStatus.Text = _vm.RsScanState switch
        {
            ScanIndicator.On     => "◎  Scanning…",
            ScanIndicator.Paused => "◎  Paused (tab back to scan)",
            _                    => "◎  Scan off",
        };
        OverlayScanStatus.Foreground = (Brush)FindResource("FgDimBrush");
    }

    // ── HAULING tab controls (Auto-scan contracts / Contract box, mirror the SCAN tab) ──────────
    // Independent of the RS scan switches above: these drive the isolated ContractScan path and the
    // yellow contract indicator, never the OcrService / magenta _scanIndicator.
    private Border? _haulScanSwTrack, _haulScanSwKnob, _haulBoxSwTrack, _haulBoxSwKnob;
    private FrameworkElement? _haulScanSwitchPair, _haulBoxSwitchPair;

    private void BuildHaulingControls()
    {
        HaulingControlBar.Children.Clear();

        _haulScanSwTrack = NewSwitchTrack();
        _haulScanSwKnob  = NewSwitchKnob();
        _haulScanSwTrack.Child = _haulScanSwKnob;
        _haulScanSwitchPair = SwitchPair(_haulScanSwTrack, "Auto-scan contracts", ToggleContractScanSwitch, SyncHaulingControls);
        HaulingControlBar.Children.Add(_haulScanSwitchPair);

        _haulBoxSwTrack = NewSwitchTrack();
        _haulBoxSwKnob  = NewSwitchKnob();
        _haulBoxSwTrack.Child = _haulBoxSwKnob;
        _haulBoxSwitchPair = SwitchPair(_haulBoxSwTrack, "Show/Hide contract detection box", ToggleContractBoxSwitch, SyncHaulingControls);
        HaulingControlBar.Children.Add(_haulBoxSwitchPair);

        SyncHaulingControls();
    }

    // Auto-scan contracts is opt-in; flipping it starts/stops the contract scanner, then persists the choice.
    private void ToggleContractScanSwitch()
    {
        if (App.ContractScan.IsRunning) App.ContractScan.Stop();
        else App.ContractScan.Start();
        App.Settings.Current.AutoScanContracts = App.ContractScan.IsRunning;
        App.Settings.Save();
    }

    private void ToggleContractBoxSwitch()
    {
        _contractBoxVisible = !_contractBoxVisible;
        ContractBoxVisibilityToggled?.Invoke(_contractBoxVisible);
    }

    // Reflects contract-scanner-running / contract-box-visible state onto the two HAULING switches.
    private void SyncHaulingControls()
    {
        // Contract auto-scan: running = on, intent-on-but-not-running = paused (foreground-gated), else off.
        var contractState = App.ContractScan.IsRunning ? ScanIndicator.On
            : App.Settings.Current.AutoScanContracts ? ScanIndicator.Paused : ScanIndicator.Off;
        SetSwitch(_haulScanSwTrack, _haulScanSwKnob, contractState);   // amber on / yellow paused / grey off
        SetSwitch(_haulBoxSwTrack, _haulBoxSwKnob, _contractBoxVisible);
        SetHubLed(_hubHaulScanLed, contractState);   // Hub status LED (green on / yellow paused / red off)
    }

    // ── HUB tab: a READ-ONLY status glance (mock's SCAN STATUS row): Auto-scan RS + Contracts mirror the
    // SCAN / HAULING auto-scan toggles (green on / yellow paused / red off), and Session shows Game.log
    // monitoring (green = live game session being watched, red = SC closed / no log). The scan LEDs sync
    // via SyncScanControls / SyncHaulingControls; Session via RefreshSessionLed. Toggles live on the tabs.
    private Border? _hubScanLed, _hubHaulScanLed, _hubSessionLed;

    private void BuildHubScanControls()
    {
        HubScanBar.Children.Clear();

        _hubSessionLed = NewLed();
        HubScanBar.Children.Add(HubLedRow(_hubSessionLed, "Session",
            "Game.log session tracking (always on): green = monitoring a live game session, red = Star Citizen closed / no log"));

        _hubScanLed = NewLed();
        HubScanBar.Children.Add(HubLedRow(_hubScanLed, "Auto-scan RS", "Auto-scan RS: toggle on the SCAN tab"));

        _hubHaulScanLed = NewLed();
        HubScanBar.Children.Add(HubLedRow(_hubHaulScanLed, "Auto-scan Contracts", "Auto-scan contracts: toggle on the HAULING tab"));

        SyncScanControls();
        SyncHaulingControls();
        RefreshSessionLed();
    }

    // SESSION LED: green (pulsing) while a live game session is being monitored, red when Star Citizen is
    // closed / no log. Refreshed from the same GameLog state + status events the old monitoring pill used.
    private void RefreshSessionLed() => SetHubLed(_hubSessionLed, App.GameLog.IsSessionLive);
    private void OnGameLogStatusChanged(string _) => RefreshSessionLed();

    // Read-only HUB status pill (mock .led): a bordered chip with an LED dot + short label, full text in
    // the tooltip. Sizes to content and tiles in a WrapPanel. Not interactive; the live toggle is on
    // SCAN / HAULING.
    private FrameworkElement HubLedRow(Border led, string label, string tooltip)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(led);
        row.Children.Add(new TextBlock
        {
            Text = label, FontSize = 9, FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("FgDimBrush"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0),
        });
        return new Border
        {
            Child = row,
            Background = (Brush)FindResource("Bg2NavBrush"),
            BorderBrush = (Brush)FindResource("NavBorderBrush"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(0, 0, 7, 7),
            ToolTip = tooltip,
        };
    }

    // HUB status LED: paint it (green on / yellow paused / red off) and gently pulse it while On, matching
    // the mock's breathing scan-status dots. Static while paused / off.
    private void SetHubLed(Border? led, ScanIndicator state)
    {
        SetLed(led, state);
        if (led != null) Hud.PulseDot(led, state == ScanIndicator.On);
    }

    private void SetHubLed(Border? led, bool on) => SetHubLed(led, on ? ScanIndicator.On : ScanIndicator.Off);

    // Status LED colors: green = on, red = off, yellow = paused (neither Nexus nor SC in front).
    private static readonly Color LedOn     = Color.FromRgb(0x3E, 0xD6, 0x8B);
    private static readonly Color LedOff    = Color.FromRgb(0xE5, 0x48, 0x4D);
    private static readonly Color LedPaused = Color.FromRgb(0xEA, 0xB3, 0x08);

    private static Border NewLed() => new()
    {
        Width = 9, Height = 9, CornerRadius = new CornerRadius(4.5),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private void SetLed(Border? led, bool on) => SetLed(led, on ? ScanIndicator.On : ScanIndicator.Off);

    // Paints an LED green (on), red (off), or yellow (paused) with a soft matching glow.
    private void SetLed(Border? led, ScanIndicator state)
    {
        if (led == null) return;
        var c = state switch
        {
            ScanIndicator.On     => LedOn,
            ScanIndicator.Paused => LedPaused,
            _                    => LedOff,
        };
        led.Background = new SolidColorBrush(c);
        led.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = c, BlurRadius = 7, ShadowDepth = 0, Opacity = state == ScanIndicator.Off ? 0.55 : 0.9,
        };
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
                InteractionLog.Click($"recent scan RS {e2.Rs:N0}", (Border)s);
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
    private void TabHauling_Click(object sender, RoutedEventArgs e) => SwitchTab("hauling");

    private void SwitchTab(string tab)
    {
        _activeTab = tab;
        StatsTabContent.Visibility    = tab == "stats"    ? Visibility.Visible : Visibility.Collapsed;
        ScanTabContent.Visibility     = tab == "scan"     ? Visibility.Visible : Visibility.Collapsed;
        OrdersTabContent.Visibility   = tab == "orders"   ? Visibility.Visible : Visibility.Collapsed;
        ShoppingTabContent.Visibility = tab == "shopping" ? Visibility.Visible : Visibility.Collapsed;
        HaulingTabContent.Visibility  = tab == "hauling"  ? Visibility.Visible : Visibility.Collapsed;

        var accent = (System.Windows.Media.SolidColorBrush)System.Windows.Application.Current.FindResource("AccentBrush");
        var none   = Brushes.Transparent;
        TabStatsIndicator.Background    = tab == "stats"    ? accent : none;
        TabScanIndicator.Background     = tab == "scan"     ? accent : none;
        TabOrdersIndicator.Background   = tab == "orders"   ? accent : none;
        TabShoppingIndicator.Background = tab == "shopping" ? accent : none;
        TabHaulingIndicator.Background  = tab == "hauling"  ? accent : none;

        // RECENT scans belong to the SCAN tab only - hide the strip on every other tab.
        SetHistoryStripVisible(tab == "scan");

        if (tab == "stats") RebuildStatsPanel();
        if (tab == "scan") SyncScanControls();
        if (tab == "shopping") RebuildShoppingPanel();
        if (tab == "hauling") RebuildHaulingPanel();

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
    // tabs), so they'd otherwise show on every tab. They're scan-only - collapse them and
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

    // ── STATS / HUB tab (Beta Game.log session) ────────────────────────────────
    // The HUB carries no toggles or status text: Session Tracking + Auto-Track are always on (App
    // startup), the live scan state shows via the LED pills, and the session count + collection feed are
    // rebuilt in RebuildStatsPanel. Reset session and the raw log view now live in the advanced Game.log
    // monitor (LogMonitorWindow, reachable from Settings > Open Game.log Monitor).


    private static Border NewSwitchTrack() => new()
    {
        Width = 30, Height = 17, CornerRadius = new CornerRadius(4),
        BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center,
    };

    private static Border NewSwitchKnob() => new()
    {
        Width = 13, Height = 13, CornerRadius = new CornerRadius(4),
        VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left,
        Margin = new Thickness(2, 0, 2, 0),
        RenderTransform = new System.Windows.Media.TranslateTransform(),
    };

    private FrameworkElement SwitchPair(Border track, string label, Action onToggle, Action sync)
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
        pair.MouseLeftButtonUp += (_, _) => { InteractionLog.Toggle(label, pair); onToggle(); sync(); };
        return pair;
    }

    private void SetSwitch(Border? track, Border? knob, bool on)
        => SetSwitch(track, knob, on ? ScanIndicator.On : ScanIndicator.Off);

    // Tri-state toggle visual: On = amber, Paused = yellow (the knob stays in the on position to show the
    // user's intent is on, just suspended because neither Nexus nor SC is in front), Off = grey.
    private void SetSwitch(Border? track, Border? knob, ScanIndicator state)
    {
        if (track == null || knob == null) return;
        bool active = state != ScanIndicator.Off;
        Brush onBrush = state switch
        {
            ScanIndicator.On     => (Brush)FindResource("AccentBrush"),
            ScanIndicator.Paused => new SolidColorBrush(LedPaused),
            _                    => (Brush)FindResource("Bg3Brush"),
        };
        track.Background  = onBrush;
        track.BorderBrush = active ? onBrush : (Brush)FindResource("NavBorderBrush");
        // Slide the knob (track inner 28 - knob 13 - margins 2/2 = 11px travel) instead of snapping ends.
        if (knob.RenderTransform is System.Windows.Media.TranslateTransform kt)
        {
            double knobX = active ? 11 : 0;
            if (Motion.Reduced) { kt.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null); kt.X = knobX; }
            else kt.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty,
                new System.Windows.Media.Animation.DoubleAnimation(knobX, new Duration(TimeSpan.FromMilliseconds(Motion.HoverMs)))
                { EasingFunction = Motion.SlideOut });
        }
        knob.Background = active ? (Brush)FindResource("OnAccentBrush") : (Brush)FindResource("FgDimBrush");
    }

    private void OnGameLogMarked(BlueprintMark m)
    {
        if (_activeTab == "stats") RebuildStatsPanel();
    }

    // Session tally was cleared (new SC session, or a manual reset from the advanced monitor) - refresh
    // the visible count + feed while the HUB is on screen.
    private void OnSessionReset()
    {
        if (_activeTab == "stats") RebuildStatsPanel();
    }

    // The overlay is app-lifetime (hidden/shown, not closed) in normal use; this only runs if
    // it's discarded - e.g. MainWindow recreates it after an error - so detach the app-lifetime
    // session handlers to avoid leaking them onto a dead window.
    // Focus changes on the in-game overlay are a prime diagnostic for the mid-session tab-out
    // reports: an [WIN] overlay activated line at the moment a user got pulled out of the game
    // points the finger at the overlay grabbing focus.
    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        Logger.Info("[WIN] overlay activated (gained focus)");
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        Logger.Info("[WIN] overlay deactivated (lost focus)");
    }

    protected override void OnClosed(EventArgs e)
    {
        App.GameLog.Marked -= OnGameLogMarked;
        App.GameLog.SessionReset -= OnSessionReset;
        App.GameLog.StateChanged -= RefreshSessionLed;
        App.GameLog.StatusChanged -= OnGameLogStatusChanged;
        App.Hauls.Changed -= OnHaulsChanged;
        App.Shards.Changed -= OnShardsChanged;
        App.ForegroundRelevanceChanged -= OnForegroundRelevanceChanged;
        App.ContractScan.RunningChanged -= SyncContractFromShared;
        App.ContractBoxVisibilityChanged -= OnContractBoxShared;
        WorkOrderEditorPanel.OrderReadyToCollect -= _onOrderReady;
        base.OnClosed(e);
    }

    // Refresh the HAULING glance list when the tracker changes, but only while that tab is on screen.
    private void OnHaulsChanged()
    {
        UpdateHaulingTabLabel();                 // keep the tab count fresh even off the HAULING tab
        if (_activeTab == "hauling") RebuildHaulingPanel();
    }

    // Shows the active-haul count on the overlay tab button: "HAULING" or "HAULING (N)".
    private void UpdateHaulingTabLabel()
    {
        var n = App.Hauls.ActiveHauls.Count;
        TabHaulingBtn.Content = n > 0 ? $"HAULING ({n})" : "HAULING";
    }

    // Refresh the Server / Shard section when the shard history changes, but only while the STATS
    // tab is on screen (mirrors OnHaulsChanged's tab guard).
    private void OnShardsChanged()
    {
        if (_activeTab == "stats") RebuildShardPanel();
    }

    // Foreground relevance flipped (Nexus/SC moved to or from the front): re-sync the HUB scan LEDs so
    // the auto-scan indicators move between green (on) and yellow (paused).
    private void OnForegroundRelevanceChanged(bool relevant)
        => Dispatcher.Invoke(() => { SyncScanControls(); SyncHaulingControls(); });

    // The contract scanner started/stopped, or the contract box was toggled, on another surface: pull the
    // shared App state into the overlay's local box flag and re-sync the contract toggle + box switch/LED.
    private void SyncContractFromShared()
        => Dispatcher.Invoke(() => { _contractBoxVisible = App.ContractBoxVisible; SyncHaulingControls(); });
    private void OnContractBoxShared(bool on) => SyncContractFromShared();

    private void RebuildStatsPanel()
    {
        // Server / Shard section sits in the STATS tab; refresh it whenever the tab is built.
        RebuildShardPanel();

        // Shared brushes / fonts for the feed below the hero count.
        var dim    = (Brush)FindResource("FgDimBrush");
        var fg     = (Brush)FindResource("FgBrush");
        var border = (Brush)FindResource("NavBorderBrush");
        var mono   = (FontFamily)FindResource("MonoFont");

        // Hero KPI: count this session's collected blueprints up from 0 into the big cyan readout (the
        // BLUEPRINTS COLLECTED accent panel is in XAML; CountUp animates StatsBigCount). Re-runs only when
        // the value changes, so flipping back to the HUB doesn't reset a settled number.
        CountUp.SetTo(StatsBigCount, App.GameLog.Count);

        // Blueprints-collected feed (newest first).
        StatsFeedItems.Children.Clear();
        var marks = App.GameLog.Marks;
        StatsEmptyState.Visibility = marks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        for (int i = marks.Count - 1; i >= 0; i--)   // newest first
        {
            var mk = marks[i];
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var time = new TextBlock
            {
                Text = mk.At.ToString("HH:mm:ss"), FontFamily = mono, FontSize = 9, Foreground = dim,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 9, 0),
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

    // ── Server / Shard section (inside the HUB's SERVER / SHARD chamfer panel) ──────
    // Amber title, then the current shard (cyan dot + label, raw id beneath) and up to 3 recent shards
    // (dot + region/instance, relative join time on the right) - the mock's SERVER / SHARD panel. The
    // ChamferPanel host supplies the card frame, so this no longer draws its own border. Renders only
    // shard metadata, never the player.
    private void RebuildShardPanel()
    {
        ShardPanel.Children.Clear();

        var accent   = (Brush)FindResource("AccentBrush");
        var cyan     = (Brush)FindResource("CyanBrush");
        var dim      = (Brush)FindResource("FgDimBrush");
        var headFont = (FontFamily)FindResource("HeadFont");
        var monoFont = (FontFamily)FindResource("MonoFont");

        // Panel title (amber kicker).
        ShardPanel.Children.Add(new TextBlock
        {
            Text = "SERVER / SHARD", FontSize = 9, FontWeight = FontWeights.Bold,
            Foreground = accent, Margin = new Thickness(0, 0, 0, 5),
        });

        // CURRENT: the shard the player is on right now (cyan), or a "not on a shard" line after they leave
        // (App.Shards.Current goes null once the log shows they left, until the next join).
        var current = App.Shards.Current;
        if (current != null)
        {
            // Current shard/instance is the live "where am I" readout -> cyan (MOBIGLAS signature).
            ShardPanel.Children.Add(ShardRow(ShardDot(cyan),
                $"{current.Region}  .  Shard {current.Instance}", cyan, headFont, 13, FontWeights.SemiBold));
            ShardPanel.Children.Add(new TextBlock
            {
                Text = current.ShardId, FontFamily = monoFont, FontSize = 9.5, Foreground = dim,
                Margin = new Thickness(15, 1, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }
        else
        {
            ShardPanel.Children.Add(new TextBlock
            {
                Text = "Not on a shard.", FontSize = 11, Foreground = dim, Margin = new Thickness(0, 1, 0, 0),
            });
        }

        // RECENT subheader + up to 3 prior shards (App.Shards.Recent already excludes Current).
        var recent = App.Shards.Recent;
        if (recent.Count > 0)
        {
            ShardPanel.Children.Add(new TextBlock
            {
                Text = "RECENT", FontSize = 9, FontWeight = FontWeights.Bold,
                Foreground = dim, Margin = new Thickness(0, 9, 0, 3),
            });
            foreach (var s in recent)
            {
                var row = new Grid { Margin = new Thickness(0, 2, 0, 0) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(ShardRow(ShardDot(dim), $"{s.Region} . {s.Instance}", dim, null, 10, FontWeights.Normal));
                var ago = new TextBlock
                {
                    Text = Ago(s.JoinedAt), FontFamily = monoFont, FontSize = 9, Foreground = dim,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(ago, 1);
                row.Children.Add(ago);
                ShardPanel.Children.Add(row);
            }
        }
    }

    // A small filled status dot (mock .dot) leading a shard row.
    private static Border ShardDot(Brush fill) => new()
    {
        Width = 7, Height = 7, CornerRadius = new CornerRadius(3.5), Background = fill,
        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0),
    };

    // Dot + label row, shared by the current shard line and each recent-shard line.
    private static StackPanel ShardRow(Border dot, string text, Brush fg, FontFamily? font, double size, FontWeight weight)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(dot);
        var tb = new TextBlock
        {
            Text = text, FontSize = size, FontWeight = weight, Foreground = fg,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        if (font != null) tb.FontFamily = font;
        row.Children.Add(tb);
        return row;
    }

    // Compact relative-time label for a UTC instant: "just now" / "Nm ago" / "Nh ago" / "Nd ago".
    // Recomputed on each panel rebuild (tab show / shard change), not on a live ticking timer.
    private static string Ago(DateTime utcWhen)
    {
        var span = DateTime.UtcNow - utcWhen;
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalHours   < 1) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalDays    < 1) return $"{(int)span.TotalHours}h ago";
        return $"{(int)span.TotalDays}d ago";
    }

    // ── HAULING tab (Cargo Hauling glance) ──────────────────────────────────────
    // Compact mirror of the main-window HaulingPage: a count header, one block per active
    // haul (Company - Topology + its incomplete legs), then a where-to-drop consolidation
    // summary. Built in code from App.Hauls with the same TextBlock/FindResource idiom the
    // STATS tab uses; no live SCU progress exists (legs are binary done / not-done).
    // The HAULING tab leads with the action plan a hauler actually needs at a glance: stack TOTALS
    // (count / SCU / aUEC + delivered-drops progress), then CONSOLIDATED STOPS grouped COLLECT/DELIVER
    // by location (the cross-contract rollup the in-game MobiGlas does not give you), and finally the
    // per-contract CONTRACTS cards (identity + payout) in a collapsible section so the plan stays glanceable.
    private void RebuildHaulingPanel()
    {
        HaulingList.Children.Clear();

        var accent = (Brush)FindResource("AccentBrush");
        var cyan   = (Brush)FindResource("CyanBrush");
        var dim    = (Brush)FindResource("FgDimBrush");
        var border = (Brush)FindResource("NavBorderBrush");
        var cardBg = (Brush)FindResource("Bg2NavBrush");
        var mono   = (FontFamily)FindResource("MonoFont");

        var active = App.Hauls.ActiveHauls;

        // Compact "Clear all" affordance, shown whenever there is anything to clear (active or finished).
        Button? ClearAllButton()
        {
            if (App.Hauls.AllHauls.Count == 0) return null;
            var b = new Button
            {
                Content = "Clear all", FontSize = 10, FontWeight = FontWeights.Bold,
                Background = cardBg, Foreground = accent, BorderBrush = border, BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 2, 8, 2), Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right,
            };
            b.Click += (_, __) => App.Hauls.ClearAll();   // Changed -> OnHaulsChanged rebuilds
            return b;
        }

        if (active.Count == 0)
        {
            var c = ClearAllButton();
            if (c != null) HaulingList.Children.Add(c);
            HaulingList.Children.Add(new TextBlock { Text = "No active hauls.", FontSize = 11, Foreground = dim, Margin = new Thickness(2, 2, 0, 0) });
            return;
        }

        // ── TOTALS: how big is this run, will it fit, what is it worth, and how far along am I ──
        int totalScu = 0, totalReward = 0, drops = 0, dropsDone = 0;
        foreach (var h in active)
        {
            // SCU committed: prefer the OCR objectives the cards render, fall back to the dropoff legs.
            totalScu += h.ContractObjectives.Count > 0
                ? h.ContractObjectives.Sum(o => o.Scu)
                : h.Legs.Where(l => l.Role == HaulRole.Dropoff).Sum(l => l.TargetScu);
            if (h.Reward > 0) totalReward += h.Reward;
            var d = h.Legs.Where(l => l.Role == HaulRole.Dropoff).ToList();
            drops += d.Count;
            dropsDone += d.Count(l => l.Completed);
        }

        var totals = new TextBlock { Margin = new Thickness(2, 2, 0, 2), TextWrapping = TextWrapping.Wrap };
        totals.Inlines.Add(new System.Windows.Documents.Run("HAULS ") { Foreground = accent, FontWeight = FontWeights.Bold, FontSize = 11 });
        totals.Inlines.Add(new System.Windows.Documents.Run($"{active.Count}") { Foreground = cyan, FontWeight = FontWeights.Bold, FontSize = 12 });
        totals.Inlines.Add(new System.Windows.Documents.Run($"    {totalScu:N0} SCU") { Foreground = cyan, FontFamily = mono, FontSize = 11 });
        if (totalReward > 0)
            totals.Inlines.Add(new System.Windows.Documents.Run($"    {FormatAuec(totalReward)}") { Foreground = cyan, FontFamily = mono, FontSize = 11 });
        HaulingList.Children.Add(totals);

        if (drops > 0)
        {
            HaulingList.Children.Add(new TextBlock { Text = $"DELIVERED  {dropsDone}/{drops} drops", FontFamily = mono, FontSize = 9, FontWeight = FontWeights.Bold, Foreground = dim, Margin = new Thickness(2, 2, 0, 2) });
            var bar = Hud.StateBar((double)dropsDone / drops, Hud.BarState.Cyan, 6);
            if (bar is FrameworkElement fe) fe.Margin = new Thickness(2, 0, 2, 2);
            HaulingList.Children.Add(bar);
        }

        // ── STOPS: the cross-contract action plan (consolidated COLLECT + DELIVER by location) ──
        var stopsHeader = new Grid { Margin = new Thickness(2, 10, 0, 2) };
        stopsHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        stopsHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var stopsTitle = new TextBlock { Text = "STOPS", FontSize = 9, FontWeight = FontWeights.Bold, Foreground = dim, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(stopsTitle, 0); stopsHeader.Children.Add(stopsTitle);
        var clear = ClearAllButton();
        if (clear != null) { Grid.SetColumn(clear, 1); stopsHeader.Children.Add(clear); }
        HaulingList.Children.Add(stopsHeader);

        var con = App.Hauls.BuildConsolidation();
        AddStopGroup("COLLECT", con.Pickups);
        AddStopGroup("DELIVER", con.Dropoffs);

        // ── CONTRACTS: per-contract identity + payout, collapsible (collapse by default when busy) ──
        var cards = new StackPanel();
        foreach (var h in active.OrderByDescending(x => x.Reward))
            cards.Children.Add(BuildHaulCard(h));
        bool startCollapsed = active.Count > 3;
        cards.Visibility = startCollapsed ? Visibility.Collapsed : Visibility.Visible;

        HaulingList.Children.Add(new Border { Height = 1, Background = border, Margin = new Thickness(0, 10, 0, 0) });
        var chevron = new TextBlock { Text = startCollapsed ? "v" : "^", FontFamily = mono, FontSize = 11, Foreground = dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0) };
        var contractsHeader = new Grid { Margin = new Thickness(2, 8, 0, 2), Cursor = Cursors.Hand, Background = Brushes.Transparent };
        contractsHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        contractsHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var ch = new TextBlock { Text = $"CONTRACTS ({active.Count})", FontSize = 9, FontWeight = FontWeights.Bold, Foreground = dim, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(ch, 0); contractsHeader.Children.Add(ch);
        Grid.SetColumn(chevron, 1); contractsHeader.Children.Add(chevron);
        contractsHeader.MouseLeftButtonUp += (_, __) =>
        {
            bool show = cards.Visibility != Visibility.Visible;
            cards.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            chevron.Text = show ? "^" : "v";
        };
        HaulingList.Children.Add(contractsHeader);
        HaulingList.Children.Add(cards);
    }

    // Indented mono detail line used for the per-contract cargo / route rows.
    private TextBlock HaulRow(string text, Brush brush, double indent)
        => new() { Text = text, FontFamily = (FontFamily)FindResource("MonoFont"), FontSize = 11, Foreground = brush, Margin = new Thickness(indent, 2, 0, 0), TextWrapping = TextWrapping.Wrap };

    // A consolidated stop group (COLLECT or DELIVER): each location with its total SCU and a per-commodity
    // breakdown (commodity, summed SCU, and how many contracts a single placement there clears).
    private void AddStopGroup(string label, System.Collections.Generic.List<ConsolidationStop> stops)
    {
        if (stops.Count == 0) return;
        var accent = (Brush)FindResource("AccentBrush");
        var cyan   = (Brush)FindResource("CyanBrush");
        var fg     = (Brush)FindResource("FgBrush");
        var dim    = (Brush)FindResource("FgDimBrush");
        var mono   = (FontFamily)FindResource("MonoFont");

        HaulingList.Children.Add(new TextBlock { Text = label, FontFamily = mono, FontSize = 10, FontWeight = FontWeights.Bold, Foreground = accent, Margin = new Thickness(4, 6, 0, 2) });

        foreach (var stop in stops.OrderByDescending(s => s.TotalScu))
        {
            var row = new Grid { Margin = new Thickness(8, 2, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var loc = new TextBlock { Text = string.IsNullOrWhiteSpace(stop.Location) ? "Unknown" : stop.Location, FontSize = 11, Foreground = fg, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(loc, 0); row.Children.Add(loc);
            var tot = new TextBlock { Text = $"{stop.TotalScu} SCU", FontFamily = mono, FontSize = 11, Foreground = cyan, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(6, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(tot, 1); row.Children.Add(tot);
            HaulingList.Children.Add(row);

            // Per-commodity mini-lines: "Titanium  120 SCU (2)" where (2) = contracts contributing.
            var groups = stop.Items
                .GroupBy(i => string.IsNullOrWhiteSpace(i.Commodity) ? "Cargo" : i.Commodity)
                .Select(g => new { Commodity = g.Key, Scu = g.Sum(i => i.Scu), Count = g.Count() })
                .OrderByDescending(g => g.Scu);
            foreach (var g in groups)
                HaulingList.Children.Add(new TextBlock { Text = $"{g.Commodity}  {g.Scu} SCU ({g.Count})", FontFamily = mono, FontSize = 10, Foreground = dim, Margin = new Thickness(18, 1, 0, 0), TextWrapping = TextWrapping.Wrap });
        }
    }

    // One per-contract card: identity (paired dot + company + topology) and drops progress on the title
    // row, then reward and the cargo lines (OCR objectives when paired, else the incomplete log legs).
    private UIElement BuildHaulCard(Haul h)
    {
        var accent = (Brush)FindResource("AccentBrush");
        var cyan   = (Brush)FindResource("CyanBrush");
        var fg     = (Brush)FindResource("FgBrush");
        var dim    = (Brush)FindResource("FgDimBrush");
        var mono   = (FontFamily)FindResource("MonoFont");

        var missionId = h.MissionId;   // capture for this card's delete handler
        var company = string.IsNullOrWhiteSpace(h.ContractedBy)
            ? (string.IsNullOrWhiteSpace(h.Company) ? "Unknown company" : h.Company)
            : h.ContractedBy;

        var card = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };

        var titleGrid = new Grid();
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var enriched = h.ContractObjectives.Count > 0 || h.Reward > 0;
        var titleStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (enriched)
            titleStack.Children.Add(new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Fill = accent, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Details paired from a contract scan" });
        titleStack.Children.Add(new TextBlock { Text = company, FontSize = 11, FontWeight = FontWeights.Bold, Foreground = fg, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        var topo = TopologyShort(h.Topology);
        if (topo.Length > 0)
            titleStack.Children.Add(new TextBlock { Text = topo, FontFamily = mono, FontSize = 10, Foreground = dim, Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(titleStack, 0); titleGrid.Children.Add(titleStack);

        var dropLegs = h.Legs.Where(l => l.Role == HaulRole.Dropoff).ToList();
        if (dropLegs.Count > 0)
        {
            var prog = new TextBlock { Text = $"{dropLegs.Count(l => l.Completed)}/{dropLegs.Count}", FontFamily = mono, FontSize = 10, Foreground = cyan, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0) };
            Grid.SetColumn(prog, 1); titleGrid.Children.Add(prog);
        }

        var deleteBtn = new Button { Content = "x", FontFamily = mono, FontSize = 13, FontWeight = FontWeights.Bold, Foreground = dim, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(6, 0, 6, 0), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center };
        deleteBtn.Click += (_, __) => App.Hauls.Remove(missionId);   // Changed -> OnHaulsChanged rebuilds
        Grid.SetColumn(deleteBtn, 2); titleGrid.Children.Add(deleteBtn);
        card.Children.Add(titleGrid);

        if (h.Reward > 0)
            card.Children.Add(new TextBlock { Text = $"{h.Reward:N0} aUEC", FontSize = 11, Foreground = cyan, Margin = new Thickness(8, 2, 0, 0) });

        if (h.ContractObjectives.Count > 0)
        {
            foreach (var o in h.ContractObjectives)
            {
                var pickup  = string.IsNullOrWhiteSpace(o.Pickup)  ? "?" : o.Pickup;
                var dropoff = string.IsNullOrWhiteSpace(o.Dropoff) ? "?" : o.Dropoff;
                var cargo   = ((o.Scu > 0 ? $"{o.Scu} SCU " : "") + o.Commodity).Trim();
                card.Children.Add(HaulRow(cargo.Length == 0 ? "(cargo unknown)" : cargo, fg, 8));
                card.Children.Add(HaulRow($"{pickup} -> {dropoff}", dim, 16));
            }
        }
        else
        {
            foreach (var leg in h.Legs)
            {
                if (leg.Completed) continue;
                var role = leg.Role == HaulRole.Pickup ? "Collect" : "Deliver";
                var location = leg.Role == HaulRole.Dropoff ? leg.Destination : h.PickupName;
                var segs = new System.Collections.Generic.List<string>();
                if (leg.TargetScu > 0) segs.Add($"{leg.TargetScu} SCU");
                if (!string.IsNullOrWhiteSpace(leg.Commodity)) segs.Add(leg.Commodity);
                if (!string.IsNullOrWhiteSpace(location)) segs.Add($"@ {location}");
                var desc = string.Join(" ", segs);
                card.Children.Add(new TextBlock { Text = desc.Length == 0 ? $"{role}:" : $"{role}: {desc}", FontSize = 11, Foreground = fg, Margin = new Thickness(8, 3, 0, 0), TextWrapping = TextWrapping.Wrap });
            }
        }

        return card;
    }

    // "1 to 2" -> "1->2"; blank / Unknown -> "" (so the tag is simply omitted).
    private static string TopologyShort(string t)
        => string.IsNullOrWhiteSpace(t) || t == "Unknown" ? "" : t.Replace(" to ", "->");

    // Compact aUEC: 1.85M / 745K / full number for small values.
    private static string FormatAuec(int v)
        => v >= 1_000_000 ? $"{v / 1_000_000.0:0.##}M aUEC"
         : v >= 10_000    ? $"{v / 1000.0:0.#}K aUEC"
         : $"{v:N0} aUEC";

    private System.Windows.Threading.DispatcherTimer? _ordersTicker;
    // The countdown text per active order; the fill bar animates itself over the remaining
    // time (smooth ScaleX), so the ticker only refreshes the text each second.
    private readonly Dictionary<string, TextBlock> _orderTimerRefs = new();

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
        WorkOrderStatus.Refining       => "#FF9D4D",
        WorkOrderStatus.ReadyToCollect => "#66E6A6",
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
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(0, 0, 6, 6),
        };
    }

    private UIElement BuildOverlayOrderCard(WorkOrder wo)
    {
        var cardBg   = (Brush)FindResource("Bg2NavBrush");
        var navB     = (Brush)FindResource("NavBorderBrush");
        var fg       = (Brush)FindResource("FgBrush");
        var dim      = (Brush)FindResource("FgDimBrush");
        var chipBg   = (Brush)FindResource("Bg3Brush");
        var trackBg  = (Brush)FindResource("BorderBrush");
        var cyan     = (Brush)FindResource("CyanBrush");
        var headFont = (FontFamily)FindResource("HeadFont");
        var statusBrush = HexBrush(wo.StatusColorHex);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        // Flush status bar; the chamfer panel below clips it to the bevel (clipContent), so no rounding here.
        grid.Children.Add(new Border { Background = statusBrush });

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
            Background = chipBg, CornerRadius = new CornerRadius(4),
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
            // Live work-order countdown reads as instrument data -> cyan (MOBIGLAS signature).
            var tTxt = new TextBlock { Text = wo.TimerRemainingShort, FontSize = 9, FontWeight = FontWeights.Bold, Foreground = cyan, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            timerRow.Children.Add(tTxt);

            var frac = System.Math.Clamp(wo.TimerFraction, 0, 1);
            // Smooth fill: scale a stretched bar from frac -> 1 over the remaining time (matches the
            // main page / refinery flyout), instead of stepping GridLength once a second.
            var scale = new System.Windows.Media.ScaleTransform(frac, 1);
            var fill = new Border
            {
                Background = statusBrush, CornerRadius = new CornerRadius(2),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                RenderTransform = scale, RenderTransformOrigin = new System.Windows.Point(0, 0.5),
            };
            var track = new Border { Background = trackBg, CornerRadius = new CornerRadius(2), Height = 5, VerticalAlignment = VerticalAlignment.Center, Child = fill };
            Grid.SetColumn(track, 1);
            timerRow.Children.Add(track);
            stack.Children.Add(timerRow);

            var remaining = wo.TimerEnd.HasValue ? wo.TimerEnd.Value - DateTime.UtcNow : TimeSpan.Zero;
            if (remaining > TimeSpan.Zero)
                scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty,
                    new System.Windows.Media.Animation.DoubleAnimation(frac, 1.0, remaining)
                    { FillBehavior = System.Windows.Media.Animation.FillBehavior.HoldEnd });

            _orderTimerRefs[wo.Id] = tTxt;
        }

        grid.Children.Add(stack);
        // Chamfered MOBIGLAS card (TL+BR bevel) with the status bar clipped to the silhouette.
        var card = Hud.Panel(grid, chamfer: 10, bg: cardBg, border: navB,
                             padding: new Thickness(0), clipContent: true);
        card.Margin = new Thickness(0, 0, 0, 8);
        return card;
    }

    private void OrdersTicker_Tick(object? sender, EventArgs e)
    {
        bool anyActive = false;
        foreach (var wo in _vm.WorkOrders)
        {
            if (!_orderTimerRefs.TryGetValue(wo.Id, out var txt)) continue;
            if (!wo.HasActiveTimer) { RebuildOrdersPanel(); return; }
            anyActive = true;
            txt.Text = wo.TimerRemainingShort;
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
                Text = $"{item.ResourceName}  ×{CraftAmount.Format(item.Quantity, item.Unit)}",
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
