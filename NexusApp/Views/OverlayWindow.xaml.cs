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
    /// <summary>STATS tab asked to open the advanced Game.log monitor window.</summary>
    public event Action? OpenMonitorRequested;

    // ── Welcome-tour targets ───────────────────────────────────────────────────
    public FrameworkElement SetRegionTarget   => SetRegionBtn;
    public FrameworkElement BoxToggleTarget   => _boxSwitchPair ?? SetRegionBtn;
    public FrameworkElement ScanToggleTarget  => _scanSwitchPair ?? SetRegionBtn;
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
            else if (e.PropertyName == nameof(MainViewModel.IsScanActive))
                SyncScanControls();
        };

        SwitchTab("stats");

        // BETA Game.log blueprint session — drive + mirror it from the STATS tab. The
        // overlay lives for the app's lifetime (created once, hidden/shown), so these
        // never need unsubscribing.
        App.GameLog.Marked += OnGameLogMarked;
        App.GameLog.StateChanged += SyncStatsControls;
        App.GameLog.StatusChanged += OnGameLogStatus;
        App.GameLog.SessionReset += OnSessionReset;

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

        BuildStatsControls();
        BuildScanControls();
        BuildHaulingControls();
        BuildHubScanControls();

        WorkOrderEditorPanel.OrderReadyToCollect += label => PulseWorkOrderButton();

        IsVisibleChanged += (_, e) =>
        {
            bool visible = (bool)e.NewValue;
            Logger.Info($"[WIN] overlay {(visible ? "shown" : "hidden")}");
            if (visible) Shown?.Invoke();
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
        // game's monitor), not always the primary — issue #6.
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
        SetLed(_hubScanLed, _vm.RsScanState);   // Hub status LED (green on / yellow paused / red off)
        SetLed(_hubBoxLed, _boxVisible);
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
        SetLed(_hubHaulScanLed, contractState);   // Hub status LED (green on / yellow paused / red off)
        SetLed(_hubContractBoxLed, _contractBoxVisible);
    }

    // ── HUB tab: a READ-ONLY status glance of every auto-scan control (RS + contract). Each is shown as
    // an LED (green = on, red = off) synced by SyncScanControls / SyncHaulingControls so the HUB stays in
    // lockstep with the SCAN and HAULING tabs. The actual toggles live on those tabs, not here.
    private Border? _hubScanLed, _hubBoxLed, _hubHaulScanLed, _hubContractBoxLed;

    private void BuildHubScanControls()
    {
        HubScanBar.Children.Clear();

        _hubScanLed = NewLed();
        HubScanBar.Children.Add(HubLedRow(_hubScanLed, "Auto-scan RS", "Auto-scan RS: toggle on the SCAN tab"));

        _hubBoxLed = NewLed();
        HubScanBar.Children.Add(HubLedRow(_hubBoxLed, "RS box", "RS detection box: toggle on the SCAN tab"));

        _hubHaulScanLed = NewLed();
        HubScanBar.Children.Add(HubLedRow(_hubHaulScanLed, "Auto-scan contracts", "Auto-scan contracts: toggle on the HAULING tab"));

        _hubContractBoxLed = NewLed();
        HubScanBar.Children.Add(HubLedRow(_hubContractBoxLed, "Contract box", "Contract detection box: toggle on the HAULING tab"));

        SyncScanControls();
        SyncHaulingControls();
    }

    // Read-only HUB status indicator: an LED + short label (full text in the tooltip). Fixed width so a
    // WrapPanel tiles two indicators per row. Not interactive; the live toggle is on SCAN / HAULING.
    private FrameworkElement HubLedRow(Border led, string label, string tooltip)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal, Width = 138,
            Margin = new Thickness(0, 0, 0, 5), VerticalAlignment = VerticalAlignment.Center,
            ToolTip = tooltip,
        };
        row.Children.Add(led);
        row.Children.Add(new TextBlock
        {
            Text = label, FontSize = 10, Foreground = (Brush)FindResource("FgBrush"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        return row;
    }

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

    private void ResultCard_Click(object sender, MouseButtonEventArgs e)
    {
        InteractionLog.Click("result card", (System.Windows.DependencyObject)sender);
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

        // RECENT scans belong to the SCAN tab only — hide the strip on every other tab.
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
    // Session Tracking + Auto-Track Blueprints are always on (see App startup), so the HUB has no
    // on/off switches; just the tracking pill + status line below, which SyncStatsControls paints.
    private void BuildStatsControls() => SyncStatsControls();

    private void OpenMonitorFromStats_Click(object sender, MouseButtonEventArgs e)
    {
        InteractionLog.Click("Open advanced monitor", (System.Windows.DependencyObject)sender);
        OpenMonitorRequested?.Invoke();
    }

    // Reset() raises SessionReset, which OnSessionReset handles (clears the note + refreshes).
    private void ResetSession_Click(object sender, MouseButtonEventArgs e)
    {
        InteractionLog.Click("Reset session", (System.Windows.DependencyObject)sender);
        App.GameLog.Reset();
    }

    // Feedback line under the switches: what the watcher is doing + the last blueprint collected.
    private string _lastWatcherStatus = "";
    private string _lastCollected = "";

    private void OnGameLogStatus(string s) { _lastWatcherStatus = s; UpdateStatsStatus(); }

    private void UpdateStatsStatus()
    {
        if (StatsStatus == null) return;
        var running = App.GameLog.IsRunning;

        // Tracking pill: green = monitoring, red = no log found yet (tracking is always on).
        if (SessionPillDot != null && SessionPillText != null)
        {
            SetLed(SessionPillDot, running);
            SessionPillText.Text = running ? "MONITORING" : "NO LOG";
            SessionPillText.Foreground = new SolidColorBrush(running ? LedOn : LedOff);
        }

        // Blueprint-tracking pill: green = auto-collecting blueprints (always on while monitoring), red off.
        if (BlueprintPillDot != null && BlueprintPillText != null)
        {
            bool tracking = running && App.GameLog.AutoMark;
            SetLed(BlueprintPillDot, tracking);
            BlueprintPillText.Text = tracking ? "TRACKING" : "OFF";
            BlueprintPillText.Foreground = new SolidColorBrush(tracking ? LedOn : LedOff);
        }

        if (!running) { StatsStatus.Text = "Session tracking on, waiting for Game.log"; return; }
        var s = string.IsNullOrEmpty(_lastWatcherStatus) ? "Watching" : _lastWatcherStatus;
        if (!string.IsNullOrEmpty(_lastCollected)) s += $"  ·  last: {_lastCollected}";
        StatsStatus.Text = s;
    }


    private static Border NewSwitchTrack() => new()
    {
        Width = 30, Height = 17, CornerRadius = new CornerRadius(4),
        BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center,
    };

    private static Border NewSwitchKnob() => new()
    {
        Width = 13, Height = 13, CornerRadius = new CornerRadius(4),
        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 2, 0),
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
        knob.HorizontalAlignment = active ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        knob.Background = active ? (Brush)FindResource("OnAccentBrush") : (Brush)FindResource("FgDimBrush");
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
        App.GameLog.StateChanged -= SyncStatsControls;
        App.GameLog.StatusChanged -= OnGameLogStatus;
        App.GameLog.SessionReset -= OnSessionReset;
        App.Hauls.Changed -= OnHaulsChanged;
        App.Shards.Changed -= OnShardsChanged;
        App.ForegroundRelevanceChanged -= OnForegroundRelevanceChanged;
        App.ContractScan.RunningChanged -= SyncContractFromShared;
        App.ContractBoxVisibilityChanged -= OnContractBoxShared;
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

    // Session Tracking + Auto-Track are always on, so there are no switches to reflect; just refresh
    // the tracking pill + status line when the watcher state changes.
    private void SyncStatsControls() => UpdateStatsStatus();

    private void RebuildStatsPanel()
    {
        // Server / Shard section sits at the top of the STATS tab; refresh it whenever the tab is built.
        RebuildShardPanel();

        SyncStatsControls();

        // Shared brushes / fonts for the hero KPI and the feed below it.
        var cyan   = (Brush)FindResource("CyanBrush");
        var dim    = (Brush)FindResource("FgDimBrush");
        var fg     = (Brush)FindResource("FgBrush");
        var border = (Brush)FindResource("NavBorderBrush");
        var mono   = (FontFamily)FindResource("MonoFont");

        // Hero KPI: one big cyan blueprints-collected count for this session (instrument data -> cyan,
        // MOBIGLAS signature), with a small "this session" caption. The "THIS SESSION" header is static
        // in XAML; this fills the StatsListItems body.
        StatsListItems.Children.Clear();
        var kpiRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        kpiRow.Children.Add(new TextBlock
        {
            Text = App.GameLog.Count.ToString(),
            FontFamily = mono, FontSize = 38, FontWeight = FontWeights.Bold, Foreground = cyan,
            VerticalAlignment = VerticalAlignment.Bottom,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Hud.Col("CyanBrush"), BlurRadius = 12, ShadowDepth = 0, Opacity = 0.4,
            },
        });
        kpiRow.Children.Add(new TextBlock
        {
            Text = "this session", FontFamily = mono, FontSize = 11, Foreground = dim,
            Margin = new Thickness(9, 0, 0, 7), VerticalAlignment = VerticalAlignment.Bottom,
        });
        StatsListItems.Children.Add(kpiRow);

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

    // ── Server / Shard section (bottom of the STATS tab) ───────────────────────────
    // Current shard card (region + instance, then the raw shard id) plus up to 3 recent shards
    // from App.Shards. Built in code with the same FindResource / Border-card / TextBlock idiom
    // the STATS and HAULING glance panels use. Renders only shard metadata, never the player.
    private void RebuildShardPanel()
    {
        ShardPanel.Children.Clear();

        var accent   = (Brush)FindResource("AccentBrush");
        var cyan     = (Brush)FindResource("CyanBrush");
        var fg       = (Brush)FindResource("FgBrush");
        var dim      = (Brush)FindResource("FgDimBrush");
        var cardBg   = (Brush)FindResource("Bg2NavBrush");
        var navB     = (Brush)FindResource("NavBorderBrush");
        var headFont = (FontFamily)FindResource("HeadFont");
        var monoFont = (FontFamily)FindResource("MonoFont");

        // Section header (accent, like "BLUEPRINTS COLLECTED THIS SESSION").
        ShardPanel.Children.Add(new TextBlock
        {
            Text = "SERVER / SHARD", FontSize = 9, FontWeight = FontWeights.Bold,
            Foreground = accent, Margin = new Thickness(2, 0, 0, 4),
        });

        // CURRENT: the shard the player is on right now, or a "not on a shard" line after they leave
        // (App.Shards.Current goes null once the log shows they left, until the next join).
        var current = App.Shards.Current;
        if (current != null)
        {
            ShardPanel.Children.Add(new TextBlock
            {
                Text = "CURRENT", FontSize = 9, FontWeight = FontWeights.Bold,
                Foreground = dim, Margin = new Thickness(2, 2, 0, 3),
            });
            var cardStack = new StackPanel();
            cardStack.Children.Add(new TextBlock
            {
                // Current shard/instance is the live "where am I" readout -> cyan (MOBIGLAS signature).
                Text = $"{current.Region}  -  Shard {current.Instance}",
                FontFamily = headFont, FontSize = 13, Foreground = cyan,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            cardStack.Children.Add(new TextBlock
            {
                Text = current.ShardId, FontFamily = monoFont, FontSize = 10, Foreground = dim,
                Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis,
            });
            ShardPanel.Children.Add(new Border
            {
                Child = cardStack,
                Background = cardBg, BorderBrush = navB, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4), Padding = new Thickness(13, 9, 13, 9),
            });
        }
        else
        {
            ShardPanel.Children.Add(new TextBlock
            {
                Text = "Not on a shard.", FontSize = 11, Foreground = dim,
                Margin = new Thickness(2, 2, 0, 0),
            });
        }

        // RECENT subheader + up to 3 prior shards (App.Shards.Recent already excludes Current).
        var recent = App.Shards.Recent;
        if (recent.Count > 0)
        {
            ShardPanel.Children.Add(new TextBlock
            {
                Text = "RECENT", FontSize = 9, FontWeight = FontWeights.Bold,
                Foreground = dim, Margin = new Thickness(2, 10, 0, 4),
            });
            foreach (var s in recent)
                ShardPanel.Children.Add(new TextBlock
                {
                    Text = $"{Ago(s.JoinedAt)}   {s.Region} - {s.Instance}",
                    FontFamily = monoFont, FontSize = 10, Foreground = dim,
                    Margin = new Thickness(8, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis,
                });
        }
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
    private void RebuildHaulingPanel()
    {
        HaulingList.Children.Clear();

        var accent = (Brush)FindResource("AccentBrush");
        var cyan   = (Brush)FindResource("CyanBrush");
        var fg     = (Brush)FindResource("FgBrush");
        var dim    = (Brush)FindResource("FgDimBrush");
        var border = (Brush)FindResource("NavBorderBrush");
        var cardBg = (Brush)FindResource("Bg2NavBrush");
        var mono   = (FontFamily)FindResource("MonoFont");

        // Indented mono detail line used for the haul route / collect / deliver rows.
        TextBlock Row(string text, Brush brush, double indent) => new()
        {
            Text = text, FontFamily = mono, FontSize = 11, Foreground = brush,
            Margin = new Thickness(indent, 2, 0, 0), TextWrapping = TextWrapping.Wrap,
        };

        var active = App.Hauls.ActiveHauls;

        // Count header + a compact "Clear all" affordance. The count sits in column 0; the button
        // (shown only when there's something to clear) is right-aligned in column 1. Click clears
        // every haul; the resulting Changed -> OnHaulsChanged path rebuilds this panel.
        var headerGrid = new Grid { Margin = new Thickness(2, 2, 0, 6) };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var countBlock = new TextBlock
        {
            // Live active-haul count reads as instrument data -> cyan (MOBIGLAS signature).
            Text = $"Active hauls: {active.Count}", FontSize = 12, FontWeight = FontWeights.Bold,
            Foreground = cyan, VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(countBlock, 0);
        headerGrid.Children.Add(countBlock);

        if (App.Hauls.AllHauls.Count > 0)
        {
            var clearBtn = new Button
            {
                Content = "Clear all", FontSize = 10, FontWeight = FontWeights.Bold,
                Background = cardBg, Foreground = accent,
                BorderBrush = border, BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 2, 8, 2), Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right,
            };
            clearBtn.Click += (_, __) => App.Hauls.ClearAll();   // Changed -> OnHaulsChanged rebuilds
            Grid.SetColumn(clearBtn, 1);
            headerGrid.Children.Add(clearBtn);
        }

        HaulingList.Children.Add(headerGrid);

        if (active.Count == 0)
        {
            HaulingList.Children.Add(new TextBlock
            {
                Text = "No active hauls.", FontSize = 11, Foreground = dim,
                Margin = new Thickness(2, 2, 0, 0),
            });
            return;
        }

        // One block per active haul: "Company - Topology" then each incomplete leg.
        foreach (var h in active)
        {
            var missionId = h.MissionId;   // capture this haul's id for its own delete handler
            var company = string.IsNullOrWhiteSpace(h.ContractedBy)
                ? (string.IsNullOrWhiteSpace(h.Company) ? "Unknown company" : h.Company)
                : h.ContractedBy;

            // Title row: "Company - Topology" (ellipsis-trimmed) in column 0, a flat "x" delete
            // affordance right-aligned in column 1. Click removes just this haul; Changed ->
            // OnHaulsChanged rebuilds the panel.
            var titleGrid = new Grid { Margin = new Thickness(2, 8, 0, 2) };
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Faction (line 1). A small accent dot marks hauls whose detail was paired from a contract scan.
            var enriched = h.ContractObjectives.Count > 0 || h.Reward > 0;
            var titleStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (enriched)
                titleStack.Children.Add(new System.Windows.Shapes.Ellipse
                {
                    Width = 7, Height = 7, Fill = accent, Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = "Details paired from a contract scan",
                });
            titleStack.Children.Add(new TextBlock
            {
                Text = company, FontSize = 11, FontWeight = FontWeights.Bold, Foreground = fg,
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            });
            Grid.SetColumn(titleStack, 0);
            titleGrid.Children.Add(titleStack);

            var deleteBtn = new Button
            {
                Content = "x", FontFamily = mono, FontSize = 13, FontWeight = FontWeights.Bold,
                Foreground = dim, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Padding = new Thickness(6, 0, 6, 0), Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
            };
            deleteBtn.Click += (_, __) => App.Hauls.Remove(missionId);   // Changed -> OnHaulsChanged rebuilds
            Grid.SetColumn(deleteBtn, 1);
            titleGrid.Children.Add(deleteBtn);

            HaulingList.Children.Add(titleGrid);

            if (h.Reward > 0)
                HaulingList.Children.Add(new TextBlock
                {
                    // aUEC reward is a live numeric payout readout -> cyan (MOBIGLAS signature).
                    Text = $"{h.Reward:N0} aUEC", FontSize = 11, Foreground = cyan,
                    Margin = new Thickness(8, 2, 0, 0),
                });

            if (h.ContractObjectives.Count > 0)
            {
                // Per objective: the cargo (SCU + commodity) as the primary line, then a dim from -> to
                // route subline. Collect and Deliver carry the same cargo, so showing it once is enough.
                foreach (var o in h.ContractObjectives)
                {
                    var pickup  = string.IsNullOrWhiteSpace(o.Pickup)  ? "?" : o.Pickup;
                    var dropoff = string.IsNullOrWhiteSpace(o.Dropoff) ? "?" : o.Dropoff;
                    var cargo   = ((o.Scu > 0 ? $"{o.Scu} SCU " : "") + o.Commodity).Trim();
                    HaulingList.Children.Add(Row(cargo.Length == 0 ? "(cargo unknown)" : cargo, fg, 8));
                    HaulingList.Children.Add(Row($"{pickup} -> {dropoff}", dim, 16));
                }
            }
            else
            {
                foreach (var leg in h.Legs)
                {
                    if (leg.Completed) continue;

                    var role = leg.Role == HaulRole.Pickup ? "Load" : "Drop";
                    // Dropoff legs carry their own destination; a pickup leg borrows the haul's
                    // best-effort PickupName (the dropoff's own location lives on the sibling leg).
                    var location = leg.Role == HaulRole.Dropoff ? leg.Destination : h.PickupName;

                    var segs = new System.Collections.Generic.List<string>();
                    if (leg.TargetScu > 0) segs.Add($"{leg.TargetScu} SCU");
                    if (!string.IsNullOrWhiteSpace(leg.Commodity)) segs.Add(leg.Commodity);
                    if (!string.IsNullOrWhiteSpace(location)) segs.Add($"@ {location}");
                    var desc = string.Join(" ", segs);

                    HaulingList.Children.Add(new TextBlock
                    {
                        Text = desc.Length == 0 ? $"{role}:" : $"{role}: {desc}",
                        FontSize = 11, Foreground = fg, Margin = new Thickness(8, 3, 0, 0),
                        TextWrapping = TextWrapping.Wrap,
                    });
                }
            }
        }

        // Compact consolidation glance: where to drop and how much, across all active hauls.
        var con = App.Hauls.BuildConsolidation();
        if (con.Dropoffs.Count > 0)
        {
            HaulingList.Children.Add(new Border { Height = 1, Background = border, Margin = new Thickness(0, 10, 0, 6) });
            HaulingList.Children.Add(new TextBlock
            {
                Text = "CONSOLIDATION", FontSize = 9, FontWeight = FontWeights.Bold,
                Foreground = dim, Margin = new Thickness(2, 0, 0, 4),
            });
            foreach (var stop in con.Dropoffs)
                HaulingList.Children.Add(new TextBlock
                {
                    Text = $"Drop at {stop.Location}: {stop.TotalScu} SCU", FontSize = 11,
                    Foreground = fg, Margin = new Thickness(8, 2, 0, 0), TextWrapping = TextWrapping.Wrap,
                });
        }
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
            CornerRadius = new CornerRadius(4),
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
        var cyan     = (Brush)FindResource("CyanBrush");
        var headFont = (FontFamily)FindResource("HeadFont");
        var statusBrush = HexBrush(wo.StatusColorHex);

        var outer = new Border { Background = cardBg, BorderBrush = navB, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 0, 8) };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new Border { Background = statusBrush, CornerRadius = new CornerRadius(4, 0, 0, 4) });

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
