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

    // Named chamfer for the "hero" detail panels (Codex/reference dossier + Blueprint detail): both
    // call sites are commented as matching each other, so they share one constant instead of two
    // independently hand-picked literals.
    private const double HeroChamfer = 14;

    public MainWindow()
    {
        InitializeComponent();
        AppVersionText.Text = $"App v{AppInfo.Version}";
        GameVersionText.Text = $"SC PU {GameData.Version}";
        UpdateShardChip();
        // The Game.log chain (shards, blueprint session) is pumped by the shared feed's
        // DispatcherTimer, so it already raises on the UI thread - no marshaling, the same
        // contract App.xaml.cs and the overlay follow. Dispatcher.Invoke/BeginInvoke here is
        // reserved for genuinely background sources (ContractScanner, MarketDataService).
        if (App.Shards != null) App.Shards.Changed += UpdateShardChip;
        UpdateSessionChip();
        UpdateBlueprintChip();
        if (App.GameLog != null)
        {
            App.GameLog.StateChanged += () => { UpdateSessionChip(); UpdateBlueprintChip(); };
            // Channel switches (LIVE <-> PTU/EPTU/etc, issue #28) don't flip IsSessionLive, so they
            // don't fire StateChanged - the SESSION chip needs its own trigger to pick up the new
            // ChipSuffix on the next Game.log channel resolve.
            App.GameLogFeed.ChannelChanged += _ => UpdateSessionChip();
            // Republished from the shared GameLogFeed (Task 5): the watcher's own diagnostic text
            // ("Waiting for file to appear: ...", "Error opening: ...") becomes the SESSION chip's
            // tooltip while it is in its "no log" state (app review, Task 10). Cached even while not
            // in that state so the very first no-log render has real text instead of the fallback.
            App.GameLog.StatusChanged += s =>
            {
                _lastGameLogStatus = s;
                if (_sessionChipNoLog) SessionChip.ToolTip = s;
            };
            App.GameLog.HandleDetected += h => { UpdateOperatorIdentity(h); RefreshApprovedTools(); RefreshOwnerTools(); };
            // Auto-mark import (a single mark, or a bulk pass from Import owned from logs) refreshes
            // the Blueprint Library's owned count + nav live. Same feed, same UI-thread contract as
            // StateChanged above - moved here from App.xaml.cs's OnStartup wiring (app review, Task 9:
            // that composition-root file no longer casts to the concrete view).
            App.GameLog.Marked += m => RefreshBlueprintOwnership();
            App.GameLog.BulkOwnershipChanged += () => RefreshBlueprintOwnership();
        }
        // SESSION chip click-through (app review, Task 10): only its "no log" state is clickable, so
        // the hover tint's entry and the navigation are gated on the flag UpdateSessionChip maintains -
        // the normal monitoring/offline states keep no click affordance. Hover mirrors the app's
        // existing chip-hover idiom (NetworkPage's SubTab/GroupChip: HighlightBrush on enter, back to
        // Bg2NavBrush on leave); the chip's own Background is that same Bg2NavBrush at rest.
        // MouseLeave resets UNCONDITIONALLY (review fix): if _sessionChipNoLog flips to false while the
        // chip is hovered (Star Citizen's log path resolves mid-hover), a leave gated on the CURRENT flag
        // would skip the reset and strand the chip tinted/looking-clickable indefinitely - Bg2NavBrush is
        // the correct rest background in every non-hover case regardless of state, so resetting it
        // unconditionally is always safe. UpdateSessionChip's non-no-log branch also resets Background
        // unconditionally, so the same flip is covered even if it happens while the mouse never leaves.
        SessionChip.MouseEnter += (_, _) =>
        {
            if (_sessionChipNoLog) SessionChip.Background = (System.Windows.Media.Brush)FindResource("HighlightBrush");
        };
        SessionChip.MouseLeave += (_, _) =>
        {
            SessionChip.Background = (System.Windows.Media.Brush)FindResource("Bg2NavBrush");
        };
        SessionChip.MouseLeftButtonUp += (_, _) =>
        {
            if (!_sessionChipNoLog) return;
            Logger.Info("[UI] Session chip: opened Settings (no Game.log)");
            OpenSettingsGameTab();
        };
        UpdateOperatorIdentity();
        RefreshApprovedTools();
        RefreshOwnerTools();
        NexusApp.Services.GatePreview.Changed += () => Dispatcher.Invoke(OnGatePreviewChanged);
        App.ContractBoxVisibilityChanged += v => Dispatcher.Invoke(() => ApplyContractBoxVisible(v));
        // When an OCR scan first pairs with a log-detected haul, confirm it with a green flash of
        // the yellow contract box (mirrors the RS scan-success flash). No popup - toasts are removed
        // app-wide by design. HaulTracker.ContractPaired fires from ApplyAndNotify, which has two
        // raiser paths, both already UI-thread by the time they get here: the OCR path
        // (ApplyContractDetails, reached via App's own Dispatcher.Invoke around it) and the ordinary
        // Game.log path (TryApplyPending, reached via Ingest off the shared GameLogFeed, whose
        // DispatcherTimer sourcing is documented UI-thread at GameLogFeed.cs - "Events are raised
        // from the watcher's DispatcherTimer, i.e. on the UI thread"). No extra wrap needed here
        // (moved from App.xaml.cs's OnStartup wiring, app review Task 9; the Task 5 marshaling rule
        // already treats a same-thread re-wrap as redundant).
        App.Hauls.ContractPaired += h => FlashContractIndicator();
        // Keeps the price surfaces current as fetch cycles land. Fired on a worker thread;
        // BeginInvoke (not Invoke) matches the SettingsPage market subscription, since
        // Market.Dispose only drains an in-flight cycle for up to 3s.
        App.Market.Changed += () => Dispatcher.BeginInvoke(OnMarketDataChanged);
        MarketChipLabel.Text = MarketNotice.PillLabel;
        InitCodexSellToggle();
        // Amber edge on hover, the mock's affordance for the one status chip that is clickable.
        MarketChip.MouseEnter += (_, _) => MarketChip.BorderBrush = Hud.Br("AccentStrongBrush");
        MarketChip.MouseLeave += (_, _) => MarketChip.BorderBrush = Hud.Br("NavBorderBrush");
        RefreshMarketPill();
        _vm = new MainViewModel();
        DataContext = _vm;
        _vm.OcrValueReceived    += v => { _overlay?.ReceiveOcrValue(v); _scanIndicator?.FlashGreen(); };
        _vm.OcrPhaseReceived    += p => _overlay?.ReceiveScanPhase(p);
        _vm.OcrProgressReceived += c => _overlay?.ReceiveScanProgress(c);

        // RS Decoder match choreography: the scan-result surfaces are data-bound, so the reveal
        // is driven here. Watch the derived best-match for the full/settle decision, and the
        // recent-scan list for a genuine new row. Both defer to Loaded priority so the bound
        // visuals have been generated before they are animated.
        _vm.PropertyChanged += OnScanVmPropertyChanged;
        _vm.ScanHistory.CollectionChanged += OnScanHistoryChanged;

        _scanChipTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        // The status-strip refresh tick: the SCAN chip's poll, and the MARKET pill's only route to
        // the two states nothing raises an event for (a cycle STARTING, and the Settings toggle
        // being flipped). RefreshMarketPill returns immediately unless the state actually changed.
        _scanChipTimer.Tick += (_, __) => { UpdateScanChip(); RefreshMarketPill(); };
        _scanChipTimer.Start();
        UpdateScanChip();
        // Flip the SCAN chip to/from the paused (yellow) state the instant foreground relevance changes.
        App.ForegroundRelevanceChanged += _ => Dispatcher.Invoke(UpdateScanChip);
        // Pause/resume the RS auto-scan itself on the same signal - moved from App.xaml.cs's
        // OnForegroundRelevanceChanged (app review, Task 9), unwrapped exactly as it ran there
        // (that handler called SetScanForegroundActive directly, with no Dispatcher marshal).
        App.ForegroundRelevanceChanged += relevant => SetScanForegroundActive(relevant);

        KeyPopup.Closed += (_, __) => _keyPopupClosedAt = DateTime.UtcNow;

        // Ambient HUD glyphs: each always-populated tab carries its own signature looping animation, in the
        // spirit of the RS Decoder reticle. RS Decoder keeps its reticle and Network keeps its coverage donut.
        ReferenceGlyphHost.Content = Hud.AmbientGlyph(Hud.Ambient.SpectralAssay, 36);
        BlueprintGlyphHost.Content = Hud.AmbientGlyph(Hud.Ambient.Hologram, 46);
        WorkOrderGlyphHost.Content = Hud.AmbientGlyph(Hud.Ambient.OreConveyor, 38);

        // The empty-state reticle spins only when motion is allowed; under Reduce
        // Animations it reads as a static instrument.
        if (!Motion.Reduced && ReticleRing.RenderTransform is System.Windows.Media.RotateTransform rt)
        {
            var spin = new System.Windows.Media.Animation.DoubleAnimation(0, 360, TimeSpan.FromSeconds(9))
            { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever };
            rt.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, spin);
        }

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
        ApplyUiScale();
        UiScaleService.Changed += ApplyUiScale;
        SetActivePage("command");
        Closing += (s, e) => { SaveWindowPosition(); _vm.StopScanner(); _listTicker?.Stop(); _scanChipTimer?.Stop(); _eggTimer?.Stop(); _scanIndicator?.Close(); _contractIndicator?.Close(); };

        // The Codex hologram pauses when the app loses focus and resumes when it regains it -
        // an unfocused window has no business burning CPU on an ambient render loop.
        Activated   += (_, _) => _codexHologram?.Resume();
        Deactivated += (_, _) => _codexHologram?.Pause();

        BuildHistoryFilterPills();
        _vm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.HistoryFilter))
                BuildHistoryFilterPills();
        };

        RestoreScanRegion();

        // Coalesced: LoadWorkOrders (SaveWorkOrder's reseed) clears + re-adds every order, firing one
        // Reset and N Add notifications for N orders - scheduling the rebuild itself (same
        // BeginInvoke+queued-flag idiom as ScheduleWorkOrderAnimations below) collapses that storm to a
        // single rebuild instead of running the full gallery pass N+1 times per save.
        _vm.WorkOrders.CollectionChanged += (s, e) => ScheduleWorkOrderRebuild();

        Loaded += (s, e) => MaybeShowFirstRunWizard();
        Loaded += (s, e) => App.MaybeStartUpdateCheck();
    }

    // Applies the persisted App scale (issue #20): scales all window content via a
    // LayoutTransform on the root Grid, and raises the window minimums so the logical layout
    // never drops below its designed 900x600 floor. The window's own Width/Height are the
    // user's actual on-screen size and are left alone.
    private void ApplyUiScale()
    {
        var k = UiScaleService.AppScale;
        UiScaleService.ApplyTransform(RootLayout, k);
        // Popups render in their own PopupRoot HWND, a separate visual tree that does not inherit
        // RootLayout's LayoutTransform, so scale their content explicitly to match the window.
        // The suggest dropdown's width is recomputed against the scaled box each time it opens
        // (BlueprintSearch_TextChanged), so only the content transform is needed here.
        if (BlueprintSuggestPopup.Child is FrameworkElement suggestChild) UiScaleService.ApplyTransform(suggestChild, k);
        if (KeyPopup.Child is FrameworkElement keyChild) UiScaleService.ApplyTransform(keyChild, k);
        MinWidth = 900 * k;
        MinHeight = 600 * k;
        if (k != 1.0) Logger.Info($"[UI] Popup scale applied: {Math.Round(k * 100)}%");
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
        PageGuides.Visibility     = page == "guides"     ? Visibility.Visible : Visibility.Collapsed;
        PageTrade.Visibility      = page == "trade"      ? Visibility.Visible : Visibility.Collapsed;
        PagePlanner.Visibility    = page == "planner"    ? Visibility.Visible : Visibility.Collapsed;
        PageGridStudio.Visibility = page == "gridstudio" ? Visibility.Visible : Visibility.Collapsed;
        PageAdmin.Visibility      = page == "admin"      ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility   = page == "settings"   ? Visibility.Visible : Visibility.Collapsed;

        NavCommand.IsChecked  = page == "command";
        NavScan.IsChecked     = page == "scan";
        NavBlue.IsChecked     = page == "blueprints";
        NavRef.IsChecked      = page == "reference";
        NavWork.IsChecked     = page == "workorders";
        NavNetwork.IsChecked  = page == "network";
        NavHauling.IsChecked  = page == "hauling";
        NavGuides.IsChecked   = page == "guides";
        NavTrade.IsChecked    = page == "trade";
        NavPlanner.IsChecked  = page == "planner";
        NavGridStudio.IsChecked = page == "gridstudio";
        NavAdmin.IsChecked    = page == "admin";
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
            "guides"     => "Nexus - Mission Guides",
            "trade"      => "Nexus - Trade",
            "planner"    => "Nexus - Cargo Planner",
            "gridstudio" => "Nexus - Grid Studio",
            "admin"      => "Nexus - Admin",
            "settings"   => "Nexus - Settings",
            _            => "Nexus",
        };

        if (page == "blueprints") InitBlueprintBrowse();
        if (page == "reference") { BuildFilterPills(); BuildReferenceTree(staggerEntry: true); }
        if (page != "reference") _codexHologram?.Stop();   // leaving (or never on) the Codex - stop the ambient loop
        if (page != "command") _commandPage?.ResetEntrance();   // leaving (or never on) Operations - clear the entrance flag so it replays next open
        if (page == "workorders") RebuildWorkOrderList();
        if (page == "command") { InitCommandPage(); _commandPage?.PlayEntrance(); }
        if (page == "network") InitNetworkPage();
        if (page == "hauling") InitHaulingPage();
        if (page == "guides") InitGuidesPage();
        if (page == "trade") InitTradePage();
        if (page == "planner") InitPlannerPage();
        if (page == "gridstudio") InitGridStudioPage();
        if (page == "admin") InitAdminPage();
        if (page == "settings") InitSettingsPage();
        RefreshMarketConsent();
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
            "guides"     => PageGuides,
            "trade"      => PageTrade,
            "admin"      => PageAdmin,
            "settings"   => PageSettings,
            _            => (FrameworkElement?)null,
        });
    }

    // ── Market data consent strip ────────────────────────────────────────────
    // The three price-capable surfaces (RS Decoder, Mining Codex, Refinery Tracker) share one
    // host above the page stage, so the one-time question is asked once no matter which of them
    // the user opens first.
    private bool _marketConsentLogged;   // "shown" logged once per session, not on every page switch

    /// <summary>
    /// Fills or clears the market data consent host for the current page. Called by SetActivePage
    /// on every page switch and by the strip's own buttons, so answering collapses it immediately.
    /// The gate itself is pure (MarketNotice.ShouldShowConsent): unanswered consent only, never in
    /// the demo profile.
    /// </summary>
    private void RefreshMarketConsent()
    {
        if (MarketConsentHost == null) return;

        var show = _activePage is "scan" or "reference" or "workorders" or "trade"
                   && MarketNotice.ShouldShowConsent(App.Settings.Current.MarketDataEnabled, AppPaths.IsDemoProfile);
        if (!show)
        {
            MarketConsentHost.Content = null;
            MarketConsentHost.Visibility = Visibility.Collapsed;
            return;
        }

        if (!_marketConsentLogged)
        {
            _marketConsentLogged = true;
            Logger.Info("[NET] market consent strip shown");
        }

        var enable = Hud.StripButton(MarketNotice.ConsentEnable);
        enable.Click += (_, _) =>
        {
            App.Settings.Current.MarketDataEnabled = true;
            App.Settings.Save();
            Logger.Info("[NET] market consent: enabled");
            App.Market.MaybeAutoRefresh();
            RefreshMarketConsent();
            RefreshCodexPrices();
            RefreshMarketPill();   // the pill appears the moment the feature is turned on
            // Same fan-out reason as RefreshCodexPrices below: answering "Turn on" while standing on
            // TRADE has to repaint that page too, or all three of its flows keep showing the
            // "Turn on live market data..." message until the fetch's Changed lands.
            if (_activePage == "trade") _tradePage?.Refresh();
        };
        var decline = Hud.StripButton(MarketNotice.ConsentDecline);
        decline.Click += (_, _) =>
        {
            App.Settings.Current.MarketDataEnabled = false;
            App.Settings.Save();
            Logger.Info("[NET] market consent: declined");
            RefreshMarketConsent();
            RefreshCodexPrices();
            RefreshMarketPill();
        };
        // Both buttons are ghost StripButtons by design (mock review ruling): the accent
        // "Turn on" from the mock is deliberately NOT copied.
        MarketConsentHost.Content = Hud.NoticeStrip(MarketNotice.ConsentEyebrow, MarketNotice.ConsentBody,
                                                    new[] { enable, decline }, onDismiss: null);
        MarketConsentHost.Visibility = Visibility.Visible;
        // Both answers call RefreshCodexPrices, so answering "Turn on" while standing on the Codex
        // rebuilds the dossier with its price block as soon as a snapshot exists (a cached one
        // right away, a first-ever fetch when the cycle publishes) without the user having to
        // leave and re-enter the page. The decoder line needs no equivalent: it repaints on its
        // next decode.
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
        if (NavGuides.IsChecked == true)   return NavGuides;
        if (NavTrade.IsChecked == true)    return NavTrade;
        if (NavPlanner.IsChecked == true)  return NavPlanner;
        if (NavGridStudio.IsChecked == true) return NavGridStudio;
        if (NavAdmin.IsChecked == true)    return NavAdmin;
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

    /// <summary>Navigate to Settings > Game (the Game.log path controls). Used by the SESSION
    /// chip's no-log click-through and the custom-channel notice on Operations (issue #28).
    /// Navigation only - each caller logs its own accurate context line before calling this,
    /// since a shared log line here would misdescribe whichever caller didn't originate it.</summary>
    public void OpenSettingsGameTab()
    {
        SetActivePage("settings");
        _settingsPage?.SwitchToGameTab();
    }

    /// <summary>Re-evaluate the Settings GAME tab's needs-attention pip (issue #28). The
    /// custom-folder notice can be acknowledged from Operations, which is a different page, and
    /// SettingsPage is built once and kept - nothing re-runs its refresh on navigation - so the
    /// acknowledging view calls this. No-op until Settings has been opened at least once: the page's
    /// own constructor computes the pip from current state.</summary>
    public void RefreshSettingsGameDot() => _settingsPage?.RefreshGameDot();

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

    // Semantic status brushes for the header telemetry chips, looked up once from the palette
    // theme (OkBrush/DangerBrush/WarnBrush) instead of each chip painter allocating its own
    // Color.FromRgb literal (which had silently drifted from the palette's OkColor/DangerColor
    // tokens - review finding: status-chip green/red duplicated the theme tokens with mismatching
    // hex values). Single-theme app, no runtime palette swap, so one lookup per brush is enough.
    private readonly System.Windows.Media.Brush _chipOkBrush     = (System.Windows.Media.Brush)Application.Current.FindResource("OkBrush");
    private readonly System.Windows.Media.Brush _chipDangerBrush = (System.Windows.Media.Brush)Application.Current.FindResource("DangerBrush");
    private readonly System.Windows.Media.Brush _chipWarnBrush   = (System.Windows.Media.Brush)Application.Current.FindResource("WarnBrush");

    // SESSION chip's third state (app review: "Game.log health is invisible on the main window").
    // True while the effective Game.log path resolves to nothing - the identical condition
    // SettingsPage's GAME tab pip computes - which is orthogonal to whether Star Citizen is
    // running: a broken path with the game open would otherwise show a falsely reassuring green
    // "monitoring" while blueprint/hauling/shard tracking silently gets nothing. Only this state
    // makes the chip clickable.
    private bool _sessionChipNoLog;
    // Latest text from the shared Game.log tail's StatusChanged (via App.GameLog, which
    // republishes it - Task 5's GameLogFeed note), shown as the chip's tooltip only while
    // _sessionChipNoLog is true; the normal states keep the chip's static tooltip.
    private string? _lastGameLogStatus;
    private const string SessionChipDefaultTooltip = "Star Citizen session tracking (always on)";

    // Live SHARD telemetry chip in the header status strip (updates on shard join/leave).
    private void UpdateShardChip()
    {
        var s = App.Shards?.Current;
        if (s != null)
        {
            ShardChipText.Text = (string.IsNullOrWhiteSpace(s.Instance) ? s.Region : $"{s.Region} · {s.Instance}")
                + (s.Channel is "" or "LIVE" ? "" : $" · {s.Channel}");
            ShardDot.Fill = _chipOkBrush;
        }
        else
        {
            ShardChipText.Text = "not detected";
            ShardDot.Fill = (System.Windows.Media.Brush)FindResource("FgDimBrush");
        }
    }

    // Live SESSION telemetry chip in the header status strip: tracking is always on, so this confirms a
    // live game session (green, monitoring) vs Star Citizen being closed / shut down (red, offline). "Live"
    // is read from Game.log freshness (process-based - unchanged), so the chip flips off shortly after
    // the player exits the game. A third state (amber, "no log") is orthogonal to that: it fires whenever
    // the effective Game.log PATH resolves to nothing, regardless of whether Star Citizen is running, and
    // takes over the dot/text/tooltip and the chip's click affordance (app review: the old two-state
    // read let a broken path with the game open show a falsely reassuring green "monitoring").
    private void UpdateSessionChip()
    {
        if (App.GameLog == null || SessionChipText == null) return;
        bool live = App.GameLog.IsSessionLive;
        var brush = live ? _chipOkBrush : _chipDangerBrush;   // process-based; also drives the LED mirrors below

        _sessionChipNoLog = string.IsNullOrWhiteSpace(App.Settings.Current.GameLogPath)
            && !System.IO.File.Exists(GameLogWatcher.FindGameLog());

        if (_sessionChipNoLog)
        {
            SessionChipText.Text = "no log";
            SessionDot.Fill = _chipWarnBrush;
            SessionChipText.Foreground = _chipWarnBrush;
            Hud.PulseDot(SessionDot, false);
            SessionChip.ToolTip = _lastGameLogStatus ?? SessionChipDefaultTooltip;
            SessionChip.Cursor = Cursors.Hand;
        }
        else
        {
            SessionChipText.Text = (live ? "monitoring" : "offline")
                + GameChannels.ChipSuffix(App.GameLogFeed.ActiveChannel);
            SessionDot.Fill = brush;
            SessionChipText.Foreground = brush;
            Hud.PulseDot(SessionDot, live);   // the green LED gently flashes while a session is live
            SessionChip.ToolTip = SessionChipDefaultTooltip;
            SessionChip.Cursor = Cursors.Arrow;
            // Unconditional reset (review fix): if _sessionChipNoLog just flipped false while the chip
            // was hovered and mid-hover-tint, this resync keeps Background from staying stuck on
            // HighlightBrush - MouseLeave's own unconditional reset (above) covers the mirror case
            // (flip happens while the mouse is never over the chip at all).
            SessionChip.Background = (System.Windows.Media.Brush)FindResource("Bg2NavBrush");
        }

        // Mirror the SESSION LED on the dock-foot identity badge so they always agree:
        // green ONLINE while Star Citizen is running, red OFFLINE when it's closed.
        if (LinkDot != null)
        {
            LinkDot.Fill = brush;
            Hud.PulseDot(LinkDot, live);
        }
        if (LinkStatusText != null)
            LinkStatusText.Text = live ? "ONLINE . SECURE LINK" : "OFFLINE . NO LINK";

        // Same signal on the Operations dock tile badge: LIVE while Star Citizen runs,
        // OFFLINE once it's closed.
        if (OpsLiveDot != null)
        {
            OpsLiveDot.Fill = brush;
            Hud.PulseDot(OpsLiveDot, live);
        }
        if (OpsLiveText != null)
        {
            OpsLiveText.Text = live ? "LIVE" : "OFFLINE";
            OpsLiveText.Foreground = brush;
        }
    }

    // Live BLUEPRINTS telemetry chip: Auto-Track Blueprints is always on, so this confirms blueprint
    // auto-collection is active (green) while a game session is live, else off (red, SC closed).
    private void UpdateBlueprintChip()
    {
        if (App.GameLog == null || BlueprintChipText == null) return;
        bool tracking = App.GameLog.IsSessionLive && App.GameLog.AutoMark;
        BlueprintChipText.Text = tracking ? "tracking" : "off";
        var brush = tracking ? _chipOkBrush : _chipDangerBrush;
        BlueprintDot.Fill = brush;
        BlueprintChipText.Foreground = brush;
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
                ScanChipText.Foreground = _chipOkBrush;
                break;
            case ScanIndicator.Paused:
                ScanChipText.Text = "paused";
                ScanChipText.Foreground = _chipWarnBrush;
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

    // Mission Guides: lazily built on first visit, so nothing decodes until the page is opened.
    private GuidesPage? _guidesPage;
    private void InitGuidesPage()
    {
        if (_guidesPage == null)
        {
            _guidesPage = new GuidesPage();
            PageGuides.Children.Add(_guidesPage);
        }
        _guidesPage.Activate();
    }

    private TradePage? _tradePage;
    private void InitTradePage()
    {
        if (_tradePage == null)
        {
            _tradePage = new TradePage();
            PageTrade.Children.Add(_tradePage);
        }
        _tradePage.Refresh();
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

    // Ends both embedded browser viewports (Cargo Planner + Grid Studio) so the portable
    // self-swap can rename Web\cargo files without msedgewebview2 holding them open.
    public void ShutdownWebViewsForUpdate()
    {
        Logger.Info("[UPDATE] closing embedded browser views before the swap");
        _plannerPage?.ShutdownWebViewForUpdate();
        _gridStudioPage?.ShutdownWebViewForUpdate();
    }

    private AdminPage? _adminPage;
    private void InitAdminPage()
    {
        if (_adminPage == null)
        {
            _adminPage = new AdminPage(ShowLogMonitor, ShowAppLogMonitor);
            PageAdmin.Children.Add(_adminPage);
        }
        _adminPage.Refresh();
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

    // Owner-only Admin tab. Deliberately gated on the preview-BLIND owner check: the owner
    // must never be able to preview themselves out of the way back (Exit preview lives there).
    private void RefreshOwnerTools()
    {
        NavAdmin.Visibility = NexusApp.Services.OwnerGate.IsOwnerReal ? Visibility.Visible : Visibility.Collapsed;
    }

    // Preview flips what the gates report; the UI must tell the same story: re-gate the dock,
    // drop cached pages that captured gate state at build time so they rebuild honestly, and
    // if the page being viewed just vanished from the dock, land back on Operations.
    private void OnGatePreviewChanged()
    {
        try
        {
            RefreshApprovedTools();
            RefreshOwnerTools();
            // Dispose the embedded WebView2 controls before dropping the pages (same call
            // ShutdownWebViewsForUpdate makes for the swap path) - otherwise Children.Clear() drops
            // the only references to CargoPlannerPage/GridStudioPage while their native WebView2
            // control/process is still alive, leaking it until the whole app exits.
            _plannerPage?.ShutdownWebViewForUpdate();
            _gridStudioPage?.ShutdownWebViewForUpdate();
            _plannerPage = null; PagePlanner.Children.Clear();
            _gridStudioPage = null; PageGridStudio.Children.Clear();
            // The two cached pages were just cleared; if one of them is what the user is looking
            // at, land back on Operations regardless of the new gate state (a cleared page is
            // blank either way, and a BetaTester preview keeps the approved gate open).
            if (_activePage == "planner" || _activePage == "gridstudio")
                SetActivePage("command");
        }
        catch (Exception ex) { Logger.Error("[UI] admin: preview transition failed", ex); }
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

    // ── RS Decoder match choreography ─────────────────────────────────────────
    // The scan card, other-matches list, and recent-scan rail are all data-bound, so their reveal
    // is animated from the code-behind once WPF has generated the templated visuals. The tracker
    // gates the full reveal to a genuinely changed best match; a same-value rescan just settles.

    private readonly NexusApp.Services.ScanMotionTracker _scanMotion = new();
    private int? _lastRecentRs;   // top recent-scan row already revealed - dedupes cart/rebuild churn

    // ── RS Decoder deposit composition (G3) ───────────────────────────────────
    // Own cache instance (not shared with the overlay). Bar/rows/motion come from ScanCardComposition,
    // shared verbatim with the overlay so both surfaces stay in lockstep.
    private readonly NexusApp.Services.CompositionCache _composition = new(App.Data.GetCompositionForResource);
    private string? _expandedName;            // the single expanded OTHER MATCHES card (survives cart rebuilds)
    private FrameworkElement? _openRows;      // its live rows element, refreshed on each rebuild

    private void OnScanVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.BestMatch)) return;
        var name = _vm.BestMatch?.Resource?.Name;
        bool full = _scanMotion.ShouldChoreograph(name);
        // A genuinely new best match starts the OTHER MATCHES collapsed; a cart-toggle rebuild
        // (same match) keeps the open card open so its rows re-render in place, never re-animated.
        if (full) { _expandedName = null; _openRows = null; }
        // Defer: the ContentControl/ItemsControl content is regenerated during layout, so the
        // card parts and other-match containers do not exist until after this notification.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (full) PlayScanChoreography(name!);
            else if (!string.IsNullOrEmpty(name)) SettleScanResults();
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // A new scan inserts one row at the top of ScanHistory (the visible list is then rebuilt).
    // Animate only that single new row - never the whole rebuilt list - and only when it actually
    // surfaces at the top under the active filter and is not a cart/rebuild echo of the same row.
    private void OnScanHistoryChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) { _lastRecentRs = null; return; }
        if (e.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Add || e.NewStartingIndex != 0) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (Motion.Reduced) return;
            if (_vm.FilteredScanHistory.Count == 0 || _vm.ScanHistory.Count == 0) return;
            int topRs = _vm.FilteredScanHistory[0].Rs;
            if (topRs != _vm.ScanHistory[0].Rs) return;   // the new scan was filtered out - nothing new to reveal
            if (topRs == _lastRecentRs) return;           // same top as last time (e.g. cart refresh rebuild)
            _lastRecentRs = topRs;
            if (RecentScansList.ItemContainerGenerator.ContainerFromIndex(0) is UIElement row)
                FadeRise(row);
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // Full match reveal (frozen values, Item 3): hero fade+rise, the two corner brackets contracting
    // in from 8px outside, the rarity swatch wiping open, and the other-matches list cascading.
    private void PlayScanChoreography(string name)
    {
        Logger.Info($"[UI] Scan: match choreography ({name})");
        if (Motion.Reduced) return;

        FadeRise(BestMatchHost);   // hero card: fade 0-1 + rise 12px, 200ms, settle

        // Corner brackets: the ChamferPanel draws two L-brackets (top-right + bottom-left) grouped in
        // PART_Brackets. Each contracts diagonally in from 8px outside its resting corner while the
        // bracket layer fades 0.4 -> 1 (250ms, settle).
        if (FindByName(BestMatchHost, "PART_Brackets") is Grid brackets)
        {
            brackets.BeginAnimation(UIElement.OpacityProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0.4, 1, System.TimeSpan.FromMilliseconds(250))
                { EasingFunction = Motion.Settle });
            var dur = System.TimeSpan.FromMilliseconds(250);
            foreach (UIElement child in brackets.Children)
            {
                if (child is not Border b) continue;
                double dx = b.HorizontalAlignment == HorizontalAlignment.Right ? 8 : -8;
                double dy = b.VerticalAlignment == VerticalAlignment.Top ? -8 : 8;
                var t = new System.Windows.Media.TranslateTransform(dx, dy);
                b.RenderTransform = t;
                t.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty,
                    new System.Windows.Media.Animation.DoubleAnimation(dx, 0, dur) { EasingFunction = Motion.Settle });
                t.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
                    new System.Windows.Media.Animation.DoubleAnimation(dy, 0, dur) { EasingFunction = Motion.Settle });
            }
        }

        // Rarity swatch: ScaleX 0 -> 1 wiping open from the left edge (300ms, ease-out).
        if (FindByName(BestMatchHost, "RaritySwatch") is FrameworkElement swatch)
        {
            var scale = new System.Windows.Media.ScaleTransform(0, 1);
            swatch.RenderTransformOrigin = new Point(0, 0.5);
            swatch.RenderTransform = scale;
            scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0, 1, System.TimeSpan.FromMilliseconds(300))
                { EasingFunction = Motion.SlideOut });
        }

        // Other matches: reuse the dossier cascade (200ms each / 40ms stagger / 12px rise, cap 8).
        if (FindItemsHost(OtherMatchesList) is Panel host)
            CascadeIn(host.Children, maxAnimated: 8);
    }

    // Same-match rescan: no full reveal, just a quiet settle on the hero card
    // (120ms, opacity 0.55 -> 1) - the idiom shared with the Codex filter rebuild.
    private void SettleScanResults()
    {
        if (Motion.Reduced) return;
        BestMatchHost.BeginAnimation(UIElement.OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0.55, 1, System.TimeSpan.FromMilliseconds(120))
            {
                EasingFunction = new System.Windows.Media.Animation.QuadraticEase
                { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
            });
    }

    // Single-element fade 0-1 + rise 12px, 200ms settle - the CascadeIn treatment for one element.
    private static void FadeRise(UIElement el)
    {
        if (Motion.Reduced) return;
        var slide = new System.Windows.Media.TranslateTransform(0, 12);
        el.RenderTransform = slide;
        el.Opacity = 0;
        var dur = System.TimeSpan.FromMilliseconds(200);
        el.BeginAnimation(UIElement.OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0, 1, dur) { EasingFunction = Motion.Settle });
        slide.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new System.Windows.Media.Animation.DoubleAnimation(12, 0, dur) { EasingFunction = Motion.Settle });
    }

    // One-shot border color flash: amber -> cyan (peak at 45%) -> resting, 400ms, ease-out. Animates the Color of
    // a per-card SolidColorBrush clone (the caller must never pass a shared/frozen resource brush). FillBehavior.Stop
    // + resting base value means the stroke settles back to its resting color once the flash ends.
    private static void FlashBorder(System.Windows.Media.SolidColorBrush target, System.Windows.Media.Color resting)
    {
        var amber = System.Windows.Media.Color.FromRgb(0xFF, 0xB2, 0x3E);
        var cyan  = System.Windows.Media.Color.FromRgb(0x7F, 0xE9, 0xE0);
        var anim = new System.Windows.Media.Animation.ColorAnimationUsingKeyFrames
        {
            Duration = System.TimeSpan.FromMilliseconds(Motion.FlashMs),
            FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop,
        };
        anim.KeyFrames.Add(new System.Windows.Media.Animation.EasingColorKeyFrame(amber, System.Windows.Media.Animation.KeyTime.FromPercent(0.0)));
        anim.KeyFrames.Add(new System.Windows.Media.Animation.EasingColorKeyFrame(cyan, System.Windows.Media.Animation.KeyTime.FromPercent(0.45)) { EasingFunction = Motion.SlideOut });
        anim.KeyFrames.Add(new System.Windows.Media.Animation.EasingColorKeyFrame(resting, System.Windows.Media.Animation.KeyTime.FromPercent(1.0)) { EasingFunction = Motion.SlideOut });
        target.Color = resting;
        target.BeginAnimation(System.Windows.Media.SolidColorBrush.ColorProperty, anim);
    }

    // Status-chip cross-dissolve (frozen: 150ms - 75ms out, swap, 75ms in). The outgoing chip fades out over the
    // first half then removes itself; the incoming chip fades in over the second half.
    private static void CrossfadePill(FrameworkElement outgoing, FrameworkElement incoming)
    {
        var half = System.TimeSpan.FromMilliseconds(75);
        incoming.Opacity = 0;
        var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(1, 0, half) { EasingFunction = Motion.SlideOut };
        fadeOut.Completed += (_, _) => { if (outgoing.Parent is Panel p) p.Children.Remove(outgoing); };
        var fadeIn = new System.Windows.Media.Animation.DoubleAnimation(0, 1, half) { BeginTime = half, EasingFunction = Motion.Settle };
        outgoing.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        incoming.BeginAnimation(UIElement.OpacityProperty, fadeIn);
    }

    // Walk the visual tree for a named element. The scan card's parts live inside a DataTemplate,
    // so they are reachable only after generation, by name, rather than as compiled fields.
    private static DependencyObject? FindByName(DependencyObject root, string name)
    {
        int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement fe && fe.Name == name) return child;
            if (FindByName(child, name) is { } found) return found;
        }
        return null;
    }

    // The panel that hosts an ItemsControl's generated item containers (for CascadeIn).
    private static Panel? FindItemsHost(DependencyObject root)
    {
        int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is Panel p && p.IsItemsHost) return p;
            if (FindItemsHost(child) is { } found) return found;
        }
        return null;
    }

    // ── RS Decoder deposit composition (G3) ───────────────────────────────────
    // Frozen values: docs/superpowers/specs/2026-07-11-overlay-pass-values.md ("Part G additions").
    // Shared bar/rows/motion builders live in ScanCardComposition; only the per-surface expand
    // orchestration lives here (hero always open; other-matches tap-to-expand, one at a time).

    // Hero (BEST MATCH) composition: bar + CAN CONTAIN rows OPEN by default (it is the best match).
    // Built static/open - the hero card's lock-on FadeRise (PlayScanChoreography) carries the 200ms
    // fade+rise entrance, so the rows never animate separately. No-composition ores show no bar/rows.
    private void HeroComposition_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not StackPanel host || host.DataContext is not MatchResult m) return;
        host.Children.Clear();
        var parts = _composition.Get(m.Resource.Name);
        if (parts.Count == 0) return;

        host.Children.Add(ScanCardComposition.BuildBar(parts));
        var rows = ScanCardComposition.BuildExpandRows(parts);
        rows.Opacity = 1;   // open + static; the hero FadeRise animates the whole card
        host.Children.Add(rows);
    }

    // ── Market data fan-out ───────────────────────────────────────────────────
    // Single UI-thread landing point for a published snapshot: every price surface this window
    // owns refreshes from here, so the service keeps exactly one subscriber.
    private void OnMarketDataChanged()
    {
        RefreshHeroMarket();
        RefreshCodexPrices();
        RefreshWorkOrderSells();
        RefreshMarketPill();
    }

    // ── MARKET status pill (top strip) ────────────────────────────────────────
    // The strip's own chip chrome (MainWindow.xaml, between BLUEPRINTS and SCAN) carrying the state
    // of the live market data channel, per the approved mock (nexus-design-lab/market-data section
    // 07B). Not rendered at all when the feature is off - the same silence-over-placeholder rule
    // the price surfaces follow. Every state differs in TEXT as well as colour, so none of them
    // rides on colour alone.
    //
    // It is polled from the status-strip timer as well as fired from Changed because the service
    // raises Changed only at the END of a cycle (the same reason SettingsPage disables its refresh
    // button at click time): a cycle STARTING, and a Settings toggle flip - which raises nothing at
    // all - would otherwise never reach the pill. The cached state below makes the poll free and,
    // more importantly, keeps the breathing dot from being restarted every 1.5 seconds.
    private string? _marketPillState;
    private string? _marketPillText;
    // The tooltip is part of the cache key, not just the visuals: in the error state it carries
    // LastError, and two consecutive failures with DIFFERENT reasons produce the same state and the
    // same "offline" text. Comparing state and text alone would leave the first failure's reason
    // showing (a fast-failing cycle can start and fail between two 1.5s polls, so the busy state
    // that would otherwise break the tie is not guaranteed to be observed).
    private string? _marketPillTip;

    private void RefreshMarketPill()
    {
        if (MarketChip == null) return;

        var (state, text, tip) = MarketPillState();
        if (state == _marketPillState && text == _marketPillText && tip == _marketPillTip) return;
        _marketPillState = state;
        _marketPillText = text;
        _marketPillTip = tip;

        if (state == "off")
        {
            MarketChip.Visibility = Visibility.Collapsed;
            Hud.PulseDot(MarketDot, false);
            return;
        }

        var (dot, value) = state switch
        {
            "busy"  => (Hud.Br("AccentBrush"), Hud.Br("FgBrush")),
            "error" => (Hud.Br("DangerBrush"), Hud.Br("DangerBrush")),
            "fresh" => (Hud.Br("CyanBrush"),   Hud.Br("FgBrush")),
            _       => (Hud.Br("FgDimBrush"),  Hud.Br("FgDimBrush")),   // stale, nodata
        };

        MarketChip.Visibility = Visibility.Visible;
        MarketChip.ToolTip = tip;
        MarketChipText.Text = text;
        MarketChipText.Foreground = value;
        MarketDot.Fill = dot;
        Hud.PulseDot(MarketDot, state == "busy");   // amber breathe while a cycle runs; solid otherwise
    }

    // Which state the pill is in, its value text, and its tooltip. Priority: a refresh in flight is
    // the most current fact about the channel, so it outranks the previous cycle's error (which
    // comes back by itself if this cycle fails too). Staleness is measured off the refined price
    // stamp, not the snapshot's newest fetch, for the same reason the dossier's age note is: the
    // daily reference datasets would otherwise report day-old prices as fresh.
    private (string State, string Text, string Tip) MarketPillState()
    {
        // The demo profile never fetches (MarketDataService.ShouldFetch), so a pill there could
        // only ever read "no data" forever - it stays hidden, exactly like the Settings section
        // renders its inert Unavailable row instead of the live controls.
        if (AppPaths.IsDemoProfile || App.Settings.Current.MarketDataEnabled != true)
            return ("off", "", MarketNotice.PillTooltip);

        var snap = App.Market.Snapshot;
        var priced = snap is { } s && s.RefinedPrices.FetchedUtc != default;
        DateTime? clock = App.Settings.Current.LastMarketFetchUtc?.ToLocalTime()
                          ?? (priced ? DateTime.SpecifyKind(snap!.RefinedPrices.FetchedUtc, DateTimeKind.Utc).ToLocalTime() : null);

        if (App.Market.FetchInProgress)
            return ("busy", clock is { } c ? MarketNotice.PillClock(c) : MarketNotice.PillSyncing, MarketNotice.PillTooltip);
        if (App.Market.LastError is { } err)
            return ("error", MarketNotice.PillOffline, err);
        if (!priced)
            return ("nodata", MarketNotice.PillNoData, MarketNotice.PillTooltip);

        var age = DateTime.UtcNow - snap!.RefinedPrices.FetchedUtc;
        if (age > TimeSpan.FromHours(24))
            return ("stale", MarketNotice.PillAge(age), MarketNotice.PillTooltip);
        return ("fresh", clock is { } t ? MarketNotice.PillClock(t) : MarketNotice.PillNoData, MarketNotice.PillTooltip);
    }

    // The pill is a shortcut to the setting that governs it: mouse only, like every other control.
    private void MarketChip_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        InteractionLog.Click("market status pill", MarketChip);
        SetActivePage("settings");
    }

    // Rebuilds the Codex only while it is the page on screen; from anywhere else the prices are
    // picked up by SetActivePage's own BuildReferenceTree when the user next opens the Codex.
    // The rebuild is ours, not the user's, so the open dossier is carried across it: the dossier
    // is the Codex's only price surface (amendment 2026-07-27), and an hourly publish that threw
    // the reader back to the first ore would be the feature working against itself.
    private void RefreshCodexPrices()
    {
        if (_activePage != "reference") return;
        BuildReferenceTree(staggerEntry: false, preserveSelection: (_selectedRefCard?.Tag as Resource)?.Name);
    }

    // Rebuilds the work order gallery only while it is the page on screen (Task 12), same gating
    // as RefreshCodexPrices above; from anywhere else the new sell lines are picked up by
    // SetActivePage's own RebuildWorkOrderList call when the user next opens Refinery Tracker.
    private void RefreshWorkOrderSells()
    {
        if (_activePage == "workorders") RebuildWorkOrderList();
    }

    // ── RS Decoder live sell line (market data, Task 9) ────────────────────────
    // One line under the hero card: the best REFINED sell UEX has for this resource, with its age
    // or (when stale) the patch it was captured in - data honesty means a price never renders
    // without one of the two. Refined and not raw because UEX's raw ore-sales dataset has had no
    // community reports since patch 4.8 (amendment 2026-07-27). Silent (hairline included) unless
    // market data is on, a snapshot has landed, there is a best match, and UEX has a priced row.
    private StackPanel? _heroMarketHost;

    private void HeroMarket_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not StackPanel host) return;
        _heroMarketHost = host;
        // New decodes recreate the template (and re-fire Loaded), but a page switch away just
        // unloads this instance - drop the reference so Changed does not fill a detached panel.
        host.Unloaded += (_, _) => { if (_heroMarketHost == host) _heroMarketHost = null; };
        FillHeroMarket(host);
    }

    // Re-fills the hero market line whenever a fetch cycle publishes a new snapshot. A no-op
    // between decodes or while another page is active, when the field is null.
    private void RefreshHeroMarket()
    {
        if (_heroMarketHost is { } host) FillHeroMarket(host);
    }

    private void FillHeroMarket(StackPanel host)
    {
        host.Children.Clear();
        if (App.Settings.Current.MarketDataEnabled != true) return;
        if (App.Market.Snapshot is not { } snap) return;
        if (_vm.BestMatch is not { } m) return;

        var hit = MarketQueries.BestRefinedSell(snap, m.Resource.Name);
        if (hit is null) return;   // no priced row for this resource: render nothing, not a blank line

        host.Children.Add(new Border
        {
            Height = 1, Background = Hud.Br("NavBorderBrush"), Margin = new Thickness(0, 0, 0, 6),
        });

        var ageText = hit.Stale
            ? MarketNotice.PatchTag(hit.GameVersion)
            : MarketNotice.FormatAge(DateTime.UtcNow - hit.ModifiedUtc);
        var line = new TextBlock { FontSize = 12 };
        SellLineRuns(line, MarketNotice.DecoderLabel, hit, ageText);
        host.Children.Add(line);
    }

    // The mock renders every one-line sell surface SEGMENTED (mock .sellline: label dim, value
    // gold, terminal name Fg, age dim), and the first implementation flattened it to one brush
    // (owner ruling 2026-07-27, live run 5). The runs are composed from MarketNotice's own parts,
    // which the full-line formatters are also built from, so the rendered text and the string the
    // copy tests pin can never drift. A STALE line still drops to dim as a whole, so staleness
    // never has to be read off one segment's colour.
    private static void SellLineRuns(TextBlock line, string label, PriceHit hit, string ageText)
    {
        var dim = Hud.Br("FgDimBrush");
        line.Inlines.Add(new System.Windows.Documents.Run(label) { Foreground = dim });
        line.Inlines.Add(new System.Windows.Documents.Run(" " + MarketNotice.PriceValue(hit.Display))
        { Foreground = hit.Stale ? dim : Hud.Br("GoldBrush") });
        line.Inlines.Add(new System.Windows.Documents.Run(" " + MarketNotice.AtTerminal(hit.TerminalName))
        { Foreground = hit.Stale ? dim : Hud.Br("FgBrush") });
        line.Inlines.Add(new System.Windows.Documents.Run(" " + MarketNotice.AgePart(ageText)) { Foreground = dim });
    }

    // OTHER MATCHES composition: bar + collapsed rows, tap-to-expand. Idempotent - clears and
    // rebuilds on every (re)generation, honouring the current _expandedName so the open card
    // survives a cart-toggle rebuild without re-animating. No-composition cards stay inert.
    private void OtherMatchComposition_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not StackPanel host || host.DataContext is not MatchResult m) return;
        host.Children.Clear();
        var parts = _composition.Get(m.Resource.Name);

        if (parts.Count == 0)
        {
            if (FindCardPanel(host) is { } bare) bare.Cursor = Cursors.Arrow;
            return;
        }
        if (FindCardPanel(host) is { } card) card.Cursor = Cursors.Hand;

        host.Children.Add(ScanCardComposition.BuildBar(parts));
        var rows = ScanCardComposition.BuildExpandRows(parts);
        bool expanded = string.Equals(m.Resource.Name, _expandedName, StringComparison.OrdinalIgnoreCase);
        rows.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        host.Children.Add(rows);
        if (expanded) { _openRows = rows; rows.Opacity = 1; }   // static on rebuild, no re-entrance
    }

    // Card tap: toggle the composition rows (200ms fade+rise on expand; Reduced snaps). Bubbling
    // MouseLeftButtonDown, so the "Add" button (which handles its own click) never reaches here.
    // One card open at a time - opening a new one collapses the previously open one.
    private void OtherMatchCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement card || card.DataContext is not MatchResult m) return;
        if (FindByName(card, "CompositionHost") is not StackPanel { Children.Count: >= 2 } host) return;
        var rows = (FrameworkElement)host.Children[1];
        var name = m.Resource.Name;

        InteractionLog.Click($"scan composition {name}", card);

        if (string.Equals(name, _expandedName, StringComparison.OrdinalIgnoreCase))
        {
            ScanCardComposition.CollapseRows(rows);
            _expandedName = null;
            _openRows = null;
            return;
        }

        if (_expandedName != null && _openRows != null) ScanCardComposition.CollapseRows(_openRows); // one open at a time
        ScanCardComposition.ExpandRows(rows, animate: !Motion.Reduced);
        _expandedName = name;
        _openRows = rows;
    }

    // The ChamferPanel card root that owns a composition host (walk up the visual tree).
    private static FrameworkElement? FindCardPanel(DependencyObject from)
    {
        var d = from;
        while (d != null)
        {
            if (d is ChamferPanel cp) return cp;
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        }
        return null;
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
            // Cheap hardening only (low-priority finding, needs a double fault to matter): log a
            // Close() failure distinctly instead of letting it vanish into this catch block. If
            // Close() itself throws, OverlayWindow.OnClosed may not run to completion, leaving the
            // broken instance's App-level static-event subscriptions (Market/GameLog/Hauls/Shards/
            // OverlayGhostMode) alive even though _overlay is set to null right below.
            try { _overlay?.Close(); }
            catch (Exception closeEx) { Logger.Error("[WIN] overlay Close() failed while discarding a broken instance", closeEx); }
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

        if (result.Refused)
        {
            MessageBox.Show(this, result.Status, "Import owned blueprints", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (result.FilesScanned == 0)
        {
            MessageBox.Show(this,
                "Couldn't find a Star Citizen Game.log to scan. Set its location in Settings, then try again.",
                "Import owned blueprints", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (result.Applied) RefreshBlueprintOwnership();   // reflect the new ownership in the count + nav
    }

    // Blueprint Library → "Import from SCMDB…": reads a scmdb.net blueprint-tracking export and
    // marks recognized, completed blueprints owned (issue #3). FILE IMPORT ONLY - mirrors the
    // Game.log import's shape via the small ScmdbImportFlow rather than touching that flow;
    // RefreshBlueprintOwnership fires via App.GameLog.BulkOwnershipChanged (wired in the ctor),
    // same path the Game.log import already uses.
    private void BlueprintImportFromScmdb_Click(object sender, RoutedEventArgs e) => ScmdbImportFlow.Run(this);

    // Mirrors AppSettings.cs's own default property values (WindowLeft/Top/Width/Height) - the
    // rectangle a fresh install (or a saved rect that lands on no connected display) restores to.
    private static readonly Rect DefaultWindowRect = new(100, 100, 1280, 820);

    // Clamps the persisted rect onto the currently connected desktop before applying it: a window
    // last positioned on a second monitor that has since been disconnected, undocked, or resized
    // would otherwise restore fully or mostly off-screen with no in-app recovery path (mouse-only,
    // no menu bar). DIP-level clamping via SystemParameters is sufficient here - unlike the overlay,
    // MainWindow is a normal taskbar window with no Win32 physical-px positioning of its own.
    private void RestoreWindowPosition()
    {
        var s = App.Settings.Current;
        double left = s.WindowLeft, top = s.WindowTop, width = s.WindowWidth, height = s.WindowHeight;

        var virtualScreen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                                      SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        // Math.Max guards a corrupted/negative saved width or height, which Rect's constructor
        // would otherwise throw on.
        var saved = new Rect(left, top, Math.Max(width, 0), Math.Max(height, 0));

        Rect restore;
        if (!virtualScreen.IntersectsWith(saved))
        {
            // Saved rect intersects no connected display at all - fall back to the shipped
            // defaults rather than opening off-screen.
            restore = DefaultWindowRect;
            Logger.Info("[WIN] Main window position reset to defaults (saved rect off every display)");
        }
        else
        {
            // Clamp saved.Width/saved.Height (already Math.Max(.., 0)-guarded above), not the raw
            // width/height locals - a negative saved size (corrupted settings.json) still passes
            // IntersectsWith via its zero-area saved rect, and re-reading the raw negative value
            // here fed a negative Height/Width into the new Rect below, throwing ArgumentException
            // out of the MainWindow constructor (an unhandled startup crash) instead of clamping.
            double clampedWidth = Math.Min(saved.Width, virtualScreen.Width);
            double clampedHeight = Math.Min(saved.Height, virtualScreen.Height);
            double clampedLeft = Math.Clamp(left, virtualScreen.Left, virtualScreen.Left + virtualScreen.Width - clampedWidth);
            double clampedTop = Math.Clamp(top, virtualScreen.Top, virtualScreen.Top + virtualScreen.Height - clampedHeight);
            restore = new Rect(clampedLeft, clampedTop, clampedWidth, clampedHeight);
            if (clampedLeft != left || clampedTop != top || clampedWidth != width || clampedHeight != height)
                Logger.Info("[WIN] Main window position clamped onto the visible desktop");
        }

        Left = restore.Left; Top = restore.Top;
        Width = restore.Width; Height = restore.Height;
    }

    private void SaveWindowPosition()
    {
        App.Settings.Current.WindowLeft = Left; App.Settings.Current.WindowTop = Top;
        App.Settings.Current.WindowWidth = Width; App.Settings.Current.WindowHeight = Height;
        App.Settings.Save();
    }

}
