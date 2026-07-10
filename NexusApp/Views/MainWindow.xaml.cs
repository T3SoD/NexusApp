using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NexusApp.Models;
using NexusApp.Services;
using NexusApp.ViewModels;
using static NexusApp.Views.UiHelpers;

namespace NexusApp.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private OverlayWindow? _overlay;
    private ScanIndicatorWindow? _scanIndicator;
    private bool _boxVisible = false;
    private ScanIndicatorWindow? _contractIndicator;   // separate yellow indicator for the cargo-contract region
    private bool _contractBoxVisible;

    private bool _suppressAutocomplete;

    private NetworkPage? _networkPage;   // Blueprint Network page, built lazily on first visit

    public MainWindow()
    {
        InitializeComponent();
        AppVersionText.Text = $"App v{AppInfo.Version}";
        GameVersionText.Text = $"SC PU {GameData.Version}";
        UpdateShardChip();
        if (App.Shards != null) App.Shards.Changed += () => Dispatcher.Invoke(UpdateShardChip);
        UpdateSessionChip();
        UpdateBlueprintChip();
        if (App.GameLog != null)
        {
            App.GameLog.StateChanged += () => Dispatcher.Invoke(() => { UpdateSessionChip(); UpdateBlueprintChip(); });
            App.GameLog.HandleDetected += h => Dispatcher.Invoke(() => { UpdateOperatorIdentity(h); RefreshApprovedTools(); });
        }
        UpdateOperatorIdentity();
        RefreshApprovedTools();
        App.ContractBoxVisibilityChanged += v => Dispatcher.Invoke(() => ApplyContractBoxVisible(v));
        _vm = new MainViewModel();
        DataContext = _vm;
        _vm.OcrValueReceived    += v => { _overlay?.ReceiveOcrValue(v); _scanIndicator?.FlashGreen(); };
        _vm.OcrPhaseReceived    += p => _overlay?.ReceiveScanPhase(p);
        _vm.OcrProgressReceived += c => _overlay?.ReceiveScanProgress(c);

        _scanChipTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _scanChipTimer.Tick += (_, __) => UpdateScanChip();
        _scanChipTimer.Start();
        UpdateScanChip();
        // Flip the SCAN chip to/from the paused (yellow) state the instant foreground relevance changes.
        App.ForegroundRelevanceChanged += _ => Dispatcher.Invoke(UpdateScanChip);

        KeyPopup.Closed += (_, __) => _keyPopupClosedAt = DateTime.UtcNow;

        // Ambient HUD glyphs: each always-populated tab carries its own signature looping animation, in the
        // spirit of the RS Decoder reticle. RS Decoder keeps its reticle and Network keeps its coverage donut.
        ReferenceGlyphHost.Content = Hud.AmbientGlyph(Hud.Ambient.SpectralAssay, 36);
        BlueprintGlyphHost.Content = Hud.AmbientGlyph(Hud.Ambient.Hologram, 46);
        WorkOrderGlyphHost.Content = Hud.AmbientGlyph(Hud.Ambient.OreConveyor, 38);

        // Nav is the Wrist-OS app dock (mock #31): static line glyphs in chamfered dock tiles, styled in
        // GameTheme (DockTile). The old animated NavIco rail glyphs were retired with the rail.
        StartOsClock();
        DockTiles.SizeChanged += (_, _) => PositionDockSelector(false);
        Loaded += (_, _) =>
        {
            AnimateDockIn();                         // staggered tile entrance
            PositionDockSelector(false);             // place the active selector once laid out
            Hud.PulseDot(VpRunDot, true);            // breathing viewport run dot; the Operations
                                                     // LIVE badge is driven by UpdateSessionChip
        };

        RestoreWindowPosition();
        SetActivePage("command");
        Closing += (s, e) => { SaveWindowPosition(); _vm.StopScanner(); _listTicker?.Stop(); _scanChipTimer?.Stop(); _scanIndicator?.Close(); _contractIndicator?.Close(); };

        BuildHistoryFilterPills();
        _vm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.HistoryFilter))
                BuildHistoryFilterPills();
        };

        RestoreScanRegion();

        _vm.WorkOrders.CollectionChanged += (s, e) => RebuildWorkOrderList();

        Loaded += (s, e) => MaybeShowFirstRunWizard();
    }

    // ── First-run welcome wizard ───────────────────────────────────────────────

    private bool _firstRunChecked;

    private void MaybeShowFirstRunWizard()
    {
        if (_firstRunChecked) return;
        _firstRunChecked = true;
        if (App.Settings.Current.FirstRunComplete) return;

        App.Settings.Current.FirstRunComplete = true;
        App.Settings.Save();

        ShowTutorial();
    }

    /// <summary>Runs the welcome tour. Always shows, regardless of FirstRunComplete -
    /// the first-run gate lives in MaybeShowFirstRunWizard, while Help can replay this.
    /// Launches the modeless anchored coach-mark tour (TourController).</summary>
    public void ShowTutorial()
    {
        var tour = new TourController(this, ResolveTutorialTarget, StartScanRegionSetup);
        tour.Start();
    }

    /// <summary>Navigates to the page/overlay a tour step needs and returns the element to anchor on
    /// (null = a centered, anchorless step).</summary>
    private FrameworkElement? ResolveTutorialTarget(TutorialTarget t) => t switch
    {
        TutorialTarget.SessionPill     => SessionChip,
        TutorialTarget.BlueprintsPill  => BlueprintChip,
        TutorialTarget.AppDock         => DockTiles,
        TutorialTarget.OperationsKpis  => OperationsKpiAnchor(),
        TutorialTarget.RsDecoderTile   => NavScan,
        TutorialTarget.RefineryTile    => NavWork,
        TutorialTarget.HaulingTile     => NavHauling,
        TutorialTarget.NetworkTile     => NavNetwork,
        TutorialTarget.OpenOverlay     => OverlayToggleBtn,
        TutorialTarget.OverlayHub      => PrepareOverlayForTutorial("hub")?.HubTarget,
        TutorialTarget.ScanToggle      => PrepareOverlayForTutorial("scan")?.ScanToggleTarget,
        TutorialTarget.ContractRegion  => PrepareOverlayForTutorial("hauling")?.ContractRegionTarget,
        _                              => null,
    };

    // The Operations step navigates to the dashboard first, then rings its KPI row.
    private FrameworkElement? OperationsKpiAnchor()
    {
        SetActivePage("command");   // lazily creates + refreshes the dashboard
        return _commandPage?.KpiRowTarget ?? _commandPage;
    }

    private void StartScanRegionSetup()
    {
        var selector = new RegionSelectorWindow();
        selector.RegionSelected += ApplyScanRegion;
        selector.ShowOnMonitorOf(this);   // draw surface opens on this window's monitor (issue #6)
    }

    /// <summary>Ensures the overlay is open, visible, and on the requested tab for the tour.</summary>
    private OverlayWindow? PrepareOverlayForTutorial(string tab = "scan")
    {
        EnsureOverlay();
        if (_overlay == null) return null;
        if (!_overlay.IsVisible) _overlay.Show();
        switch (tab)
        {
            case "hub": _overlay.ShowHubTabForTutorial(); break;
            case "hauling": _overlay.ShowHaulingTabForTutorial(); break;
            default: _overlay.ShowScanTabForTutorial(); break;
        }
        _overlay.UpdateLayout();
        return _overlay;
    }

    // ── Nav ──────────────────────────────────────────────────────────────────

    // Wired to BOTH Click and Checked on the dock tiles: Checked also fires when the
    // tile is selected through UI Automation (accessibility tools, scripted drivers),
    // which never raises Click. The _activePage guard makes the double dispatch on a
    // plain mouse click (Checked then Click) a no-op.
    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        // Checked fires for NavCommand's IsChecked="True" DURING InitializeComponent,
        // before the page elements exist - ignore until the window is up (the ctor's
        // explicit SetActivePage("command") sets the initial page).
        if (!IsLoaded) return;
        if (sender is System.Windows.Controls.RadioButton rb && rb.Tag is string page)
            SetActivePage(page);
    }

    private string? _activePage;

    private void SetActivePage(string page)
    {
        if (page == _activePage) return;
        _activePage = page;

        PageCommand.Visibility    = page == "command"    ? Visibility.Visible : Visibility.Collapsed;
        PageScan.Visibility       = page == "scan"       ? Visibility.Visible : Visibility.Collapsed;
        PageBlueprints.Visibility = page == "blueprints" ? Visibility.Visible : Visibility.Collapsed;
        PageReference.Visibility  = page == "reference"  ? Visibility.Visible : Visibility.Collapsed;
        PageWorkOrders.Visibility = page == "workorders" ? Visibility.Visible : Visibility.Collapsed;
        PageNetwork.Visibility    = page == "network"    ? Visibility.Visible : Visibility.Collapsed;
        PageHauling.Visibility    = page == "hauling"    ? Visibility.Visible : Visibility.Collapsed;
        PagePlanner.Visibility    = page == "planner"    ? Visibility.Visible : Visibility.Collapsed;
        PageGridStudio.Visibility = page == "gridstudio" ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility   = page == "settings"   ? Visibility.Visible : Visibility.Collapsed;

        NavCommand.IsChecked  = page == "command";
        NavScan.IsChecked     = page == "scan";
        NavBlue.IsChecked     = page == "blueprints";
        NavRef.IsChecked      = page == "reference";
        NavWork.IsChecked     = page == "workorders";
        NavNetwork.IsChecked  = page == "network";
        NavHauling.IsChecked  = page == "hauling";
        NavPlanner.IsChecked  = page == "planner";
        NavGridStudio.IsChecked = page == "gridstudio";
        NavSettings.IsChecked = page == "settings";

        // Viewport (Wrist-OS launched-app window): update the module path readout and replay the boot
        // flicker + scan sweep so switching modules reads like the OS launching the app.
        if (VpModule != null) VpModule.Text = $"module://nexus/{page}";
        PlayViewportSweep();
        PositionDockSelector(true);

        Title = page switch
        {
            "command"    => "Nexus - Operations",
            "scan"       => "Nexus - RS Signal Decoder",
            "blueprints" => "Nexus - Blueprint Library",
            "reference"  => "Nexus - Mining Codex",
            "workorders" => "Nexus - Refinery Tracker",
            "network"    => "Nexus - Blueprint Network",
            "hauling"    => "Nexus - Cargo Hauling",
            "planner"    => "Nexus - Cargo Planner",
            "gridstudio" => "Nexus - Grid Studio",
            "settings"   => "Nexus - Settings",
            _            => "Nexus",
        };

        if (page == "blueprints") InitBlueprintBrowse();
        if (page == "reference") { BuildFilterPills(); BuildReferenceTree(staggerEntry: true); }
        if (page == "workorders") RebuildWorkOrderList();
        if (page == "command") InitCommandPage();
        if (page == "network") InitNetworkPage();
        if (page == "hauling") InitHaulingPage();
        if (page == "planner") InitPlannerPage();
        if (page == "gridstudio") InitGridStudioPage();
        if (page == "settings") InitSettingsPage();
        UpdateNavBadges();

        AnimatePageIn(page switch
        {
            "command"    => PageCommand,
            "scan"       => PageScan,
            "blueprints" => PageBlueprints,
            "reference"  => PageReference,
            "workorders" => PageWorkOrders,
            "network"    => PageNetwork,
            "hauling"    => PageHauling,
            "settings"   => PageSettings,
            _            => (FrameworkElement?)null,
        });
    }

    // Brief holographic page-in (fade + rise) played whenever a tab becomes active, extending the
    // RS-decoder/reticle motion language to every page.
    private static void AnimatePageIn(FrameworkElement? page)
    {
        if (page == null) return;
        if (Motion.Reduced) { page.Opacity = 1; page.RenderTransform = null; return; }
        var ease = Motion.Reveal;   // exact mock page-reveal bezier (0.2,0.8,0.2,1)
        var slide = new System.Windows.Media.TranslateTransform(0, 12);
        page.RenderTransform = slide;
        page.BeginAnimation(UIElement.OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(Motion.PageFadeMs)) { EasingFunction = ease });
        slide.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new System.Windows.Media.Animation.DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(Motion.PageRiseMs)) { EasingFunction = ease });
    }

    // OS status-bar clock (Wrist-OS, mock #31): ticks the HH:mm:ss readout once a second.
    private System.Windows.Threading.DispatcherTimer? _osClockTimer;
    private void StartOsClock()
    {
        void Tick() { if (OsClock != null) OsClock.Text = DateTime.Now.ToString(App.Settings.Current.Clock24Hour ? "HH:mm:ss" : "h:mm:ss tt"); }
        Tick();
        _osClockTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _osClockTimer.Tick += (_, _) => Tick();
        _osClockTimer.Start();
    }

    // Wrist-OS launch fx: on each page switch, redraw the amber underline beneath the viewport title
    // bar with a quick left-to-right draw, so opening a module reads as a deliberate page change
    // without a full-height scan band wiping across the content.
    private void PlayViewportSweep()
    {
        if (VpUnderlineT == null) return;
        if (Motion.Reduced)
        {
            VpUnderlineT.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
            VpUnderlineT.ScaleX = 1;
            return;
        }
        VpUnderlineT.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)) { EasingFunction = Motion.SlideOut });
    }

    // The currently-checked dock tile (the active module), or null during very early init.
    private System.Windows.Controls.RadioButton? ActiveDockTile()
    {
        if (NavCommand.IsChecked == true)  return NavCommand;
        if (NavScan.IsChecked == true)     return NavScan;
        if (NavWork.IsChecked == true)     return NavWork;
        if (NavRef.IsChecked == true)      return NavRef;
        if (NavBlue.IsChecked == true)     return NavBlue;
        if (NavNetwork.IsChecked == true)  return NavNetwork;
        if (NavHauling.IsChecked == true)  return NavHauling;
        if (NavPlanner.IsChecked == true)  return NavPlanner;
        if (NavGridStudio.IsChecked == true) return NavGridStudio;
        if (NavSettings.IsChecked == true) return NavSettings;
        return null;
    }

    // Slide the single amber selector bar to the active dock tile (mock #31's layoutId bar). Re-runs on
    // page switch, dock resize, and load; defers until the tile is laid out so the math is valid.
    private void PositionDockSelector(bool animated)
    {
        var tile = ActiveDockTile();
        if (tile == null || DockTiles == null || DockSelector == null || DockSelectorT == null) return;
        if (!tile.IsLoaded || tile.ActualHeight < 1)
        {
            Dispatcher.BeginInvoke(new Action(() => PositionDockSelector(animated)),
                System.Windows.Threading.DispatcherPriority.Loaded);
            return;
        }
        const double inset = 7;
        double top = tile.TransformToVisual(DockTiles).Transform(new System.Windows.Point(0, 0)).Y;
        double targetY = top + inset;
        DockSelector.Height = Math.Max(8, tile.ActualHeight - inset * 2);
        DockSelector.Opacity = 1;
        if (animated && !Motion.Reduced)
        {
            DockSelectorT.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
                new System.Windows.Media.Animation.DoubleAnimation(targetY, TimeSpan.FromMilliseconds(280))
                { EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut } });
        }
        else
        {
            DockSelectorT.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
            DockSelectorT.Y = targetY;
        }
    }

    // Staggered entrance for the dock tiles on first show (slide in from the left + fade).
    private void AnimateDockIn()
    {
        if (DockTiles == null) return;
        if (Motion.Reduced) return;   // reduce animations: tiles just appear, no staggered slide
        int i = 0;
        foreach (var child in DockTiles.Children)
        {
            if (child is FrameworkElement fe)
            {
                var begin = TimeSpan.FromMilliseconds(70 + i * 45);
                var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
                var tt = new System.Windows.Media.TranslateTransform(-12, 0);
                fe.RenderTransform = tt;
                fe.Opacity = 0;
                fe.BeginAnimation(UIElement.OpacityProperty,
                    new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)) { BeginTime = begin, EasingFunction = ease });
                tt.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty,
                    new System.Windows.Media.Animation.DoubleAnimation(-12, 0, TimeSpan.FromMilliseconds(340)) { BeginTime = begin, EasingFunction = ease });
                i++;
            }
        }
    }

    private SettingsPage? _settingsPage;
    private void InitSettingsPage()
    {
        if (_settingsPage == null)
        {
            _settingsPage = new SettingsPage(ShowLogMonitor, ShowAppLogMonitor);
            PageSettings.Children.Add(_settingsPage);
        }
    }

    private CommandPage? _commandPage;
    private void InitCommandPage()
    {
        if (_commandPage == null)
        {
            _commandPage = new CommandPage(SetActivePage, _vm);   // dashboard drills via SetActivePage; reads last scan from _vm
            PageCommand.Children.Add(_commandPage);
        }
        _commandPage.Refresh();
    }

    // Live SHARD telemetry chip in the header status strip (updates on shard join/leave).
    private void UpdateShardChip()
    {
        var s = App.Shards?.Current;
        if (s != null)
        {
            ShardChipText.Text = string.IsNullOrWhiteSpace(s.Instance) ? s.Region : $"{s.Region} · {s.Instance}";
            ShardDot.Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(62, 214, 139));
        }
        else
        {
            ShardChipText.Text = "not detected";
            ShardDot.Fill = (System.Windows.Media.Brush)FindResource("FgDimBrush");
        }
    }

    // Live SESSION telemetry chip in the header status strip: tracking is always on, so this confirms a
    // live game session (green, monitoring) vs Star Citizen being closed / shut down (red, offline). "Live"
    // is read from Game.log freshness, so the chip flips off shortly after the player exits the game.
    private void UpdateSessionChip()
    {
        if (App.GameLog == null || SessionChipText == null) return;
        bool live = App.GameLog.IsSessionLive;
        SessionChipText.Text = live ? "monitoring" : "offline";
        var c = live
            ? System.Windows.Media.Color.FromRgb(0x3E, 0xD6, 0x8B)
            : System.Windows.Media.Color.FromRgb(0xE5, 0x48, 0x4D);
        SessionDot.Fill = new System.Windows.Media.SolidColorBrush(c);
        SessionChipText.Foreground = new System.Windows.Media.SolidColorBrush(c);
        Hud.PulseDot(SessionDot, live);   // the green LED gently flashes while a session is live

        // Mirror the SESSION LED on the dock-foot identity badge so they always agree:
        // green ONLINE while Star Citizen is running, red OFFLINE when it's closed.
        if (LinkDot != null)
        {
            LinkDot.Fill = new System.Windows.Media.SolidColorBrush(c);
            Hud.PulseDot(LinkDot, live);
        }
        if (LinkStatusText != null)
            LinkStatusText.Text = live ? "ONLINE . SECURE LINK" : "OFFLINE . NO LINK";

        // Same signal on the Operations dock tile badge: LIVE while Star Citizen runs,
        // OFFLINE once it's closed.
        if (OpsLiveDot != null)
        {
            OpsLiveDot.Fill = new System.Windows.Media.SolidColorBrush(c);
            Hud.PulseDot(OpsLiveDot, live);
        }
        if (OpsLiveText != null)
        {
            OpsLiveText.Text = live ? "LIVE" : "OFFLINE";
            OpsLiveText.Foreground = new System.Windows.Media.SolidColorBrush(c);
        }
    }

    // Live BLUEPRINTS telemetry chip: Auto-Track Blueprints is always on, so this confirms blueprint
    // auto-collection is active (green) while a game session is live, else off (red, SC closed).
    private void UpdateBlueprintChip()
    {
        if (App.GameLog == null || BlueprintChipText == null) return;
        bool tracking = App.GameLog.IsSessionLive && App.GameLog.AutoMark;
        BlueprintChipText.Text = tracking ? "tracking" : "off";
        var c = tracking
            ? System.Windows.Media.Color.FromRgb(0x3E, 0xD6, 0x8B)
            : System.Windows.Media.Color.FromRgb(0xE5, 0x48, 0x4D);
        BlueprintDot.Fill = new System.Windows.Media.SolidColorBrush(c);
        BlueprintChipText.Foreground = new System.Windows.Media.SolidColorBrush(c);
        Hud.PulseDot(BlueprintDot, tracking);   // the green LED gently flashes while tracking
    }

    // Dock-foot identity: show the detected RSI handle, or fall back to CITIZEN when no handle
    // has been detected from the Game.log yet. (The avatar box is a static "SC" badge.)
    private void UpdateOperatorIdentity(string? handle = null)
    {
        if (OperatorName == null) return;
        handle = string.IsNullOrWhiteSpace(handle) ? App.Settings.Current.DetectedRsiHandle : handle;
        OperatorName.Text = string.IsNullOrWhiteSpace(handle) ? "CITIZEN" : handle.Trim();
    }

    private System.Windows.Threading.DispatcherTimer? _scanChipTimer;
    // SCAN telemetry chip (auto-scan on/off), refreshed on a light timer.
    private void UpdateScanChip()
    {
        switch (_vm.RsScanState)
        {
            case ScanIndicator.On:
                ScanChipText.Text = "Auto · on";
                ScanChipText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3E, 0xD6, 0x8B));
                break;
            case ScanIndicator.Paused:
                ScanChipText.Text = "paused";
                ScanChipText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xEA, 0xB3, 0x08));
                break;
            default:
                ScanChipText.Text = "off";
                ScanChipText.Foreground = (System.Windows.Media.Brush)FindResource("FgDimBrush");
                break;
        }
    }

    // Active-count badge on the Refinery rail item.
    private void UpdateNavBadges()
    {
        int orders = App.Data.GetWorkOrders().FindAll(o => o.Status != WorkOrderStatus.Complete).Count;
        NavWorkBadge.Text = orders > 0 ? orders.ToString() : "";
        NavWorkPill.Visibility = orders > 0 ? Visibility.Visible : Visibility.Collapsed;

        int hauls = App.Hauls?.ActiveHauls.Count ?? 0;
        NavHaulBadge.Text = hauls > 0 ? hauls.ToString() : "";
        NavHaulPill.Visibility = hauls > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void InitNetworkPage()
    {
        if (_networkPage == null)
        {
            _networkPage = new NetworkPage(App.Network, App.Settings);
            PageNetwork.Children.Add(_networkPage);
        }
        _networkPage.Refresh();
    }

    private HaulingPage? _haulingPage;
    private void InitHaulingPage()
    {
        if (_haulingPage == null)
        {
            _haulingPage = new HaulingPage();
            PageHauling.Children.Add(_haulingPage);
        }
        _haulingPage.Refresh();
    }

    private CargoPlannerPage? _plannerPage;
    private void InitPlannerPage()
    {
        if (_plannerPage == null)
        {
            _plannerPage = new CargoPlannerPage();
            PagePlanner.Children.Add(_plannerPage);
        }
        _plannerPage.OnShown();
    }

    private GridStudioPage? _gridStudioPage;
    private void InitGridStudioPage()
    {
        if (_gridStudioPage == null)
        {
            _gridStudioPage = new GridStudioPage();
            PageGridStudio.Children.Add(_gridStudioPage);
        }
        _gridStudioPage.OnShown();
    }

    // Approved-list gated tabs (Grid Studio dev tool, and the Cargo Planner until it is ship-ready)
    // show when the detected RSI handle is on the approved contributor list. Re-evaluated at
    // startup and whenever a handle is detected from Game.log.
    private void RefreshApprovedTools()
    {
        var approved = NexusApp.Services.AccessGate.IsApprovedActive;
        NavGridStudio.Visibility = approved ? Visibility.Visible : Visibility.Collapsed;
        NavPlanner.Visibility = approved ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── RS Scan ──────────────────────────────────────────────────────────────

    private void RsInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _vm.LookupCommand.Execute(null);
            _vm.RsInput = "";
        }
    }

    private void HistoryItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is ScanHistoryEntry entry)
            _vm.RunScanNoHistory(entry.Rs);
    }

    // ── Reference Tree ───────────────────────────────────────────────────────

    private readonly HashSet<string> _systemFilter = new();
    private readonly HashSet<string> _methodFilter = new();

    private static readonly (string Key, string Label)[] _systems =
        [("Stanton", "Stanton"), ("Pyro", "Pyro"), ("Nyx", "Nyx")];

    private static readonly (string Key, string Label)[] _methods =
        [("ship", "Ship"), ("vehicle", "ROC"), ("fps", "FPS")];

    private void BuildFilterPills()
    {
        SystemFilterPanel.Children.Clear();
        var allSys = MakePill("All", _systemFilter.Count == 0);
        allSys.Click += (_, __) => { _systemFilter.Clear(); BuildFilterPills(); BuildReferenceTree(); };
        SystemFilterPanel.Children.Add(allSys);
        foreach (var (key, label) in _systems)
        {
            var k = key;
            var pill = MakePillDot(label, _systemFilter.Contains(k), SystemBrush(k));
            pill.Click += (_, __) => { if (!_systemFilter.Remove(k)) _systemFilter.Add(k); BuildFilterPills(); BuildReferenceTree(); };
            SystemFilterPanel.Children.Add(pill);
        }

        MethodFilterPanel.Children.Clear();
        var allMeth = MakePill("All", _methodFilter.Count == 0);
        allMeth.Click += (_, __) => { _methodFilter.Clear(); BuildFilterPills(); BuildReferenceTree(); };
        MethodFilterPanel.Children.Add(allMeth);
        foreach (var (key, label) in _methods)
        {
            var k = key;
            var pill = MakePill(label, _methodFilter.Contains(k));
            pill.Click += (_, __) => { if (!_methodFilter.Remove(k)) _methodFilter.Add(k); BuildFilterPills(); BuildReferenceTree(); };
            MethodFilterPanel.Children.Add(pill);
        }
    }

    private void BuildHistoryFilterPills()
    {
        HistoryFilterPanel.Children.Clear();
        (string Label, HistoryFilter Filter)[] options =
        [
            ("All",          HistoryFilter.All),
            ("Exact + Close", HistoryFilter.ExactAndClose),
            ("Exact",        HistoryFilter.Exact),
        ];
        foreach (var (label, filter) in options)
        {
            var f = filter;
            var btn = MakePill(label, _vm.HistoryFilter == f);
            btn.Height = 20;
            btn.Click += (_, __) => { _vm.HistoryFilter = f; };
            HistoryFilterPanel.Children.Add(btn);
        }
    }

    private Button MakePill(string label, bool active) => new()
    {
        Content = label,
        Style = (Style)FindResource(active ? "AccentButton" : "NexusButton"),
        Padding = new Thickness(8, 2, 8, 2),
        Margin = new Thickness(0, 0, 4, 0),
        FontSize = 10,
    };

    private Button MakePillDot(string label, bool active, System.Windows.Media.Brush dot)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = dot, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        sp.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        return new Button
        {
            Content = sp,
            Style = (Style)FindResource(active ? "AccentButton" : "NexusButton"),
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(0, 0, 4, 0),
            FontSize = 10,
        };
    }

    private void RefClear_Click(object sender, RoutedEventArgs e)
    {
        _vm.RefFilter = "";
        BuildReferenceTree();
    }

    private DateTime _keyPopupClosedAt = DateTime.MinValue;

    private void KeyButton_Click(object sender, RoutedEventArgs e)
    {
        // StaysOpen=False closes the popup on the outside-click before this fires;
        // ignore the click that immediately follows a close so the button toggles cleanly.
        if ((DateTime.UtcNow - _keyPopupClosedAt).TotalMilliseconds < 200) return;
        KeyPopup.IsOpen = !KeyPopup.IsOpen;
    }

    private void ResetSort_Click(object sender, RoutedEventArgs e)
    {
        _vm.RefFilter = "";
        _systemFilter.Clear();
        _methodFilter.Clear();
        BuildFilterPills();
        BuildReferenceTree();
    }

    private FrameworkElement? _selectedRefCard;
    private Action? _deselectRefCard;   // resets the currently-selected resource card's chamfer visuals
    private readonly Dictionary<string, Action> _refSelectByName = new(StringComparer.OrdinalIgnoreCase);   // select a resource card by name

    private Dictionary<string, string>? _oreClassByName;   // ore -> Metal/Mineral/Gem ("" when unknown)

    private string OreClass(string name)
    {
        _oreClassByName ??= _vm.AllResources.ToDictionary(
            res => res.Name,
            res => App.Data.GetMiningProfile(res.Name)?.Class ?? "",
            StringComparer.OrdinalIgnoreCase);
        return _oreClassByName.TryGetValue(name, out var c) ? c : "";
    }

    // Tiny static glyph, 10x10: diamond = Metal, three dots = Mineral, hexagon = Gem.
    // Shapes and stroke come from the frozen codex-motion mock values.
    private static FrameworkElement? ClassGlyph(string oreClass)
    {
        var amber = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xC0, 0xFF, 0xB2, 0x3E));
        amber.Freeze();
        string? data = oreClass.ToLowerInvariant() switch
        {
            "metal"   => "M 5,0 L 10,5 L 5,10 L 0,5 Z",
            "gem"     => "M 2.5,0.7 L 7.5,0.7 L 10,5 L 7.5,9.3 L 2.5,9.3 L 0,5 Z",
            "mineral" => "M 2,7 A 1.6,1.6 0 1 0 2,7.01 M 7,3 A 2,2 0 1 0 7,3.01 M 7.5,8 A 1.3,1.3 0 1 0 7.5,8.01",
            _ => null,
        };
        if (data is null) return null;
        return new System.Windows.Shapes.Path
        {
            Data = System.Windows.Media.Geometry.Parse(data),
            Stroke = amber, StrokeThickness = 1.2, Width = 10, Height = 10,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0),
        };
    }

    private void BuildReferenceTree(bool staggerEntry = false)
    {
        ReferenceList.Children.Clear();
        _deselectRefCard = null;
        _selectedRefCard = null;
        _refSelectByName.Clear();
        var filter = _vm.RefFilter.ToLower();

        var bpMatches = string.IsNullOrWhiteSpace(filter)
            ? new HashSet<string>()
            : App.Data.GetResourceNamesForBlueprintSearch(filter);

        var filtered = _vm.AllResources
            .Where(r => string.IsNullOrEmpty(filter) ||
                        r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                        r.Locations.Any(l => l.Contains(filter, StringComparison.OrdinalIgnoreCase)) ||
                        bpMatches.Contains(r.Name))
            .Where(r => _systemFilter.Count == 0 || r.Locations.Any(l => _systemFilter.Contains(GetSystem(l))))
            .Where(r => _methodFilter.Count == 0 || _methodFilter.Contains(r.Method) ||
                        (_methodFilter.Contains("fps") && r.Method == "fps+vehicle"))
            .OrderBy(r => Array.IndexOf(new[] { "legendary", "epic", "rare", "uncommon", "common" }, r.Rarity))
            .ThenByDescending(r => r.BaseRs)
            .ToList();

        if (filtered.Count == 0)
        {
            _codexHologram?.Stop();   // the detail panel is going away - do not rely on Unloaded timing
            ReferenceDetailPanel.Children.Clear();
            ReferenceList.Children.Add(new TextBlock
            {
                Text = "No resources match", FontSize = 12, FontStyle = FontStyles.Italic,
                Foreground = (System.Windows.Media.Brush)FindResource("FgDimBrush"),
                Margin = new Thickness(12, 12, 0, 0),
            });
            return;
        }

        Action? selectFirst = null;
        for (int i = 0; i < filtered.Count; i++)
        {
            var card = BuildResourceCard(filtered[i], out var select);
            ReferenceList.Children.Add(card);
            _refSelectByName[filtered[i].Name] = select;
            if (i == 0) selectFirst = select;
        }
        selectFirst?.Invoke();   // auto-select the first card (shows its detail)

        if (staggerEntry)
        {
            CascadeIn(ReferenceList.Children, maxAnimated: 14);
        }
        else
        {
            // Filter/search rebuilds happen per keystroke (debounced) - a per-card stagger would
            // be jank and noise, so the rebuilt list gets one quick settle fade instead.
            ReferenceList.BeginAnimation(UIElement.OpacityProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0.55, 1,
                    System.TimeSpan.FromMilliseconds(120)));
        }
    }

    private FrameworkElement BuildResourceCard(Resource r, out Action select)
    {
        var rb          = RarityBrush(r.Rarity);   // semantic rarity color (kept)
        var bg2         = (System.Windows.Media.Brush)FindResource("Bg2NavBrush");
        var navBorder   = (System.Windows.Media.Brush)FindResource("NavBorderBrush");
        var accentDim   = (System.Windows.Media.Brush)FindResource("AccentDimBrush");
        var accentBrush = (System.Windows.Media.Brush)FindResource("AccentBrush");
        var highlight   = (System.Windows.Media.Brush)FindResource("HighlightBrush");
        var headFont    = (System.Windows.Media.FontFamily)FindResource("HeadFont");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var gem = new Border { Width = 11, Height = 11, CornerRadius = new CornerRadius(3), Background = rb, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        Grid.SetColumn(gem, 0); grid.Children.Add(gem);
        var classGlyph = ClassGlyph(OreClass(r.Name));
        if (classGlyph != null) { Grid.SetColumn(classGlyph, 1); grid.Children.Add(classGlyph); }
        var name = new TextBlock { Text = r.Name, FontSize = 13, Foreground = rb, FontFamily = headFont, VerticalAlignment = VerticalAlignment.Center, TextTrimming = System.Windows.TextTrimming.CharacterEllipsis };
        Grid.SetColumn(name, 2); grid.Children.Add(name);
        var rs = new TextBlock { Text = r.BaseRs > 0 ? $"RS {r.BaseRs:N0}" : "-", FontSize = 12, FontFamily = headFont, Foreground = (System.Windows.Media.Brush)FindResource("GoldBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        Grid.SetColumn(rs, 3); grid.Children.Add(rs);

        var host = Hud.CardFrame(grid, out var frame, out _, chamfer: 8, padding: new Thickness(12, 9, 12, 9));
        host.Margin = new Thickness(0, 0, 8, 6);
        host.Cursor = System.Windows.Input.Cursors.Hand;
        host.Tag = r;

        select = () =>
        {
            _deselectRefCard?.Invoke();
            frame.Fill = accentDim; frame.Stroke = accentBrush;
            _deselectRefCard = () => { frame.Fill = bg2; frame.Stroke = navBorder; };
            _selectedRefCard = host;
            ShowResourceDetail(r);
        };
        var sel = select;
        host.MouseEnter += (s, e) => { if (!ReferenceEquals(host, _selectedRefCard)) frame.Fill = highlight; };
        host.MouseLeave += (s, e) => { if (!ReferenceEquals(host, _selectedRefCard)) frame.Fill = bg2; };
        host.MouseLeftButtonDown += (s, e) => sel();
        return host;
    }

    private TextBlock RefSectionLabel(string text) => new TextBlock
    {
        Text = text, FontSize = 9, FontWeight = FontWeights.Bold,
        Foreground = (System.Windows.Media.Brush)FindResource("FgDimBrush"),
        Margin = new Thickness(0, 4, 0, 8),
    };

    private Border YieldRow(NexusApp.Models.RefineryYield y)
    {
        var row = new Border { Margin = new Thickness(0, 0, 0, 4), Padding = new Thickness(12, 7, 12, 7), CornerRadius = new CornerRadius(6), Background = (System.Windows.Media.Brush)FindResource("Bg2NavBrush"), BorderBrush = (System.Windows.Media.Brush)FindResource("NavBorderBrush"), BorderThickness = new Thickness(1) };
        var rg = new Grid();
        rg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        rg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
        rg.Children.Add(new TextBlock { Text = y.Station, FontSize = 12, Foreground = (System.Windows.Media.Brush)FindResource("FgBrush"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = System.Windows.TextTrimming.CharacterEllipsis });
        var sysT = new TextBlock { Text = y.System, FontSize = 12, Foreground = (System.Windows.Media.Brush)FindResource("FgDimBrush"), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(sysT, 1); rg.Children.Add(sysT);
        var sign = y.ModifierPct > 0 ? "+" : "";
        var yld = new TextBlock { Text = $"{sign}{y.ModifierPct}%", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = ModifierBrush(y.ModifierPct), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(yld, 2); rg.Children.Add(yld);
        row.Child = rg;
        return row;
    }

    private TextBlock LocRow(string loc) => new TextBlock
    {
        Text = $"◆  {loc}", FontSize = 12, Foreground = SystemBrush(GetSystem(loc)),
        Margin = new Thickness(0, 0, 0, 5), TextTrimming = System.Windows.TextTrimming.CharacterEllipsis,
    };

    // A byproduct-source row: another ore's deposit that also yields the current resource. The
    // proportional bar + band text show the % of that rock this resource makes up; the right value
    // is the best spawn chance across the host's rock-type variants. Clicking opens the host ore.
    private Border FoundInRow(NexusApp.Models.FoundInSource f)
    {
        var fg   = (System.Windows.Media.Brush)FindResource("FgBrush");
        var dim  = (System.Windows.Media.Brush)FindResource("FgDimBrush");
        var headFont = (System.Windows.Media.FontFamily)System.Windows.Application.Current.FindResource("HeadFont");
        var monoFont = (System.Windows.Media.FontFamily)System.Windows.Application.Current.FindResource("MonoFont");
        var cyan = BrushFromHex("#7FE9E0");

        var host = _vm.AllResources.FirstOrDefault(x => x.Name.Equals(f.Ore, StringComparison.OrdinalIgnoreCase));
        var dotBrush = RarityBrush(host?.Rarity ?? "common");

        var row = new Border
        {
            Margin = new Thickness(0, 0, 0, 4), Padding = new Thickness(12, 7, 12, 7),
            CornerRadius = new CornerRadius(6),
            Background = (System.Windows.Media.Brush)FindResource("Bg2NavBrush"),
            BorderBrush = (System.Windows.Media.Brush)FindResource("NavBorderBrush"),
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = $"Open {f.Ore} in the Codex  ·  carried by {f.Variants} {f.Ore} rock type{(f.Variants == 1 ? "" : "s")}",
        };
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // ore name
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });                   // band bar
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });                   // band %
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(66) });                   // probability

        var nameStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        nameStack.Children.Add(new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(3), Background = dotBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 9, 0) });
        nameStack.Children.Add(new TextBlock { Text = f.Ore, FontSize = 13, FontWeight = FontWeights.SemiBold, FontFamily = headFont, Foreground = fg, VerticalAlignment = VerticalAlignment.Center, TextTrimming = System.Windows.TextTrimming.CharacterEllipsis });
        g.Children.Add(nameStack);

        // proportional band bar: [min gap][occupied band][rest] as star columns, a cyan fill over a faint track
        double min = System.Math.Max(0, System.Math.Min(100, f.MinPct));
        double max = System.Math.Max(min, System.Math.Min(100, f.MaxPct));
        var bar = new Grid { Height = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(min, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(System.Math.Max(1, max - min), GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(System.Math.Max(0, 100 - max), GridUnitType.Star) });
        var track = new Border { CornerRadius = new CornerRadius(5), Background = BrushFromHex("#147FE9E0"), BorderBrush = (System.Windows.Media.Brush)FindResource("NavBorderBrush"), BorderThickness = new Thickness(1) };
        Grid.SetColumnSpan(track, 3); bar.Children.Add(track);
        var fill = new Border { CornerRadius = new CornerRadius(4), Background = cyan, Margin = new Thickness(0, 1.5, 0, 1.5) };
        Grid.SetColumn(fill, 1); bar.Children.Add(fill);
        Grid.SetColumn(bar, 1); g.Children.Add(bar);

        static string Pc(double v) => ((int)System.Math.Round(v)).ToString();
        var pct = new TextBlock { Text = $"{Pc(f.MinPct)}-{Pc(f.MaxPct)}%", FontSize = 12, FontFamily = monoFont, Foreground = cyan, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(pct, 2); g.Children.Add(pct);

        var probTxt = f.Probability >= 0.995 ? "always" : $"up to {(int)System.Math.Round(f.Probability * 100)}%";
        var prob = new TextBlock { Text = probTxt, FontSize = 11, FontFamily = monoFont, Foreground = dim, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(prob, 3); g.Children.Add(prob);

        row.Child = g;
        row.MouseLeftButtonDown += (s, e) => NavigateToResource(f.Ore);
        return row;
    }

    // Compact number: drop a trailing ".0" but keep real decimals.
    private static string FmtD(double v) => v == System.Math.Floor(v) ? ((long)v).ToString() : v.ToString("0.##");

    // Severity colour for the mining gauges: lo = green (easy/good), md = amber, hi = red (hard/risky).
    private System.Windows.Media.Brush LevelBrush(string level) => level switch
    {
        "hi" => BrushFromHex("#EF4444"),
        "md" => (System.Windows.Media.Brush)FindResource("AccentBrush"),
        _    => BrushFromHex("#66E6A6"),
    };

    // One gauge in the Mining Profile: label, value + qualitative tag, and a severity-coloured bar.
    private Border MiningStatCard(string label, string value, string tag, string level, double fillPct)
    {
        var lvl = LevelBrush(level);
        var dim = (System.Windows.Media.Brush)FindResource("FgDimBrush");
        var headFont = (System.Windows.Media.FontFamily)System.Windows.Application.Current.FindResource("HeadFont");
        var monoFont = (System.Windows.Media.FontFamily)System.Windows.Application.Current.FindResource("MonoFont");

        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = label, FontSize = 8.5, FontWeight = FontWeights.Bold, FontFamily = headFont, Foreground = dim });
        var vrow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 7) };
        vrow.Children.Add(new TextBlock { Text = value, FontSize = 17, FontFamily = monoFont, Foreground = lvl, VerticalAlignment = VerticalAlignment.Bottom });
        vrow.Children.Add(new TextBlock { Text = tag, FontSize = 9, FontFamily = monoFont, Foreground = lvl, Margin = new Thickness(6, 0, 0, 2), VerticalAlignment = VerticalAlignment.Bottom });
        sp.Children.Add(vrow);

        var bar = new Grid { Height = 5 };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(System.Math.Max(0, System.Math.Min(100, fillPct)), GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(System.Math.Max(0, 100 - fillPct), GridUnitType.Star) });
        var track = new Border { CornerRadius = new CornerRadius(3), Background = BrushFromHex("#147FE9E0") };
        Grid.SetColumnSpan(track, 2); bar.Children.Add(track);
        var fill = new Border { CornerRadius = new CornerRadius(3), Background = lvl };
        Grid.SetColumn(fill, 0); bar.Children.Add(fill);
        sp.Children.Add(bar);

        return new Border
        {
            Width = 132, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(11, 9, 11, 10),
            CornerRadius = new CornerRadius(6), Background = BrushFromHex("#0A0E14"),
            BorderBrush = (System.Windows.Media.Brush)FindResource("NavBorderBrush"), BorderThickness = new Thickness(1),
            Child = sp,
        };
    }

    // The optimal-charge-window strip: a green sweet-spot zone centred on WindowMid, its width set
    // by WindowThin (tighter window = narrower zone = harder to mine).
    private System.Windows.FrameworkElement ChargeWindow(NexusApp.Models.MiningProfile p)
    {
        var dim = (System.Windows.Media.Brush)FindResource("FgDimBrush");
        var monoFont = (System.Windows.Media.FontFamily)System.Windows.Application.Current.FindResource("MonoFont");
        double mid = System.Math.Max(0, System.Math.Min(100, p.WindowMid * 100));
        double halfW = System.Math.Max(5, System.Math.Min(24, 20 - p.WindowThin * 6));
        double zL = System.Math.Max(0, mid - halfW);
        double zW = System.Math.Min(100 - zL, halfW * 2);

        var outer = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        var lr = new Grid();
        lr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        lr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        lr.Children.Add(new TextBlock { Text = "OPTIMAL CHARGE WINDOW", FontSize = 9, FontFamily = monoFont, Foreground = dim, Margin = new Thickness(0, 0, 0, 6) });
        var tt = new TextBlock { Text = $"tightness {FmtD(p.WindowThin)}", FontSize = 9, FontFamily = monoFont, Foreground = dim };
        Grid.SetColumn(tt, 1); lr.Children.Add(tt);
        outer.Children.Add(lr);

        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(zL, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(System.Math.Max(1, zW), GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(System.Math.Max(0, 100 - zL - zW), GridUnitType.Star) });
        var zone = new Border { Background = BrushFromHex("#3366E6A6"), BorderBrush = BrushFromHex("#66E6A6"), BorderThickness = new Thickness(1, 0, 1, 0) };
        Grid.SetColumn(zone, 1); g.Children.Add(zone);
        var track = new Border
        {
            Height = 24, CornerRadius = new CornerRadius(5), Background = BrushFromHex("#0A0E14"),
            BorderBrush = (System.Windows.Media.Brush)FindResource("NavBorderBrush"), BorderThickness = new Thickness(1),
            ClipToBounds = true, Child = g,
        };
        outer.Children.Add(track);
        return outer;
    }

    // Segmented composition bar (each element sized by its average share of the rock).
    private Border CompositionBar(System.Collections.Generic.List<NexusApp.Models.CompositionPart> comp)
    {
        var g = new Grid { Height = 16 };
        double sum = comp.Sum(c => (c.MinPct + c.MaxPct) / 2);
        if (sum <= 0) sum = 1;
        for (int i = 0; i < comp.Count; i++)
        {
            var c = comp[i];
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength((c.MinPct + c.MaxPct) / 2, GridUnitType.Star) });
            var seg = new Border { Background = c.IsPrimary ? (System.Windows.Media.Brush)FindResource("GoldBrush") : RarityBrush(RarityOf(c.Ore)) };
            Grid.SetColumn(seg, i); g.Children.Add(seg);
        }
        return new Border
        {
            Height = 16, CornerRadius = new CornerRadius(5), BorderBrush = (System.Windows.Media.Brush)FindResource("NavBorderBrush"),
            BorderThickness = new Thickness(1), ClipToBounds = true, Margin = new Thickness(0, 0, 0, 11), Child = g,
        };
    }

    // A composition row: rarity dot (gold if the resource itself) + ore + its % band.
    private UIElement CompRow(NexusApp.Models.CompositionPart c)
    {
        var fg = (System.Windows.Media.Brush)FindResource("FgBrush");
        var gold = (System.Windows.Media.Brush)FindResource("GoldBrush");
        var monoFont = (System.Windows.Media.FontFamily)System.Windows.Application.Current.FindResource("MonoFont");
        var col = c.IsPrimary ? gold : RarityBrush(RarityOf(c.Ore));

        var g = new Grid { Margin = new Thickness(0, 0, 0, 3) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(2), Background = col, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        left.Children.Add(new TextBlock { Text = c.Ore, FontSize = 12.5, Foreground = c.IsPrimary ? gold : fg, FontWeight = c.IsPrimary ? FontWeights.SemiBold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center });
        g.Children.Add(left);
        var pct = new TextBlock { Text = $"{FmtD(c.MinPct)}-{FmtD(c.MaxPct)}%", FontSize = 12, FontFamily = monoFont, Foreground = c.IsPrimary ? gold : BrushFromHex("#7FE9E0"), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(pct, 1); g.Children.Add(pct);
        return g;
    }

    // Rarity string for another ore by name (for byproduct/composition dot colour); common if unknown.
    private string RarityOf(string ore) =>
        _vm.AllResources.FirstOrDefault(x => x.Name.Equals(ore, StringComparison.OrdinalIgnoreCase))?.Rarity ?? "common";

    private Border ToggleLink(string showText, string hideText, System.Windows.FrameworkElement target)
    {
        var accent = (System.Windows.Media.Brush)FindResource("AccentBrush");
        var tb = new TextBlock { Text = showText + "  ▾", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = accent };
        var btn = new Border
        {
            Child = tb, Padding = new Thickness(12, 5, 12, 5), CornerRadius = new CornerRadius(8),
            BorderBrush = (System.Windows.Media.Brush)FindResource("NavBorderBrush"), BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 8),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        bool expanded = false;
        btn.MouseLeftButtonDown += (s, e) =>
        {
            expanded = !expanded;
            target.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            tb.Text = (expanded ? hideText : showText) + (expanded ? "  ▴" : "  ▾");
        };
        return btn;
    }

    private NexusHologram? _codexHologram;   // the one ambient element in the Codex dossier

    private void ShowResourceDetail(Resource r)
    {
        ReferenceDetailPanel.Children.Clear();
        var rb   = RarityBrush(r.Rarity);
        var dim  = (System.Windows.Media.Brush)FindResource("FgDimBrush");
        var fg   = (System.Windows.Media.Brush)FindResource("FgBrush");
        var gold = (System.Windows.Media.Brush)FindResource("GoldBrush");
        var headFont = (System.Windows.Media.FontFamily)System.Windows.Application.Current.FindResource("HeadFont");
        var monoFont = (System.Windows.Media.FontFamily)System.Windows.Application.Current.FindResource("MonoFont");

        var profile = App.Data.GetMiningProfile(r.Name);

        // hero header
        var hg = new Grid();
        hg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // hologram column, filled in once comp is available below
        var ht = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        // Grid (not StackPanel) so the name column is width-constrained and CharacterEllipsis actually engages;
        // a horizontal StackPanel measures children with infinite width and never trims.
        var nameRow = new Grid();
        nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // class badge column, filled in below when profile is known
        var nameSwatch = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(4), Background = rb, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        Grid.SetColumn(nameSwatch, 0);
        nameRow.Children.Add(nameSwatch);
        var nameText = new TextBlock { Text = r.Name, FontSize = 24, FontWeight = FontWeights.Bold, FontFamily = headFont, Foreground = rb, VerticalAlignment = VerticalAlignment.Center, TextTrimming = System.Windows.TextTrimming.CharacterEllipsis };
        Grid.SetColumn(nameText, 1);
        nameRow.Children.Add(nameText);
        // class badge (Metal / Mineral / Gem) from the datamined resource type
        if (profile != null)
        {
            var clsBrush = profile.Class == "Metal" ? gold : profile.Class == "Mineral" ? BrushFromHex("#7FE9E0") : BrushFromHex("#A855F7");
            var classBadge = new Border
            {
                Child = new TextBlock { Text = profile.Class.ToUpperInvariant(), FontSize = 9, FontWeight = FontWeights.SemiBold, FontFamily = monoFont, Foreground = clsBrush, VerticalAlignment = VerticalAlignment.Center },
                BorderBrush = clsBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(11, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(classBadge, 2);
            nameRow.Children.Add(classBadge);
        }
        ht.Children.Add(nameRow);
        var qFloor = r.Method == "ship" ? "501" : r.Method.Contains("fps") ? "201" : r.Method.Contains("vehicle") ? "~297" : "";
        var metaText = $"{CapFirst(r.Rarity)}  ·  {MethodLabel(r.Method)}" + (qFloor.Length > 0 ? $"  ·  quality floor {qFloor}" : "");
        ht.Children.Add(new TextBlock { Text = metaText, FontSize = 12, Foreground = dim, Margin = new Thickness(0, 4, 0, 0), TextTrimming = System.Windows.TextTrimming.CharacterEllipsis });
        hg.Children.Add(ht);
        var rsStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        rsStack.Children.Add(new TextBlock { Text = "RS VALUE", FontSize = 9, FontWeight = FontWeights.Bold, Foreground = dim, HorizontalAlignment = HorizontalAlignment.Right });
        rsStack.Children.Add(new TextBlock { Text = r.BaseRs > 0 ? $"{r.BaseRs:N0}" : "-", FontSize = 32, FontFamily = monoFont, Foreground = gold, HorizontalAlignment = HorizontalAlignment.Right });
        Grid.SetColumn(rsStack, 1); hg.Children.Add(rsStack);

        // Chamfered HUD hero panel with corner brackets (matches the Blueprint detail hero).
        var hero = Hud.Panel(hg, chamfer: 13, brackets: true,
            bg: (System.Windows.Media.Brush)FindResource("Bg2NavBrush"),
            border: (System.Windows.Media.Brush)FindResource("NavBorderBrush"),
            padding: new Thickness(20, 14, 18, 14));
        hero.Margin = new Thickness(0, 0, 0, 12);
        ReferenceDetailPanel.Children.Add(hero);

        // mining profile - ship-mining minigame parameters (datamined). Gems/refined show a note.
        ReferenceDetailPanel.Children.Add(RefSectionLabel("MINING PROFILE"));
        if (r.Method == "ship" && profile != null && (profile.Instability > 0 || profile.Resistance > 0 || profile.Explosion > 0))
        {
            var mp = new StackPanel();
            mp.Children.Add(ChargeWindow(profile));
            var gauges = new System.Windows.Controls.WrapPanel();
            var inst = profile.Instability;
            gauges.Children.Add(MiningStatCard("INSTABILITY", FmtD(inst), inst >= 700 ? "volatile" : inst >= 300 ? "moderate" : "stable", inst >= 700 ? "hi" : inst >= 300 ? "md" : "lo", inst / 1000 * 100));
            var res = profile.Resistance;
            gauges.Children.Add(MiningStatCard("RESISTANCE", $"{System.Math.Round(res * 100)}%", res >= 0.7 ? "hard" : res >= 0.4 ? "medium" : "soft", res >= 0.7 ? "hi" : res >= 0.4 ? "md" : "lo", res * 100));
            var ex = profile.Explosion;
            gauges.Children.Add(MiningStatCard("EXPLOSION RISK", $"{FmtD(ex)}x", ex >= 160 ? "high" : ex >= 80 ? "med" : "low", ex >= 160 ? "hi" : ex >= 80 ? "md" : "lo", ex / 260 * 100));
            var cl = profile.Cluster;
            gauges.Children.Add(MiningStatCard("CLUSTER DENSITY", $"{System.Math.Round(cl * 100)}%", cl >= 0.5 ? "clustered" : "sparse", cl >= 0.5 ? "lo" : "md", cl * 100));
            mp.Children.Add(gauges);
            ReferenceDetailPanel.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(8), Background = (System.Windows.Media.Brush)FindResource("Bg2NavBrush"),
                BorderBrush = (System.Windows.Media.Brush)FindResource("NavBorderBrush"), BorderThickness = new Thickness(1),
                Padding = new Thickness(14, 13, 14, 6), Margin = new Thickness(0, 0, 0, 4), Child = mp,
            });
        }
        else
        {
            var noteText = r.Method == "refined"
                ? $"{r.Name} is a refined product, not mined directly."
                : profile?.Class == "Gem" || r.Method != "ship"
                    ? $"Hand or vehicle mined. Extracted by {MethodLabel(r.Method)} - no ship-mining charge, instability or fracture mechanics."
                    : $"No ship-mining charge profile. Extracted by {MethodLabel(r.Method)}.";
            ReferenceDetailPanel.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(8), Background = (System.Windows.Media.Brush)FindResource("Bg2NavBrush"),
                BorderBrush = (System.Windows.Media.Brush)FindResource("NavBorderBrush"), BorderThickness = new Thickness(1),
                Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 0, 0, 4),
                Child = new TextBlock { Text = noteText, FontSize = 12.5, Foreground = dim, TextWrapping = System.Windows.TextWrapping.Wrap },
            });
        }

        // deposit composition - what this ore's own rock actually yields (datamined).
        var comp = App.Data.GetCompositionForResource(r.Name);

        // The one ambient element: class crystal + composition ring (design spec 2026-07-09).
        // comp may be empty for non-ship ores - the control renders crystal-only, no special casing.
        _codexHologram?.Stop();
        _codexHologram = new NexusHologram { Width = 120, Height = 120, Margin = new Thickness(0, 0, 4, 0) };
        var pcts = comp.Select(c => (c.MinPct + c.MaxPct) / 2.0).ToList();
        _codexHologram.Show(r.Name, profile?.Class ?? "Metal", pcts);
        Grid.SetColumn(_codexHologram, 2);
        hg.Children.Add(_codexHologram);

        if (comp.Count > 0)
        {
            var primary = comp.FirstOrDefault(c => c.IsPrimary);
            var byproducts = comp.Where(c => !c.IsPrimary).ToList();
            ReferenceDetailPanel.Children.Add(RefSectionLabel($"IF YOU MINE A {r.Name.ToUpperInvariant()} ROCK"));
            var primTxt = primary != null ? $"{FmtD(primary.MinPct)}-{FmtD(primary.MaxPct)}%" : "-";
            ReferenceDetailPanel.Children.Add(new TextBlock
            {
                Text = byproducts.Count > 0
                    ? $"Its own deposit: {r.Name} makes up {primTxt}, plus these byproducts."
                    : $"Its own deposit is effectively pure {r.Name} ({primTxt}).",
                FontSize = 11, Foreground = dim, TextWrapping = System.Windows.TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
            });
            if (byproducts.Count > 0)
            {
                var box = new StackPanel();
                box.Children.Add(CompositionBar(comp));
                foreach (var c in comp) box.Children.Add(CompRow(c));
                ReferenceDetailPanel.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(6), Background = (System.Windows.Media.Brush)FindResource("Bg2NavBrush"),
                    BorderBrush = (System.Windows.Media.Brush)FindResource("NavBorderBrush"), BorderThickness = new Thickness(1),
                    Padding = new Thickness(13, 12, 14, 10), Margin = new Thickness(0, 0, 0, 4), Child = box,
                });
            }
        }

        // found in other deposits - byproduct sourcing (datamined). Only shown when this ore
        // actually appears in another ore's rock; headline-only ores and hand/vehicle gems have none.
        var found = App.Data.GetFoundInForResource(r.Name);
        if (found.Count > 0)
        {
            ReferenceDetailPanel.Children.Add(RefSectionLabel($"FOUND IN OTHER DEPOSITS  ·  {found.Count}"));
            ReferenceDetailPanel.Children.Add(new TextBlock
            {
                Text = $"Other ores whose rock also yields {r.Name} - its share of that rock, and how often the rock carries it.",
                FontSize = 11, Foreground = dim, TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
            });
            const int fShow = 6;
            for (int i = 0; i < System.Math.Min(fShow, found.Count); i++)
                ReferenceDetailPanel.Children.Add(FoundInRow(found[i]));
            if (found.Count > fShow)
            {
                var moreF = new StackPanel { Visibility = Visibility.Collapsed };
                for (int i = fShow; i < found.Count; i++) moreF.Children.Add(FoundInRow(found[i]));
                ReferenceDetailPanel.Children.Add(moreF);
                ReferenceDetailPanel.Children.Add(ToggleLink($"Show all {found.Count}", "Show fewer", moreF));
            }
        }

        // locations - top 6 + show all
        ReferenceDetailPanel.Children.Add(RefSectionLabel($"LOCATIONS  ·  {r.Locations.Count}"));
        if (r.Locations.Count == 0)
            ReferenceDetailPanel.Children.Add(new TextBlock { Text = "None", FontSize = 12, Foreground = dim, Margin = new Thickness(0, 0, 0, 8) });
        else
        {
            const int locShow = 6;
            for (int i = 0; i < System.Math.Min(locShow, r.Locations.Count); i++)
                ReferenceDetailPanel.Children.Add(LocRow(r.Locations[i]));
            if (r.Locations.Count > locShow)
            {
                var moreLoc = new StackPanel { Visibility = Visibility.Collapsed };
                for (int i = locShow; i < r.Locations.Count; i++) moreLoc.Children.Add(LocRow(r.Locations[i]));
                ReferenceDetailPanel.Children.Add(moreLoc);
                ReferenceDetailPanel.Children.Add(ToggleLink($"Show all {r.Locations.Count}", "Show fewer", moreLoc));
            }
        }

        // blueprints - full list
        var bps = App.Data.GetBlueprintsForResource(r.Name);
        ReferenceDetailPanel.Children.Add(RefSectionLabel($"USED IN BLUEPRINTS  ·  {bps.Count}"));
        if (bps.Count == 0)
            ReferenceDetailPanel.Children.Add(new TextBlock { Text = "None", FontSize = 12, Foreground = dim });
        else
        {
            var bpList = bps.OrderBy(b => b.Name).ToList();
            var accentBr = (System.Windows.Media.Brush)FindResource("AccentBrush");
            UIElement BpRow(string nm)
            {
                var tb = new TextBlock { Text = $"▪  {nm}", FontSize = 12, Foreground = fg, Margin = new Thickness(0, 0, 0, 4), TextTrimming = System.Windows.TextTrimming.CharacterEllipsis };
                var b = new Border { Child = tb, Background = System.Windows.Media.Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand, ToolTip = "Open in Blueprint Library" };
                b.MouseEnter += (s, _) => tb.Foreground = accentBr;
                b.MouseLeave += (s, _) => tb.Foreground = fg;
                b.MouseLeftButtonDown += (s, _) => NavigateToBlueprint(nm);
                return b;
            }
            const int bpShow = 8;
            for (int i = 0; i < System.Math.Min(bpShow, bpList.Count); i++)
                ReferenceDetailPanel.Children.Add(BpRow(bpList[i].Name));
            if (bpList.Count > bpShow)
            {
                var moreBp = new StackPanel { Visibility = Visibility.Collapsed };
                for (int i = bpShow; i < bpList.Count; i++) moreBp.Children.Add(BpRow(bpList[i].Name));
                ReferenceDetailPanel.Children.Add(moreBp);
                ReferenceDetailPanel.Children.Add(ToggleLink($"Show all {bpList.Count}", "Show fewer", moreBp));
            }
        }

        // refinery yields - best first, top 5 + show all (moved below blueprints)
        if (r.Refineries.Count > 0)
        {
            ReferenceDetailPanel.Children.Add(RefSectionLabel($"REFINERY YIELDS  ·  {r.Refineries.Count}"));
            var sorted = r.Refineries.OrderByDescending(x => x.ModifierPct).ToList();
            const int show = 5;
            for (int i = 0; i < System.Math.Min(show, sorted.Count); i++)
                ReferenceDetailPanel.Children.Add(YieldRow(sorted[i]));
            if (sorted.Count > show)
            {
                var more = new StackPanel { Visibility = Visibility.Collapsed };
                for (int i = show; i < sorted.Count; i++) more.Children.Add(YieldRow(sorted[i]));
                ReferenceDetailPanel.Children.Add(more);
                ReferenceDetailPanel.Children.Add(ToggleLink($"Show all {sorted.Count}", "Show fewer", more));
            }
        }

        CascadeIn(ReferenceDetailPanel.Children, maxAnimated: 8);
    }

    // Fade + rise the first few children in sequence - the MOBIGLAS dossier reveal.
    // Capped so long dossiers do not animate below the fold.
    private static void CascadeIn(UIElementCollection children, int maxAnimated)
    {
        int n = System.Math.Min(children.Count, maxAnimated);
        for (int i = 0; i < n; i++)
        {
            if (children[i] is not FrameworkElement fe) continue;
            var slide = new System.Windows.Media.TranslateTransform(0, 12);
            fe.RenderTransform = slide;
            fe.Opacity = 0;
            var delay = System.TimeSpan.FromMilliseconds(i * 40);
            var dur = System.TimeSpan.FromMilliseconds(200);
            var ease = new System.Windows.Media.Animation.QuadraticEase
            { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
            var fade = new System.Windows.Media.Animation.DoubleAnimation(0, 1, dur)
            { BeginTime = delay, EasingFunction = ease };
            var rise = new System.Windows.Media.Animation.DoubleAnimation(12, 0, dur)
            { BeginTime = delay, EasingFunction = ease };
            fe.BeginAnimation(UIElement.OpacityProperty, fade);
            slide.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, rise);
        }
    }

    // The Codex list is rebuilt imperatively, so rebuilding on every keystroke is
    // costly. Debounce: rebuild once the user pauses typing (Enter / button = immediate).
    private System.Windows.Threading.DispatcherTimer? _refFilterDebounce;

    private void RefSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        _refFilterDebounce?.Stop();
        BuildReferenceTree();
    }

    private void RefSearch_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) return;   // already handled on key down
        if (_refFilterDebounce == null)
        {
            _refFilterDebounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
            _refFilterDebounce.Tick += (_, __) => { _refFilterDebounce!.Stop(); BuildReferenceTree(); };
        }
        _refFilterDebounce.Stop();
        _refFilterDebounce.Start();
    }

    private void RefSearch_Click(object sender, RoutedEventArgs e) { _refFilterDebounce?.Stop(); BuildReferenceTree(); }

    private System.Windows.FrameworkElement BuildResourceHeader(Resource r)
    {
        var grid = new Grid { Margin = new Thickness(12, 7, 12, 7) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });

        var nameStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        nameStack.Children.Add(new TextBlock
        {
            Text = r.Name, FontSize = 13, FontWeight = FontWeights.SemiBold,
            FontFamily = (System.Windows.Media.FontFamily)System.Windows.Application.Current.FindResource("HeadFont"),
            Foreground = RarityBrush(r.Rarity), VerticalAlignment = VerticalAlignment.Center,
        });
        if (r.IsPinned)
            nameStack.Children.Add(new TextBlock { Text = "★", Foreground = AccentBrush(), FontSize = 11, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        grid.Children.Add(nameStack);

        var sig = new TextBlock
        {
            Text = r.BaseRs > 0 ? $"RS {r.BaseRs:N0}" : "-", FontSize = 12,
            FontFamily = (System.Windows.Media.FontFamily)FindResource("MonoFont"),
            Foreground = r.BaseRs > 0 ? TierBrush(r.Tier) : (System.Windows.Media.Brush)FindResource("FgDimBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(sig, 1); grid.Children.Add(sig);

        var meth = new TextBlock
        {
            Text = MethodLabel(r.Method), FontSize = 11,
            Foreground = (System.Windows.Media.Brush)FindResource("FgDimBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(meth, 2); grid.Children.Add(meth);

        return new Border
        {
            Background = (System.Windows.Media.Brush)FindResource("Bg2NavBrush"),
            BorderBrush = (System.Windows.Media.Brush)FindResource("NavBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Margin = new Thickness(0, 3, 6, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = grid,
        };
    }

    // ── Work Orders ──────────────────────────────────────────────────────────

    private System.Windows.Threading.DispatcherTimer? _listTicker;
    private readonly Dictionary<string, TextBlock?> _rowLiveRefs = new();

    private void NewWorkOrder_Click(object sender, RoutedEventArgs e) => OpenWorkOrderEditor(new WorkOrder());

    // Add/edit now happens in a modal popup (the page is a card gallery). The editor's Save/Delete run the
    // VM commands, which raise CollectionChanged -> RebuildWorkOrderList, so the gallery refreshes itself.
    private void OpenWorkOrderEditor(WorkOrder wo)
        => new WorkOrderEditorDialog(wo, _vm, this).ShowDialog();

    private void RebuildWorkOrderList()
    {
        _rowLiveRefs.Clear();
        WorkOrderGallery.Children.Clear();

        var orders = _vm.WorkOrders;
        if (orders.Count == 0)
        {
            WorkOrderGallery.Children.Add(EmptyWorkOrders());
        }
        else
        {
            var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 };
            foreach (var wo in orders)
                grid.Children.Add(BuildWorkOrderCard(wo));
            grid.Children.Add(AddWorkOrderTile());
            WorkOrderGallery.Children.Add(grid);
        }

        var hasTimers = orders.Any(w => w.HasActiveTimer);
        if (hasTimers && _listTicker == null)
        {
            _listTicker = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _listTicker.Tick += ListTicker_Tick;
            _listTicker.Start();
        }
        else if (!hasTimers)
        {
            _listTicker?.Stop();
            _listTicker = null;
        }
    }

    private void ListTicker_Tick(object? sender, EventArgs e)
    {
        bool anyActive = false;
        foreach (var wo in _vm.WorkOrders)
        {
            if (!_rowLiveRefs.TryGetValue(wo.Id, out var timer)) continue;
            if (!wo.HasActiveTimer) continue;
            anyActive = true;
            if (timer != null) timer.Text = string.IsNullOrEmpty(wo.TimerRemainingShort) ? "-" : wo.TimerRemainingShort;
        }
        if (!anyActive)
        {
            _listTicker?.Stop();
            _listTicker = null;
        }
    }

    // One self-contained work-order card (the mock's .wo): name + 3 meta fields + status chip, with an
    // always-present progress bar and a big timer on the footer row. Clicking the card opens the popup editor.
    private FrameworkElement BuildWorkOrderCard(WorkOrder wo)
    {
        var bg2      = (System.Windows.Media.Brush)FindResource("Bg2NavBrush");
        var highlight= (System.Windows.Media.Brush)FindResource("HighlightBrush");
        var accent   = (System.Windows.Media.Brush)FindResource("AccentBrush");
        var fgBrush  = (System.Windows.Media.Brush)FindResource("FgBrush");
        var dimBrush = (System.Windows.Media.Brush)FindResource("FgDimBrush");
        var headFont = (System.Windows.Media.FontFamily)FindResource("HeadFont");
        var dispFont = (System.Windows.Media.FontFamily)FindResource("DisplayFont");
        var monoFont = (System.Windows.Media.FontFamily)FindResource("MonoFont");

        var inner = new StackPanel();

        // top row: name + meta (left) | status chip + delete (right)
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel();
        left.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(wo.Label) ? (string.IsNullOrWhiteSpace(wo.Resources) ? "Work order" : wo.Resources) : wo.Label,
            FontFamily = dispFont, FontSize = 16, FontWeight = FontWeights.Bold, Foreground = fgBrush,
            TextTrimming = System.Windows.TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 6),
        });
        var meta = new WrapPanel { Orientation = Orientation.Horizontal };
        void MetaField(string label, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var tb = new TextBlock { Margin = new Thickness(0, 0, 14, 2), FontFamily = headFont, FontSize = 11, TextTrimming = System.Windows.TextTrimming.CharacterEllipsis, MaxWidth = 220 };
            tb.Inlines.Add(new System.Windows.Documents.Run(label + " ") { Foreground = dimBrush });
            tb.Inlines.Add(new System.Windows.Documents.Run(value) { Foreground = fgBrush, FontWeight = FontWeights.SemiBold });
            meta.Children.Add(tb);
        }
        MetaField("Resources", wo.Resources);
        MetaField("Mined at", wo.Location);
        MetaField("Refinery", wo.Refinery);
        left.Children.Add(meta);
        Grid.SetColumn(left, 0); top.Children.Add(left);

        var rightTop = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        var chip = Hud.StatusChip(wo.Status);
        chip.VerticalAlignment = VerticalAlignment.Center;
        rightTop.Children.Add(chip);
        var deleteTb = new TextBlock { Text = "x", FontFamily = monoFont, FontSize = 13, FontWeight = FontWeights.Bold, Foreground = dimBrush, VerticalAlignment = VerticalAlignment.Center };
        var deleteBtn = new Border { Child = deleteTb, Padding = new Thickness(10, 0, 2, 0), Cursor = System.Windows.Input.Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Delete" };
        deleteBtn.MouseEnter += (s, _) => deleteTb.Foreground = BrushFromHex("#EF4444");
        deleteBtn.MouseLeave += (s, _) => deleteTb.Foreground = dimBrush;
        rightTop.Children.Add(deleteBtn);
        Grid.SetColumn(rightTop, 1); top.Children.Add(rightTop);
        inner.Children.Add(top);

        // footer row: always-on progress bar (flex) + big timer text
        var footer = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var bar = BuildWorkOrderBar(wo);
        ((FrameworkElement)bar).VerticalAlignment = VerticalAlignment.Center;   // every BuildWorkOrderBar path returns a Grid
        Grid.SetColumn(bar, 0); footer.Children.Add(bar);

        string timerText; System.Windows.Media.Brush timerBrush;
        if (wo.Status == WorkOrderStatus.ReadyToCollect) { timerText = "ready"; timerBrush = BrushFromHex("#66E6A6"); }
        else if (wo.Status == WorkOrderStatus.Complete)  { timerText = "done";  timerBrush = dimBrush; }
        else if (wo.HasActiveTimer && !string.IsNullOrEmpty(wo.TimerRemainingShort)) { timerText = wo.TimerRemainingShort; timerBrush = accent; }
        else { timerText = "-"; timerBrush = dimBrush; }
        var timerTb = new TextBlock { Text = timerText, FontFamily = dispFont, FontSize = 18, FontWeight = FontWeights.Bold, Foreground = timerBrush, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(timerTb, 1); footer.Children.Add(timerTb);
        inner.Children.Add(footer);

        _rowLiveRefs[wo.Id] = timerTb;

        // Chamfered card with persistent corner brackets (the mock shows them always); hover highlight; click -> editor.
        var host = Hud.CardFrame(inner, out var frame, out var brackets, chamfer: 12, padding: new Thickness(15, 13, 14, 14));
        brackets.Visibility = Visibility.Visible;
        host.Margin = new Thickness(7, 7, 7, 7);
        host.Cursor = System.Windows.Input.Cursors.Hand;

        if (wo.Status == WorkOrderStatus.ReadyToCollect)
            frame.Stroke = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x59, 0x66, 0xE6, 0xA6));   // green-tinted border
        if (wo.Status == WorkOrderStatus.Complete)
            host.Opacity = 0.72;

        host.MouseEnter += (s, e) => { if (wo.Status != WorkOrderStatus.Complete) frame.Fill = highlight; };
        host.MouseLeave += (s, e) => frame.Fill = bg2;
        host.MouseLeftButtonDown += (s, e) => OpenWorkOrderEditor(wo);

        deleteBtn.MouseLeftButtonDown += (s, e) =>
        {
            e.Handled = true;
            _vm.DeleteWorkOrderCommand.Execute(wo.Id);   // CollectionChanged -> RebuildWorkOrderList
        };

        return host;
    }

    // Always-present progress bar: an animated gradient fill for an active timer, else a static status bar
    // (green 100% ready, flat gray 100% complete, faint otherwise) matching the mock's per-state treatments.
    private UIElement BuildWorkOrderBar(WorkOrder wo)
    {
        if (wo.HasActiveTimer)
        {
            var sc = BrushFromHex(wo.StatusColorHex).Color;
            var grid = new Grid { Height = 6 };
            grid.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x1C, sc.R, sc.G, sc.B)) });
            var scale = new System.Windows.Media.ScaleTransform(System.Math.Clamp(wo.TimerFraction, 0, 1), 1);
            var fill = new Border
            {
                CornerRadius = new CornerRadius(3),
                Background = new System.Windows.Media.LinearGradientBrush(System.Windows.Media.Color.FromArgb(0xCC, sc.R, sc.G, sc.B), sc, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                RenderTransform = scale, RenderTransformOrigin = new System.Windows.Point(0, 0.5),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = sc, BlurRadius = 8, ShadowDepth = 0, Opacity = 0.55 },
            };
            grid.Children.Add(fill);
            var remaining = wo.TimerEnd!.Value - DateTime.UtcNow;
            if (remaining > TimeSpan.Zero)
                scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty,
                    new System.Windows.Media.Animation.DoubleAnimation(System.Math.Clamp(wo.TimerFraction, 0, 1), 1.0, remaining) { FillBehavior = System.Windows.Media.Animation.FillBehavior.HoldEnd });
            return grid;
        }

        return wo.Status switch
        {
            WorkOrderStatus.ReadyToCollect => Hud.StateBar(1, Hud.BarState.Green, 6),
            WorkOrderStatus.Complete       => CompleteBar(),
            WorkOrderStatus.Mining         => Hud.StateBar(System.Math.Clamp(wo.TimerFraction, 0, 1), Hud.BarState.Blue, 6),
            _                              => Hud.StateBar(System.Math.Clamp(wo.TimerFraction, 0, 1), Hud.BarState.Amber, 6),
        };
    }

    // Completed order: a flat gray 100% bar with no glow (the mock dims completed cards).
    private UIElement CompleteBar()
    {
        var gray = System.Windows.Media.Color.FromRgb(0x7F, 0x8C, 0x8D);
        var g = new Grid { Height = 6 };
        g.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x1C, gray.R, gray.G, gray.B)) });
        g.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = new System.Windows.Media.SolidColorBrush(gray), HorizontalAlignment = HorizontalAlignment.Stretch });
        return g;
    }

    // The mock's dashed "+ Add work order" tile, as the last cell of the gallery (and the empty state).
    private FrameworkElement AddWorkOrderTile()
    {
        var dim = (System.Windows.Media.Brush)FindResource("FgDimBrush");
        var navBorder = (System.Windows.Media.Brush)FindResource("NavBorderBrush");
        var accent = (System.Windows.Media.Brush)FindResource("AccentBrush");

        var plus = new TextBlock { Text = "+", FontFamily = (System.Windows.Media.FontFamily)FindResource("DisplayFont"), FontSize = 26, FontWeight = FontWeights.Bold, Foreground = dim, HorizontalAlignment = HorizontalAlignment.Center };
        var label = new TextBlock { Text = "ADD WORK ORDER", FontFamily = (System.Windows.Media.FontFamily)FindResource("HeadFont"), FontSize = 11, FontWeight = FontWeights.Bold, Foreground = dim, Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(plus); stack.Children.Add(label);

        var dash = new System.Windows.Shapes.Rectangle
        {
            Stroke = navBorder, StrokeThickness = 1.2,
            StrokeDashArray = new System.Windows.Media.DoubleCollection { 4, 3 }, RadiusX = 3, RadiusY = 3,
        };
        var grid = new Grid { MinHeight = 118, Margin = new Thickness(7, 7, 7, 7), Cursor = System.Windows.Input.Cursors.Hand, Background = System.Windows.Media.Brushes.Transparent };
        grid.Children.Add(dash);
        grid.Children.Add(stack);
        grid.MouseEnter += (s, e) => { plus.Foreground = accent; dash.Stroke = accent; };
        grid.MouseLeave += (s, e) => { plus.Foreground = dim; dash.Stroke = navBorder; };
        grid.MouseLeftButtonDown += (s, e) => OpenWorkOrderEditor(new WorkOrder());
        return grid;
    }

    // Empty state: the ambient scan glyph + a hint + a single add tile.
    private FrameworkElement EmptyWorkOrders()
    {
        var dim = (System.Windows.Media.Brush)FindResource("FgDimBrush");
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 40, 0, 0) };
        var glyph = Hud.AmbientGlyph(Hud.Ambient.OreConveyor, 96);
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        glyph.Margin = new Thickness(0, 0, 0, 18);
        stack.Children.Add(glyph);
        stack.Children.Add(new TextBlock
        {
            Text = "No work orders yet. Add one to track refine timers.",
            Foreground = dim, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 0, 0, 16),
        });
        var tile = AddWorkOrderTile();
        tile.Width = 260;
        tile.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(tile);
        return stack;
    }

    // ── Blueprints ───────────────────────────────────────────────────────────

    // ── Drill-down browse (Category → Subcategory → blueprint) ──────────────────
    private bool _bpInit;
    private List<NexusApp.Models.Blueprint>? _allBlueprints;
    private string _bpLevel = "root";   // root | category | subgroup | family | search
    private string _bpCat = "";
    private string _bpSub = "";          // real subcategory or armor piece ("" = none)
    private string _bpFam = "";          // variant family
    private List<NexusApp.Models.Blueprint> _bpSearchResults = new();
    private FrameworkElement? _selectedBpRow;
    private Action? _deselectBpRow;   // resets the currently-selected blueprint row's chamfer visuals
    private enum BpOwnFilter { All, Owned, NotOwned }
    private BpOwnFilter _bpOwnFilter = BpOwnFilter.All;
    private string? _detailBpName;                 // blueprint currently shown in the detail panel
    private Border? _detailOwnedToggle;            // its "Owned" toggle, kept in sync with nav checkboxes
    // Maps a blueprint name to its nav-row toggle pill so a single toggle updates that
    // one row in place instead of rebuilding the whole list (the source of the lag).
    // Maps a blueprint name to a callback that refreshes that nav row's ownership
    // visuals (left strip, ✓ tick, hover pill) in place - so one toggle updates the
    // row without rebuilding the whole list.
    private readonly Dictionary<string, Action<bool>> _bpRowOwned = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string[] _bpCategories = ["Armor", "Weapons", "Ship Components", "Ammo"];
    private static readonly string[] _armorPieces = ["Helmet", "Core", "Arms", "Legs", "Backpack", "Undersuit", "Suit"];
    private static readonly HashSet<string> _variantWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "black","blue","green","red","grey","gray","white","dark","aqua","crusader","edition","woodland",
        "desert","tan","olive","sand","orange","yellow","purple","pink","brown","navy","teal","crimson",
        "forest","storm","snow","arctic","modified","light","silver","gold","bronze","maroon","khaki",
        "digital","urban","jungle","midnight","obsidian","frost","ember","rust","slate","charcoal","ivory",
        "copper","azure","emerald","ruby","onyx","steel","carbon","ash","coal","mint","lime","rose","plum",
        "cobalt","sage","clay","stone","smoke","blood","ghost","shadow","night","solar","lunar","nova",
    };

    private void InitBlueprintBrowse()
    {
        if (_bpInit) return;
        _bpInit = true;
        _allBlueprints = App.Data.GetAllBlueprints();
        UpdateOwnedChips();
        UpdateOwnedCount();
        GoRoot();
    }

    // ── Ownership filter chips + count ──────────────────────────────────────────
    private void BpChipAll_Click(object sender, MouseButtonEventArgs e)
    {
        InteractionLog.Click("filter: All", (DependencyObject)sender);
        SetBpOwnFilter(BpOwnFilter.All);
    }

    private void BpChipOwned_Click(object sender, MouseButtonEventArgs e)
    {
        InteractionLog.Click("filter: Owned", (DependencyObject)sender);
        SetBpOwnFilter(BpOwnFilter.Owned);
    }

    private void BpChipNotOwned_Click(object sender, MouseButtonEventArgs e)
    {
        InteractionLog.Click("filter: Not owned", (DependencyObject)sender);
        SetBpOwnFilter(BpOwnFilter.NotOwned);
    }

    private void SetBpOwnFilter(BpOwnFilter filter)
    {
        _bpOwnFilter = filter;
        UpdateOwnedChips();
        // A filter is a lens, not a mode switch: stay where the user is (browse level
        // OR search results) and just re-filter the current level in place.
        RenderBlueprintNav();
        ShowBlueprintLanding();
    }

    private void UpdateOwnedChips()
    {
        StyleChip(BpChipAll, BpChipAllText, _bpOwnFilter == BpOwnFilter.All);
        StyleChip(BpChipOwned, BpChipOwnedText, _bpOwnFilter == BpOwnFilter.Owned);
        StyleChip(BpChipNotOwned, BpChipNotOwnedText, _bpOwnFilter == BpOwnFilter.NotOwned);
    }

    private void StyleChip(Border chip, TextBlock label, bool active)
    {
        var accent = (System.Windows.Media.Brush)FindResource("AccentBrush");
        if (active)
        {
            chip.Background = accent;
            chip.BorderBrush = accent;
            label.Foreground = (System.Windows.Media.Brush)FindResource("OnAccentBrush");
            label.FontWeight = FontWeights.SemiBold;
        }
        else
        {
            chip.Background = System.Windows.Media.Brushes.Transparent;
            chip.BorderBrush = (System.Windows.Media.Brush)FindResource("NavBorderBrush");
            label.Foreground = (System.Windows.Media.Brush)FindResource("FgDimBrush");
            label.FontWeight = FontWeights.Normal;
        }
    }

    private void UpdateOwnedCount()
    {
        var n = App.Settings.OwnedBlueprintCount;
        BpOwnedCount.Text = n == 1 ? "1 owned" : $"{n} owned";
    }

    private int CatCount(string cat) => _allBlueprints?.Count(b => b.Category == cat && MatchesOwnFilter(b)) ?? 0;

    // True when a blueprint should appear under the active ownership filter. The
    // filter constrains every level of the drill-down (the chips show which is on).
    private bool MatchesOwnFilter(NexusApp.Models.Blueprint b) => _bpOwnFilter switch
    {
        BpOwnFilter.Owned    => App.Settings.IsBlueprintOwned(b.Name),
        BpOwnFilter.NotOwned => !App.Settings.IsBlueprintOwned(b.Name),
        _                    => true,
    };

    // ── Cross-navigation ───────────────────────────────────────────────────────
    private void NavigateToBlueprint(string name)
    {
        SetActivePage("blueprints");           // triggers InitBlueprintBrowse on first visit
        var bp = _allBlueprints?.FirstOrDefault(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (bp == null) return;
        _bpSearchResults = App.Data.SearchBlueprints(name);
        ClearOwnFilter();
        _bpLevel = "search";
        RenderBlueprintNav();
        ShowBlueprintDetail(bp);
    }

    // Searching/cross-navigating takes over the nav, so the ownership filter is
    // cleared to keep the chips and the displayed list in agreement.
    private void ClearOwnFilter()
    {
        if (_bpOwnFilter == BpOwnFilter.All) return;
        _bpOwnFilter = BpOwnFilter.All;
        UpdateOwnedChips();
    }

    private void NavigateToResource(string name)
    {
        var res = _vm.AllResources.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (res == null) return;
        InteractionLog.Nav($"Mining Codex: open {name}");
        _vm.RefFilter = "";
        _systemFilter.Clear();
        _methodFilter.Clear();
        SetActivePage("reference");            // rebuilds filter pills + reference list
        foreach (var child in ReferenceList.Children)
        {
            if (child is FrameworkElement fe && fe.Tag is Resource cr && cr.Name == res.Name)
            {
                fe.BringIntoView();
                break;
            }
        }
        if (_refSelectByName.TryGetValue(res.Name, out var sel)) sel();   // selects the card + shows its detail
        else ShowResourceDetail(res);
    }

    private void GoRoot()
    {
        InteractionLog.Nav("Blueprint Library: Browse (root)");
        _bpLevel = "root"; _bpCat = ""; _bpSub = "";
        // Leaving search: drop the search term and its clear button so the box reflects browse mode.
        _vm.BlueprintSearch = "";
        BlueprintSearchClear.Visibility = Visibility.Collapsed;
        RenderBlueprintNav();
        ShowBlueprintLanding();
    }

    private void EnterCategory(string cat)
    {
        InteractionLog.Nav($"Blueprint Library: category {cat}");
        _bpCat = cat; _bpSub = ""; _bpLevel = "category";
        RenderBlueprintNav();
        ShowBlueprintLanding();
    }

    // subgroup = real subcategory, or armor piece, or null (no grouping level)
    private string? Subgroup(NexusApp.Models.Blueprint b)
    {
        if (!string.IsNullOrEmpty(b.SubCategory)) return b.SubCategory;
        if (b.Category == "Armor") return ArmorPiece(b.Name);
        return null;
    }

    private static string ArmorPiece(string name)
    {
        foreach (var p in _armorPieces)
            if (System.Text.RegularExpressions.Regex.IsMatch(name, $"\\b{p}\\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                return p;
        return "Other";
    }

    // Drops quoted skins + parentheticals and collapses whitespace, leaving the bare model words.
    private static string StripDecorations(string name)
    {
        var s = System.Text.RegularExpressions.Regex.Replace(name, "\"[^\"]*\"", "");
        s = System.Text.RegularExpressions.Regex.Replace(s, "\\([^)]*\\)", "");
        return System.Text.RegularExpressions.Regex.Replace(s, "\\s+", " ").Trim();
    }

    // family = name with quoted skins / parentheticals / trailing colour words removed (collapses variants)
    private static string FamilyKey(string name)
    {
        var s = StripDecorations(name);
        var parts = s.Split(' ').ToList();
        while (parts.Count > 0 && _variantWords.Contains(parts[^1])) parts.RemoveAt(parts.Count - 1);
        return parts.Count > 0 ? string.Join(" ", parts) : (s.Length > 0 ? s : name);
    }

    // Family key used for grouping variants together. Weapon/ship skins are quoted
    // or parenthesised, so the colour-list FamilyKey handles them. Armor skins are
    // free-text words trailing the piece ("Antium Helmet Moss Camo") that a fixed
    // colour list can't catch - so for armor we keep everything up to and including
    // the piece word and drop the rest, collapsing all of a model's skins into one.
    private static string FamilyKeyOf(NexusApp.Models.Blueprint b)
        => b.Category == "Armor" ? ArmorFamilyKey(b.Name) : FamilyKey(b.Name);

    private static string ArmorFamilyKey(string name)
    {
        var piece = ArmorPiece(name);
        if (piece != "Other")
        {
            var parts = StripDecorations(name).Split(' ');
            for (int i = 0; i < parts.Length; i++)
                if (string.Equals(parts[i], piece, StringComparison.OrdinalIgnoreCase))
                    return string.Join(" ", parts.Take(i + 1));
        }
        return FamilyKey(name);   // piece word not found as a standalone token; fall back
    }

    private void RenderBlueprintNav()
    {
        BlueprintNavPanel.Children.Clear();
        BlueprintCrumbHost.Content = null;
        _deselectBpRow = null;
        _selectedBpRow = null;
        _bpRowOwned.Clear();
        if (_allBlueprints == null) return;
        var accent = (System.Windows.Media.Brush)FindResource("AccentBrush");
        var catCol = CategoryBrush(_bpCat);

        // The ownership filter constrains the drill-down at every level (the chips
        // above show which filter is active); the breadcrumb path is identical to
        // the All view. Drilling never realizes the whole catalog at once, so no
        // render cap is needed.
        var src = _allBlueprints.Where(MatchesOwnFilter);

        switch (_bpLevel)
        {
            case "category":
            {
                BlueprintCrumbHost.Content = Breadcrumb(catCol, ("Browse", GoRoot), (_bpCat, (Action?)null));
                BlueprintNavPanel.Children.Add(NavHeader(_bpCat, CatCount(_bpCat), catCol));
                var inCat = src.Where(b => b.Category == _bpCat).ToList();
                if (inCat.Count == 0) { BlueprintNavPanel.Children.Add(NavEmptyNote()); break; }
                var groups = inCat.Where(b => Subgroup(b) != null)
                    .GroupBy(b => Subgroup(b)!).OrderBy(g => g.Key).ToList();
                if (groups.Count > 0)
                {
                    foreach (var grp in groups)
                    {
                        var sub = grp.Key;
                        BlueprintNavPanel.Children.Add(DrillRow(sub, grp.Count(), catCol, () => EnterSubgroup(sub)));
                    }
                    RenderLeafGroup(inCat.Where(b => Subgroup(b) == null), catCol);
                }
                else
                {
                    RenderLeafGroup(inCat, catCol);
                }
                break;
            }

            case "subgroup":
            {
                BlueprintCrumbHost.Content = Breadcrumb(catCol, ("Browse", GoRoot), (_bpCat, () => EnterCategory(_bpCat)), (_bpSub, (Action?)null));
                var items = src.Where(b => b.Category == _bpCat && Subgroup(b) == _bpSub).ToList();
                BlueprintNavPanel.Children.Add(NavHeader(_bpSub, items.Count, catCol));
                if (items.Count == 0) BlueprintNavPanel.Children.Add(NavEmptyNote());
                else RenderLeafGroup(items, catCol);
                break;
            }

            case "family":
            {
                var famCrumbs = new System.Collections.Generic.List<(string, Action?)> { ("Browse", GoRoot), (_bpCat, () => EnterCategory(_bpCat)) };
                if (_bpSub.Length > 0) famCrumbs.Add((_bpSub, () => EnterSubgroup(_bpSub)));
                famCrumbs.Add((_bpFam, (Action?)null));
                BlueprintCrumbHost.Content = Breadcrumb(catCol, famCrumbs.ToArray());
                var variants = src
                    .Where(b => b.Category == _bpCat && (_bpSub.Length == 0 ? Subgroup(b) == null : Subgroup(b) == _bpSub) && FamilyKeyOf(b) == _bpFam)
                    .OrderBy(b => b.Name).ToList();
                BlueprintNavPanel.Children.Add(NavHeader(_bpFam, variants.Count, catCol));
                if (variants.Count == 0) BlueprintNavPanel.Children.Add(NavEmptyNote());
                foreach (var bp in variants)
                    BlueprintNavPanel.Children.Add(BlueprintRow(bp, false));
                break;
            }

            case "search":
            {
                BlueprintCrumbHost.Content = Breadcrumb(accent, ("Browse", GoRoot), ("Results", (Action?)null));
                // Search respects the ownership pill: filter the raw matches by the active lens.
                var results = _bpSearchResults.Where(MatchesOwnFilter).ToList();
                BlueprintNavPanel.Children.Add(NavHeader("Results", results.Count, accent));
                if (results.Count == 0)
                {
                    // Distinguish "found nothing" from "the pill filtered everything out".
                    var empty = _bpSearchResults.Count > 0 && _bpOwnFilter != BpOwnFilter.All
                        ? (_bpOwnFilter == BpOwnFilter.Owned ? "No owned matches" : "No not-owned matches")
                        : "No matches";
                    BlueprintNavPanel.Children.Add(new TextBlock { Text = empty, FontSize = 12, Foreground = (System.Windows.Media.Brush)FindResource("FgDimBrush"), Margin = new Thickness(6, 8, 0, 0) });
                }
                foreach (var bp in results)
                    BlueprintNavPanel.Children.Add(BlueprintRow(bp, true));
                break;
            }

            default: // root
            {
                var cards = 0;
                foreach (var cat in _bpCategories)
                {
                    var c = CatCount(cat);
                    if (_bpOwnFilter != BpOwnFilter.All && c == 0) continue;   // hide empties when filtered
                    BlueprintNavPanel.Children.Add(CategoryCard(cat, c));
                    cards++;
                }
                if (cards == 0)
                    BlueprintNavPanel.Children.Add(NavEmptyNote(
                        _bpOwnFilter == BpOwnFilter.Owned ? "Nothing marked owned yet" : "Every blueprint is marked owned"));
                break;
            }
        }
    }

    private TextBlock NavEmptyNote(string text = "Nothing here in this filter") => new()
    {
        Text = text, FontSize = 12, Foreground = (System.Windows.Media.Brush)FindResource("FgDimBrush"),
        Margin = new Thickness(6, 8, 0, 0), TextWrapping = TextWrapping.Wrap,
    };

    // within a leaf set: families with >1 variant become drill rows; singles become blueprint rows
    private void RenderLeafGroup(System.Collections.Generic.IEnumerable<NexusApp.Models.Blueprint> items, System.Windows.Media.Brush col)
    {
        var fams = items.GroupBy(FamilyKeyOf).OrderBy(g => g.Key).ToList();
        foreach (var fam in fams)
        {
            if (fam.Count() > 1)
            {
                var key = fam.Key;
                BlueprintNavPanel.Children.Add(DrillRow(key, fam.Count(), col, () => EnterFamily(key)));
            }
            else
            {
                BlueprintNavPanel.Children.Add(BlueprintRow(fam.First(), false));
            }
        }
    }

    private void EnterSubgroup(string sub)
    {
        InteractionLog.Nav($"Blueprint Library: subgroup {sub}");
        _bpSub = sub; _bpFam = ""; _bpLevel = "subgroup";
        RenderBlueprintNav();
        ShowBlueprintLanding();
    }

    private void EnterFamily(string fam)
    {
        InteractionLog.Nav($"Blueprint Library: family {fam}");
        _bpFam = fam; _bpLevel = "family";
        RenderBlueprintNav();
        ShowBlueprintLanding();
    }

    // Clickable breadcrumb trail for the drill-down. Non-final segments navigate to
    // that level; the final segment is the current location in the category colour.
    private UIElement Breadcrumb(System.Windows.Media.Brush currentCol, params (string Label, Action? OnClick)[] segs)
    {
        var mono   = (System.Windows.Media.FontFamily)FindResource("MonoFont");
        var dim    = (System.Windows.Media.Brush)FindResource("FgDimBrush");
        var accent = (System.Windows.Media.Brush)FindResource("AccentBrush");
        var panel  = new System.Windows.Controls.WrapPanel { Margin = new Thickness(6, 4, 6, 8) };
        for (int i = 0; i < segs.Length; i++)
        {
            var seg = segs[i];
            bool isLast = i == segs.Length - 1;
            var tb = new TextBlock { Text = seg.Label, FontFamily = mono, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            if (isLast)
            {
                tb.Foreground = currentCol; tb.FontWeight = FontWeights.SemiBold;
            }
            else
            {
                tb.Foreground = dim;
                if (seg.OnClick is { } onClick)
                {
                    tb.Cursor = System.Windows.Input.Cursors.Hand;
                    tb.MouseEnter += (s, _) => tb.Foreground = accent;
                    tb.MouseLeave += (s, _) => tb.Foreground = dim;
                    tb.MouseLeftButtonDown += (s, _) => onClick();
                }
            }
            panel.Children.Add(tb);
            if (!isLast)
                panel.Children.Add(new TextBlock { Text = "  ›  ", FontFamily = mono, FontSize = 11, Foreground = dim, VerticalAlignment = VerticalAlignment.Center });
        }
        return panel;
    }

    private UIElement NavHeader(string text, int count, System.Windows.Media.Brush col)
    {
        var headFont = (System.Windows.Media.FontFamily)FindResource("HeadFont");
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 2, 0, 8) };
        sp.Children.Add(new TextBlock { Text = text, FontFamily = headFont, FontSize = 16, Foreground = col, VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(new TextBlock { Text = $"  ·  {count}", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = (System.Windows.Media.Brush)FindResource("FgDimBrush"), VerticalAlignment = VerticalAlignment.Center });
        return sp;
    }

    private FrameworkElement CategoryCard(string cat, int count)
    {
        var col       = CategoryBrush(cat);
        var fg        = (System.Windows.Media.Brush)FindResource("FgBrush");
        var dim       = (System.Windows.Media.Brush)FindResource("FgDimBrush");
        var bg2       = (System.Windows.Media.Brush)FindResource("Bg2NavBrush");
        var highlight = (System.Windows.Media.Brush)FindResource("HighlightBrush");
        var headFont  = (System.Windows.Media.FontFamily)FindResource("HeadFont");

        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new TextBlock { Text = cat, FontFamily = headFont, FontSize = 15, Foreground = fg });
        stack.Children.Add(new TextBlock { Text = "blueprints", FontSize = 9, Foreground = dim, Margin = new Thickness(0, 2, 0, 0) });
        g.Children.Add(stack);

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(new TextBlock { Text = count.ToString(), FontFamily = headFont, FontSize = 18, Foreground = col, VerticalAlignment = VerticalAlignment.Center });
        right.Children.Add(new TextBlock { Text = "  ›", FontSize = 15, Foreground = dim, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(right, 1); g.Children.Add(right);

        var host = Hud.CardFrame(g, out var frame, out _, chamfer: 11, padding: new Thickness(16, 12, 14, 12));
        host.Margin = new Thickness(0, 0, 0, 8);
        host.Cursor = System.Windows.Input.Cursors.Hand;
        host.MouseEnter += (_, __) => frame.Fill = highlight;
        host.MouseLeave += (_, __) => frame.Fill = bg2;
        host.MouseLeftButtonDown += (_, __) => EnterCategory(cat);
        return host;
    }

    private FrameworkElement DrillRow(string label, int count, System.Windows.Media.Brush col, Action onClick)
    {
        var fg        = (System.Windows.Media.Brush)FindResource("FgBrush");
        var dim       = (System.Windows.Media.Brush)FindResource("FgDimBrush");
        var bg2       = (System.Windows.Media.Brush)FindResource("Bg2NavBrush");
        var highlight = (System.Windows.Media.Brush)FindResource("HighlightBrush");
        var headFont  = (System.Windows.Media.FontFamily)FindResource("HeadFont");

        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = new TextBlock { Text = label, FontFamily = headFont, FontSize = 12, Foreground = fg, VerticalAlignment = VerticalAlignment.Center, TextTrimming = System.Windows.TextTrimming.CharacterEllipsis };
        g.Children.Add(name);

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(new TextBlock { Text = count.ToString(), FontSize = 11, FontWeight = FontWeights.Bold, Foreground = col, VerticalAlignment = VerticalAlignment.Center });
        right.Children.Add(new TextBlock { Text = "  ›", FontSize = 13, Foreground = dim, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(right, 1); g.Children.Add(right);

        var host = Hud.CardFrame(g, out var frame, out _, chamfer: 9, padding: new Thickness(14, 9, 12, 9));
        host.Margin = new Thickness(0, 0, 0, 6);
        host.Cursor = System.Windows.Input.Cursors.Hand;
        host.MouseEnter += (_, __) => frame.Fill = highlight;
        host.MouseLeave += (_, __) => frame.Fill = bg2;
        host.MouseLeftButtonDown += (_, __) => onClick();
        return host;
    }

    private FrameworkElement BlueprintRow(NexusApp.Models.Blueprint bp, bool showCategory)
    {
        var fg        = (System.Windows.Media.Brush)FindResource("FgBrush");
        var dim       = (System.Windows.Media.Brush)FindResource("FgDimBrush");
        var hover     = (System.Windows.Media.Brush)FindResource("HighlightBrush");
        var accent    = (System.Windows.Media.Brush)FindResource("AccentBrush");
        var accentDim = (System.Windows.Media.Brush)FindResource("AccentDimBrush");
        var trans     = System.Windows.Media.Brushes.Transparent;

        bool owned0 = App.Settings.IsBlueprintOwned(bp.Name);

        var rowGrid = new Grid();
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // ownership accent strip
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });  // name
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // marker (tick / pill)

        var strip = new Border { Width = 3, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 1, 11, 1), Background = owned0 ? _ownedGreen : trans };
        Grid.SetColumn(strip, 0); rowGrid.Children.Add(strip);

        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(sp, 1);
        sp.Children.Add(new TextBlock { Text = bp.Name, FontWeight = FontWeights.SemiBold, Foreground = fg, TextTrimming = System.Windows.TextTrimming.CharacterEllipsis });
        if (showCategory)
            sp.Children.Add(new TextBlock { Text = bp.Category + (string.IsNullOrEmpty(bp.SubCategory) ? "" : " · " + bp.SubCategory), FontSize = 10, Foreground = dim, Margin = new Thickness(0, 2, 0, 0) });
        rowGrid.Children.Add(sp);

        // Quiet ownership: a green ✓ tick at rest, the actionable pill only on hover.
        var marker = new Grid { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        var tick = new TextBlock { Text = "✓", FontSize = 12, FontWeight = FontWeights.Bold, Foreground = _ownedGreen, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Visibility = owned0 ? Visibility.Visible : Visibility.Collapsed };
        var pill = OwnedCheckbox(bp);
        pill.Margin = new Thickness(0);
        pill.Visibility = Visibility.Collapsed;
        marker.Children.Add(tick);
        marker.Children.Add(pill);
        Grid.SetColumn(marker, 2); rowGrid.Children.Add(marker);

        // Chamfered HUD row; transparent at rest so the leaf list stays clean, chamfer shows on hover/select.
        var host = Hud.CardFrame(rowGrid, out var frame, out _, chamfer: 7, padding: new Thickness(10, 7, 11, 7));
        frame.Fill = trans; frame.Stroke = trans;
        host.Margin = new Thickness(0, 0, 0, 3);
        host.Cursor = System.Windows.Input.Cursors.Hand;

        // in-place refresh of this row's ownership visuals (called by OnOwnershipChanged)
        _bpRowOwned[bp.Name] = owned =>
        {
            strip.Background = owned ? _ownedGreen : trans;
            ApplyCheckVisual(pill, owned);
            tick.Visibility = owned && pill.Visibility != Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        };

        host.MouseEnter += (s, _) =>
        {
            if (!ReferenceEquals(host, _selectedBpRow)) frame.Fill = hover;
            pill.Visibility = Visibility.Visible;
            tick.Visibility = Visibility.Collapsed;
        };
        host.MouseLeave += (s, _) =>
        {
            if (!ReferenceEquals(host, _selectedBpRow)) frame.Fill = trans;
            pill.Visibility = Visibility.Collapsed;
            tick.Visibility = App.Settings.IsBlueprintOwned(bp.Name) ? Visibility.Visible : Visibility.Collapsed;
        };
        host.MouseLeftButtonDown += (s, _) =>
        {
            _deselectBpRow?.Invoke();
            frame.Fill = accentDim; frame.Stroke = accent;
            _deselectBpRow = () => { frame.Fill = trans; frame.Stroke = trans; };
            _selectedBpRow = host;
            ShowBlueprintDetail(bp);
        };
        return host;
    }

    // ── Ownership labeled toggle (nav rows) ─────────────────────────────────────
    // Reads "Own" (faint) when not owned and "✓ Owned" (green) once marked, so the
    // control says what it does instead of looking like a bare checkbox.
    private static readonly System.Windows.Media.SolidColorBrush _ownedGreen     = BrushFromHex("#3FB950");
    private static readonly System.Windows.Media.SolidColorBrush _ownedGreenFill = BrushFromHex("#2E3FB950");

    private Border OwnedCheckbox(NexusApp.Models.Blueprint bp)
    {
        var label = new TextBlock { FontSize = 10.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var pill = new Border
        {
            Child = label,
            CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(9, 2, 10, 2),
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "Click to mark whether you own this blueprint",
        };
        ApplyCheckVisual(pill, App.Settings.IsBlueprintOwned(bp.Name));
        pill.MouseEnter += (s, e) =>
        {
            if (App.Settings.IsBlueprintOwned(bp.Name)) return;
            var accent = (System.Windows.Media.Brush)FindResource("AccentBrush");
            pill.BorderBrush = accent;
            label.Foreground = accent;
        };
        pill.MouseLeave += (s, e) =>
        {
            if (!App.Settings.IsBlueprintOwned(bp.Name)) ApplyCheckVisual(pill, false);
        };
        pill.MouseLeftButtonDown += (s, e) =>
        {
            e.Handled = true;   // toggle ownership without opening the detail panel
            var now = !App.Settings.IsBlueprintOwned(bp.Name);
            App.Settings.SetBlueprintOwned(bp.Name, now);
            OnOwnershipChanged(bp.Name, now);
        };
        return pill;
    }

    // Applies a single ownership change to the UI. Always updates the count and the
    // detail toggle. In the All view it updates just the toggled row's pill in place
    // - no rebuild. In a filtered view that row no longer belongs in the list, so the
    // current drill-down level is re-rendered (cheap - one level, not the catalog).
    private void OnOwnershipChanged(string name, bool nowOwned)
    {
        UpdateOwnedCount();

        if (_detailBpName != null && string.Equals(_detailBpName, name, StringComparison.OrdinalIgnoreCase)
            && _detailOwnedToggle != null)
            ApplyOwnedToggleVisual(_detailOwnedToggle, nowOwned);

        if (_bpOwnFilter == BpOwnFilter.All)
        {
            if (_bpRowOwned.TryGetValue(name, out var apply))
                apply(nowOwned);
            return;
        }

        RenderBlueprintNav();
    }

    private void ApplyCheckVisual(Border pill, bool owned)
    {
        if (pill.Child is not TextBlock label) return;
        if (owned)
        {
            pill.Background = _ownedGreenFill;
            pill.BorderBrush = _ownedGreen;
            label.Text = "✓ Owned";
            label.Foreground = _ownedGreen;
        }
        else
        {
            pill.Background = System.Windows.Media.Brushes.Transparent;
            pill.BorderBrush = (System.Windows.Media.Brush)FindResource("FgDimBrush");
            label.Text = "Own";
            label.Foreground = (System.Windows.Media.Brush)FindResource("FgDimBrush");
        }
    }

    // ── Ownership toggle (detail panel) ─────────────────────────────────────────
    private Border OwnedToggle(string bpName)
    {
        var toggle = new Border
        {
            CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center, Cursor = System.Windows.Input.Cursors.Hand,
        };
        _detailOwnedToggle = toggle;
        ApplyOwnedToggleVisual(toggle, App.Settings.IsBlueprintOwned(bpName));
        toggle.MouseLeftButtonDown += (s, e) =>
        {
            var now = !App.Settings.IsBlueprintOwned(bpName);
            App.Settings.SetBlueprintOwned(bpName, now);
            ApplyOwnedToggleVisual(toggle, now);
            OnOwnershipChanged(bpName, now);   // sync nav row + count in place (no full rebuild)
        };
        return toggle;
    }

    private void ApplyOwnedToggleVisual(Border toggle, bool owned)
    {
        var accent = (System.Windows.Media.Brush)FindResource("AccentBrush");
        var label = toggle.Child as TextBlock
            ?? new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        toggle.Child = label;
        if (owned)
        {
            toggle.Background = _ownedGreenFill;
            toggle.BorderBrush = _ownedGreen;
            label.Text = "✓ Owned";
            label.Foreground = _ownedGreen;
        }
        else
        {
            toggle.Background = System.Windows.Media.Brushes.Transparent;
            toggle.BorderBrush = accent;
            label.Text = "Mark owned";
            label.Foreground = accent;
        }
    }

    private void ShowBlueprintLanding()
    {
        BlueprintDetailPanel.Children.Clear();
        _detailBpName = null;
        _detailOwnedToggle = null;
        var fg       = (System.Windows.Media.Brush)FindResource("FgBrush");
        var dim      = (System.Windows.Media.Brush)FindResource("FgDimBrush");
        var accent   = (System.Windows.Media.Brush)FindResource("AccentBrush");
        var headFont = (System.Windows.Media.FontFamily)FindResource("HeadFont");
        var monoFont = (System.Windows.Media.FontFamily)FindResource("MonoFont");

        var all   = _allBlueprints ?? new List<NexusApp.Models.Blueprint>();
        int total = all.Count;
        int owned = all.Count(b => App.Settings.IsBlueprintOwned(b.Name));
        int pct   = total > 0 ? (int)System.Math.Round(owned * 100.0 / total) : 0;

        BlueprintDetailPanel.Children.Add(new TextBlock { Text = "BLUEPRINT MANIFEST", FontFamily = monoFont, FontSize = 11, Foreground = accent, Margin = new Thickness(2, 4, 0, 8) });

        var line = new TextBlock { FontSize = 15, Margin = new Thickness(2, 0, 0, 0), TextWrapping = TextWrapping.Wrap };
        line.Inlines.Add(new System.Windows.Documents.Run("You own ") { Foreground = dim });
        line.Inlines.Add(new System.Windows.Documents.Run(owned.ToString("N0")) { Foreground = fg, FontWeight = FontWeights.SemiBold });
        line.Inlines.Add(new System.Windows.Documents.Run(" of ") { Foreground = dim });
        line.Inlines.Add(new System.Windows.Documents.Run(total.ToString("N0")) { Foreground = fg, FontWeight = FontWeights.SemiBold });
        line.Inlines.Add(new System.Windows.Documents.Run(" blueprints") { Foreground = dim });
        BlueprintDetailPanel.Children.Add(line);

        BlueprintDetailPanel.Children.Add(new TextBlock { Text = $"{pct}%", FontFamily = headFont, FontSize = 48, FontWeight = FontWeights.Bold, Foreground = accent, Margin = new Thickness(2, 4, 0, 0) });
        BlueprintDetailPanel.Children.Add(new TextBlock { Text = "Mark blueprints as Owned as you unlock them in-game - your manifest fills in here.", FontSize = 12, Foreground = dim, Margin = new Thickness(2, 2, 0, 18), TextWrapping = TextWrapping.Wrap, MaxWidth = 540, HorizontalAlignment = HorizontalAlignment.Left });

        foreach (var cat in _bpCategories)
        {
            int catTotal = all.Count(b => b.Category == cat);
            int catOwned = all.Count(b => b.Category == cat && App.Settings.IsBlueprintOwned(b.Name));
            BlueprintDetailPanel.Children.Add(CategoryProgress(cat, catOwned, catTotal));
        }
    }

    private UIElement CategoryProgress(string cat, int owned, int total)
    {
        var col  = CategoryBrush(cat);
        var fg   = (System.Windows.Media.Brush)FindResource("FgBrush");
        var dim  = (System.Windows.Media.Brush)FindResource("FgDimBrush");
        var mono = (System.Windows.Media.FontFamily)FindResource("MonoFont");

        var container = new StackPanel { Margin = new Thickness(2, 8, 6, 8), Cursor = System.Windows.Input.Cursors.Hand };
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new System.Windows.Shapes.Ellipse { Width = 10, Height = 10, Fill = col, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 9, 0) });
        left.Children.Add(new TextBlock { Text = cat, FontSize = 13, Foreground = fg, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(left);
        var cnt = new TextBlock { Text = $"{owned} / {total}", FontFamily = mono, FontSize = 12, Foreground = dim, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(cnt, 1); row.Children.Add(cnt);
        container.Children.Add(row);

        double frac = total > 0 ? (double)owned / total : 0;
        var barGrid = new Grid { Height = 8, Margin = new Thickness(0, 6, 0, 0) };
        barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(System.Math.Max(0.0001, frac), GridUnitType.Star) });
        barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(System.Math.Max(0.0001, 1 - frac), GridUnitType.Star) });
        var track = new Border { Background = (System.Windows.Media.Brush)FindResource("Bg3Brush"), CornerRadius = new CornerRadius(4) };
        Grid.SetColumnSpan(track, 2); barGrid.Children.Add(track);
        if (frac > 0)
        {
            var fill = new Border { Background = col, CornerRadius = new CornerRadius(4) };
            Grid.SetColumn(fill, 0); barGrid.Children.Add(fill);
        }
        container.Children.Add(barGrid);
        container.MouseLeftButtonDown += (_, __) => EnterCategory(cat);
        return container;
    }


    private void BlueprintSearchRun_Click(object sender, RoutedEventArgs e)
    {
        BlueprintSuggestPopup.IsOpen = false;
        RunBlueprintSearch();
    }

    private void RunBlueprintSearch()
    {
        var text = (_vm.BlueprintSearch ?? "").Trim();
        if (string.IsNullOrEmpty(text)) { GoRoot(); return; }
        _bpSearchResults = App.Data.SearchBlueprints(text);
        _bpLevel = "search";
        BlueprintSearchClear.Visibility = Visibility.Visible;   // keep the term in the box; show the clear affordance
        RenderBlueprintNav();
        ShowBlueprintLanding();
    }

    private void BlueprintSearchClear_Click(object sender, RoutedEventArgs e)
    {
        InteractionLog.Click("Blueprint search: clear", (DependencyObject)sender);
        BlueprintSuggestPopup.IsOpen = false;
        GoRoot();   // clears the box, hides this button, and returns to the (filtered) browse root
    }

    private void BlueprintSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { BlueprintSuggestPopup.IsOpen = false; return; }
        if (e.Key == Key.Enter)
        {
            BlueprintSuggestPopup.IsOpen = false;
            RunBlueprintSearch();
        }
    }

    private void BlueprintSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressAutocomplete) return;
        var text = BlueprintSearchBox.Text.Trim();
        if (text.Length < 2) { BlueprintSuggestPopup.IsOpen = false; return; }
        var suggestions = App.Data.SearchBlueprints(text).Select(b => b.Name).Take(12).ToList();
        if (suggestions.Count == 0) { BlueprintSuggestPopup.IsOpen = false; return; }
        BlueprintSuggestList.Items.Clear();
        foreach (var name in suggestions)
        {
            var item = new System.Windows.Controls.ListBoxItem { Tag = name, Content = BuildHighlightedText(name, text) };
            BlueprintSuggestList.Items.Add(item);
        }
        BlueprintSuggestPopup.IsOpen = true;
    }

    private void BlueprintSuggest_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (BlueprintSuggestList.SelectedItem is System.Windows.Controls.ListBoxItem li && li.Tag is string name)
        {
            _suppressAutocomplete = true;
            _vm.BlueprintSearch = name;
            BlueprintSuggestPopup.IsOpen = false;
            _bpSearchResults = App.Data.SearchBlueprints(name);
            _bpLevel = "search";
            BlueprintSearchClear.Visibility = Visibility.Visible;   // the picked name stays in the box
            RenderBlueprintNav();
            _suppressAutocomplete = false;

            // Show the chosen blueprint's detail immediately
            var match = _bpSearchResults.FirstOrDefault(b => b.Name == name) ?? _bpSearchResults.FirstOrDefault();
            if (match != null) ShowBlueprintDetail(match);
            else ShowBlueprintLanding();
        }
    }

    private static TextBlock BuildHighlightedText(string name, string query)
    {
        var tb = new TextBlock();
        int idx = name.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) { tb.Inlines.Add(new System.Windows.Documents.Run(name)); return tb; }
        if (idx > 0)
            tb.Inlines.Add(new System.Windows.Documents.Run(name[..idx]));
        tb.Inlines.Add(new System.Windows.Documents.Run(name.Substring(idx, query.Length))
        {
            FontWeight = FontWeights.Bold,
            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("AccentBrush"),
        });
        if (idx + query.Length < name.Length)
            tb.Inlines.Add(new System.Windows.Documents.Run(name[(idx + query.Length)..]));
        return tb;
    }

    private UIElement HeroSpec(string label, string value, System.Windows.Media.Brush fg, System.Windows.Media.Brush dim, System.Windows.Media.FontFamily mono)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 26, 0) };
        sp.Children.Add(new TextBlock { Text = label, FontFamily = mono, FontSize = 9, Foreground = dim });
        sp.Children.Add(new TextBlock { Text = value, FontSize = 14, Foreground = fg, Margin = new Thickness(0, 2, 0, 0) });
        return sp;
    }

    private void ShowBlueprintDetail(NexusApp.Models.Blueprint selected)
    {
        InteractionLog.Nav($"Blueprint Library: open {selected.Name}");
        var full = App.Data.GetBlueprintFull(selected.Name);
        BlueprintDetailPanel.Children.Clear();
        _detailBpName = null;
        _detailOwnedToggle = null;
        if (full == null) return;
        _detailBpName = full.Name;

        // ── Schematic hero: drafting-sheet header ─────────────────────────────
        var heroAccent = (System.Windows.Media.Brush)FindResource("AccentBrush");
        var fgB      = (System.Windows.Media.Brush)FindResource("FgBrush");
        var dimB     = (System.Windows.Media.Brush)FindResource("FgDimBrush");
        var monoFont = (System.Windows.Media.FontFamily)FindResource("MonoFont");
        var headFont = (System.Windows.Media.FontFamily)System.Windows.Application.Current.FindResource("HeadFont");

        var eyebrow = full.SubCategory is { Length: > 0 }
            ? $"{full.Category} · {full.SubCategory}".ToUpperInvariant()
            : full.Category.ToUpperInvariant();
        double totalScu = full.Ingredients.Sum(i => i.Quantity);
        var heroContent = new StackPanel();
        heroContent.Children.Add(new TextBlock { Text = eyebrow, FontFamily = monoFont, FontSize = 11, Foreground = heroAccent });
        heroContent.Children.Add(new TextBlock
        {
            Text = full.Name, FontFamily = headFont, FontSize = 25, FontWeight = FontWeights.SemiBold,
            Foreground = fgB, Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap,
        });
        heroContent.Children.Add(new Border { Height = 1, Background = heroAccent, Opacity = 0.6, Margin = new Thickness(0, 12, 0, 0) });

        var heroRow = new Grid { Margin = new Thickness(0, 13, 0, 0) };
        heroRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heroRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var specs = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
        specs.Children.Add(HeroSpec("INGREDIENTS", full.Ingredients.Count.ToString(), fgB, dimB, monoFont));
        specs.Children.Add(HeroSpec("TOTAL COST", CraftAmount.Format(totalScu, "SCU"), fgB, dimB, monoFont));
        Grid.SetColumn(specs, 0); heroRow.Children.Add(specs);
        var heroActions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
        heroActions.Children.Add(OwnedToggle(full.Name));
        var heroAddBtn = new Button
        {
            Content = "+ Add all to cart", Style = (Style)FindResource("AccentButton"),
            Padding = new Thickness(14, 7, 14, 7), VerticalAlignment = VerticalAlignment.Center,
        };
        heroAddBtn.Click += (s, e) => { foreach (var i in full.Ingredients) _vm.AddToShoppingCommand.Execute(i); };
        heroActions.Children.Add(heroAddBtn);
        Grid.SetColumn(heroActions, 1); heroRow.Children.Add(heroActions);
        heroContent.Children.Add(heroRow);

        var heroRoot = new Grid();
        heroRoot.Children.Add(heroContent);

        // Chamfered HUD hero panel with amber corner brackets (replaces the rounded drafting card).
        var heroCard = Hud.Panel(heroRoot, chamfer: 14, brackets: true,
            bg: (System.Windows.Media.Brush)FindResource("Bg2NavBrush"),
            border: (System.Windows.Media.Brush)FindResource("NavBorderBrush"),
            padding: new Thickness(20, 16, 18, 16));
        heroCard.Margin = new Thickness(0, 0, 0, 8);
        BlueprintDetailPanel.Children.Add(heroCard);

        // ── two-column split: ingredients (left) | unlock + locations (right) ──
        var splitGrid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        splitGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        splitGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        splitGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var leftHost = new StackPanel();
        var rightHost = new StackPanel();
        Grid.SetColumn(leftHost, 0);
        Grid.SetColumn(rightHost, 2);
        splitGrid.Children.Add(leftHost);
        splitGrid.Children.Add(rightHost);
        BlueprintDetailPanel.Children.Add(splitGrid);
        System.Windows.Controls.Panel host = rightHost;   // unlock builds first -> right column

        // ── HOW TO UNLOCK ────────────────────────────────────────────────────
        host.Children.Add(new TextBlock
        {
            Text = "HOW TO UNLOCK",
            FontSize = 10, FontWeight = FontWeights.Bold,
            Foreground = (System.Windows.Media.Brush)FindResource("FgDimBrush"),
            Margin = new Thickness(0, 0, 0, 6),
        });

        if (full.UnlockEntries.Count == 0)
        {
            host.Children.Add(new TextBlock
            {
                Text = "No unlock information available",
                FontSize = 11, FontStyle = FontStyles.Italic,
                Foreground = (System.Windows.Media.Brush)FindResource("FgDimBrush"),
                Margin = new Thickness(0, 0, 0, 12),
            });
        }
        else
        {
            // Group by faction
            var byFaction = full.UnlockEntries
                .GroupBy(e => (e.Faction, e.MissionType))
                .ToList();

            foreach (var group in byFaction)
            {
                var (faction, mtype) = group.Key;
                var missions = group.ToList();

                var factionLabel = mtype != null ? $"{faction}  ·  {mtype}" : faction;
                host.Children.Add(new TextBlock
                {
                    Text = factionLabel, FontSize = 11, FontWeight = FontWeights.SemiBold,
                    Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush"),
                    Margin = new Thickness(0, 4, 0, 2),
                });

                if (missions.Count <= 6)
                {
                    foreach (var m in missions)
                    {
                        var rankPart = m.Rank is null or "Any" or "Neutral" ? m.Rank ?? "Any" : m.Rank;
                        var sysPart  = m.Systems is { Length: > 0 } ? string.Join("/", m.Systems) : null;
                        var meta     = sysPart != null ? $" ({rankPart}  ·  {sysPart})" : $" ({rankPart})";

                        var row = new System.Windows.Controls.DockPanel { Margin = new Thickness(8, 1, 0, 1), LastChildFill = true };
                        var bullet = new TextBlock
                        {
                            Text = "·  ", FontSize = 11,
                            Foreground = (System.Windows.Media.Brush)FindResource("FgDimBrush"),
                            VerticalAlignment = VerticalAlignment.Top,
                        };
                        System.Windows.Controls.DockPanel.SetDock(bullet, System.Windows.Controls.Dock.Left);
                        row.Children.Add(bullet);
                        var missionLine = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
                        missionLine.Inlines.Add(new System.Windows.Documents.Run(m.MissionTitle)
                        {
                            Foreground = (System.Windows.Media.Brush)FindResource("FgBrush"),
                        });
                        missionLine.Inlines.Add(new System.Windows.Documents.Run(meta)
                        {
                            Foreground = (System.Windows.Media.Brush)FindResource("FgDimBrush"),
                            FontSize = 10,
                        });
                        row.Children.Add(missionLine);
                        host.Children.Add(row);
                    }
                }
                else
                {
                    var typeLabel = mtype != null ? $" {mtype}" : "";
                    host.Children.Add(new TextBlock
                    {
                        Text = $"  ·  Any{typeLabel} mission  ({missions.Count} available)",
                        FontSize = 11,
                        Foreground = (System.Windows.Media.Brush)FindResource("FgDimBrush"),
                        Margin = new Thickness(8, 1, 0, 1),
                    });
                }
            }

            host.Children.Add(new Border
            {
                Height = 1, Margin = new Thickness(0, 10, 0, 4),
                Background = (System.Windows.Media.Brush)FindResource("NavBorderBrush"),
            });
        }

        host = leftHost;
        var navBorder0 = (System.Windows.Media.Brush)FindResource("NavBorderBrush");

        // Bill of materials header (label + QTY column heading)
        var bomHead = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        bomHead.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bomHead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bomHead.Children.Add(new TextBlock { Text = "BILL OF MATERIALS", FontFamily = monoFont, FontSize = 11, Foreground = dimB });
        var bomQtyHd = new TextBlock { Text = "QTY", FontFamily = monoFont, FontSize = 11, Foreground = dimB };
        Grid.SetColumn(bomQtyHd, 1); bomHead.Children.Add(bomQtyHd);
        host.Children.Add(new Border { Child = bomHead, BorderBrush = navBorder0, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 0, 0, 8), Margin = new Thickness(0, 0, 0, 2) });

        double bomTotal = full.Ingredients.Sum(i => i.Quantity);

        foreach (var ing in full.Ingredients)
        {
            var ingCopy = ing;
            var rarity = _vm.AllResources.FirstOrDefault(r => r.Name == ing.ResourceName)?.Rarity ?? "common";
            var rb = RarityBrush(rarity);
            var tier = rarity.Length > 0 ? char.ToUpper(rarity[0]) + rarity.Substring(1) : "";

            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // dot
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });  // name + tier
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // qty + unit
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // add

            var dot = new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(2), Background = rb, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 11, 0) };
            Grid.SetColumn(dot, 0); g.Children.Add(dot);

            var nameWrap = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            nameWrap.Children.Add(new TextBlock { Text = ing.ResourceName, FontSize = 13.5, Foreground = rb, VerticalAlignment = VerticalAlignment.Center });
            if (tier.Length > 0)
                nameWrap.Children.Add(new TextBlock { Text = "   " + tier, FontSize = 10, Foreground = dimB, VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(nameWrap, 1); g.Children.Add(nameWrap);

            var qtyWrap = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            qtyWrap.Children.Add(new TextBlock { Text = CraftAmount.Value(ing.Quantity, ing.Unit), FontFamily = monoFont, FontSize = 13, Foreground = fgB, Width = 50, TextAlignment = System.Windows.TextAlignment.Right });
            qtyWrap.Children.Add(new TextBlock { Text = " " + CraftAmount.Unit(ing.Quantity, ing.Unit), FontFamily = monoFont, FontSize = 11, Foreground = dimB, Width = 38, TextAlignment = System.Windows.TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(qtyWrap, 2); g.Children.Add(qtyWrap);

            var addBtn = new Button { Content = "+", Style = (Style)FindResource("NexusButton"), Padding = new Thickness(8, 1, 8, 1), FontSize = 13, FontWeight = FontWeights.Bold, ToolTip = "Add to shopping list", Tag = ingCopy, VerticalAlignment = VerticalAlignment.Center };
            addBtn.Click += (s, e) => _vm.AddToShoppingCommand.Execute(((Button)s).Tag);
            Grid.SetColumn(addBtn, 3); g.Children.Add(addBtn);

            var rowBorder = new Border { Child = g, Background = System.Windows.Media.Brushes.Transparent, Padding = new Thickness(2, 9, 2, 9), BorderBrush = navBorder0, BorderThickness = new Thickness(0, 0, 0, 1) };

            // cross-link: clicking the row opens the ingredient in the Mining Codex
            if (_vm.AllResources.Any(r => r.Name.Equals(ing.ResourceName, StringComparison.OrdinalIgnoreCase)))
            {
                rowBorder.Cursor = System.Windows.Input.Cursors.Hand;
                rowBorder.ToolTip = "Open in Mining Codex";
                var hov = (System.Windows.Media.Brush)FindResource("HighlightBrush");
                rowBorder.MouseEnter += (s, _) => rowBorder.Background = hov;
                rowBorder.MouseLeave += (s, _) => rowBorder.Background = System.Windows.Media.Brushes.Transparent;
                rowBorder.MouseLeftButtonDown += (s, _) => NavigateToResource(ingCopy.ResourceName);
            }

            host.Children.Add(rowBorder);
        }

        // Total footer
        var totalGrid = new Grid { Margin = new Thickness(2, 11, 2, 0) };
        totalGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        totalGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        totalGrid.Children.Add(new TextBlock { Text = "TOTAL", FontFamily = monoFont, FontSize = 11, Foreground = dimB, VerticalAlignment = VerticalAlignment.Center });
        var totalVal = new TextBlock { Text = CraftAmount.Format(bomTotal, "SCU"), FontFamily = monoFont, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = heroAccent, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(totalVal, 1); totalGrid.Children.Add(totalVal);
        host.Children.Add(new Border { Child = totalGrid, BorderBrush = navBorder0, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 10, 0, 0), Margin = new Thickness(0, 2, 0, 0) });

        host = rightHost;

        // ── Location recommendation (greedy set cover) ───────────────────────
        var ingredientNames = full.Ingredients.Select(i => i.ResourceName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var locToIngredients = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var ing in full.Ingredients)
        {
            var res = _vm.AllResources.FirstOrDefault(r => r.Name == ing.ResourceName);
            if (res == null) continue;
            foreach (var loc in res.Locations)
            {
                if (!locToIngredients.TryGetValue(loc, out var set))
                    locToIngredients[loc] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                set.Add(ing.ResourceName);
            }
        }

        var withLocation = full.Ingredients
            .Where(i => _vm.AllResources.Any(r => r.Name == i.ResourceName && r.Locations.Count > 0))
            .Select(i => i.ResourceName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var noLocation = full.Ingredients.Select(i => i.ResourceName).Where(n => !withLocation.Contains(n)).ToList();

        var remaining = new HashSet<string>(withLocation, StringComparer.OrdinalIgnoreCase);
        var rankedLocations = new List<(string Location, List<string> Covers)>();
        var availableLocs = new Dictionary<string, HashSet<string>>(locToIngredients, StringComparer.OrdinalIgnoreCase);

        while (remaining.Count > 0)
        {
            string? bestLoc = null; int bestCount = 0; List<string>? bestCovers = null;
            foreach (var (loc, ings) in availableLocs)
            {
                var covered = ings.Intersect(remaining).ToList();
                if (covered.Count > bestCount) { bestCount = covered.Count; bestLoc = loc; bestCovers = covered; }
            }
            if (bestLoc == null || bestCount == 0) break;
            rankedLocations.Add((bestLoc, bestCovers!));
            foreach (var r in bestCovers!) remaining.Remove(r);
            availableLocs.Remove(bestLoc);
        }

        if (rankedLocations.Count > 0 || noLocation.Count > 0)
        {
            host.Children.Add(new Border
            {
                Height = 1, Margin = new Thickness(0, 14, 0, 10),
                Background = (System.Windows.Media.Brush)FindResource("NavBorderBrush"),
            });
            host.Children.Add(new TextBlock
            {
                Text = $"WHERE TO MINE  ·  {rankedLocations.Count} location{(rankedLocations.Count == 1 ? "" : "s")}",
                FontSize = 10, FontWeight = FontWeights.Bold,
                Foreground = (System.Windows.Media.Brush)FindResource("FgDimBrush"),
                Margin = new Thickness(0, 0, 0, 6),
            });

            int locRank = 1;
            foreach (var (location, covers) in rankedLocations)
            {
                var system = GetSystem(location);
                var sysBrush = SystemBrush(system);

                var locRow = new Grid();
                locRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
                locRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                locRow.Children.Add(new Border { Background = sysBrush });

                var locContent = new StackPanel { Margin = new Thickness(10, 7, 10, 7) };

                var topRow = new StackPanel { Orientation = Orientation.Horizontal };
                topRow.Children.Add(new TextBlock
                {
                    Text = $"#{locRank++}", FontSize = 9, FontWeight = FontWeights.Bold,
                    Foreground = sysBrush, Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                topRow.Children.Add(new TextBlock
                {
                    Text = location, FontSize = 12, FontWeight = FontWeights.SemiBold,
                    Foreground = (System.Windows.Media.Brush)FindResource("FgBrush"),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                topRow.Children.Add(new Border
                {
                    Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(5, 1, 5, 1),
                    CornerRadius = new CornerRadius(3), Background = sysBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = system.ToUpper(), FontSize = 8, FontWeight = FontWeights.Bold,
                        Foreground = System.Windows.Media.Brushes.White,
                    },
                });
                locContent.Children.Add(topRow);

                locContent.Children.Add(new TextBlock
                {
                    Text = $"{covers.Count}/{ingredientNames.Count} ingredients · {string.Join(", ", covers)}",
                    FontSize = 10,
                    Foreground = (System.Windows.Media.Brush)FindResource("FgDimBrush"),
                    Margin = new Thickness(0, 3, 0, 0),
                    TextWrapping = TextWrapping.Wrap,
                });

                Grid.SetColumn(locContent, 1);
                locRow.Children.Add(locContent);

                host.Children.Add(new Border
                {
                    Margin = new Thickness(0, 0, 0, 4),
                    CornerRadius = new CornerRadius(4),
                    Background = (System.Windows.Media.Brush)FindResource("Bg2NavBrush"),
                    BorderBrush = (System.Windows.Media.Brush)FindResource("NavBorderBrush"),
                    BorderThickness = new Thickness(1),
                    ClipToBounds = true,
                    Child = locRow,
                });
            }

            if (noLocation.Count > 0)
                host.Children.Add(new TextBlock
                {
                    Text = $"No known location: {string.Join(", ", noLocation)}",
                    FontSize = 10,
                    Foreground = (System.Windows.Media.Brush)FindResource("FgDimBrush"),
                    Margin = new Thickness(0, 4, 0, 0),
                    TextWrapping = TextWrapping.Wrap,
                });
        }
    }

    private static void AddDetailRow(StackPanel panel, string label, string value)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        sp.Children.Add(new TextBlock
        {
            Text = label + ":  ", FontSize = 11,
            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("FgDimBrush"),
        });
        sp.Children.Add(new TextBlock
        {
            Text = value, FontSize = 11,
            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("FgBrush"),
        });
        panel.Children.Add(sp);
    }

    // ── Overlay ──────────────────────────────────────────────────────────────

    /// <summary>Creates and wires the overlay window if it doesn't exist yet (without changing its visibility).</summary>
    private void EnsureOverlay()
    {
        if (_overlay != null) return;
        _overlay = new OverlayWindow(_vm);
        _overlay.ScanRegionSelected  += ApplyScanRegion;
        _overlay.BoxVisibilityToggled += visible =>
        {
            _boxVisible = visible;
            if (_scanIndicator == null) return;
            Logger.Info($"[WIN] scan-indicator {(visible ? "shown" : "hidden")}");
            if (visible) _scanIndicator.Show();
            else         _scanIndicator.Hide();
        };
        _overlay.ContractRegionSelected += ApplyContractRegion;
        // Route the overlay toggle through the single source; ApplyContractBoxVisible (subscribed to
        // App.ContractBoxVisibilityChanged) does the actual show/hide so every surface stays in sync.
        _overlay.ContractBoxVisibilityToggled += App.SetContractBoxVisible;
        _overlay.Hidden += () => _vm.PauseScanner();
        _overlay.Shown  += () => _vm.ResumeScanner();
    }

    private void ToggleOverlay_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            EnsureOverlay();

            if (!_overlay!.IsVisible)
                _overlay.Show();
            else
                _overlay.Hide();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Overlay error:\n\n{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}",
                            "Overlay Error", MessageBoxButton.OK, MessageBoxImage.Error);
            try { _overlay?.Close(); } catch { /* discarding a broken overlay - its OnClosed detaches handlers */ }
            _overlay = null;
        }
    }

    private void ApplyScanRegion(NexusApp.Models.ScanRegion r)
    {
        // Diagnostic for multi-monitor capture (issue #6): logs the stored region and the system
        // DPI. On a monitor whose scale differs from the primary, these coords won't line up with
        // the BitBlt screen-grab, which pins detection to the primary monitor.
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        Logger.Info($"[SCAN] scan region set ({r.X},{r.Y}) {r.Width}x{r.Height}; main-window monitor DPI {dpi.DpiScaleX:0.##}x");

        App.Settings.Current.ScanRegion = r;
        App.Settings.Save();
        _vm.SetScanRegion(r);
        ShowScanIndicator(r);
    }

    private void RestoreScanRegion()
    {
        var r = App.Settings.Current.ScanRegion;
        if (r == null) return;
        _vm.SetScanRegion(r);
        ShowScanIndicator(r);
    }

    private void ShowScanIndicator(NexusApp.Models.ScanRegion r)
    {
        if (_scanIndicator == null)
        {
            _scanIndicator = new ScanIndicatorWindow();
            if (_boxVisible) { Logger.Info("[WIN] scan-indicator shown"); _scanIndicator.Show(); }
        }
        _scanIndicator.SetRegion(r);   // indicator positions itself in physical pixels (issue #6)
    }

    // ── Cargo-contract region (independent of the RS region above) ───────────────
    // The contract path uses its OWN settings key, OCR service, and a SEPARATE yellow
    // ScanIndicatorWindow - it never touches _scanIndicator / OcrService / the RS region.
    private void ApplyContractRegion(NexusApp.Models.ScanRegion r)
    {
        App.Settings.Current.ContractRegion = r;
        App.Settings.Save();
        App.ContractOcr.SetRegion(r.X, r.Y, r.Width, r.Height);
        EnsureContractIndicator();
        _contractIndicator!.SetRegion(r);   // positions itself in physical pixels (issue #6)
    }

    /// <summary>Flash the yellow contract box green to confirm an OCR scan paired with a haul (no-op if hidden).</summary>
    public void FlashContractIndicator() => _contractIndicator?.FlashGreen();

    // Shows/hides the yellow contract indicator. Subscribed to App.ContractBoxVisibilityChanged so it
    // runs no matter which surface flipped the box (overlay, Cargo Hauling page).
    private void ApplyContractBoxVisible(bool visible)
    {
        _contractBoxVisible = visible;
        EnsureContractIndicator();
        if (_contractIndicator == null) return;
        Logger.Info($"[WIN] contract-indicator {(visible ? "shown" : "hidden")}");
        if (visible) _contractIndicator.Show();
        else         _contractIndicator.Hide();
    }

    /// <summary>Pause/resume the RS auto-scan when neither Nexus nor Star Citizen is the foreground window.</summary>
    public void SetScanForegroundActive(bool relevant)
    {
        if (relevant) _vm.ResumeForBackground();
        else          _vm.PauseForBackground();
    }

    // Creates the yellow cargo-contract indicator on first use; a distinct ScanIndicatorWindow
    // instance from the magenta _scanIndicator. Restores any saved region and shows it if toggled on.
    private void EnsureContractIndicator()
    {
        if (_contractIndicator != null) return;
        _contractIndicator = new ScanIndicatorWindow(System.Windows.Media.Color.FromArgb(255, 255, 209, 0));   // clear gold/yellow
        if (App.Settings.Current.ContractRegion is { } saved) _contractIndicator.SetRegion(saved);
        if (_contractBoxVisible) { Logger.Info("[WIN] contract-indicator shown"); _contractIndicator.Show(); }
    }

    // Main-window focus changes round out the tab-out picture: if a user is pulled from the game
    // and the main window (not the overlay) gained focus, an [WIN] main activated line shows it.
    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        Logger.Info("[WIN] main window activated (gained focus)");
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        Logger.Info("[WIN] main window deactivated (lost focus)");
    }

    // ── Shopping ─────────────────────────────────────────────────────────────

    private void ShowShopping_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ShoppingDialog(_vm) { Owner = this };
        dlg.ShowDialog();
    }

    // ── Window chrome ────────────────────────────────────────────────────────

    private void Help_Click(object sender, RoutedEventArgs e)
    {
        var help = new HelpDialog { Owner = this };
        help.ShowDialog();
        if (help.TutorialRequested) ShowTutorial();
    }
    private void About_Click(object sender, RoutedEventArgs e)
    {
        new AboutDialog { Owner = this }.ShowDialog();
    }

    private LogMonitorWindow? _logMonitor;

    // BETA: opens (or re-surfaces) the floating Game.log monitor - kept modeless and un-owned
    // so it can float over the game while you play. Reached from Settings → Game.log.
    public void ShowLogMonitor()
    {
        if (_logMonitor == null)
        {
            // Drives the shared App.GameLog session; the toast + Blueprint Library refresh
            // are wired centrally in App, so no seed names / callback are passed here.
            _logMonitor = new LogMonitorWindow();
            _logMonitor.Closed += (_, _) => _logMonitor = null;
        }
        if (_logMonitor.WindowState == WindowState.Minimized) _logMonitor.WindowState = WindowState.Normal;
        _logMonitor.Show();
        _logMonitor.Activate();
    }

    private AppLogMonitorWindow? _appLogMonitor;

    // Opens (or re-surfaces) the Nexus app-log monitor - Settings → Diagnostics. Modeless so it can
    // float beside the app while a bug is reproduced; its Save-snapshot button bundles a bug report.
    public void ShowAppLogMonitor()
    {
        if (_appLogMonitor == null)
        {
            _appLogMonitor = new AppLogMonitorWindow();
            _appLogMonitor.Closed += (_, _) => _appLogMonitor = null;
        }
        if (_appLogMonitor.WindowState == WindowState.Minimized) _appLogMonitor.WindowState = WindowState.Normal;
        _appLogMonitor.Show();
        _appLogMonitor.Activate();
    }

    // Called by the beta Game.log importer after it auto-marks ownership, so the
    // Blueprint page's owned count + nav reflect the change immediately.
    public void RefreshBlueprintOwnership()
    {
        if (!_bpInit) return;            // not visited yet - it'll read current ownership on first open
        UpdateOwnedCount();
        RenderBlueprintNav();
        // Rebuild the manifest landing so its "You own X of Y blueprints" line, percentage and
        // category bars reflect the new count live. Only when the landing is showing - if a single
        // blueprint's detail is open (_detailBpName != null) it has no manifest count, and rebuilding
        // would replace the detail the user is reading.
        if (_detailBpName == null) ShowBlueprintLanding();
    }

    // Blueprint Library → "Import owned from logs…": scans the configured Game.log + its logbackups
    // for blueprints already received and marks them owned. Shares the advanced monitor's exact flow
    // (BlueprintImportFlow), so both surfaces preview, confirm and report identically. (Beta)
    private async void BlueprintImportFromLogs_Click(object sender, RoutedEventArgs e)
    {
        var path = App.GameLog.StartPath();
        var prev = BpImportBtn.Content;
        BpImportBtn.IsEnabled = false;
        BpImportBtn.Content = "Scanning…";
        var result = await BlueprintImportFlow.RunAsync(this, path);
        BpImportBtn.Content = prev;
        BpImportBtn.IsEnabled = true;

        if (result.FilesScanned == 0)
        {
            MessageBox.Show(this,
                "Couldn't find a Star Citizen Game.log to scan. Set its location in Settings, then try again.",
                "Import owned blueprints", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (result.Applied) RefreshBlueprintOwnership();   // reflect the new ownership in the count + nav
    }

    // ── Easter egg (app version badge) ───────────────────────────────────────
    private int _eggClicks;
    private System.Windows.Threading.DispatcherTimer? _eggTimer;
    private static readonly string[] _eggWarnings =
        ["I wouldn't do that...", "Don't.", "Great! Now you've pissed off CannonActual!"];

    private void AppBadge_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        InteractionLog.Click("app version badge", (DependencyObject)sender);
        _eggClicks++;
        EggHintLabel.Text = _eggWarnings[Math.Min(_eggClicks - 1, _eggWarnings.Length - 1)];
        EggHintLabel.Visibility = Visibility.Visible;

        _eggTimer?.Stop();

        if (_eggClicks >= 3)
        {
            _eggClicks = 0;
            _eggTimer = new System.Windows.Threading.DispatcherTimer
                { Interval = TimeSpan.FromMilliseconds(900) };
            _eggTimer.Tick += (s, _) =>
            {
                _eggTimer.Stop();
                EggHintLabel.Visibility = Visibility.Collapsed;
                ShowEggDialog();
            };
            _eggTimer.Start();
        }
        else
        {
            _eggTimer = new System.Windows.Threading.DispatcherTimer
                { Interval = TimeSpan.FromSeconds(2) };
            _eggTimer.Tick += (s, _) =>
            {
                _eggTimer.Stop();
                EggHintLabel.Visibility = Visibility.Collapsed;
            };
            _eggTimer.Start();
        }
    }

    private void ShowEggDialog()
    {
        var dlg = new Window
        {
            Title = "Words of Wisdom", Width = 380, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this,
            ResizeMode = ResizeMode.NoResize, WindowStyle = WindowStyle.ToolWindow,
            Background = (System.Windows.Media.Brush)FindResource("Bg2NavBrush"),
        };
        var panel = new StackPanel { Margin = new Thickness(28) };
        panel.Children.Add(new TextBlock
        {
            Text = "“No questions until we swap server!”",
            FontSize = 15, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap,
            Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush"),
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 8),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "- CannonActual", FontSize = 11,
            Foreground = (System.Windows.Media.Brush)FindResource("FgDimBrush"),
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 20),
        });
        var okBtn = new Button
        {
            Content = "Understood", Height = 34, HorizontalAlignment = HorizontalAlignment.Stretch,
            Style = (Style)FindResource("AccentButton"),
        };
        okBtn.Click += (s, e) => dlg.Close();
        panel.Children.Add(okBtn);
        dlg.Content = panel;
        dlg.ShowDialog();
    }

    private void RestoreWindowPosition()
    {
        var s = App.Settings.Current;
        Left = s.WindowLeft; Top = s.WindowTop;
        Width = s.WindowWidth; Height = s.WindowHeight;
    }

    private void SaveWindowPosition()
    {
        App.Settings.Current.WindowLeft = Left; App.Settings.Current.WindowTop = Top;
        App.Settings.Current.WindowWidth = Width; App.Settings.Current.WindowHeight = Height;
        App.Settings.Save();
    }

}
