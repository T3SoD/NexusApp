using System.Runtime.InteropServices;
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
    // Guards SwitchTab's first call: the saved-tab restore at construction is not a user switch,
    // so it places the pill without motion and without logging (see SwitchTab).
    private bool _tabStripReady;
    private WorkOrderFlyoutWindow? _woFlyout;
    private RegionSelectorWindow? _regionSelector;   // single live draw-region overlay (issue #8)
    private RegionSelectorWindow? _contractRegionSelector;   // independent draw overlay for the contract region
    private bool _contractBoxVisible;
    // Overlay UI scale currently applied to RootScale (issue #20). Tracked so SaveBounds can divide
    // the on-screen size back to the persisted BASE size, and OnUiScaleChanged can resize live.
    private double _uiScale = 1.0;
    // Ghost rail scale, independent of _uiScale: the rail and its flyout render at THIS factor
    // while the panel renders at _uiScale. Base (unscaled) metrics live in GhostFootprints; every
    // physical-pixel footprint multiplies rail furniture by _railScale and the panel by _uiScale.
    private double _railScale = 1.0;

    // ── Deposit composition (Task 7, C3/C4) ────────────────────────────────────
    // One window-level cache: each resource's composition is loaded once, lazily, on
    // first card build (App.Data.GetCompositionForResource). Empty results cache too.
    private readonly CompositionCache _composition = new(App.Data.GetCompositionForResource);
    // Resource name of the single currently-expanded card, or null when none is open.
    // Keyed by name (not a UI reference) so it survives ScanResults rebuilds - a cart
    // toggle replaces every MatchResult with a `with`-clone and regenerates containers,
    // but Resource.Name is stable, so the same card re-renders expanded without re-firing
    // the entrance animation.
    private string? _expandedName;
    // Live rows element of the currently-expanded card, refreshed on every rebuild by the
    // expanded card's own build. Only read while _expandedName is non-null, so it is never
    // a stale/dead reference at that point.
    private FrameworkElement? _openRows;
    // Set only by ApplyExactAutoExpand (a real scan) to the exact match's name so that card's
    // first build plays the entrance animation once; consumed on that build. Cart-toggle
    // rebuilds leave it null, so the expanded card re-renders statically (no re-animation).
    private string? _animateExpandName;

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
    public void ShowScanTabForTutorial() => SwitchTab("scan", persist: false);

    /// <summary>Force the HUB tab visible so the tour can point at the status lights.</summary>
    public void ShowHubTabForTutorial() => SwitchTab("stats", persist: false);

    /// <summary>Force the HAULING tab visible so the tour can point at the contract scan controls.</summary>
    public void ShowHaulingTabForTutorial() => SwitchTab("hauling", persist: false);

    // Static-event handlers held as fields so OnClosed can detach them (a recreated overlay must not leak).
    private readonly Action<string> _onOrderReady;
    private readonly Action _onMarketChanged;

    public OverlayWindow(MainViewModel vm)
    {
        InitializeComponent();
        QuickSettingsBtn.Content = BuildHeaderGearGlyph();

        // Header close/quick-settings hover chips (issue #27 review: close-control parity with
        // the ghost rail's own hover chips, which already use this zeroed-chrome ToolTip pattern).
        CloseBtn.ToolTip = BuildHeaderHoverChip("CLOSE OVERLAY");
        ToolTipService.SetInitialShowDelay(CloseBtn, 150);
        ToolTipService.SetPlacement(CloseBtn, System.Windows.Controls.Primitives.PlacementMode.Bottom);
        QuickSettingsBtn.ToolTip = BuildHeaderHoverChip("QUICK SETTINGS");
        ToolTipService.SetInitialShowDelay(QuickSettingsBtn, 150);
        ToolTipService.SetPlacement(QuickSettingsBtn, System.Windows.Controls.Primitives.PlacementMode.Bottom);

        // Region-link underline-on-hover affordance (SCAN + HAULING "Set ... detection region" links).
        static void WireLinkHover(TextBlock link)
        {
            link.MouseEnter += (_, _) => link.TextDecorations = TextDecorations.Underline;
            link.MouseLeave += (_, _) => link.TextDecorations = null;
        }
        WireLinkHover(SetRegionBtn);
        WireLinkHover(SetContractRegionBtn);

        TabStrip.TabSelected += id => SwitchTab(id);

        // ── Ghost mode rail (issue #27). Wired unconditionally; the rail is collapsed and
        // inert until ApplyGhostMode turns the chrome on, so normal mode is unaffected. ──
        GhostRail.TabSelected += OnGhostTabSelected;
        GhostRail.CloseSelected += () => Close_Click(this, new RoutedEventArgs());
        GhostRail.GearSelected += ToggleGhostFlyout;
        GhostRail.DragRequested += _ =>
        {
            DragMove();                                // returns when the drag ends
            // Recompute the expand direction only when fully collapsed: while a panel or
            // the flyout is open, the layout is committed to _ghostDir and flipping it
            // mid-open would strand the rail on the wrong side (spec: recompute on drag
            // end applies to the rail).
            if (!_ghostPanelOpen && !_ghostFlyoutOpen)
            {
                var old = _ghostDir;
                var (dragWin, dragMon, _) = GhostContext();
                _ghostDir = GhostGeometry.DirectionFor(dragWin, dragMon);
                GhostRail.SetExpandDirection(_ghostDir);
                if (old != _ghostDir)
                    Logger.Info($"[WIN] Overlay ghost: rail dragged across screen center, expands {(_ghostDir == GhostExpandDirection.Left ? "left" : "right")}");
            }
            SaveBounds();
        };
        App.OverlayGhostModeChanged += OnGhostModeChanged;   // detached in OnClosed

        _vm = vm;
        // Lets the results ItemTemplate reach VM-level commands (ToggleCartCommand) via
        // RelativeSource AncestorType=Window, same pattern MainWindow uses. Nothing else in
        // this file's XAML binds to the inherited Window DataContext (results-card bindings
        // resolve against each item's own DataContext), so this is safe to introduce here.
        DataContext = _vm;

        // Chamfered shell: recompute the frame Path + the content clip whenever the PANEL is resized
        // (CanResizeWithGrip in normal mode, the ghost panel opening in ghost mode), so the MOBIGLAS
        // bevel tracks the panel rather than the window - in ghost mode the window also holds the
        // rail. Fires on first layout too.
        PanelHost.SizeChanged += (_, _) => UpdateChamfer();

        var s = App.Settings.Current;
        // Overlay scale (issue #20): persisted OverlayWidth/Height are the BASE (unscaled)
        // size; the on-screen window is base * scale so the layout keeps its designed logical
        // size and simply renders larger. SaveBounds divides by the same factor on the way out.
        _uiScale = UiScaleService.OverlayScale;
        _railScale = UiScaleService.GhostRailScale;
        Left = s.OverlayLeft;
        Top = s.OverlayTop;
        Width = s.OverlayWidth * _uiScale;
        Height = s.OverlayHeight * _uiScale;
        UiScaleService.ApplyTransform(RootScale, _uiScale);
        ApplyGhostScaleTransforms();
        HistoryStripRow.Height = new GridLength(s.OverlayHistoryHeight);

        // Restore the saved opacity, THEN attach the save-on-change handler (it is deliberately
        // not wired in XAML - see the comment on the slider). Attaching after the restore means
        // construction-time coercion can never clobber the saved value again.
        double opacity = Math.Clamp(s.OverlayOpacity, 0.2, 1.0);
        OpacitySlider.Value = opacity;
        this.Opacity = opacity;
        OpacityLabel.Text = $"{(int)(opacity * 100)}%";
        OpacitySlider.ValueChanged += OpacitySlider_ValueChanged;

        _vm.FilteredScanHistory.CollectionChanged += (s, e) => RebuildHistory();
        RebuildHistory();

        _vm.WorkOrders.CollectionChanged += (s, e) =>
        {
            UpdateRefineryTabBadge();
            if (IsTabPresented("orders")) RebuildOrdersPanel();
            if (IsTabPresented("stats")) RebuildStatsPanel();   // F1: READY ORDERS hero tile tracks the same count
        };
        _vm.ShoppingList.CollectionChanged += (s, e) => { if (IsTabPresented("shopping")) RebuildShoppingPanel(); };

        BuildOverlayHistoryFilterPills();
        _vm.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.HistoryFilter))
                BuildOverlayHistoryFilterPills();
            else if (e.PropertyName == nameof(MainViewModel.IsScanActive))
                SyncScanControls();
        };

        SwitchTab(OverlayTabs.NormalizeForRestore(s.OverlayActiveTab));

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

        // Live market prices on the scan cards: repaint the sell lines already on screen when a
        // fetch cycle publishes a new snapshot, instead of leaving an hour-old price up until the
        // next decode. Changed fires off the UI thread, so marshal (the same contract MainWindow's
        // market fan-out follows).
        _onMarketChanged = () => Dispatcher.BeginInvoke(RefreshMarketSellLines);
        App.Market.Changed += _onMarketChanged;

        // Foreground gating: when neither Nexus nor Star Citizen is in front, OCR auto-scans pause.
        // Re-sync the HUB scan LEDs so they flip to/from the yellow paused state as that happens.
        App.ForegroundRelevanceChanged += OnForegroundRelevanceChanged;

        // Contract scan/box are shared state: re-sync the contract toggle + box switch/LED whenever the
        // main Cargo Hauling page (or anything else) flips them, so the two surfaces never drift.
        App.ContractScan.RunningChanged += SyncContractFromShared;
        App.ContractScan.StageChanged += OnContractStageChanged;   // refresh the HAULING "last scan: ..." status line
        App.ContractBoxVisibilityChanged += OnContractBoxShared;
        _contractBoxVisible = App.ContractBoxVisible;   // seed from shared state so the switch isn't stale on first open

        UpdateHaulingTabBadge();   // initial overlay tab count (updates as hauls stream in)
        UpdateRefineryTabBadge();  // initial REFINERY (N) ready-to-collect badge

        BuildScanControls();
        BuildHaulingControls();
        BuildHubScanControls();
        BuildQuickAddPanel();      // E2 quick-add trigger + form (built once into QuickAddHost)

        // When an order turns ready, rebuild the orders panel if it is showing: the ready card flashes itself in
        // BuildOverlayOrderCard (pill fade + one-shot border flash). The old 4x opacity pulse on the dock button is gone.
        _onOrderReady = _ => { if (IsTabPresented("orders")) RebuildOrdersPanel(); };
        WorkOrderEditorPanel.OrderReadyToCollect += _onOrderReady;

        IsVisibleChanged += (_, e) =>
        {
            bool visible = (bool)e.NewValue;
            Logger.Info($"[WIN] overlay {(visible ? "shown" : "hidden")}");
            if (visible) Shown?.Invoke();
            else
            {
                if (_normalFlyoutOpen) CloseNormalFlyout(animate: false);
                SaveBounds();
                Hidden?.Invoke();
            }
        };

        // Overlay scale (issue #20): re-apply live when the Settings slider moves. Detached in OnClosed.
        UiScaleService.Changed += OnUiScaleChanged;
        UiScaleService.RailChanged += OnRailScaleChanged;   // detached in OnClosed
    }

    // ── Issue #7: click-through the overlay while the game hides the cursor (FPS / flight) ──────────
    // The overlay is a Topmost interactive window, so when the game hides the OS cursor the physical
    // mouse can still land on it and steal focus. While the overlay is visible we poll the cursor state
    // and pass the mouse straight through (WS_EX_TRANSPARENT) whenever the cursor is hidden. Gated by a
    // Settings toggle (default on); it becomes interactive again the instant the cursor is shown.
    private IntPtr _overlayHwnd;
    private System.Windows.Threading.DispatcherTimer? _cursorPoll;
    private bool _passThrough;

    // Two-tap destructive-action guards for the HAULING tab (Clear all + per-card x). A Button.Click
    // handler arms via TwoTapConfirm.Tap and registers its revert here; this list is polled from the
    // existing 150ms cursor-poll tick below (UpdateCursorPassThrough) instead of a dedicated timer, per
    // the no-new-timers constraint. Once IsArmed(now) goes false for an entry, its revert runs once and
    // the entry drops off the list.
    private readonly List<(TwoTapConfirm Confirm, Action Revert)> _armedConfirms = new();

    // Solid red "Sure?" fill for an armed two-tap confirm (mock value, not the softer DangerBrush text color).
    private static readonly SolidColorBrush ArmedConfirmBrush = MakeFrozenBrush(0xE5, 0x48, 0x4D);
    private static SolidColorBrush MakeFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _overlayHwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        _cursorPoll = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _cursorPoll.Tick += (_, _) => UpdateCursorPassThrough();
        IsVisibleChanged += (_, ev) =>
        {
            if ((bool)ev.NewValue) { _cursorPoll.Start(); UpdateCursorPassThrough(); }
            // Never leave either window click-through once the poll stops: the flyout can stay visible
            // when the overlay is hidden via the main-window toggle (only Close_Click hides the flyout),
            // so force it interactive here too or it would be click-dead until the overlay reappears.
            else { _cursorPoll.Stop(); SetPassThrough(false); _woFlyout?.SetPassThrough(false); }
        };
        if (IsVisible) { _cursorPoll.Start(); UpdateCursorPassThrough(); }

        // Ghost mode restore (issue #27): applied here rather than in the ctor because the chrome
        // swap needs the window handle (monitor lookup + MoveWindow). App.SetOverlayGhostMode is
        // deliberately not used - the setting is already true, so its no-op early return would skip
        // the apply; the direct call still logs.
        if (App.Settings.Current.OverlayGhostMode)
        {
            Logger.Info("[WIN] Overlay ghost mode: restored on");
            ApplyGhostMode(true, "restore");
        }
        else
        {
            // A saved position from a monitor layout that no longer exists would strand the
            // overlay off every screen (custom chrome, no taskbar entry, no OS recovery).
            // Ghost mode already clamps through GhostGeometry; give normal mode the same guard.
            var (win, mon, _) = GhostContext();
            var clamped = GhostGeometry.Clamp(win, mon);
            if (Math.Abs(clamped.Left - win.Left) > 0.5 || Math.Abs(clamped.Top - win.Top) > 0.5)
            {
                GhostApplyRect(clamped);
                Logger.Info("[WIN] Overlay position clamped onto the nearest monitor");
            }
        }
    }

    private void UpdateCursorPassThrough()
    {
        bool passThrough = App.Settings.Current.OverlayPassThroughWhenCursorHidden && IsCursorHidden();
        SetPassThrough(passThrough);
        // Mirror the same decision onto the Refinery Tracker flyout: it is a second Topmost
        // interactive window anchored to the overlay, and was otherwise never click-through
        // when the game hides the cursor (issue A4).
        if (_woFlyout != null && _woFlyout.IsVisible) _woFlyout.SetPassThrough(passThrough);

        PollArmedConfirms();
    }

    // Revert any armed two-tap confirm ("Sure?") whose window has lapsed without a second tap, restoring
    // its resting visual. Piggybacks this tick rather than a dedicated timer (no new timers).
    private void PollArmedConfirms()
    {
        if (_armedConfirms.Count == 0) return;
        var now = DateTime.UtcNow;
        for (int i = _armedConfirms.Count - 1; i >= 0; i--)
        {
            if (_armedConfirms[i].Confirm.IsArmed(now)) continue;
            _armedConfirms[i].Revert();
            _armedConfirms.RemoveAt(i);
        }
    }

    // Toggle WS_EX_TRANSPARENT so the OS routes the mouse to the game below when true. No-op when the
    // desired state already matches, so the [WIN] log records only real transitions.
    private void SetPassThrough(bool on)
    {
        if (on == _passThrough || _overlayHwnd == IntPtr.Zero) return;
        _passThrough = on;
        int ex = GetWindowLong(_overlayHwnd, GWL_EXSTYLE);
        SetWindowLong(_overlayHwnd, GWL_EXSTYLE, on ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT);
        Logger.Info(on
            ? "[WIN] overlay input: pass-through (game cursor hidden)"
            : "[WIN] overlay input: interactive (game cursor shown)");
    }

    // True when the OS cursor is currently hidden (Star Citizen hides it in FPS / flight / vehicle).
    private static bool IsCursorHidden()
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        return GetCursorInfo(ref ci) && (ci.flags & CURSOR_SHOWING) == 0;
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int CURSOR_SHOWING = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public POINT ptScreenPos; }

    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO pci);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    // ── Ghost mode (issue #27) ─────────────────────────────────────────────────────────────
    // One window, two chromes. In ghost mode the overlay IS the 44px icon rail; clicking a rail
    // glyph grows the window to rail + panel and slides the panel out beside it, clicking the same
    // glyph collapses back. Every footprint is computed in PHYSICAL pixels through GhostGeometry and
    // applied with MoveWindow, never WPF Left/Top, because under Per-Monitor-DPI V2 DIP positioning
    // lands on the wrong monitor across a DPI boundary (issue #6 lesson). Normal mode never reaches
    // any of this: every ghost path is gated on _ghostActive.
    // ExitGhost runs synchronously and must never pump a nested message loop (no dialogs, no
    // DragMove) because queued ApplyGhostMode calls rely on _ghostActive not being observed mid-teardown.
    private bool _ghostActive;
    private bool _ghostPanelOpen;
    private bool _ghostFlyoutOpen;
    private GhostExpandDirection _ghostDir = GhostExpandDirection.Left;
    private int _ghostMotionGen;
    // The gear flyout's own slide transform, created once in EnsureGhostFlyoutBuilt and reused for
    // every open/close (mirrors PanelSlide, which is XAML-declared because PanelHost always exists;
    // GhostFlyoutHost's content is built lazily, so its transform is created in code alongside it).
    private TranslateTransform? _flyoutSlide;
    // The flyout's mirrored controls, kept as fields so ReseedFlyoutControls can re-seed them from
    // shared state on every open, not just the one-time build in EnsureGhostFlyoutBuilt (fix round
    // 1, Findings 2 and 3): the header OpacitySlider, App.Settings.Current.OverlayGhostMode, the
    // click-through setting and the ghost rail scale are all reachable while the flyout is closed,
    // so a construction-time-only seed goes stale.
    private Slider? _flyoutOpacitySlider;
    private TextBlock? _flyoutOpacityPercentLabel;
    private Hud.ToggleSwitch? _flyoutGhostSwitch;
    private Hud.ToggleSwitch? _flyoutPassSwitch;
    private Slider? _flyoutRailSlider;
    private TextBlock? _flyoutRailPercentLabel;
    // Guards the opacity slider's re-seed so setting its Value doesn't echo back into OpacitySlider
    // through ValueChanged (OpacitySlider owns apply + persist; the flyout only mirrors it).
    private bool _seedingFlyoutOpacity;
    // Same guard, for the rail-size slider mirroring UiScaleService.GhostRailScale.
    private bool _seedingFlyoutRail;

    // Window + monitor rects in physical px. The window rect comes straight from the OS so it is
    // exact at any monitor DPI; the DIP-derived value is only the fallback for the moment before a
    // handle exists (and ActualWidth is 0 before the first layout - the OnSourceInitialized restore
    // path runs pre-Show - so the ctor-set Width/Height stand in there).
    private (PxRect Win, PxRect Mon, double Dpi) GhostContext()
    {
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double w = ActualWidth > 0 ? ActualWidth : Width, h = ActualHeight > 0 ? ActualHeight : Height;
        var win = new PxRect(Left * dpi, Top * dpi, w * dpi, h * dpi);
        var mon = new PxRect(0, 0, SystemParameters.PrimaryScreenWidth * dpi, SystemParameters.PrimaryScreenHeight * dpi);
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out var wr) && wr.right > wr.left)
            win = new PxRect(wr.left, wr.top, wr.right - wr.left, wr.bottom - wr.top);
        var m = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (m != IntPtr.Zero && GetMonitorInfo(m, ref mi))
            mon = new PxRect(mi.rcMonitor.left, mi.rcMonitor.top,
                             mi.rcMonitor.right - mi.rcMonitor.left, mi.rcMonitor.bottom - mi.rcMonitor.top);
        return (win, mon, dpi);
    }

    private void GhostApplyRect(PxRect r)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        MoveWindow(hwnd, (int)Math.Round(r.Left), (int)Math.Round(r.Top),
                   (int)Math.Round(r.Width), (int)Math.Round(r.Height), repaint: true);
    }

    // The single mode-apply path. App.SetOverlayGhostMode broadcasts; this reacts.
    private void OnGhostModeChanged(bool on, string source)
        => Dispatcher.BeginInvoke(new Action(() => ApplyGhostMode(on, source)));

    private void ApplyGhostMode(bool on, string source)
    {
        if (_ghostActive == on) return;
        // Only the enter path flips the flag here. ExitGhost clears it itself, after its own
        // SaveBounds, so SaveBounds' size guard stays armed across the whole teardown (see there).
        if (on) { _ghostActive = true; EnterGhost(restore: source == "restore"); }
        else ExitGhost();
        RefreshCloseGlyphTooltip();
    }

    // Ghost transitions shrink the overlay to (or toward) the rail; a Refinery Tracker flyout
    // anchored to it must not survive them, or a full work-order list floats beside the
    // minimal rail (review 2026-07-28). Reopening is one click on the ORDERS panel.
    private void HideRefineryTrackerForGhost()
    {
        if (_woFlyout is { IsVisible: true })
        {
            _woFlyout.Hide();
            Logger.Info("[WIN] Refinery tracker hidden (ghost transition)");
        }
    }

    // True when this tab's content is actually on screen: normal mode shows the active tab
    // always; ghost mode only while its panel is open. Data-change handlers gate on this so
    // nothing rebuilds (or re-arms the ORDERS ticker) behind a collapsed rail.
    private bool IsTabPresented(string tab) => _activeTab == tab && (!_ghostActive || _ghostPanelOpen);

    // The rail (and its flyout) render at _railScale while RootScale carries _uiScale, so both
    // carry an extra ratio transform. Re-applied whenever either scale changes.
    private void ApplyGhostScaleTransforms()
    {
        var r = _railScale / _uiScale;
        UiScaleService.ApplyTransform(GhostRail, r);
        UiScaleService.ApplyTransform(GhostFlyoutHost, r);
    }

    private void EnterGhost(bool restore)
    {
        HideRefineryTrackerForGhost();
        var (win, mon, dpi) = GhostContext();
        LeaveActiveTabForGhost();                      // stop per-tab timers; the panel is going away
        GhostSnapToRailChrome();                       // collapsed rail chrome, nothing mid-flight
        TabStrip.Visibility = Visibility.Collapsed;    // the rail is the nav in ghost mode
        GhostEyebrow.Visibility = Visibility.Visible;  // shows only when the panel does; harmless while collapsed
        GhostRail.Visibility = Visibility.Visible;
        // The grip must not resize the rail: in ghost mode the footprint is mode-derived, and a
        // dragged size would be thrown away on the next expand or collapse anyway.
        ResizeMode = ResizeMode.NoResize;
        var (railWpx, railHpx) = GhostFootprints.CollapsedSize(_railScale, dpi);
        // A live toggle hugs the screen-nearest edge of the panel footprint the user is looking at.
        // A restore must not: the saved position ALREADY is the rail's own last spot (SaveBounds
        // keeps OverlayLeft/Top on the rail while ghost is on), and hugging from the panel-sized
        // window the ctor just built would walk the rail toward the screen edge every launch.
        var from = restore ? new PxRect(win.Left, win.Top, railWpx, railHpx) : win;
        var rail = GhostGeometry.CollapsedRect(from, mon, railWpx, railHpx);
        _ghostDir = GhostGeometry.DirectionFor(rail, mon);
        GhostRail.SetExpandDirection(_ghostDir);
        GhostApplyRect(rail);
        GhostModeSwapFade();
        SaveBounds(rail);
    }

    private void ExitGhost()
    {
        var (win, mon, dpi) = GhostContext();
        var k = _uiScale;
        // If a panel or the flyout is open (the Settings page toggle can fire any time), the rail
        // sits on the _ghostDir side of the expanded window, NOT at win.Left.
        var railOnly = _ghostPanelOpen || _ghostFlyoutOpen
            ? CurrentRailRect(win, dpi)
            : new PxRect(win.Left, win.Top, GhostFootprints.RailW * _railScale * dpi, win.Height);
        GhostSnapToRailChrome();                       // orphans any in-flight slide, clears its animations
        GhostRail.Visibility = Visibility.Collapsed;
        GhostEyebrow.Visibility = Visibility.Collapsed;
        TabStrip.Visibility = Visibility.Visible;
        PanelHost.Visibility = Visibility.Visible;
        ResizeMode = ResizeMode.CanResizeWithGrip;     // the grip belongs to the normal panel again
        var s = App.Settings.Current;
        var target = GhostGeometry.ExpandedRect(railOnly, mon, GhostGeometry.DirectionFor(railOnly, mon),
                                                s.OverlayWidth * k * dpi, s.OverlayHeight * k * dpi);
        GhostApplyRect(target);
        SwitchTab(_activeTab, persist: false);         // rebuild + restart per-tab timers (idempotent)
        GhostModeSwapFade();
        // Save while STILL flagged ghost, then clear the flag. Window.Width is not guaranteed to have
        // caught up with the MoveWindow above, so an unguarded save here would write the ghost
        // footprint into the user's OverlayWidth/Height - the exact corruption the guard exists to
        // prevent - and int-rounding would drift the saved size across repeated toggles even when it
        // has caught up. Ghost never touches OverlayWidth/Height; the restored window already IS the
        // persisted size, so there is nothing to re-save. _ghostPanelOpen and _ghostFlyoutOpen are
        // both false by now (GhostSnapToRailChrome), so the left adjustment falls through to plain Left.
        SaveBounds();
        _ghostActive = false;
    }

    // Spec: mode switches crossfade (~180ms). The window resize itself is instant; fade the
    // scaled content back in. Uses RootScale.Opacity so the persisted window Opacity setting
    // is never touched. Reduced-motion: skip.
    private void GhostModeSwapFade()
    {
        if (Motion.Reduced) return;
        RootScale.BeginAnimation(UIElement.OpacityProperty, null);
        var fade = new System.Windows.Media.Animation.DoubleAnimation(
            0.5, 1.0, TimeSpan.FromMilliseconds(Motion.DialogOpenMs))
        {
            EasingFunction = Motion.Settle,
        };
        // The fill-behavior end value is 1.0, the resting value, so no Completed teardown is needed.
        RootScale.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    // Mirror of SwitchTab's leave-tab branches, for collapse (leaving without a new tab).
    private void LeaveActiveTabForGhost()
    {
        _guidesHangarLine?.Stop();
        _ordersTicker?.Stop();
        _ordersTicker = null;
    }

    // Strips any in-flight panel slide/fade and resets PanelHost to its collapsed-chrome resting
    // state: hidden, no margin, full opacity. Mirrors the reset GhostReturnToCollapsed/
    // GhostSnapToRailChrome already do inline; pulled out so ToggleGhostFlyout can reach the same
    // reset when it orphans an in-flight panel collapse whose own Completed handler was the only
    // other path here (fix round 1, Finding 1). Window placement and SaveBounds stay each caller's
    // own concern.
    private void ResetPanelHostToCollapsed()
    {
        PanelSlide.BeginAnimation(TranslateTransform.XProperty, null);
        PanelHost.BeginAnimation(UIElement.OpacityProperty, null);
        PanelSlide.X = 0;
        PanelHost.Opacity = 1;
        PanelHost.Visibility = Visibility.Collapsed;
        PanelHost.Margin = new Thickness(0);
    }

    // Snap the ghost chrome back to rail-only with no motion and no window move: drops any
    // in-flight slide (clocks AND their held end values, which would otherwise win over plain
    // assignment), closes panel + flyout state, and resets the panel's transform and margin.
    // Placement stays with the caller, which is what lets mode exit and a scale change reuse it.
    private void GhostSnapToRailChrome()
    {
        HideRefineryTrackerForGhost();
        _ghostMotionGen++;                             // orphan any in-flight slide
        if (_ghostPanelOpen) LeaveActiveTabForGhost();
        _ghostPanelOpen = false;
        RefreshCloseGlyphTooltip();
        _ghostFlyoutOpen = false;
        // Normal mode's header gear can leave its own flyout state behind when ghost mode engages
        // mid-open (Settings toggle, live from anywhere): reset both here so a later normal-mode
        // QuickSettings_Click never mistakes stale state for an already-open flyout, and the ghost
        // flyout's own footprint (which never sets Margin) is not left offset by the normal-mode
        // bottom-right placement (issue #27 review).
        _normalFlyoutOpen = false;
        GhostFlyoutHost.Margin = new Thickness(0);
        PanelSlide.BeginAnimation(TranslateTransform.XProperty, null);
        PanelHost.BeginAnimation(UIElement.OpacityProperty, null);
        PanelSlide.X = 0;
        PanelHost.Opacity = 1;
        PanelHost.Visibility = Visibility.Collapsed;
        PanelHost.Margin = new Thickness(0);
        GhostFlyoutHost.Visibility = Visibility.Collapsed;
        GhostRail.SetActive(null);
        GhostRail.SetGearActive(false);
        GhostRail.HorizontalAlignment = HorizontalAlignment.Stretch;   // rail alone fills the window
    }

    // ── Ghost panel expand / collapse ──────────────────────────────────────────────────────
    private void OnGhostTabSelected(string tab)
    {
        CloseGhostFlyout(animate: false);
        if (_ghostPanelOpen && _activeTab == tab) { CollapseGhostPanel(); return; }
        ExpandGhostPanel(tab);
    }

    private void ExpandGhostPanel(string tab)
    {
        var gen = ++_ghostMotionGen;
        var wasOpen = _ghostPanelOpen;
        _ghostPanelOpen = true;
        RefreshCloseGlyphTooltip();
        var (win, mon, dpi) = GhostContext();
        var s = App.Settings.Current;
        // Where the rail is has to come from the WINDOW's actual footprint, not from _ghostPanelOpen.
        // CollapseGhostPanel clears that flag immediately but the window stays expanded for the whole
        // 150ms slide, so a re-click inside that window would read the EXPANDED rect as the rail and
        // recompute _ghostDir from its centre. The window would not move, but the layout would flip
        // sides and the rail would jump a panel width, which the next collapse then persists. Measuring
        // the footprint is correct on every entry path (collapsed, open, mid-collapse, flyout open).
        bool windowIsRailOnly = win.Width <= GhostFootprints.RailOnlyThreshold(_railScale, dpi);
        var railRect = windowIsRailOnly
            ? win                                       // collapsed: the window IS the rail
            : CurrentRailRect(win, dpi);                // expanded: rail keeps its on-screen spot
        _ghostDir = GhostGeometry.DirectionFor(railRect, mon);
        GhostRail.SetExpandDirection(_ghostDir);
        var (totalW, totalH) = GhostFootprints.ExpandedSize(_railScale, _uiScale, s.OverlayWidth, s.OverlayHeight, dpi);
        GhostApplyRect(GhostGeometry.ExpandedRect(railRect, mon, _ghostDir, totalW, totalH));
        // Layout: rail on the screen-edge side, panel on the center side, 2px seam. The rail's
        // LOCAL width stays the base 44 - its own ratio transform supplies the size difference -
        // so the panel's reservation is the ratio-scaled rail plus seam in RootScale-local units.
        bool railRightSide = _ghostDir == GhostExpandDirection.Left;
        GhostRail.HorizontalAlignment = railRightSide ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        GhostRail.Width = GhostFootprints.RailW;
        double railPad = (GhostFootprints.RailW + GhostFootprints.Seam) * (_railScale / _uiScale);
        PanelHost.Margin = railRightSide ? new Thickness(0, 0, railPad, 0) : new Thickness(railPad, 0, 0, 0);
        PanelHost.Visibility = Visibility.Visible;
        GhostEyebrow.Text = OverlayTabs.LabelFor(tab);
        SwitchTab(tab);                                 // real switch: persists, logs, starts timers
        GhostRail.SetActive(tab);
        Logger.Info($"[WIN] Overlay ghost: expand {tab}");
        // Slide only on open-from-collapsed. Switching tabs while open swaps content in place
        // (SwitchTab's visibility swap), per the mock: no re-slide.
        if (!wasOpen) AnimateGhostPanel(gen, opening: true, railRightSide);
    }

    private void CollapseGhostPanel()
    {
        HideRefineryTrackerForGhost();
        var gen = ++_ghostMotionGen;
        _ghostPanelOpen = false;
        RefreshCloseGlyphTooltip();
        var tab = _activeTab;
        LeaveActiveTabForGhost();
        GhostRail.SetActive(null);
        Logger.Info($"[WIN] Overlay ghost: collapse ({tab})");
        bool railRightSide = _ghostDir == GhostExpandDirection.Left;
        AnimateGhostPanel(gen, opening: false, railRightSide, onDone: GhostReturnToCollapsed);
    }

    // The shared "back to rail-only" completion: hide whatever was open, shrink the window to the
    // rail's collapsed footprint where the rail currently sits, and persist the new position.
    private void GhostReturnToCollapsed()
    {
        PanelHost.Visibility = Visibility.Collapsed;
        GhostFlyoutHost.Visibility = Visibility.Collapsed;
        var (win, mon, dpi) = GhostContext();
        var (cw, ch) = GhostFootprints.CollapsedSize(_railScale, dpi);
        var collapsed = GhostGeometry.CollapsedRect(CurrentRailRect(win, dpi), mon, cw, ch);
        GhostApplyRect(collapsed);
        GhostRail.HorizontalAlignment = HorizontalAlignment.Stretch;
        PanelHost.Margin = new Thickness(0);
        SaveBounds(collapsed);
    }

    // The rail's physical rect inside the current expanded window, at the CURRENT rail scale.
    private PxRect CurrentRailRect(PxRect win, double dpi) => CurrentRailRectAt(win, _railScale, dpi);

    // Same, at an explicit rail scale: a rail-scale change has to measure where the rail sits at
    // the OLD factor before adopting the new one, which the current-scale wrapper cannot express.
    private PxRect CurrentRailRectAt(PxRect win, double railK, double dpi)
    {
        double railWpx = GhostFootprints.RailW * railK * dpi;
        double left = _ghostDir == GhostExpandDirection.Left ? win.Right - railWpx : win.Left;
        return new PxRect(left, win.Top, railWpx, win.Height);
    }

    private void AnimateGhostPanel(int gen, bool opening, bool railRightSide, Action? onDone = null)
    {
        // Captured BEFORE the clocks are cleared: while a clock is live these properties read the
        // animated current value (0 at rest), so a re-click mid-slide can continue from where the
        // panel actually is instead of snapping to the full off/on extreme.
        double curX  = PanelSlide.X;
        double curOp = PanelHost.Opacity;
        PanelSlide.BeginAnimation(TranslateTransform.XProperty, null);
        PanelHost.BeginAnimation(UIElement.OpacityProperty, null);
        // Local (pre-scale) units: PanelSlide is a RenderTransform inside the LayoutTransform-scaled
        // tree. Taken from the persisted panel width rather than PanelHost.ActualWidth, which is a
        // layout pass behind on the open that just resized the window.
        double under = (railRightSide ? 1 : -1) * App.Settings.Current.OverlayWidth;
        if (Motion.Reduced)
        {
            PanelSlide.X = 0; PanelHost.Opacity = opening ? 1 : 0;
            onDone?.Invoke();
            return;
        }
        double fromX  = opening ? (curX != 0 ? curX : under) : curX;
        double toX    = opening ? 0 : under;
        double fromOp = opening ? (curX != 0 ? curOp : 0.5) : curOp;
        double ms = opening ? Motion.GhostInMs : Motion.GhostOutMs;
        var ease = opening ? Motion.Settle : Motion.SlideOut;
        var slide = new System.Windows.Media.Animation.DoubleAnimation(
            fromX, toX, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease };
        var fade = new System.Windows.Media.Animation.DoubleAnimation(
            fromOp, opening ? 1 : 0, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease };
        slide.Completed += (_, _) =>
        {
            if (gen != _ghostMotionGen) return;    // superseded; this clock is orphaned
            PanelSlide.BeginAnimation(TranslateTransform.XProperty, null);
            PanelHost.BeginAnimation(UIElement.OpacityProperty, null);
            PanelSlide.X = 0; PanelHost.Opacity = 1;
            onDone?.Invoke();
        };
        PanelSlide.BeginAnimation(TranslateTransform.XProperty, slide);
        PanelHost.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    // ── Ghost settings flyout ──────────────────────────────────────────────────────────────
    // The gear's flyout: a small chamfered shell (same gradient/border recipe as the rail's own
    // shell) holding a Ghost mode toggle, the click-through toggle, the opacity slider and the rail
    // size slider, built once lazily into GhostFlyoutHost the first time either gear is used.
    // Reachable from two gears (issue #27 review): the ghost rail's own gear (ToggleGhostFlyout,
    // mutually exclusive with the tab panel, shares the panel's "return to collapsed rail"
    // completion GhostReturnToCollapsed) and the header gear in normal mode (QuickSettings_Click /
    // CloseNormalFlyout below, tracked by its own _normalFlyoutOpen flag so the two modes never
    // confuse each other's notion of "the flyout is open").
    private bool _normalFlyoutOpen;

    private void QuickSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_ghostActive) { ToggleGhostFlyout(); return; }   // header visible with a ghost panel open
        if (_normalFlyoutOpen) { CloseNormalFlyout(animate: true); return; }
        var gen = ++_ghostMotionGen;
        _normalFlyoutOpen = true;
        EnsureGhostFlyoutBuilt();
        ReseedFlyoutControls();
        GhostFlyoutHost.HorizontalAlignment = HorizontalAlignment.Right;
        GhostFlyoutHost.VerticalAlignment = VerticalAlignment.Bottom;
        GhostFlyoutHost.Margin = new Thickness(0, 0, 14, 14);   // clear of the resize grip
        GhostFlyoutHost.Visibility = Visibility.Visible;
        Logger.Info("[WIN] Overlay quick settings open (normal)");
        AnimateGhostFlyout(gen, opening: true, railRightSide: true);
    }

    private void CloseNormalFlyout(bool animate)
    {
        if (!_normalFlyoutOpen) return;
        _normalFlyoutOpen = false;
        Logger.Info("[WIN] Overlay quick settings closed (normal)");
        var gen = ++_ghostMotionGen;
        if (animate)
        {
            AnimateGhostFlyout(gen, opening: false, railRightSide: true,
                onDone: () => { GhostFlyoutHost.Visibility = Visibility.Collapsed; GhostFlyoutHost.Margin = new Thickness(0); });
            return;
        }
        _flyoutSlide?.BeginAnimation(TranslateTransform.XProperty, null);
        GhostFlyoutHost.BeginAnimation(UIElement.OpacityProperty, null);
        if (_flyoutSlide != null) _flyoutSlide.X = 0;
        GhostFlyoutHost.Opacity = 1;
        GhostFlyoutHost.Visibility = Visibility.Collapsed;
        GhostFlyoutHost.Margin = new Thickness(0);
    }

    // Re-seeds every mirrored control from shared state. Called on EVERY open (ghost gear and
    // header gear alike), not just the one-time build in EnsureGhostFlyoutBuilt: the header
    // OpacitySlider, App.Settings.Current.OverlayGhostMode, the click-through setting and the ghost
    // rail scale are all reachable while the flyout is closed (settings page, header slider, the
    // other gear's own flyout), so a construction-time-only seed goes stale (fix round 1, Findings
    // 2 and 3, extended here to the click-through and rail-size rows). The opacity and rail seeds
    // are guarded so they do not echo back into OpacitySlider / UiScaleService.SetGhostRailScale
    // through their own ValueChanged; SetOnSilently already does the toggles' equivalent by design.
    private void ReseedFlyoutControls()
    {
        _seedingFlyoutOpacity = true;
        try
        {
            _flyoutOpacitySlider!.Value = OpacitySlider.Value;
            _flyoutOpacityPercentLabel!.Text = $"{(int)(OpacitySlider.Value * 100)}%";
        }
        finally
        {
            _seedingFlyoutOpacity = false;
        }
        _flyoutGhostSwitch!.SetOnSilently(App.Settings.Current.OverlayGhostMode);
        _flyoutPassSwitch!.SetOnSilently(App.Settings.Current.OverlayPassThroughWhenCursorHidden);
        _seedingFlyoutRail = true;
        try
        {
            _flyoutRailSlider!.Value = UiScaleService.GhostRailScale;
            _flyoutRailPercentLabel!.Text = $"{Math.Round(UiScaleService.GhostRailScale * 100)}%";
        }
        finally { _seedingFlyoutRail = false; }
    }

    private void ToggleGhostFlyout()
    {
        if (_ghostFlyoutOpen) { CloseGhostFlyout(animate: true); return; }
        if (_ghostPanelOpen) CollapseGhostPanel();     // mutually exclusive, spec rule
        // Bumped AFTER CollapseGhostPanel (which bumped its own generation to start its slide-out
        // above): this orphans that now-superseded completion so it can never fire
        // GhostReturnToCollapsed and undo the resize below, and gives the flyout's own opening
        // slide a fresh token of its own.
        var gen = ++_ghostMotionGen;
        _ghostFlyoutOpen = true;
        // Orphaning CollapseGhostPanel's completion above also orphans its ONLY path back to a
        // clean PanelHost (GhostReturnToCollapsed, which only ever runs from AnimateGhostPanel's
        // Completed). Without this, a gear click during an in-flight collapse leaves PanelHost
        // Visible with a stale margin and mid-fade opacity for as long as the flyout stays open.
        // A no-op when no collapse was in flight, since these are just resting-state assignments
        // (fix round 1, Finding 1).
        ResetPanelHostToCollapsed();
        EnsureGhostFlyoutBuilt();
        ReseedFlyoutControls();
        // The header gear's normal-mode flyout leaves its own bottom-right placement behind
        // (Margin, VerticalAlignment); reset both before this footprint takes over, or the ghost
        // flyout renders offset (issue #27 review). GhostSnapToRailChrome resets the same Margin
        // on the reverse transition (normal-open -> ghost-enter, via EnterGhost).
        GhostFlyoutHost.Margin = new Thickness(0);
        GhostFlyoutHost.VerticalAlignment = VerticalAlignment.Bottom;
        ApplyFlyoutFootprint();
        bool railRightSide = _ghostDir == GhostExpandDirection.Left;
        GhostFlyoutHost.Visibility = Visibility.Visible;
        GhostRail.SetGearActive(true);
        Logger.Info("[WIN] Overlay ghost: settings flyout open");
        AnimateGhostFlyout(gen, opening: true, railRightSide);
    }

    // Sizes and places the rail+flyout window for the CURRENT scales and rail position, and
    // returns the window rect it applied. Reused by ToggleGhostFlyout (open) and by a rail-scale
    // change while the flyout is open, so the flyout can resize live under the user's cursor
    // without closing. Visibility, the gear state and the slide stay the caller's own concern.
    // knownRail: an optional rail rect the caller already measured (e.g. at the OLD rail scale
    // before a live rescale) - the caller measured the rail at the old scale, and deriving from
    // the un-resized window with the NEW threshold could misjudge near the monitor midline.
    private PxRect ApplyFlyoutFootprint(PxRect? knownRail = null)
    {
        var (win, mon, dpi) = GhostContext();
        PxRect railRect;
        if (knownRail is { } known)
        {
            railRect = known;
        }
        else
        {
            // Same windowIsRailOnly test ExpandGhostPanel uses: on the open path CollapseGhostPanel
            // only resizes the window on its own (now-orphaned) completion, so the window here can
            // still be the wider panel-expanded footprint - read the rail's true position from it
            // rather than assuming the window is already collapsed.
            bool windowIsRailOnly = win.Width <= GhostFootprints.RailOnlyThreshold(_railScale, dpi);
            railRect = windowIsRailOnly ? win : CurrentRailRect(win, dpi);
        }
        _ghostDir = GhostGeometry.DirectionFor(railRect, mon);
        GhostRail.SetExpandDirection(_ghostDir);
        var (fw, fh) = GhostFootprints.FlyoutSize(_railScale, dpi);
        var applied = GhostGeometry.ExpandedRect(railRect, mon, _ghostDir, fw, fh);
        GhostApplyRect(applied);
        bool railRightSide = _ghostDir == GhostExpandDirection.Left;
        GhostRail.HorizontalAlignment = railRightSide ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        GhostRail.Width = GhostFootprints.RailW;
        GhostFlyoutHost.HorizontalAlignment = railRightSide ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        return applied;
    }

    private void CloseGhostFlyout(bool animate)
    {
        if (!_ghostFlyoutOpen) return;
        var gen = ++_ghostMotionGen;
        _ghostFlyoutOpen = false;
        GhostRail.SetGearActive(false);
        Logger.Info("[WIN] Overlay ghost: settings flyout closed");
        bool railRightSide = _ghostDir == GhostExpandDirection.Left;
        if (animate)
        {
            AnimateGhostFlyout(gen, opening: false, railRightSide, onDone: GhostReturnToCollapsed);
            return;
        }
        // Snap (no animation): drop any in-flight clock and its held end values, then go straight
        // to the shared return-to-rail completion - used when a rail tab click closes an open
        // flyout to make way for a panel (OnGhostTabSelected).
        _flyoutSlide?.BeginAnimation(TranslateTransform.XProperty, null);
        GhostFlyoutHost.BeginAnimation(UIElement.OpacityProperty, null);
        if (_flyoutSlide != null) _flyoutSlide.X = 0;
        GhostFlyoutHost.Opacity = 1;
        GhostReturnToCollapsed();
    }

    // Mirrors AnimateGhostPanel for the flyout: slide + fade on GhostFlyoutHost's own
    // TranslateTransform. FlyoutW (not the persisted panel width) is the offscreen offset, since
    // the flyout shell is a fixed 230px and never resizes.
    private void AnimateGhostFlyout(int gen, bool opening, bool railRightSide, Action? onDone = null)
    {
        // Captured BEFORE the clocks are cleared: while a clock is live these properties read the
        // animated current value (0 at rest), so a re-click mid-slide can continue from where the
        // flyout actually is instead of snapping to the full off/on extreme.
        double curX  = _flyoutSlide!.X;
        double curOp = GhostFlyoutHost.Opacity;
        _flyoutSlide!.BeginAnimation(TranslateTransform.XProperty, null);
        GhostFlyoutHost.BeginAnimation(UIElement.OpacityProperty, null);
        double under = (railRightSide ? 1 : -1) * GhostFootprints.FlyoutW;
        if (Motion.Reduced)
        {
            _flyoutSlide.X = 0; GhostFlyoutHost.Opacity = opening ? 1 : 0;
            onDone?.Invoke();
            return;
        }
        double fromX  = opening ? (curX != 0 ? curX : under) : curX;
        double toX    = opening ? 0 : under;
        double fromOp = opening ? (curX != 0 ? curOp : 0.5) : curOp;
        double ms = opening ? Motion.GhostInMs : Motion.GhostOutMs;
        var ease = opening ? Motion.Settle : Motion.SlideOut;
        var slide = new System.Windows.Media.Animation.DoubleAnimation(
            fromX, toX, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease };
        var fade = new System.Windows.Media.Animation.DoubleAnimation(
            fromOp, opening ? 1 : 0, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease };
        slide.Completed += (_, _) =>
        {
            if (gen != _ghostMotionGen) return;    // superseded; this clock is orphaned
            _flyoutSlide.BeginAnimation(TranslateTransform.XProperty, null);
            GhostFlyoutHost.BeginAnimation(UIElement.OpacityProperty, null);
            _flyoutSlide.X = 0; GhostFlyoutHost.Opacity = 1;
            onDone?.Invoke();
        };
        _flyoutSlide.BeginAnimation(TranslateTransform.XProperty, slide);
        GhostFlyoutHost.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    // Builds the flyout's content once, lazily, the first time the gear is used: a chamfered
    // shell (same gradient/border recipe as OverlayGhostRail's own shell) behind an eyebrow, a
    // Ghost mode toggle, and the opacity slider.
    private void EnsureGhostFlyoutBuilt()
    {
        if (GhostFlyoutHost.Child != null) return;

        var shell = new System.Windows.Shapes.Path
        {
            StrokeThickness = 1,
            SnapsToDevicePixels = true,
            Fill = BuildGhostFlyoutShellBrush(),
        };
        shell.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "BorderBrush");

        var content = new StackPanel { Margin = new Thickness(12, 10, 12, 10) };

        content.Children.Add(new TextBlock
        {
            Text = "OVERLAY SETTINGS", FontSize = 8.5, Opacity = 0.85,
            Foreground = Hud.Br("AccentBrush"),
        });

        var ghostRow = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        ghostRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ghostRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var ghostLabel = new TextBlock
        {
            Text = "Ghost mode", FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center,
            Foreground = Hud.Br("FgBrush"),
        };
        _flyoutGhostSwitch = new Hud.ToggleSwitch(true)
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            // Both directions now (issue #27 review): the flyout is reachable from normal mode too
            // (header gear), so turning it on here is a real transition, not just off.
            // App.SetOverlayGhostMode is the single write path (it already saves + logs); the
            // resulting EnterGhost/ExitGhost safely tears the calling flyout down itself either way.
            // The initial `true` here only covers this first build; ReseedFlyoutControls re-seeds
            // from App.Settings.Current.OverlayGhostMode via SetOnSilently on every subsequent open
            // (fix round 1, Finding 3), so drift while the flyout is closed does not stick.
            OnToggled = on => App.SetOverlayGhostMode(on, "flyout"),
        };
        Grid.SetColumn(ghostLabel, 0);
        Grid.SetColumn(_flyoutGhostSwitch, 1);
        ghostRow.Children.Add(ghostLabel);
        ghostRow.Children.Add(_flyoutGhostSwitch);
        content.Children.Add(ghostRow);

        var passRow = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        passRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        passRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var passLabel = new TextBlock
        {
            Text = "Click-through in FPS", FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center,
            Foreground = Hud.Br("FgBrush"),
        };
        _flyoutPassSwitch = new Hud.ToggleSwitch(App.Settings.Current.OverlayPassThroughWhenCursorHidden)
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            OnToggled = on => App.SetOverlayPassThrough(on, "flyout"),
        };
        Grid.SetColumn(passLabel, 0); Grid.SetColumn(_flyoutPassSwitch, 1);
        passRow.Children.Add(passLabel); passRow.Children.Add(_flyoutPassSwitch);
        content.Children.Add(passRow);

        var opacityRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 14, 0, 0),
        };
        opacityRow.Children.Add(new TextBlock
        {
            Text = "OPACITY", FontSize = 8.5, VerticalAlignment = VerticalAlignment.Center,
            Foreground = Hud.Br("FgDimBrush"),
        });
        _flyoutOpacitySlider = new Slider
        {
            Style = (Style)FindResource("HudSlider"),
            Minimum = 0.2, Maximum = 1.0, SmallChange = 0.05, Width = 120,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 6, 0),
        };
        _flyoutOpacityPercentLabel = new TextBlock
        {
            FontFamily = Hud.Font("MonoFont"), FontSize = 9,
            Foreground = Hud.Br("FgDimBrush"), VerticalAlignment = VerticalAlignment.Center,
        };
        // Seed BEFORE attaching ValueChanged - the same construction-order lesson as the header
        // opacity slider (:100-107): wiring the handler first would let construction-time coercion
        // fire and stomp the seeded value. This only covers the first build; ToggleGhostFlyout
        // re-seeds both on every subsequent open (fix round 1, Finding 2), guarded by
        // _seedingFlyoutOpacity so that later re-seed does not echo into OpacitySlider below.
        _flyoutOpacitySlider.Value = OpacitySlider.Value;
        _flyoutOpacityPercentLabel.Text = $"{(int)(OpacitySlider.Value * 100)}%";
        _flyoutOpacitySlider.ValueChanged += (_, e) =>
        {
            if (_seedingFlyoutOpacity) return;         // re-seed from OpacitySlider, not a user edit
            // Single write path: the header's own OpacitySlider_ValueChanged owns apply + persist
            // + its own label. This only mirrors the percent readout onto the flyout.
            OpacitySlider.Value = e.NewValue;
            _flyoutOpacityPercentLabel.Text = $"{(int)(e.NewValue * 100)}%";
        };
        opacityRow.Children.Add(_flyoutOpacitySlider);
        opacityRow.Children.Add(_flyoutOpacityPercentLabel);
        // Mouse-only nudge, same contract as the header's own OpacityRow_MouseWheel: one wheel
        // notch = one SmallChange step, applied through the slider's own ValueChanged.
        opacityRow.PreviewMouseWheel += (_, e) =>
        {
            _flyoutOpacitySlider!.Value += e.Delta > 0 ? _flyoutOpacitySlider.SmallChange : -_flyoutOpacitySlider.SmallChange;
            e.Handled = true;
        };
        content.Children.Add(opacityRow);

        var railRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 14, 0, 0),
        };
        railRow.Children.Add(new TextBlock
        {
            Text = "RAIL SIZE", FontSize = 8.5, VerticalAlignment = VerticalAlignment.Center,
            Foreground = Hud.Br("FgDimBrush"),
        });
        _flyoutRailSlider = new Slider
        {
            Style = (Style)FindResource("HudSlider"),
            Minimum = UiScaleService.RailMin, Maximum = UiScaleService.Max,
            SmallChange = UiScaleService.Step, TickFrequency = UiScaleService.Step, IsSnapToTickEnabled = true,
            Width = 104, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 6, 0),
        };
        _flyoutRailPercentLabel = new TextBlock
        {
            FontFamily = Hud.Font("MonoFont"), FontSize = 9,
            Foreground = Hud.Br("FgDimBrush"), VerticalAlignment = VerticalAlignment.Center,
        };
        _flyoutRailSlider.Value = UiScaleService.GhostRailScale;
        _flyoutRailPercentLabel.Text = $"{Math.Round(UiScaleService.GhostRailScale * 100)}%";
        _flyoutRailSlider.ValueChanged += (_, e) =>
        {
            if (_seedingFlyoutRail) return;
            UiScaleService.SetGhostRailScale(e.NewValue);   // live; RailChanged drives the footprint
            _flyoutRailPercentLabel.Text = $"{Math.Round(e.NewValue * 100)}%";
        };
        _flyoutRailSlider.LostMouseCapture += (_, _) =>
            Logger.Info($"[UI] Ghost rail scale: {Math.Round(UiScaleService.GhostRailScale * 100)}% (flyout)");
        railRow.Children.Add(_flyoutRailSlider);
        railRow.Children.Add(_flyoutRailPercentLabel);
        railRow.PreviewMouseWheel += (_, e) =>
        {
            var before = _flyoutRailSlider!.Value;
            _flyoutRailSlider.Value += e.Delta > 0 ? UiScaleService.Step : -UiScaleService.Step;
            e.Handled = true;
            // A wheel scroll never captures the mouse, so LostMouseCapture above never fires for a
            // wheel-only interaction (review 2026-07-28): log each notch here instead. Guarded on an
            // actual value change so a scroll held past Min or Max does not spam identical lines.
            if (_flyoutRailSlider.Value != before)
                Logger.Info($"[UI] Ghost rail scale: {Math.Round(UiScaleService.GhostRailScale * 100)}% (flyout)");
        };
        content.Children.Add(railRow);

        var host = new Grid();
        host.Children.Add(shell);
        host.Children.Add(content);
        GhostFlyoutHost.SizeChanged += (_, _) =>
            shell.Data = Hud.ChamferGeometry(GhostFootprints.FlyoutW, GhostFlyoutHost.ActualHeight, 10);

        GhostFlyoutHost.Child = host;
        _flyoutSlide = new TranslateTransform();
        GhostFlyoutHost.RenderTransform = _flyoutSlide;
    }

    // The rail's gear glyph at header size (vector, 1.5 stroke, FgDim like the rail's resting gear).
    private static Viewbox BuildHeaderGearGlyph()
    {
        var canvas = new Canvas { Width = 24, Height = 24 };
        var gear = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(OverlayGhostRail.GearPathData),
            StrokeThickness = 1.5,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        };
        gear.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "FgDimBrush");
        canvas.Children.Add(gear);
        var dot = new System.Windows.Shapes.Path
        {
            Data = new EllipseGeometry(new Point(12, 12), 3, 3),
            StrokeThickness = 1.5,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        };
        dot.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "FgDimBrush");
        canvas.Children.Add(dot);
        return new Viewbox { Width = 14, Height = 14, Child = canvas };
    }

    // Explicit ToolTip with its chrome zeroed out (OverlayGhostRail.BuildHoverChip's pattern,
    // duplicated here per that file's documented precedent - a bare string ToolTip renders
    // default light chrome, which would halo this dark HUD header).
    private static ToolTip BuildHeaderHoverChip(string label)
    {
        var text = new TextBlock { Text = label, FontSize = 8.5 };
        text.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");
        var chip = new Border { Padding = new Thickness(7, 2, 7, 2), BorderThickness = new Thickness(1), Child = text };
        chip.SetResourceReference(Border.BackgroundProperty, "Bg3Brush");
        chip.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        return new ToolTip
        {
            Content = chip, Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), Padding = new Thickness(0), HasDropShadow = false,
        };
    }

    // OverlayBgTopColor/OverlayBgBotColor are raw Colors, not brushes, so the gradient is
    // assembled here too (same recipe as OverlayGhostRail.BuildShellBrush, which is private to
    // that file - the duplication mirrors that file's own documented precedent).
    private static LinearGradientBrush BuildGhostFlyoutShellBrush()
    {
        Color Col(string key) => Application.Current.TryFindResource(key) is Color c ? c : Colors.Transparent;
        return new LinearGradientBrush(Col("OverlayBgTopColor"), Col("OverlayBgBotColor"), new Point(0, 0), new Point(0, 1));
    }

    // Ghost placement interop, the same MonitorFromWindow / GetMonitorInfo / MoveWindow trio the
    // region selector uses for its own physical-pixel placement (RegionSelectorWindow.xaml.cs:60-70).
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO mi);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    // Paint the chamfered shell: the FramePath silhouette (bevelled fill + 1px border) and the matching
    // clip on ContentRoot so inner content stays inside the TL + BR bevels.
    // 16px chamfer: the app's shipped silhouette (predates the 2026-07 overlay mocks; their tables
    // were corrected to 16 - owner ruling 2026-07-28).
    private void UpdateChamfer()
    {
        // PanelHost lives INSIDE the scaled tree, so its ActualWidth/ActualHeight are already in
        // the local (unscaled) space the geometry is drawn in - no divide by _uiScale (issue #20).
        // Reading the panel rather than the window is also what makes ghost mode correct: there the
        // window additionally holds the 44px rail, which the bevel must not stretch across (#27).
        var geo = Hud.ChamferGeometry(PanelHost.ActualWidth, PanelHost.ActualHeight, 16);
        FramePath.Data = geo;
        ContentRoot.Clip = geo;
    }

    // Re-applies the overlay scale live when the Settings slider moves. The window grows or
    // shrinks around its top-left corner so the logical layout size never changes.
    private void OnUiScaleChanged()
    {
        var k = UiScaleService.OverlayScale;
        if (k == _uiScale) return;
        var old = _uiScale;

        // Ghost mode (issue #27): the window footprint is mode-derived (rail, or rail plus panel
        // or flyout), so scaling it by the old Width/Height ratio would size the rail like a panel.
        // Read where the rail sits, then rebuild the collapsed footprint - a half-open panel does
        // not survive a scale change. The rail's own size rides _railScale, which this change does
        // not touch, so the rail rect measures the same before and after.
        if (_ghostActive)
        {
            var (win, mon, dpi) = GhostContext();
            var railRect = _ghostPanelOpen || _ghostFlyoutOpen ? CurrentRailRect(win, dpi) : win;
            _uiScale = k;
            UiScaleService.ApplyTransform(RootScale, k);
            ApplyGhostScaleTransforms();
            GhostSnapToRailChrome();
            var (cw, ch) = GhostFootprints.CollapsedSize(_railScale, dpi);
            var collapsed = GhostGeometry.CollapsedRect(railRect, mon, cw, ch);
            GhostApplyRect(collapsed);
            SaveBounds(collapsed);
            _woFlyout?.ApplyUiScale(k);
            Logger.Info($"[WIN] Overlay ghost: scale {old:0.##} -> {k:0.##}, collapsed to the rail");
            return;
        }

        _uiScale = k;
        UiScaleService.ApplyTransform(RootScale, k);
        ApplyGhostScaleTransforms();
        Width = Width / old * k;
        Height = Height / old * k;
        UpdateChamfer();
        _woFlyout?.ApplyUiScale(k);
    }

    // Re-applies the rail scale live. Collapsed: rebuild the collapsed footprint where the rail
    // sits. Panel open: a half-open panel does not survive a scale change (same rule as the
    // overlay scale), snap to the rail. Flyout open: the change is probably being driven from
    // the flyout's own slider, so re-apply the footprint live instead of closing it.
    private void OnRailScaleChanged()
    {
        var k = UiScaleService.GhostRailScale;
        if (k == _railScale) return;
        var old = _railScale;
        if (!_ghostActive)
        {
            _railScale = k;
            ApplyGhostScaleTransforms();
            return;
        }
        var (win, mon, dpi) = GhostContext();
        // Measure where the rail is at the OLD scale before adopting the new one.
        bool wasRailOnly = win.Width <= GhostFootprints.RailOnlyThreshold(old, dpi);
        var railRect = wasRailOnly ? win : CurrentRailRectAt(win, old, dpi);
        _railScale = k;
        ApplyGhostScaleTransforms();
        if (_ghostFlyoutOpen)
        {
            var applied = ApplyFlyoutFootprint(railRect);
            SaveBounds(CurrentRailRectAt(applied, k, dpi));
            Logger.Info($"[WIN] Overlay ghost rail scale: {old:0.##} -> {k:0.##} (flyout live)");
            return;
        }
        // wasRailOnly (not _ghostPanelOpen) also covers a scale change landing during an in-flight
        // panel collapse or flyout close, where the open flags are already false but the window is
        // still the wider panel/flyout footprint.
        if (!wasRailOnly) GhostSnapToRailChrome();
        var (cw, ch) = GhostFootprints.CollapsedSize(k, dpi);
        var collapsed = GhostGeometry.CollapsedRect(railRect, mon, cw, ch);
        GhostApplyRect(collapsed);
        SaveBounds(collapsed);
        Logger.Info($"[WIN] Overlay ghost rail scale: {old:0.##} -> {k:0.##}");
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
                OverlayScanStatus.Foreground = (Brush)FindResource("GoldBrush");
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
        OverlayScanStatus.Foreground = (Brush)FindResource("GoldBrush");
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
    private TextBlock? _haulScanStatus;   // dim mono "last scan: ..." line under the toggles

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

        // Dim mono status line under the toggles: the contract scanner's last pipeline stage in plain
        // words (a coarse stage token only - the raw OCR text / contractor name never reaches the UI).
        // Lives in the fixed strip (parent StackPanel), OUTSIDE HaulingList so a rebuild never clears it.
        if (_haulScanStatus == null && HaulingControlBar.Parent is Panel strip)
        {
            _haulScanStatus = new TextBlock
            {
                Text = ContractStageText.For(App.ContractScan.LastStage),
                FontFamily = (FontFamily)FindResource("MonoFont"),
                FontSize = 10,
                Foreground = new SolidColorBrush(ScanCardComposition.CompInert),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(2, 6, 0, 0),
            };
            strip.Children.Insert(strip.Children.IndexOf(HaulingControlBar) + 1, _haulScanStatus);
        }

        SyncHaulingControls();
    }

    // Push the scanner's current coarse stage into the dim status line. Cheap (a string set), so both
    // the live StageChanged handler and a hauling-tab rebuild call it. No logging: this only mirrors the
    // existing [CONTRACT] pipeline breadcrumbs.
    private void RefreshHaulScanStatus()
    {
        if (_haulScanStatus != null)
            _haulScanStatus.Text = ContractStageText.For(App.ContractScan.LastStage);
    }

    // The contract scanner's coarse stage changed (timer thread): marshal to the UI and refresh the
    // status line while the HAULING tab is on screen (mirrors OnHaulsChanged's tab guard; a rebuild on
    // tab entry catches up anything missed off-tab).
    private void OnContractStageChanged()
        => Dispatcher.Invoke(() => { if (IsTabPresented("hauling")) RefreshHaulScanStatus(); });

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

    // Mouse-only nudge: one wheel notch = one SmallChange step. Persistence flows through the
    // slider's own ValueChanged (the single write path).
    private void OpacityRow_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        OpacitySlider.Value += e.Delta > 0 ? OpacitySlider.SmallChange : -OpacitySlider.SmallChange;
        e.Handled = true;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        // App review 2026-08-01: in ghost mode this header stays visible above an expanded ghost
        // panel (documented at the header-visibility comment below, and QuickSettings_Click already
        // branches on _ghostActive the same way one handler up). So this glyph sat on the panel the
        // user had just opened and killed the WHOLE overlay - which also pauses the scanner, via
        // MainWindow wiring _overlay.Hidden to PauseScanner. Closing the overlay is already covered
        // by the rail's own bottom-pinned glyph, which carries a CLOSE OVERLAY tooltip; this one had
        // no tooltip at all, so the unlabelled control was the destructive one. In ghost mode it now
        // closes the PANEL and returns to the rail. Normal mode is unchanged.
        if (_ghostActive && _ghostPanelOpen) { CollapseGhostPanel(); return; }

        SaveBounds();
        _woFlyout?.Hide();
        _ordersTicker?.Stop();
        _ordersTicker = null;
        Hide();
    }

    /// <summary>Keeps the header close glyph's tooltip honest about what it will actually do, which
    /// differs by mode (see Close_Click). Called from every site that changes ghost state.</summary>
    private void RefreshCloseGlyphTooltip()
    {
        if (CloseBtn == null) return;
        CloseBtn.ToolTip = _ghostActive && _ghostPanelOpen ? "CLOSE PANEL" : "CLOSE OVERLAY";
    }

    // Persists window position/size plus the RECENT strip's current height (the strip is scan-only and
    // may currently be collapsed - _historyHidden picks the right source). Called on Close_Click and on
    // the IsVisibleChanged hidden path (:119-125 above), so bounds survive both the close button and the
    // main-window toggle hiding the overlay.
    private void SaveBounds(PxRect? appliedRail = null)
    {
        // Ghost mode (issue #27): OverlayLeft/Top must always mean "where the RAIL is", because that
        // is the spot the next session's ghost restore places. While a panel or the flyout is open
        // the window extends past the rail on the expand side, so record the rail's own edge.
        double left, top;
        if (appliedRail is { } r)
        {
            // The caller just MoveWindow'd this exact rect; deriving from it avoids trusting
            // WPF Left/Top to have caught up (the same reasoning as the Width/Height guard below).
            var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            left = r.Left / dpi; top = r.Top / dpi;
        }
        else
        {
            left = _ghostActive && (_ghostPanelOpen || _ghostFlyoutOpen) && _ghostDir == GhostExpandDirection.Left
                ? Left + Width - GhostFootprints.RailW * _railScale
                : Left;
            top = Top;
        }
        App.Settings.Current.OverlayLeft = left; App.Settings.Current.OverlayTop = top;
        // Store the BASE (unscaled) size so the overlay does not compound larger every launch (issue #20):
        // the on-screen Width/Height are base * _uiScale, and the ctor multiplies by the scale again on restore.
        // Ghost mode (issue #27) is the exception: there the window size is mode-derived (the rail, or the
        // rail plus a panel), never the user's panel size, so persisting it would corrupt the saved panel
        // size. Position still persists - the rail is what the user drags around.
        if (!_ghostActive)
        {
            App.Settings.Current.OverlayWidth = Width / _uiScale; App.Settings.Current.OverlayHeight = Height / _uiScale;
        }
        App.Settings.Current.OverlayHistoryHeight = _historyHidden ? _savedHistoryHeight.Value : HistoryStripRow.Height.Value;
        App.Settings.Save();
    }

    private void OverlayRsInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { RunScanFromInput(); OverlayRsInput.Clear(); }
        if (e.Key == Key.Escape)
        {
            // Escape used to Hide() the whole overlay - jarring mid-scan. Now it only tames the
            // input: clear typed text first, or (if already empty) just drop keyboard focus.
            if (OverlayRsInput.Text.Length > 0) OverlayRsInput.Clear();
            else { Keyboard.ClearFocus(); Focus(); }
        }
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
        ApplyExactAutoExpand();
    }

    // Cart button on a scan result card. The actual add/remove runs via ToggleCartCommand
    // (bound in XAML); this only records the specific "which resource" breadcrumb, since the
    // app-wide Button click handler (App.xaml.cs RegisterInteractionLogging) already logs the
    // generic "+ CART"/"IN CART" click - same double-log pattern as the two-tap confirm buttons.
    private void CartToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: MatchResult m } b)
            InteractionLog.Click($"overlay cart toggle {m.Resource.Name}", b);
    }

    // ── Live refined sell line on scan cards (market data, amendment 2026-07-27 item 5) ──────
    // The compact overlay twin of the main window's decoder sell line: one line under "Best
    // refinery" carrying the best REFINED sell UEX has for that ore plus its age, or the patch it
    // was captured in when the row is stale, because a price never renders without one of the two.
    // Refined and not raw for the reason the whole feature is: UEX's raw ore-sales dataset has had
    // no community reports since patch 4.8. Gated exactly like every other price surface (feature
    // on, snapshot landed, this ore has a priced row); otherwise the host stays empty and the card
    // renders identically to today. Display only - no click target, the card's own tap still owns
    // the composition rows.
    //
    // Every realized host is tracked so an hourly publish can repaint the cards already on screen;
    // hosts drop out on Unload, which is what keeps the list bounded to the visible cards.
    private readonly List<StackPanel> _marketSellHosts = new();

    private void MarketSellHost_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not StackPanel host) return;
        // Loaded can fire again for a host that was unloaded and reattached (re-parenting, a
        // recycled container), so the Unloaded handler is self-detaching: exactly one is attached
        // to a host at any time, and a Loaded / Unloaded / Loaded cycle cannot stack a second one.
        if (!_marketSellHosts.Contains(host))
        {
            _marketSellHosts.Add(host);
            RoutedEventHandler? onUnloaded = null;
            onUnloaded = (_, _) =>
            {
                _marketSellHosts.Remove(host);
                host.Unloaded -= onUnloaded;
            };
            host.Unloaded += onUnloaded;
        }
        FillMarketSell(host);
    }

    // Called on the UI thread after a fetch cycle publishes. A no-op when the SCAN tab has never
    // produced a card. The one log line per publish (never per card) is written only when a price
    // actually rendered, so it can never claim a refresh on a surface that showed nothing - the
    // feature being off, a missing snapshot and unpriced ores all land as silence here too.
    private void RefreshMarketSellLines()
    {
        if (_marketSellHosts.Count == 0) return;

        int priced = 0;
        for (int i = _marketSellHosts.Count - 1; i >= 0; i--)
        {
            if (FillMarketSell(_marketSellHosts[i])) priced++;
        }

        if (priced > 0)
        {
            Logger.Info($"{MarketDataService.Tag} overlay sell lines refreshed " +
                        $"({priced} of {_marketSellHosts.Count} cards priced)");
        }
    }

    // Returns true when a price line was rendered, which is what the refresh log counts.
    private bool FillMarketSell(StackPanel host)
    {
        host.Children.Clear();
        if (App.Settings.Current.MarketDataEnabled != true) return false;
        if (App.Market.Snapshot is not { } snap) return false;
        if (host.DataContext is not MatchResult m) return false;
        if (MarketQueries.BestRefinedSell(snap, m.Resource.Name) is not { } hit) return false;

        var ageText = hit.Stale
            ? MarketNotice.PatchTag(hit.GameVersion)
            : MarketNotice.FormatAge(DateTime.UtcNow - hit.ModifiedUtc);
        var line = new TextBlock
        {
            // Same 10.5 as the "Best refinery" line above it; the card is 452px wide, so the line
            // trims rather than wraps.
            FontSize = 10.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 3, 0, 0),
        };
        SellLineRuns(line, MarketNotice.OverlayLabel, hit, ageText);
        host.Children.Add(line);
        return true;
    }

    // The overlay card's sell line follows the decoder line's roles (owner ruling 2026-07-27, live
    // run 5, mock .sellline): label dim, value gold, terminal name Fg, age or patch tag dim, and
    // the whole line dim when the price is stale. The runs are composed from MarketNotice's own
    // parts - the same parts OverlaySellLine is built from - so the rendered text stays identical
    // to the string the copy tests pin. The main window carries its own twin of this helper: the
    // two windows share no view-helper class for market rendering.
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

    // ── Deposit composition bar + tap-to-expand (Task 7, C3/C4) ─────────────────
    // The bar/rows/chip builders + expand motion live in ScanCardComposition (shared verbatim with
    // the main-window RS Decoder). This file keeps only the per-surface expand orchestration below.
    // Frozen values: docs/superpowers/specs/2026-07-11-overlay-pass-values.md ("Scan result cards").

    // After a real scan rebinds ScanResults, auto-expand the exact match IFF it has composition data.
    // Called only from the scan entry points (RunScan / recent-scan tap), never from a cart-toggle
    // rebuild, so IsInCart churn does not re-fire the auto-expand. Runs synchronously before the queued
    // per-card Loaded handlers, so those observe the final _expandedName / _animateExpandName.
    private void ApplyExactAutoExpand()
    {
        _expandedName = null;
        _openRows = null;
        _animateExpandName = null;

        var exact = _vm.ScanResults.FirstOrDefault(r => r.IsExact);
        if (exact == null || _composition.Get(exact.Resource.Name).Count == 0) return;

        _expandedName = exact.Resource.Name;
        _animateExpandName = exact.Resource.Name;
    }

    // Each result card realizes (and re-realizes on a rebuild) a CompositionHost StackPanel; fill it
    // with the bar + collapsed/expanded rows. Idempotent - clears and rebuilds, so a repeated Loaded
    // (cart-toggle rebuild, re-parenting) stays correct and honours the current _expandedName.
    private void CompositionHost_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is StackPanel host && host.DataContext is MatchResult m)
            BuildCompositionUi(host, m);
    }

    private void BuildCompositionUi(StackPanel host, MatchResult m)
    {
        host.Children.Clear();
        var parts = _composition.Get(m.Resource.Name);

        // No composition -> no bar, no expand affordance; the card never toggles.
        if (parts.Count == 0)
        {
            if (FindCard(host) is { } bare) bare.Cursor = Cursors.Arrow;
            return;
        }

        if (FindCard(host) is { } card) card.Cursor = Cursors.Hand;

        host.Children.Add(ScanCardComposition.BuildBar(parts));

        var rows = ScanCardComposition.BuildExpandRows(parts);
        bool expanded = string.Equals(m.Resource.Name, _expandedName, StringComparison.OrdinalIgnoreCase);
        rows.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        host.Children.Add(rows);

        if (!expanded) return;

        _openRows = rows;
        bool wantAnim = string.Equals(m.Resource.Name, _animateExpandName, StringComparison.OrdinalIgnoreCase);
        if (wantAnim) _animateExpandName = null;   // one-shot: consume so cart rebuilds render static
        if (wantAnim && !Motion.Reduced) ScanCardComposition.AnimateRowsIn(rows);
        else rows.Opacity = 1;                     // Reduced / static rebuild: expanded, no entrance
    }

    // Card tap: toggle the composition rows. Bubbling MouseLeftButtonDown, so the cart button
    // (which handles its own click) never reaches here. Cards without composition have no rows and
    // never toggle. One card open at a time - opening a new one collapses the previously open one.
    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement card || card.DataContext is not MatchResult m) return;

        if (FindDescendant(card, "CompositionHost") is not StackPanel { Children.Count: >= 2 } host) return;
        var rows = (FrameworkElement)host.Children[1];
        var name = m.Resource.Name;

        InteractionLog.Click($"overlay composition {name}", card);

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
    private static FrameworkElement? FindCard(DependencyObject from)
    {
        var d = from;
        while (d != null)
        {
            if (d is ChamferPanel cp) return cp;
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    // Named descendant lookup (the CompositionHost inside one card's subtree).
    private static FrameworkElement? FindDescendant(DependencyObject root, string name)
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement fe && fe.Name == name) return fe;
            if (FindDescendant(child, name) is { } found) return found;
        }
        return null;
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
                ApplyExactAutoExpand();
            };
            HistoryStrip.Children.Add(rowBorder);
        }
    }

    private TwoTapConfirm? _clearHistoryConfirm;

    // Two-tap guarded like HAULING's Clear all: first click arms ("SURE?", solid red), a second
    // click inside the 3s window clears; an unconfirmed arm reverts via PollArmedConfirms.
    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        var b = (Button)sender;
        _clearHistoryConfirm ??= new TwoTapConfirm(TimeSpan.FromSeconds(3), () =>
        {
            int n = _vm.ScanHistory.Count;
            _vm.ScanHistory.Clear();
            Logger.Info($"[UI] Scan history cleared ({n} entries)");
        });
        void Rest()
        {
            b.Content = "CLEAR";
            b.ClearValue(Button.BackgroundProperty);
            b.ClearValue(Button.ForegroundProperty);
        }
        if (_clearHistoryConfirm.Tap(DateTime.UtcNow))
        {
            b.Content = "SURE?"; b.Background = ArmedConfirmBrush; b.Foreground = Brushes.White;
            _armedConfirms.Add((_clearHistoryConfirm, Rest));
        }
        else Rest();
    }


    // One-shot border color flash for an overlay order card: amber -> cyan (peak at 45%) -> resting, 400ms, ease-out.
    // Animates the Color of a per-card SolidColorBrush clone (never a shared/frozen resource brush).
    private static void FlashOrderBorder(SolidColorBrush target, Color resting)
    {
        var amber = Color.FromRgb(0xFF, 0xB2, 0x3E);
        var cyan  = Color.FromRgb(0x7F, 0xE9, 0xE0);
        var anim = new System.Windows.Media.Animation.ColorAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(Motion.FlashMs),
            FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop,
        };
        anim.KeyFrames.Add(new System.Windows.Media.Animation.EasingColorKeyFrame(amber, System.Windows.Media.Animation.KeyTime.FromPercent(0.0)));
        anim.KeyFrames.Add(new System.Windows.Media.Animation.EasingColorKeyFrame(cyan, System.Windows.Media.Animation.KeyTime.FromPercent(0.45)) { EasingFunction = Motion.SlideOut });
        anim.KeyFrames.Add(new System.Windows.Media.Animation.EasingColorKeyFrame(resting, System.Windows.Media.Animation.KeyTime.FromPercent(1.0)) { EasingFunction = Motion.SlideOut });
        target.Color = resting;
        target.BeginAnimation(SolidColorBrush.ColorProperty, anim);
    }

    private void WorkOrderToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_woFlyout == null || !_woFlyout.IsVisible)
        {
            if (_woFlyout == null)
            {
                _woFlyout = new WorkOrderFlyoutWindow(_vm);
                _woFlyout.AnchorTo(this);
                _woFlyout.ApplyUiScale(_uiScale);   // overlay scale (issue #20)
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

    // persist:false = a programmatic tab flip (the welcome tour) that must not overwrite the
    // user's saved tab preference; every user-driven switch keeps the default and persists.
    private void SwitchTab(string tab, bool persist = true)
    {
        var prev = _activeTab;
        _activeTab = tab;
        if (persist)
        {
            App.Settings.Current.OverlayActiveTab = tab;
            App.Settings.Save();
        }
        StatsTabContent.Visibility    = tab == "stats"    ? Visibility.Visible : Visibility.Collapsed;
        ScanTabContent.Visibility     = tab == "scan"     ? Visibility.Visible : Visibility.Collapsed;
        OrdersTabContent.Visibility   = tab == "orders"   ? Visibility.Visible : Visibility.Collapsed;
        ShoppingTabContent.Visibility = tab == "shopping" ? Visibility.Visible : Visibility.Collapsed;
        HaulingTabContent.Visibility  = tab == "hauling"  ? Visibility.Visible : Visibility.Collapsed;
        GuidesTabContent.Visibility   = tab == "guides"   ? Visibility.Visible : Visibility.Collapsed;
        TradeTabContent.Visibility    = tab == "trade"    ? Visibility.Visible : Visibility.Collapsed;

        // A tab switch is a real content change under the header gear's normal-mode flyout too
        // (issue #27 review): close it rather than let it float over content it no longer relates to.
        if (_normalFlyoutOpen) CloseNormalFlyout(animate: false);

        // First call is the saved-tab restore at construction: place the pill without motion and
        // without logging. Every later call animates; only real user switches log (persist=true
        // and the tab actually changed; tour flips pass persist=false).
        var isSwitch = _tabStripReady && prev != tab;
        TabStrip.SetActive(tab, animate: isSwitch);
        if (isSwitch && persist) Logger.Info(OverlayTabs.SwitchLogLine(prev, tab));
        _tabStripReady = true;

        // RECENT scans belong to the SCAN tab only - hide the strip on every other tab.
        SetHistoryStripVisible(tab == "scan");

        if (tab == "stats") RebuildStatsPanel();
        if (tab == "scan") SyncScanControls();
        if (tab == "shopping") RebuildShoppingPanel();
        if (tab == "hauling") RebuildHaulingPanel();
        if (tab == "trade") RebuildTradePanel();
        if (tab == "guides") ShowGuidesTab();

        // Executive Hangar (issue #26 amendment): the compact control ticks only while GUIDES is
        // the active overlay tab (plus OnClosed for the whole-window teardown - see there).
        // ShowGuidesTab (just above) builds the control on first entry, so entering "guides" here
        // always finds it non-null; the null-conditional is only a defensive guard.
        if (tab == "guides") _guidesHangarLine?.Start();
        else if (prev == "guides") _guidesHangarLine?.Stop();

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
            HistorySplitterRow.Height = new GridLength(8);
            HistorySplitterRow.MinHeight = 8;
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

            // Leaving SCAN: persist whatever height the user last dragged the strip to, so it's
            // restored next launch even if the app closes while parked on a different tab.
            App.Settings.Current.OverlayHistoryHeight = _savedHistoryHeight.Value;
            App.Settings.Save();
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
        if (IsTabPresented("stats")) RebuildStatsPanel();
    }

    // Session tally was cleared (new SC session, or a manual reset from the advanced monitor) - refresh
    // the visible count + feed while the HUB is on screen.
    private void OnSessionReset()
    {
        if (IsTabPresented("stats")) RebuildStatsPanel();
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
        App.Market.Changed -= _onMarketChanged;
        App.ForegroundRelevanceChanged -= OnForegroundRelevanceChanged;
        App.ContractScan.RunningChanged -= SyncContractFromShared;
        App.ContractScan.StageChanged -= OnContractStageChanged;
        App.ContractBoxVisibilityChanged -= OnContractBoxShared;
        WorkOrderEditorPanel.OrderReadyToCollect -= _onOrderReady;
        UiScaleService.Changed -= OnUiScaleChanged;   // overlay scale (issue #20)
        UiScaleService.RailChanged -= OnRailScaleChanged;   // ghost rail scale
        App.OverlayGhostModeChanged -= OnGhostModeChanged;   // ghost mode (issue #27)
        _guidesHangarLine?.Stop();   // issue #26 amendment: whole-window teardown
        base.OnClosed(e);
    }

    // Refresh the HAULING glance list when the tracker changes, but only while that tab is on screen.
    private void OnHaulsChanged()
    {
        UpdateHaulingTabBadge();                 // keep the tab count fresh even off the HAULING tab
        if (IsTabPresented("hauling")) RebuildHaulingPanel();
        if (IsTabPresented("stats")) RebuildStatsPanel();   // F1: HAUL hero tile tracks the same active-haul totals
    }

    // Shows the active-haul count as a chip on the overlay HAULING tab icon.
    private void UpdateHaulingTabBadge()
    {
        TabStrip.SetBadge("hauling", App.Hauls.ActiveHauls.Count);
        GhostRail.SetBadge("hauling", App.Hauls.ActiveHauls.Count);   // ghost mode carries the same counts (issue #27)
    }

    // E1/F1: work orders ready to collect - shared by the REFINERY tab badge and the HUB's READY
    // ORDERS hero tile so the enum walk lives in exactly one place.
    private int ReadyOrdersCount() => _vm.WorkOrders.Count(w => w.Status == WorkOrderStatus.ReadyToCollect);

    // E1: shows the ready-to-collect count as a chip on the overlay REFINERY tab icon.
    // Driven by the WorkOrders.CollectionChanged subscription plus the initial build,
    // so the badge stays fresh off-tab.
    private void UpdateRefineryTabBadge()
    {
        TabStrip.SetBadge("orders", ReadyOrdersCount());
        GhostRail.SetBadge("orders", ReadyOrdersCount());   // ghost mode carries the same counts (issue #27)
    }

    // F1: sums committed SCU + delivered/total dropoff legs across a set of hauls. Shared by the HUB's
    // HAUL hero tile and the HAULING tab's totals line (RebuildHaulingPanel) so this math lives once.
    private static (int Scu, int Done, int Total) HaulTotals(IEnumerable<Haul> hauls)
    {
        int scu = 0, done = 0, total = 0;
        foreach (var h in hauls)
        {
            // SCU committed: prefer the OCR objectives the cards render, fall back to the dropoff legs.
            scu += h.ContractObjectives.Count > 0
                ? h.ContractObjectives.Sum(o => o.Scu)
                : h.Legs.Where(l => l.Role == HaulRole.Dropoff).Sum(l => l.TargetScu);
            var d = h.Legs.Where(l => l.Role == HaulRole.Dropoff).ToList();
            total += d.Count;
            done += d.Count(l => l.Completed);
        }
        return (scu, done, total);
    }

    // Refresh the Server / Shard section when the shard history changes, but only while the STATS
    // tab is on screen (mirrors OnHaulsChanged's tab guard).
    private void OnShardsChanged()
    {
        if (IsTabPresented("stats")) RebuildShardPanel();
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

        // Shared brushes / fonts for the hero tiles + feed below them.
        var dim    = (Brush)FindResource("FgDimBrush");
        var fg     = (Brush)FindResource("FgBrush");
        var cyan   = (Brush)FindResource("CyanBrush");
        var border = (Brush)FindResource("NavBorderBrush");
        var mono   = (FontFamily)FindResource("MonoFont");

        // F1 hero tiles: READY ORDERS (cyan when > 0, else dim - shares the enum walk with the REFINERY
        // tab badge via ReadyOrdersCount) and HAUL (committed SCU + delivered/total drops across active
        // hauls, always cyan per the frozen values - shares the totals math with RebuildHaulingPanel via
        // HaulTotals). Both tiles keep the accent ChamferPanel look (border color set in XAML).
        var ready = ReadyOrdersCount();
        HubReadyValue.Text = ready.ToString();
        HubReadyValue.Foreground = ready > 0 ? cyan : dim;

        var (scu, done, total) = HaulTotals(App.Hauls.ActiveHauls);
        HubHaulValue.Text = $"{scu:N0} SCU";
        HubHaulSub.Text = $"{done}/{total} drops";

        // F2: the feed header carries the same session count the old hero count-up rendered
        // (App.GameLog.Count), so the demoted number is exactly the approved one.
        StatsFeedHeader.Text = $"COLLECTION LOG ({App.GameLog.Count})";

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
    // (dot + region/instance, absolute local join time on the right) - the mock's SERVER / SHARD panel. The
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
                    Text = "joined " + s.JoinedAt.ToLocalTime().ToString("HH:mm"), FontFamily = monoFont, FontSize = 9, Foreground = dim,
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
        RefreshHaulScanStatus();   // catch the status line up on tab entry / haul change (fixed strip, not cleared)
        HaulingList.Children.Clear();

        var accent = (Brush)FindResource("AccentBrush");
        var cyan   = (Brush)FindResource("CyanBrush");
        var dim    = (Brush)FindResource("FgDimBrush");
        var border = (Brush)FindResource("NavBorderBrush");
        var cardBg = (Brush)FindResource("Bg2NavBrush");
        var mono   = (FontFamily)FindResource("MonoFont");

        var active = App.Hauls.ActiveHauls;

        // Compact "Clear all" affordance, shown whenever there is anything to clear (active or finished).
        // Two-tap guarded (destructive): the first click arms ("Sure?" + solid red) instead of clearing
        // outright, and only a second click inside the confirm window actually clears. An unconfirmed
        // arm reverts on its own via the cursor-poll tick (PollArmedConfirms above), so no timer is
        // started here.
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
            void RestClearAll() { b.Content = "Clear all"; b.Background = cardBg; b.Foreground = accent; }
            var confirm = new TwoTapConfirm(TimeSpan.FromSeconds(3), () =>
            {
                InteractionLog.Click("clear all hauls (confirmed)", b);
                App.Hauls.ClearAll();   // Changed -> OnHaulsChanged rebuilds
            });
            b.Click += (_, __) =>
            {
                if (confirm.Tap(DateTime.UtcNow))
                {
                    b.Content = "Sure?"; b.Background = ArmedConfirmBrush; b.Foreground = Brushes.White;
                    _armedConfirms.Add((confirm, RestClearAll));
                }
            };
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
        // SCU + drops share the HUB hero tile's math (HaulTotals); reward is a separate, tiny sum kept
        // local to this tab since the hero tile doesn't render it.
        var (totalScu, dropsDone, drops) = HaulTotals(active);
        int totalReward = active.Where(h => h.Reward > 0).Sum(h => h.Reward);

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

        // Two-tap guarded (destructive): the "x" morphs to "Sure?" (solid red) on the first click; a
        // second click inside the window removes the card. Same pattern as ClearAllButton above,
        // reverted by the shared cursor-poll tick (PollArmedConfirms) rather than a new timer.
        var deleteBtn = new Button { Content = "x", FontFamily = mono, FontSize = 13, FontWeight = FontWeights.Bold, Foreground = dim, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(6, 0, 6, 0), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center };
        void RestDelete() { deleteBtn.Content = "x"; deleteBtn.Background = Brushes.Transparent; deleteBtn.Foreground = dim; }
        var deleteConfirm = new TwoTapConfirm(TimeSpan.FromSeconds(3), () =>
        {
            InteractionLog.Click("remove haul (confirmed)", deleteBtn);
            App.Hauls.Remove(missionId);   // Changed -> OnHaulsChanged rebuilds
        });
        deleteBtn.Click += (_, __) =>
        {
            if (deleteConfirm.Tap(DateTime.UtcNow))
            {
                deleteBtn.Content = "Sure?"; deleteBtn.Background = ArmedConfirmBrush; deleteBtn.Foreground = Brushes.White;
                _armedConfirms.Add((deleteConfirm, RestDelete));
            }
        };
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

        // Max container size from the contract OCR (int?, null = not stated in the captured text): the
        // biggest single box this haul allows. Frozen: amber-bright #FFD089, 10.5px (D1) - its own
        // style, not the dim HaulRow used for the cargo/route lines above.
        if (h.ContainerCap is > 0)
            card.Children.Add(new TextBlock
            {
                Text = $"max box {h.ContainerCap.Value} SCU", FontFamily = mono, FontSize = 10.5,
                Foreground = (Brush)FindResource("GoldBrush"), Margin = new Thickness(8, 2, 0, 0),
            });

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
    // Reduced-motion fill bars: no animation clock, so the existing 1s ticker steps them.
    private readonly Dictionary<string, (System.Windows.Media.ScaleTransform Scale, WorkOrder Order)> _orderFillRefs = new();

    // ── E2 quick-add form (built once into QuickAddHost, survives orders-panel rebuilds) ─────────
    private bool _quickAddBuilt;
    private Button _quickAddTrigger = null!;
    private Border _quickAddForm = null!;
    private TextBox _quickResourceBox = null!;
    private TextBox _quickRefineryBox = null!;
    private TextBox _quickMinutesBox = null!;
    private TextBlock _quickError = null!;

    // Builds the "+ Quick order" trigger and the (collapsed) inline form once. The refinery field is a
    // plain TextBox: the frozen values style every quick-form field uniformly as "mono 12px on #0A0F15"
    // text fields (authoritative), and the main editor's only station source is a private static array in
    // WorkOrderEditorPanel that can't be reused without an out-of-scope edit or duplication; the refinery
    // is also optional here. Everything richer stays in the main-window editor.
    private void BuildQuickAddPanel()
    {
        if (_quickAddBuilt) return;
        _quickAddBuilt = true;

        var dim     = (Brush)FindResource("FgDimBrush");
        var line    = (Brush)FindResource("NavBorderBrush");
        var mono    = (FontFamily)FindResource("MonoFont");
        var fieldBg = HexBrush("#0A0F15");

        _quickAddTrigger = new Button
        {
            Content = "+  Quick order",
            Style = (Style)FindResource("NexusButton"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(0, 6, 0, 6),
            FontSize = 11,
        };
        _quickAddTrigger.Click += QuickAddTrigger_Click;

        // Frozen field: mono 12px on #0A0F15, 1px line border, 4px radius (NexusTextBox template supplies
        // the rounded 1px border + placeholder-from-Tag; we override bg/font to the frozen values).
        TextBox Field(string placeholder) => new TextBox
        {
            Style = (Style)FindResource("NexusTextBox"),
            Background = fieldBg, FontFamily = mono, FontSize = 12,
            Padding = new Thickness(8, 5, 8, 5), Tag = placeholder,
            Margin = new Thickness(0, 3, 0, 0),
        };

        // Frozen label: 9px uppercase dim (WPF has no letter-spacing, so this matches the overlay's
        // existing 9px uppercase label idiom, e.g. "ACTIVE ORDERS").
        TextBlock Label(string text) => new TextBlock
        {
            Text = text.ToUpperInvariant(), FontSize = 9, FontWeight = FontWeights.Bold,
            Foreground = dim, Margin = new Thickness(0, 8, 0, 0),
        };

        _quickResourceBox = Field("Resource");
        _quickRefineryBox = Field("Refinery (optional)");
        _quickMinutesBox  = Field("45");

        _quickError = new TextBlock
        {
            FontSize = 10, Foreground = HexBrush("#E5484D"),
            Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };

        var addBtn = new Button
        {
            Content = "ADD", Style = (Style)FindResource("AccentButton"),
            Padding = new Thickness(16, 5, 16, 5), FontSize = 11,
        };
        addBtn.Click += QuickAddConfirm_Click;
        var cancelBtn = new Button
        {
            Content = "CANCEL", Style = (Style)FindResource("NexusButton"),
            Padding = new Thickness(14, 5, 14, 5), FontSize = 11, Margin = new Thickness(8, 0, 0, 0),
        };
        cancelBtn.Click += QuickAddCancel_Click;

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        btnRow.Children.Add(addBtn);
        btnRow.Children.Add(cancelBtn);

        var formStack = new StackPanel();
        formStack.Children.Add(Label("Resource"));
        formStack.Children.Add(_quickResourceBox);
        formStack.Children.Add(Label("Refinery"));
        formStack.Children.Add(_quickRefineryBox);
        formStack.Children.Add(Label("Timer (min)"));
        formStack.Children.Add(_quickMinutesBox);
        formStack.Children.Add(_quickError);
        formStack.Children.Add(btnRow);

        _quickAddForm = new Border
        {
            Child = formStack,
            Background = (Brush)FindResource("Bg2NavBrush"),
            BorderBrush = line, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 10, 12, 12),
            Visibility = Visibility.Collapsed,
        };

        QuickAddHost.Children.Add(_quickAddTrigger);
        QuickAddHost.Children.Add(_quickAddForm);
    }

    private void QuickAddTrigger_Click(object sender, RoutedEventArgs e)
    {
        _quickError.Visibility = Visibility.Collapsed;
        _quickResourceBox.Text = _vm.BestMatch?.Resource.Name ?? "";   // prefill the top scan match
        _quickRefineryBox.Text = "";
        _quickMinutesBox.Text  = "";
        _quickAddTrigger.Visibility = Visibility.Collapsed;
        UnfoldQuickForm();
        _quickResourceBox.Focus();
        _quickResourceBox.CaretIndex = _quickResourceBox.Text.Length;
    }

    // Frozen: unfold Motion.QuickRevealMs fade 0->1 + 12px rise, Motion.Settle, one-shot; Reduced snaps.
    private void UnfoldQuickForm()
    {
        _quickAddForm.Visibility = Visibility.Visible;
        if (Motion.Reduced)
        {
            _quickAddForm.BeginAnimation(OpacityProperty, null);
            _quickAddForm.Opacity = 1;
            _quickAddForm.RenderTransform = null;
            return;
        }
        var shift = new System.Windows.Media.TranslateTransform(0, 12);
        _quickAddForm.RenderTransform = shift;
        _quickAddForm.Opacity = 0;
        var dur = TimeSpan.FromMilliseconds(Motion.QuickRevealMs);
        _quickAddForm.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, dur) { EasingFunction = Motion.Settle });
        shift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new System.Windows.Media.Animation.DoubleAnimation(12, 0, dur) { EasingFunction = Motion.Settle });
    }

    private void CollapseQuickForm()
    {
        _quickAddForm.BeginAnimation(OpacityProperty, null);
        _quickAddForm.Opacity = 1;
        _quickAddForm.Visibility = Visibility.Collapsed;
        _quickError.Visibility = Visibility.Collapsed;
        _quickAddTrigger.Visibility = Visibility.Visible;
    }

    private void QuickAddCancel_Click(object sender, RoutedEventArgs e) => CollapseQuickForm();

    private void QuickAddConfirm_Click(object sender, RoutedEventArgs e)
    {
        var resource = _quickResourceBox.Text.Trim();
        var refinery = _quickRefineryBox.Text.Trim();
        var minutes  = int.TryParse(_quickMinutesBox.Text.Trim(), out var mv) ? mv : 0;
        try
        {
            // UtcNow is required: WorkOrder's timer math is UtcNow-based (Now would skew by the UTC offset).
            var wo = QuickOrderFactory.Create(resource, refinery, minutes, DateTime.UtcNow);
            _vm.SaveWorkOrderCommand.Execute(wo);   // same persistence path as the editor -> CollectionChanged refreshes every surface + the badge
            InteractionLog.Click($"overlay quick order ({resource})", (Button)sender);
            CollapseQuickForm();
        }
        catch (ArgumentException ex)
        {
            _quickError.Text = ex.Message;   // inline dim red, no dialog (frozen)
            _quickError.Visibility = Visibility.Visible;
        }
    }

    // E3: mark a ready order collected -> Complete, through the same persistence path the editor uses.
    private void CollectOrder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: WorkOrder wo } b) return;
        wo.Status = WorkOrderStatus.Complete;
        _vm.SaveWorkOrderCommand.Execute(wo);
        InteractionLog.Click($"overlay order collected ({wo.Label})", b);
    }

    // Ready-flash bookkeeping (mirrors MainWindow). The orders panel fully rebuilds on every change, and a save
    // fires a rebuild per re-added order, so the flash is scheduled once (deferred) against the final card set.
    private readonly HashSet<string> _orderEverRefining = new();
    private readonly HashSet<string> _orderFlashedReady = new();
    private readonly Dictionary<string, OverlayOrderParts> _orderCardParts = new();
    private bool _orderAnimQueued;

    // The animatable pieces of an overlay order card, captured at build time for the deferred flash pass.
    private sealed class OverlayOrderParts
    {
        public Border Chip = null!;
        public SolidColorBrush? FlashBorder;   // per-card clone, set only for a flash candidate
        public WorkOrderStatus Status;
        public bool PreReady;
    }

    private void RebuildOrdersPanel()
    {
        OrdersPanelItems.Children.Clear();
        OrdersSummaryPanel.Children.Clear();
        _orderTimerRefs.Clear();
        _orderFillRefs.Clear();
        _orderCardParts.Clear();

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
            OrdersPanelItems.Children.Add(BuildOverlayOrderCard(wo));   // records card parts in _orderCardParts[wo.Id]
        ScheduleOrderAnimations();

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

        // Ready-flash candidate: a card built in the ready state for an order we have already seen refining and not
        // yet flashed. Cloning the border brush (only for a candidate) lets the deferred pass animate its Color
        // without ever touching the shared resource brush. The one-per-order guard is consumed in FlushOrderAnimations.
        bool flashCandidate = wo.Status == WorkOrderStatus.ReadyToCollect
                              && !Motion.Reduced
                              && _orderEverRefining.Contains(wo.Id)
                              && !_orderFlashedReady.Contains(wo.Id);
        Brush cardBorder = navB;
        SolidColorBrush? flashBorder = null;
        if (flashCandidate && navB is SolidColorBrush navSolid)
        {
            flashBorder = new SolidColorBrush(navSolid.Color);
            cardBorder = flashBorder;
        }

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
            {
                if (Motion.Reduced)
                {
                    scale.ScaleX = frac;                    // static; the 1s ticker advances it below
                    _orderFillRefs[wo.Id] = (scale, wo);
                }
                else
                {
                    scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty,
                        new System.Windows.Media.Animation.DoubleAnimation(frac, 1.0, remaining)
                        { FillBehavior = System.Windows.Media.Animation.FillBehavior.HoldEnd });
                }
            }

            _orderTimerRefs[wo.Id] = tTxt;
        }

        // E3: a ready order gains a "COLLECTED" action (frozen: #66E6A6 on rgba(102,230,166,0.12),
        // 1px rgba(102,230,166,0.4) border, 4px radius, 10px). Collecting marks it Complete via the
        // same persistence path the editor uses, so every surface (and the REFINERY badge) refreshes.
        if (wo.Status == WorkOrderStatus.ReadyToCollect)
        {
            var collectBtn = new Button
            {
                Content = "COLLECTED",
                Style = (Style)FindResource("NexusButton"),
                Foreground = HexBrush("#66E6A6"),
                Background = HexBrush("#1F66E6A6"),   // rgba(102,230,166,0.12)
                BorderBrush = HexBrush("#6666E6A6"),  // rgba(102,230,166,0.4)
                BorderThickness = new Thickness(1),
                FontSize = 10, FontWeight = FontWeights.Bold,
                Padding = new Thickness(10, 4, 10, 4),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 9, 0, 0),
                Tag = wo,
            };
            collectBtn.Click += CollectOrder_Click;
            stack.Children.Add(collectBtn);
        }

        grid.Children.Add(stack);
        // Chamfered MOBIGLAS card (TL+BR bevel) with the status bar clipped to the silhouette.
        var card = Hud.Panel(grid, chamfer: 10, bg: cardBg, border: cardBorder,
                             padding: new Thickness(0), clipContent: true);
        card.Margin = new Thickness(0, 0, 0, 8);

        // Capture the parts; the deferred pass (FlushOrderAnimations) plays the flash once, on the final card.
        _orderCardParts[wo.Id] = new OverlayOrderParts
        {
            Chip = chip, FlashBorder = flashBorder, Status = wo.Status,
            PreReady = wo.Status is WorkOrderStatus.Refining or WorkOrderStatus.Mining || wo.HasActiveTimer,
        };
        return card;
    }

    // Coalesced deferred flash pass (mirrors MainWindow): scheduled once per rebuild storm, runs on the final cards.
    private void ScheduleOrderAnimations()
    {
        if (_orderAnimQueued) return;
        _orderAnimQueued = true;
        Dispatcher.BeginInvoke(new Action(FlushOrderAnimations), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void FlushOrderAnimations()
    {
        _orderAnimQueued = false;
        foreach (var wo in _vm.WorkOrders)
        {
            if (!_orderCardParts.TryGetValue(wo.Id, out var parts)) continue;

            if (parts.PreReady) _orderEverRefining.Add(wo.Id);

            if (parts.Status == WorkOrderStatus.ReadyToCollect
                && _orderEverRefining.Contains(wo.Id)
                && _orderFlashedReady.Add(wo.Id)   // once per order
                && !Motion.Reduced && parts.FlashBorder != null)
            {
                // Pill fade-in (frozen pill crossfade, 150ms). The overlay status chip is inline (not a Hud.StatusChip),
                // so this uses the sanctioned single-pill opacity fade rather than the two-chip cross-dissolve.
                parts.Chip.Opacity = 0;
                parts.Chip.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(Motion.ChipFadeMs)) { EasingFunction = Motion.Settle });
                // Border flash (frozen: amber -> cyan at 45% -> resting, 400ms, ease-out, one shot).
                FlashOrderBorder(parts.FlashBorder, parts.FlashBorder.Color);
                NexusApp.Services.Logger.Info($"[UI] Refinery: order ready flash ({wo.Label})");
            }
        }
    }

    private void OrdersTicker_Tick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;

        // Auto-flip owner (mirrors MainWindow.ListTicker_Tick): a refine timer that ran out while the order still
        // sits pre-ready flips to ready through the canonical path (WorkOrderEditorPanel.TryFlipToReady = store
        // write + OrderReadyToCollect), so a user watching only the overlay ORDERS tab gets live Ready flips even
        // when the main work-orders page and the editor are both closed. TryFlipToReady persists via SaveWorkOrder,
        // which clears and re-adds WorkOrders; because this ticker only runs while the ORDERS tab is active, that
        // CollectionChanged storm rebuilds this panel (restarting the ticker), so the flip candidates are
        // snapshotted before the flip runs to avoid mutating the live collection mid-enumeration. The status guard
        // inside TryFlipToReady keeps it idempotent with the main ticker: both are UI-thread DispatcherTimers
        // (serialized), so whichever reaches the order first flips it and the other's call returns false.
        List<WorkOrder>? toFlip = null;
        foreach (var wo in _vm.WorkOrders)
            if (wo.ShouldAutoFlipToReady(now))
                (toFlip ??= new()).Add(wo);
        if (toFlip != null)
        {
            foreach (var wo in toFlip)
                if (WorkOrderEditorPanel.TryFlipToReady(wo, _vm))
                    NexusApp.Services.Logger.Info($"[UI] Refinery: order auto-marked ready ({wo.Label})");
            return;   // each flip rebuilt the panel; the live-countdown pass resumes on the next tick
        }

        bool anyActive = false;
        foreach (var wo in _vm.WorkOrders)
        {
            if (!_orderTimerRefs.TryGetValue(wo.Id, out var txt)) continue;
            if (!wo.HasActiveTimer) { RebuildOrdersPanel(); return; }
            anyActive = true;
            txt.Text = wo.TimerRemainingShort;
        }
        if (!anyActive) { _ordersTicker?.Stop(); _ordersTicker = null; }

        if (Motion.Reduced)
            foreach (var (s, order) in _orderFillRefs.Values)
                s.ScaleX = Math.Clamp(order.TimerFraction, 0, 1);
    }

    // ── TRADE tab ──────────────────────────────────────────────────────────────
    // App review 2026-08-01: the overlay carried no trade information at all, on the one surface
    // actually on screen while a route is being flown, and had zero references to the player's live
    // position. This tab answers exactly two questions - what am I running, and where am I in it -
    // and deliberately nothing else. Screen space here is tiny and it is read mid-flight, so it is a
    // read-only glance, not a second planner.

    // The route TradePage currently has pinned, pushed in by MainWindow on the same event that
    // already keeps the Starmap's route overlay in sync. Null = nothing pinned.
    private TradeRoute? _pinnedRoute;

    /// <summary>MainWindow forwards TradePage's pinned route here, mirroring PushPinnedRouteToMap.
    /// Cheap and idempotent: it only repaints when this tab is the one being presented.</summary>
    public void SetPinnedRoute(TradeRoute? route)
    {
        _pinnedRoute = route;
        if (IsTabPresented("trade")) RebuildTradePanel();
    }

    private void RebuildTradePanel()
    {
        TradePanelItems.Children.Clear();

        var fg = (System.Windows.Media.Brush)FindResource("FgBrush");
        var dim = (System.Windows.Media.Brush)FindResource("FgDimBrush");
        var accent = (System.Windows.Media.Brush)FindResource("AccentBrush");
        var mono = (System.Windows.Media.FontFamily)FindResource("MonoFont");

        TextBlock Line(string text, System.Windows.Media.Brush brush, double size, bool mn = false) => new()
        {
            Text = text, Foreground = brush, FontSize = size, TextWrapping = TextWrapping.Wrap,
            FontFamily = mn ? mono : null, Margin = new Thickness(0, 0, 0, 3),
        };

        // WHERE YOU ARE. Silence when unknown is not an option on a tab this small - an empty panel
        // reads as broken - so this one case says so plainly instead.
        var place = App.Player.Label;
        TradePanelItems.Children.Add(Line(place is null ? "Location unknown" : $"You: {place}",
                                          place is null ? dim : fg, 12.5));

        if (_pinnedRoute is not { } r)
        {
            TradePanelItems.Children.Add(Line(
                "No route pinned. Pin one in Trade > Planner and it shows here and on the Starmap.",
                dim, 11.5));
            return;
        }

        var buy = r.BuyRow;
        var sell = r.SellRow;

        TradePanelItems.Children.Add(Line(buy.CommodityName, accent, 14));
        TradePanelItems.Children.Add(Line($"BUY   {buy.TerminalName}", fg, 12, mn: true));
        TradePanelItems.Children.Add(Line($"SELL  {sell.TerminalName}", fg, 12, mn: true));

        // Per-unit margin rather than the trip total: the total depends on a ship and a budget the
        // player may have changed since pinning, but the margin is a property of the route itself.
        var perUnit = sell.Sell - buy.Buy;
        TradePanelItems.Children.Add(Line($"{perUnit:N0} aUEC/SCU   ×{r.TripQty} SCU", dim, 11.5, mn: true));

        // WHICH LEG. Only possible now that App.Player exists, and the single most useful thing this
        // tab can say mid-flight. Matched by terminal name, since a pinned route carries price rows
        // rather than resolved terminals, and the player's own label is what the log gave us.
        if (place is not null)
        {
            string? leg =
                string.Equals(place, buy.TerminalName, StringComparison.OrdinalIgnoreCase) ? "At the BUY stop." :
                string.Equals(place, sell.TerminalName, StringComparison.OrdinalIgnoreCase) ? "At the SELL stop." :
                null;
            if (leg is not null)
                TradePanelItems.Children.Add(Line(leg, accent, 11.5));
        }
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

    // ── GUIDES tab (Mission Guides, compact) ────────────────────────────────────
    // The overlay read of the Mission Guides page: the same GuideCatalog, rendered as a
    // category-grouped title list instead of thumbnail cards (no room for art at 320px), handing
    // the tab body to the shared GuideViewer when a row is picked. No credits here - the creator
    // acknowledgement lives on the main page only. The viewer runs in compact mode, so its decode
    // is capped and the overlay never holds a full-resolution bitmap.
    //
    // Everything is built on first entry to the tab, so a user who never opens GUIDES pays nothing.

    private const double GuideCascadeMs     = 200;   // MainWindow CascadeIn duration
    private const double GuideCascadeStepMs = 40;    // MainWindow CascadeIn stagger
    private const double GuideCascadeRisePx = 12;    // page-in / cascade rise distance
    private const double GuideRowSlidePx    = 3;     // DockTile hover slide (GameTheme.xaml DockTile)

    private ScrollViewer? _guidesScroller;
    private GuideViewer? _guidesViewer;
    private readonly List<FrameworkElement> _guidesCascade = new();
    private GuideEntry? _openGuide;
    // Executive Hangar status line (issue #26 amendment): the shared compact control, hosted
    // inside the Contested Zones section of the catalog list (owner ruling: mirror the main
    // Guides page placement). Built once with the rest of the tab (EnsureGuidesTab); its
    // tick lifecycle is independent of the guide list/viewer - see SwitchTab and OnClosed.
    private ExecHangarStatusLine? _guidesHangarLine;

    // Entry point from SwitchTab: build once, log the show, and replay the list cascade. A guide
    // left open from a previous visit is closed so the tab always opens on the list.
    private void ShowGuidesTab()
    {
        EnsureGuidesTab();
        Logger.Info("[WIN] overlay guides tab shown");
        if (_openGuide != null) CloseOverlayGuide(replayCascade: false);
        PlayGuidesCascade();
    }

    private void EnsureGuidesTab()
    {
        if (_guidesScroller != null) return;

        var list = new StackPanel();
        bool firstSection = true;
        foreach (var category in GuideCatalog.Categories)
        {
            var head = new TextBlock
            {
                Text = category.ToUpperInvariant(),
                Style = (Style)FindResource("SectionLabel"),
                Margin = new Thickness(0, firstSection ? 0 : 14, 0, 6),
            };
            firstSection = false;
            list.Children.Add(head);
            _guidesCascade.Add(head);

            // Executive Hangar status (issue #26 amendment, owner ruling live run 2026-07-27):
            // the compact line lives INSIDE the Contested Zones section, directly under its
            // header, mirroring the main Guides page (not a block above the whole tab). It
            // scrolls with the list and is covered by the viewer like any other list content.
            if (category == "Contested Zones")
            {
                _guidesHangarLine = new ExecHangarStatusLine(compact: true, surfaceName: "overlay")
                {
                    Margin = new Thickness(2, 0, 0, 8),
                };
                list.Children.Add(_guidesHangarLine);
                _guidesCascade.Add(_guidesHangarLine);
            }

            foreach (var guide in GuideCatalog.ByCategory(category))
            {
                var row = BuildGuideRow(guide);
                list.Children.Add(row);
                _guidesCascade.Add(row);
            }
        }

        _guidesScroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(12, 10, 12, 12),   // the shared overlay tab-body padding
            Content = list,
        };
        GuidesTabContent.Children.Add(_guidesScroller);

        _guidesViewer = new GuideViewer(compact: true) { Visibility = Visibility.Collapsed };
        _guidesViewer.BackRequested += (_, _) => CloseOverlayGuide(replayCascade: true);
        GuidesTabContent.Children.Add(_guidesViewer);

        // Leaving the tab, or hiding the whole overlay, must not park a decoded bitmap in memory.
        GuidesTabContent.IsVisibleChanged += (_, _) =>
        {
            if (!GuidesTabContent.IsVisible && _openGuide != null) CloseOverlayGuide(replayCascade: false);
        };
    }

    // One catalog row: chamfered card, amber edge tick, title, native pixel size. Hover mirrors the
    // dock tile (3px slide + amber edge + tick glow); the slide is gated on Motion.Reduced.
    private FrameworkElement BuildGuideRow(GuideEntry guide)
    {
        var line = new Grid();
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var tick = new Border
        {
            Width = 2, Height = 13, Background = Hud.Br("AccentStrongBrush"),
            VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
        };
        Grid.SetColumn(tick, 0);
        line.Children.Add(tick);

        var name = new TextBlock
        {
            Text = guide.Title, FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = Hud.Br("FgDimBrush"), TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
        };
        Grid.SetColumn(name, 1);
        line.Children.Add(name);

        var px = new TextBlock
        {
            Text = $"{guide.NativeWidth}x{guide.NativeHeight}",
            FontFamily = Hud.Font("MonoFont"), FontSize = 9, Foreground = Hud.Br("FgDimBrush"),
            Opacity = 0.8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
        };
        Grid.SetColumn(px, 2);
        line.Children.Add(px);

        var host = Hud.CardFrame(line, out var frame, out _, chamfer: 7, padding: new Thickness(10, 8, 10, 8));
        host.Margin = new Thickness(0, 0, 0, 5);   // the 5px row gap
        host.Cursor = Cursors.Hand;
        var slide = new System.Windows.Media.TranslateTransform();
        host.RenderTransform = slide;

        var tickGlow = new System.Windows.Media.Effects.DropShadowEffect
        { Color = Hud.Col("AccentBrush"), BlurRadius = 8, ShadowDepth = 0, Opacity = 0.85 };

        host.MouseEnter += (_, _) =>
        {
            frame.Fill = Hud.Br("Bg3Brush");
            frame.Stroke = Hud.Br("AccentStrongBrush");
            tick.Background = Hud.Br("AccentBrush");
            tick.Effect = tickGlow;
            name.Foreground = Hud.Br("FgBrush");
            // Reduce animations: the hover keeps its colour change but loses the slide.
            if (Motion.Reduced) return;
            SlideGuideRow(slide, GuideRowSlidePx);
        };
        host.MouseLeave += (_, _) =>
        {
            frame.Fill = Hud.Br("Bg2NavBrush");
            frame.Stroke = Hud.Br("NavBorderBrush");
            tick.Background = Hud.Br("AccentStrongBrush");
            tick.Effect = null;
            name.Foreground = Hud.Br("FgDimBrush");
            if (Motion.Reduced)
            {
                slide.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
                slide.X = 0;
                return;
            }
            SlideGuideRow(slide, 0);
        };
        host.MouseLeftButtonDown += (_, _) => OpenOverlayGuide(guide, host);
        return host;
    }

    private static void SlideGuideRow(System.Windows.Media.TranslateTransform slide, double x)
        => slide.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty,
            new System.Windows.Media.Animation.DoubleAnimation(x, new Duration(TimeSpan.FromMilliseconds(Motion.HoverMs)))
            { EasingFunction = Motion.SlideOut });

    private void OpenOverlayGuide(GuideEntry guide, DependencyObject source)
    {
        if (_guidesScroller is null || _guidesViewer is null) return;
        InteractionLog.Click(guide.Title, source);
        _openGuide = guide;
        _guidesScroller.Visibility = Visibility.Collapsed;
        _guidesViewer.Visibility = Visibility.Visible;
        _guidesViewer.Show(guide);
        Logger.Info($"[UI] guide opened: {guide.Id} (overlay)");
    }

    private void CloseOverlayGuide(bool replayCascade)
    {
        var id = _openGuide?.Id;
        _openGuide = null;
        _guidesViewer?.Clear();                     // releases the decoded bitmap
        if (_guidesViewer != null) _guidesViewer.Visibility = Visibility.Collapsed;
        if (_guidesScroller != null) _guidesScroller.Visibility = Visibility.Visible;
        if (id != null) Logger.Info($"[UI] guide closed: {id}");
        if (replayCascade) PlayGuidesCascade();
    }

    // The same cascade the Mission Guides page plays (200ms, 40ms stagger, 12px rise, quad-out),
    // category heads and rows sharing one continuous index. The rows carry the hover slide on the
    // same transform, but that animates X while the cascade animates Y, so the two never fight.
    private void PlayGuidesCascade()
    {
        if (_guidesCascade.Count == 0) return;

        if (Motion.Reduced)
        {
            foreach (var fe in _guidesCascade)
            {
                fe.BeginAnimation(OpacityProperty, null);
                fe.Opacity = 1;
                var t = GuideRise(fe);
                t.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
                t.Y = 0;
            }
            return;
        }

        // Intentional local ease: the cascade's feel was tuned with QuadraticEase and is frozen.
        var ease = new System.Windows.Media.Animation.QuadraticEase
        { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        for (int i = 0; i < _guidesCascade.Count; i++)
        {
            var fe = _guidesCascade[i];
            var rise = GuideRise(fe);
            fe.Opacity = 0;
            rise.Y = GuideCascadeRisePx;
            var delay = TimeSpan.FromMilliseconds(i * GuideCascadeStepMs);
            var dur = TimeSpan.FromMilliseconds(GuideCascadeMs);
            fe.BeginAnimation(OpacityProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0, 1, dur) { BeginTime = delay, EasingFunction = ease });
            rise.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
                new System.Windows.Media.Animation.DoubleAnimation(GuideCascadeRisePx, 0, dur) { BeginTime = delay, EasingFunction = ease });
        }
    }

    // Rows already own a TranslateTransform for the hover slide; reuse it so the entrance and the
    // hover never fight over RenderTransform.
    private static System.Windows.Media.TranslateTransform GuideRise(FrameworkElement fe)
    {
        if (fe.RenderTransform is System.Windows.Media.TranslateTransform t) return t;
        var created = new System.Windows.Media.TranslateTransform();
        fe.RenderTransform = created;
        return created;
    }
}
