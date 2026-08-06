using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using NexusApp.Models;
using NexusApp.Services;
using NexusApp.Services.Map;

namespace NexusApp.Views;

/// <summary>
/// The MAP tab page: a DockPanel of a fixed-width (302px) WPF side panel plus <see cref="MapWebView"/>
/// filling the rest (airspace rule - the WebView2 HWND and the WPF chrome never overlap, precedent
/// CargoPlannerPage.cs:84-86). The side panel owns every piece of state the scene does not (active
/// system, layer toggles, selection, the draft route, the measure tool); the scene owns hover and
/// camera. Every C#-to-JS message goes through <see cref="MapSceneBuilder"/> into
/// <see cref="MapWebView.PostJson"/>. Layout and every color/size value are transcribed from the
/// approved mock (nexus-design-lab/starmap/index.html, local-only, never referenced by name here) -
/// see the task-9 report for the value-transcription table. A lazy singleton like GuidesPage: built
/// once, kept in MainWindow's tree, and shown/hidden by <see cref="Activate"/> plus Visibility, never
/// Loaded/Unloaded.
/// </summary>
public sealed class MapPage : UserControl
{
    // ── hardcoded values with no exact-value palette brush match (see task-9 report table) ──
    private static readonly SolidColorBrush OreChipBg = Frozen(Color.FromArgb(0x1A, 0x66, 0xE6, 0xA6));         // rgba(102,230,166,0.10)
    private static readonly SolidColorBrush OreChipBorder = Frozen(Color.FromArgb(0x59, 0x66, 0xE6, 0xA6));     // rgba(102,230,166,0.35)
    private static readonly SolidColorBrush MeasureArmedBg = Frozen(Color.FromArgb(0x14, 0x7F, 0xE9, 0xE0));    // rgba(127,233,224,0.08)
    private static readonly SolidColorBrush PlannerLegendSwatch = Frozen(Color.FromArgb(0x73, 0xFF, 0xB2, 0x3E)); // rgba(255,178,62,0.45)

    private static SolidColorBrush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    // The app-wide catalog (App.Map), not a private load. Kept as a local alias so the ~200 call
    // sites in this file read unchanged; the point of the promotion is that this page no longer OWNS
    // the geometry, not that it stops using it.
    private readonly MapCatalog _catalog = App.Map;
    private readonly IReadOnlyList<Resource> _resources;
    private readonly MapWebView _scene = new();

    // ── runtime state (mirrors the mock's chromeStore) ──
    // Seeded from AppSettings in the constructor (app review: a Pyro player used to re-pick Pyro and
    // re-flip every toggle on each launch, while neighbouring surfaces - TradeStartManual,
    // TradeDestManual, the Codex sell column - all persist). The initialisers here are the
    // never-saved fallback, not the runtime default.
    private const string DefaultSystem = "Stanton";
    private string _system = DefaultSystem;
    private bool _tradeOn, _guidesOn, _miningOn, _hangarOn;
    private bool _asteroidsOn = true;
    private bool _haulsOn, _ordersOn;   // app review G11, live-state layers
    private int? _selection;
    private int? _prevSelection;
    private double? _prevDistanceMeters;
    private readonly List<int> _draft = new();
    private List<int> _plannerIds = new();
    private List<(int Buy, int Sell)>? _plannerPushed;   // M-1 idempotency guard: the terminal-id pairs last pushed to the scene, so MainWindow re-pushing the same pins (or "no pins") on every MAP activation is a no-op
    private bool _measureArmed;
    private (string A, string B, double Meters)? _measureResult;
    private bool _sceneReady;

    private MapLayerPins _pins = null!;
    private MarketSnapshot? _lastSnapshotRef;
    private bool _lastConsent;

    // Player marker (design decisions a/b/c): resolved through MapCatalog.ResolvePlayerLocation from
    // the live Game.log location, independent of _system - the whole point of the cross-system case
    // is that this can name an object in a system the user is not currently looking at. Null means
    // no live location resolves (LocationTracker has nothing yet, or it named something the map
    // catalog cannot place - a jurisdiction like "Rough & Ready", or a gateway with no raw token).
    private MapObject? _playerLocation;

    private ExecHangarStatusLine _hangarLine = null!;

    // ── side panel element refs (built once, repainted live) ──
    private readonly Dictionary<string, Border> _systemPills = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LayerRowUi> _layerRows = new(StringComparer.OrdinalIgnoreCase);

    // Location search (all three systems) - shape mirrors TradePage.Sell.cs's commodity picker:
    // chrome built once, a suppression flag guards programmatic text writes, one commit choke point.
    private TextBox _searchBox = null!;
    private StackPanel _searchGrp = null!;   // the search box plus its results list, built once
    private UIElement _searchZone = null!;   // the whole SEARCH zone chrome: the click-away anchor
    private Border? _searchResultsMenu;
    private bool _suppressSearchText;

    // ── LOCATION zone (player marker side panel) ──
    private TextBlock _locEmptyText = null!;
    private StackPanel _locContent = null!;
    private StackPanel _locNameRow = null!;
    private Border _jumpToMeBtn = null!;

    private TextBlock _hintText = null!;
    private TextBlock _emptyText = null!;
    private StackPanel _selectedContent = null!;
    private TextBlock _kindText = null!;
    private TextBlock _nameText = null!;
    private TextBlock _parentRow = null!;
    private Run _parentNameRun = null!;
    private Border _distRow = null!;
    private TextBlock _distValue = null!;
    private TextBlock _distLabel = null!;
    private WrapPanel _oreRow = null!;
    private WrapPanel _terminalRow = null!;   // app review F10: one chip per UEX terminal on this object
    private Border _hangarChip = null!;
    private Border _addBtn = null!;
    private Border _viewPricesBtn = null!;
    private Border _openGuideBtn = null!;
    private Border _focusBtn = null!;

    private TextBlock _routeEmptyText = null!;
    private StackPanel _routeStopsPanel = null!;
    private Border _routeTotalRow = null!;
    private TextBlock _routeTotalValue = null!;
    private UIElement _legendRow = null!;
    private Border _sendBtn = null!;
    private TextBlock _sendBtnText = null!;

    private Border _measureBtn = null!;
    private TextBlock _measureBtnText = null!;
    private Border _measureOutRow = null!;
    private TextBlock _measureOutLabel = null!;
    private TextBlock _measureOutValue = null!;

    // SEND TO PLANNER: (first draft stop's terminal id, last draft stop's terminal id). Both ends
    // travel, not just the first - see OnSendToPlanner.
    public event Action<int, int>? OpenPlannerRequested;
    public event Action<int>? OpenPricesRequested;     // VIEW PRICES (terminal id)
    public event Action<string>? OpenGuideRequested;   // OPEN GUIDE (GuideCatalog id)

    // ── persisted view state (app review) ──

    internal const string LayerTrade = "trade", LayerGuides = "guides", LayerMining = "mining",
                          LayerHangar = "hangar", LayerAsteroids = "asteroids",
                          // Live-state layers (app review G11): the places THIS pilot has accepted
                          // contracts and running work orders at, as opposed to the static
                          // reference pins every other layer draws.
                          LayerHauls = "hauls", LayerOrders = "orders";

    /// <summary>Turns the persisted MapLayers string into the five layer booleans.
    /// <paramref name="saved"/> null means "never saved" and selects first-run defaults; an EMPTY
    /// string is a real saved state (the user switched everything off) and must round-trip as such.
    /// First-run turns MINING and ASTEROIDS on because they need no consent and no network, and
    /// TRADE only when market data is already enabled, since the TRADE row hides entirely under the
    /// consent gate and switching on a layer whose row is invisible would be a confusing default.
    /// Pure so it is testable without a WPF tree.</summary>
    internal static (bool Trade, bool Guides, bool Mining, bool Hangar, bool Asteroids, bool Hauls, bool Orders)
        ParseLayers(string? saved, bool marketConsent)
    {
        // HAULS and ORDERS default ON at first run. They need no consent and no network, their rows
        // hide themselves whenever there is nothing running (LayerRowVisible's count rule), and when
        // there is, they are the most personally relevant pins on the map. A layer that defaulted
        // off AND was invisible when empty would essentially never be discovered.
        if (saved is null) return (marketConsent, false, true, false, true, true, true);

        var keys = new HashSet<string>(
            saved.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);
        return (keys.Contains(LayerTrade), keys.Contains(LayerGuides), keys.Contains(LayerMining),
                keys.Contains(LayerHangar), keys.Contains(LayerAsteroids),
                keys.Contains(LayerHauls), keys.Contains(LayerOrders));
    }

    /// <summary>Inverse of ParseLayers. Returns "" (never null) when nothing is on, so the caller
    /// persists a real "all off" rather than a null that would re-seed the first-run defaults.</summary>
    internal static string FormatLayers(bool trade, bool guides, bool mining, bool hangar, bool asteroids,
                                        bool hauls = false, bool orders = false)
    {
        var keys = new List<string>(7);
        if (trade) keys.Add(LayerTrade);
        if (guides) keys.Add(LayerGuides);
        if (mining) keys.Add(LayerMining);
        if (hangar) keys.Add(LayerHangar);
        if (asteroids) keys.Add(LayerAsteroids);
        if (hauls) keys.Add(LayerHauls);
        if (orders) keys.Add(LayerOrders);
        return string.Join(",", keys);
    }

    private void SaveViewState()
    {
        App.Settings.Current.MapSystem = _system;
        App.Settings.Current.MapLayers = FormatLayers(_tradeOn, _guidesOn, _miningOn, _hangarOn, _asteroidsOn, _haulsOn, _ordersOn);
        App.Settings.Save();
    }

    // Work orders live on MainViewModel, which this page has no reference to, so the host supplies
    // them through a delegate read fresh on every RebuildPins. A null supplier (any caller that
    // predates G11, including tests) yields no orders and therefore an empty MY ORDERS layer whose
    // row hides itself - identical behaviour to before the layer existed.
    private readonly Func<IReadOnlyList<WorkOrder>>? _workOrders;

    public MapPage(IReadOnlyList<Resource> resources, Func<IReadOnlyList<WorkOrder>>? workOrders = null)
    {
        _resources = resources;
        _workOrders = workOrders;

        // Restore before anything reads _system or the layer flags - RebuildPins and the first
        // SendInit both depend on them. A persisted system that no longer exists in the catalog
        // (renamed, or removed like the unreachable-system exclusions) falls back to the default
        // rather than opening the map on a system with no objects.
        var savedSystem = App.Settings.Current.MapSystem;
        if (!string.IsNullOrWhiteSpace(savedSystem)
            && _catalog.Objects.Any(o => string.Equals(o.System, savedSystem, StringComparison.OrdinalIgnoreCase)))
            _system = savedSystem;

        (_tradeOn, _guidesOn, _miningOn, _hangarOn, _asteroidsOn, _haulsOn, _ordersOn) =
            ParseLayers(App.Settings.Current.MapLayers, App.Settings.Current.MarketDataEnabled == true);

        RebuildPins();
        _lastSnapshotRef = App.Market.Snapshot;
        _lastConsent = App.Settings.Current.MarketDataEnabled == true;

        Build();

        _scene.Ready += OnSceneReady;
        _scene.PinClicked += OnPinClicked;
        _scene.PinDoubleClicked += OnPinDoubleClicked;
        _scene.MeasurePicked += OnMeasurePicked;

        // Click-away dismiss for the search results list: a click anywhere on the WPF side of the
        // page that is not inside the SEARCH zone closes it (a result row's own click still lands
        // first-class - it IS inside the zone). Mouse UP, not down, and deliberately so: the menu
        // is an in-flow child of the TOP side-panel zone, so closing it reflows every control
        // below by the menu's height - on mouse-down that reflow lands BETWEEN the down and the
        // up, and the up then fires on whatever control slid under the cursor (a layer row click
        // toggling the wrong layer). On preview-mouse-up the event's route is already fixed
        // (WPF hit-tests once, when the input arrives), so the clicked control still receives
        // its bubbling up and the reflow happens after the click is spoken for. The 3D scene is
        // a native WebView2 HWND whose clicks never route through WPF, so scene interactions
        // dismiss via the pin handlers below instead; a click on EMPTY scene space reaches
        // neither side and leaves the list open - accepted, it neither blocks the scene nor
        // holds focus.
        PreviewMouseUp += (_, e) =>
        {
            if (_searchResultsMenu is not null && !IsInsideSearchZone(e.OriginalSource as DependencyObject))
                CloseSearchResults();
        };

        // Lazy-singleton page, visibility-toggled by MainWindow, never Loaded/Unloaded - the hangar
        // timer's only lifecycle signal (precedent: GuidesPage.cs:71-77).
        IsVisibleChanged += (_, _) => UpdateHangarTimer();

        // I-1: a live market tick while the user is free-floating on the MAP tab used to go
        // unnoticed until they left and came back - house pattern for a permanent singleton
        // subscription, TradePage.cs:117 (no unsubscription needed). Shares RefreshMarketDelta
        // with Activate() below so there is one implementation of the snapshot/consent delta check.
        App.Market.Changed += () => Dispatcher.BeginInvoke(() => { if (IsVisible) RefreshMarketDelta(); });

        // Player marker: same permanent-subscription idiom as App.Market.Changed right above, one
        // line up - a live Game.log location change while free-floating on the MAP tab must be
        // just as visible as a market tick is. Never moves the camera on its own (design b/c); it
        // only updates the resolved location and the side panel.
        App.Locations.Changed += () => Dispatcher.BeginInvoke(() => { if (IsVisible) RefreshPlayerLocation(); });

        // Live-state layers (app review G11). A contract accepted or completed while the MAP tab is
        // open moves pins, exactly as a Game.log location change moves the marker one line above.
        // Work orders come through RefreshLiveLayers instead, called by the host on its own
        // collection-changed subscription - this page cannot see that collection.
        App.Hauls.Changed += () => Dispatcher.BeginInvoke(() => { if (IsVisible) RefreshLiveLayers(); });

        RefreshSystemPills();
        RefreshLayerCounts();
        RefreshSelectionZone();
        RefreshRouteZone();
        RefreshMeasureZone();
        UpdateHangarTimer();
        RefreshPlayerLocation();   // resolve on load (design: opening the tab never auto-focuses, it only resolves+shows)
    }

    /// <summary>Called by MainWindow every time the dock activates this page.</summary>
    public void Activate()
    {
        Logger.Info("[UI] map: tab open");
        RefreshMarketDelta();
        RefreshPlayerLocation();   // catches a live location change that happened while this tab was hidden, same reasoning as RefreshMarketDelta above
    }

    /// <summary>B7: repaints when the market consent answer flips underneath this page. MainWindow's
    /// consent strip calls this after "Turn on" is clicked while MAP is the active page, so the trade
    /// layer and the gated hint fill in place instead of waiting for the next dock re-entry - the same
    /// fan-out TradePage.Refresh() already gets (MainWindow.xaml.cs). Deliberately separate from
    /// Activate() so it does not emit a "tab open" line; both share RefreshMarketDelta, whose delta
    /// check makes a redundant call free.</summary>
    public void Refresh()
    {
        Logger.Info("[UI] map: market consent answered, repainting");
        RefreshMarketDelta();
    }

    /// <summary>The snapshot/consent delta check shared by Activate() (every dock re-entry) and the
    /// ctor's App.Market.Changed subscription (I-1: every live market tick while this page is
    /// visible). ONE implementation so the two callers cannot drift apart. Rebuilds trade pins and
    /// re-sends init to the scene only when the market snapshot reference or the consent flag
    /// actually moved; the trailing refreshes are cheap/idempotent and always run to pick up
    /// anything else that changed while the page was off-screen.</summary>
    private void RefreshMarketDelta()
    {
        var snapshot = App.Market.Snapshot;
        var consent = App.Settings.Current.MarketDataEnabled == true;
        if (!ReferenceEquals(snapshot, _lastSnapshotRef) || consent != _lastConsent)
        {
            RebuildPins();
            _lastSnapshotRef = snapshot;
            _lastConsent = consent;
            RefreshRouteZone();   // M-4: pins rebuilt - the SEND gate depends on the draft's first stop still resolving to a terminal

            if (_sceneReady)
            {
                // M-3: a resend outside SwitchSystem (which already clears measure) leaves the
                // scene force-disarmed (applyInit always resets state.measure) while WPF's
                // _measureArmed flag still thinks it is armed - resync before the resend so the
                // MEASURE button does not show a dead "click two pins" state.
                _measureArmed = false;
                RefreshMeasureZone();
                SendInit();
            }
        }

        RefreshLayerCounts();
        RefreshSelectionZone();
        UpdateHangarTimer();
    }

    /// <summary>Rebuilds the two live-state layers and repaints (app review G11). Cheap enough to
    /// call on any haul or work order change, and a no-op for everything the user is looking at
    /// except the pins themselves. Skipped entirely while the tab is off screen: the next Activate
    /// rebuilds from scratch anyway.</summary>
    internal void RefreshLiveLayers()
    {
        if (!IsVisible) return;
        RebuildPins();
        RefreshLayerCounts();
        if (_sceneReady) SendInit();
    }

    // Portable self-swap: release the embedded browser's handles on Web\map before files are renamed.
    internal void ShutdownWebViewForUpdate() => _scene.ShutdownForUpdate();

    // ── planner route pinning (MainWindow forwards TradePage's pinned buy/sell terminals) ──

    /// <summary>Draws every pinned route's buy-to-sell leg (2026-08-01: when several
    /// routes are pinned the map shows all of them, so the map and the overlay never disagree about
    /// what is pinned). The scene reads the id list as consecutive PAIRS, one segment per pair, so
    /// two runs never get joined into one polyline through whichever terminal happened to be listed
    /// between them. A leg whose terminals do not both resolve to catalog objects is skipped
    /// individually - one unplaceable terminal must not blank the other runs.</summary>
    public void SetPlannerRoutes(IReadOnlyList<(int Buy, int Sell)> legs)
    {
        // M-1: MainWindow re-pushes the pinned routes on every MAP activation - an identical push
        // is a no-op, not a fresh scene post/log.
        if (_plannerPushed is { } pushed && pushed.SequenceEqual(legs)) return;

        var ids = new List<int>();
        int unresolved = 0;
        foreach (var (buyId, sellId) in legs)
        {
            var buyObj = _catalog.ResolveTerminal(FindTerminal(buyId));
            var sellObj = _catalog.ResolveTerminal(FindTerminal(sellId));
            if (buyObj == null || sellObj == null) { unresolved++; continue; }
            ids.Add(buyObj.Id);
            ids.Add(sellObj.Id);
        }

        _plannerIds = ids;
        _plannerPushed = legs.ToList();
        _scene.PostJson(MapSceneBuilder.BuildPlanner(_plannerIds));
        Logger.Info(ids.Count == 0
            ? $"[UI] map: planner routes cleared ({unresolved} unresolved)"
            : $"[UI] map: planner routes shown ({ids.Count / 2} of {legs.Count})");
    }

    public void ClearPlannerRoute()
    {
        // M-1: MainWindow calls this on every MAP activation with no pin. Nothing shown means
        // nothing to clear - skip the scene post and the log line.
        if (_plannerIds.Count == 0 && _plannerPushed == null) return;

        _plannerIds = new List<int>();
        _plannerPushed = null;
        _scene.PostJson(MapSceneBuilder.BuildPlanner(_plannerIds));
        Logger.Info("[UI] map: planner route cleared");
    }

    private MarketTerminal? FindTerminal(int id) =>
        App.Market.Snapshot?.Terminals.Rows.FirstOrDefault(t => t.Id == id);

    // ── pins / trade gating ──

    private void RebuildPins()
    {
        var snap = App.Market.Snapshot;
        IReadOnlyList<MarketTerminal> terminals = (snap != null && App.Settings.Current.MarketDataEnabled == true)
            ? snap.Terminals.Rows
            : Array.Empty<MarketTerminal>();

        _pins = new MapLayerPins(
            MapLayers.BuildTrade(terminals, _catalog),
            MapLayers.BuildGuides(_catalog),
            MapLayers.BuildMining(_resources, _catalog),
            MapLayers.HangarObject(_catalog),
            MapLayers.BuildHauls(App.Hauls.ActiveHauls, _catalog),
            MapLayers.BuildOrders(_workOrders?.Invoke() ?? Array.Empty<WorkOrder>(), _catalog));
    }

    private static bool TradeGated => App.Market.Snapshot == null || App.Settings.Current.MarketDataEnabled != true;

    // ── scene wiring ──

    private void OnSceneReady() => SendInit();

    private void SendInit()
    {
        _sceneReady = true;
        _scene.PostJson(MapSceneBuilder.BuildInit(_catalog, _system, _pins,
            _tradeOn, _guidesOn, _miningOn, _hangarOn, _asteroidsOn,
            _selection, _draft, _plannerIds, Motion.Reduced, _playerLocation?.Id, _haulsOn, _ordersOn));
    }

    // ── player marker (design a/b/c) ──

    /// <summary>Re-resolves the live Game.log location and, when it changed, pushes the marker to
    /// the scene and refreshes the LOCATION zone. Called on load, on every system switch (via
    /// SwitchSystem below), on every App.Locations.Changed tick while visible, and on tab
    /// activation (Activate above) - the same "resolve at every point the answer could have moved"
    /// contract RefreshMarketDelta already uses for market data. Never moves the camera (design b/c):
    /// this only updates state and posts the marker id, exactly like SendInit's own player field -
    /// the scene itself decides whether that id is a pin in the CURRENTLY active system (design c),
    /// so this never needs to gate on _system before sending.</summary>
    private void RefreshPlayerLocation()
    {
        var resolved = _catalog.ResolvePlayerLocation(App.Locations.LastKnownLocation, App.Locations.LastKnownRawToken);
        bool changed = resolved?.Id != _playerLocation?.Id;
        _playerLocation = resolved;

        // Only on change: every SendInit path (scene ready, system switch, market delta) already
        // carries _playerLocation in the init payload, so a rebuild restores the marker by itself.
        if (_sceneReady && changed)
            _scene.PostJson(MapSceneBuilder.BuildPlayerMarker(resolved?.Id));

        RefreshLocationZone();

        if (!changed) return;
        Logger.Info(resolved != null
            ? $"[UI] map: player marker {resolved.Name} ({resolved.System})"
            : "[UI] map: player marker cleared");
    }

    /// <summary>JUMP TO ME (design b): switches system first when the resolved location is not the
    /// one currently shown - SwitchSystem's own re-click guard makes the same-system case a no-op -
    /// then selects and flies exactly like a pin double-click (OnPinDoubleClicked) or a search pick
    /// (CommitSearchResult) already do. One call sequence, reused a third time rather than
    /// duplicated.</summary>
    private void OnJumpToMe()
    {
        if (_playerLocation is not { } obj) return;

        if (!string.Equals(obj.System, _system, StringComparison.OrdinalIgnoreCase))
            SwitchSystem(obj.System);

        Select(obj.Id);
        FocusOn(obj.Id);
        Logger.Info($"[UI] map: jump to me -> {obj.Name}");
    }

    private void OnPinClicked(int id)
    {
        CloseSearchResults();   // the click-away rule, carried across the native-HWND boundary
        Select(id);
    }

    private void OnPinDoubleClicked(int id)
    {
        CloseSearchResults();
        Select(id);
        FocusOn(id);
    }

    private void Select(int id)
    {
        if (_selection == id) return;   // re-select is a no-op

        _prevSelection = _selection;
        _selection = id;
        var obj = _catalog.ById(id);
        var prevObj = _prevSelection.HasValue ? _catalog.ById(_prevSelection.Value) : null;
        _prevDistanceMeters = _catalog.DistanceMeters(prevObj, obj);   // null-safe, same-system only

        _scene.PostJson(MapSceneBuilder.BuildSelect(id));
        RefreshSelectionZone();
        Logger.Info($"[UI] map: select {obj?.Name}");
    }

    private void FocusOn(int id)
    {
        var obj = _catalog.ById(id);
        if (obj == null) return;

        _scene.PostJson(MapSceneBuilder.BuildFocus(id));
        Logger.Info($"[UI] map: focus {obj.Name} ({_system})");
    }

    private void OnMeasurePicked(int a, int b)
    {
        var objA = _catalog.ById(a);
        var objB = _catalog.ById(b);
        var meters = _catalog.DistanceMeters(objA, objB);
        if (objA == null || objB == null || meters == null) return;   // defensive, scene refuses cross-system pairs itself

        // Measure is one-shot: the scene already disarmed itself before posting this result.
        _measureArmed = false;
        _measureResult = (objA.Name, objB.Name, meters.Value);
        RefreshMeasureZone();
        Logger.Info($"[UI] map: measure {objA.Name} -> {objB.Name} = {MapCatalog.FormatGm(meters.Value)}");
    }

    // ── system pills ──

    private void SwitchSystem(string sys)
    {
        if (string.Equals(sys, _system, StringComparison.OrdinalIgnoreCase)) return;   // active pill re-click is a no-op

        bool hadDraft = _draft.Count > 0;
        bool hadMeasure = _measureArmed || _measureResult != null;

        _system = sys;
        _selection = null;
        _prevSelection = null;
        _prevDistanceMeters = null;
        _draft.Clear();
        _measureArmed = false;
        _measureResult = null;

        SendInit();
        RefreshSystemPills();
        RefreshLayerCounts();
        RefreshSelectionZone();
        RefreshRouteZone();
        RefreshMeasureZone();
        RefreshPlayerLocation();   // re-resolve on system switch (design: the LOCATION zone's same-vs-cross-system state depends on _system)

        SaveViewState();
        Logger.Info($"[UI] map: system {sys}");
        if (hadDraft) Logger.Info("[UI] map: route draft cleared (system switch)");
        if (hadMeasure) Logger.Info("[UI] map: measure cleared (system switch)");
    }

    // ── layer toggles ──

    private void ToggleLayer(string key)
    {
        bool on;
        switch (key)
        {
            case "trade": _tradeOn = !_tradeOn; on = _tradeOn; break;
            case "guides": _guidesOn = !_guidesOn; on = _guidesOn; break;
            case "mining": _miningOn = !_miningOn; on = _miningOn; break;
            case "hangar": _hangarOn = !_hangarOn; on = _hangarOn; break;
            case "asteroids": _asteroidsOn = !_asteroidsOn; on = _asteroidsOn; break;
            case "hauls": _haulsOn = !_haulsOn; on = _haulsOn; break;
            case "orders": _ordersOn = !_ordersOn; on = _ordersOn; break;
            default: return;
        }

        _scene.PostJson(MapSceneBuilder.BuildLayerToggle(key, on));
        RefreshLayerRowVisual(key, on);
        RefreshSelectionZone();   // action-button gating and the trade-gated hint depend on layer state
        if (key == "trade") RefreshRouteZone();   // the draft/planner legend only shows while trade is on
        if (key == "hangar") UpdateHangarTimer();

        SaveViewState();
        Logger.Info($"[UI] map: layer {key} {(on ? "ON" : "OFF")}");
    }

    // ── selection zone actions ──

    private void OnAddToRoute()
    {
        if (_selection is not int id) return;
        if (_draft.Contains(id)) return;   // duplicate add is ignored, no log

        _draft.Add(id);
        _scene.PostJson(MapSceneBuilder.BuildRoute(_draft));
        RefreshRouteZone();
        Logger.Info($"[UI] map: route add {_catalog.ById(id)?.Name}");
    }

    private void OnRemoveFromRoute(int id)
    {
        if (!_draft.Remove(id)) return;

        _scene.PostJson(MapSceneBuilder.BuildRoute(_draft));
        RefreshRouteZone();
        Logger.Info($"[UI] map: route remove {_catalog.ById(id)?.Name}");
    }

    // App review 2026-08-01: this used to forward ONLY the first stop's terminal, so a numbered
    // multi-stop route with per-leg distances and a TOTAL row arrived at the planner as a start with
    // DESTINATION still on ANY - the zone is called ROUTE BUILDER and its hint says "The committed
    // route stays owned by the TRADE planner", so every stop after the first was silently dropped.
    // Both ends now travel: first stop becomes STARTING LOCATION, last becomes DESTINATION. That is
    // the honest contract for a two-field planner; intermediate stops still cannot be expressed
    // there, which is a planner capability question, not something to paper over here.
    private void OnSendToPlanner()
    {
        if (_draft.Count < 2) return;

        var (_, total) = MapSceneBuilder.DraftLegs(_draft, _catalog);
        int? startTerm = BestTradeTerminal(_draft[0]);
        int? destTerm = BestTradeTerminal(_draft[^1]);

        if (startTerm is null || destTerm is null)
        {
            // Names the end that failed, because "no terminals" on a six-stop route is not
            // actionable without knowing which end to fix.
            var which = startTerm is null ? (destTerm is null ? "both ends" : "first stop") : "last stop";
            Logger.Info($"[UI] map: route send skipped (no trade terminal for {which})");
            return;
        }

        OpenPlannerRequested?.Invoke(startTerm.Value, destTerm.Value);
        Logger.Info($"[UI] map: route send -> planner ({_draft.Count} stops, {MapCatalog.FormatGm(total)}, "
                    + $"start {_catalog.ById(_draft[0])?.Name}, dest {_catalog.ById(_draft[^1])?.Name})");
    }

    // Which terminal a trade pin's actions should target. A single map object routinely carries
    // SEVERAL UEX terminals (a station's admin office, its cargo deck, shops), and both actions used
    // to take terms[0] - whichever the snapshot happened to list first, which is frequently one with
    // no commodity prices at all. Preferring a terminal that actually has price rows makes VIEW
    // PRICES land on something to read and SEND TO PLANNER hand over a stop the planner can rank.
    // Falls back to terms[0] when none is priced, so the action still does what it always did rather
    // than becoming a dead button.
    //
    // App review F10, resolved 2026-08-01: the arbitrary pick is GONE from the prices path - the
    // SELECTION zone now lists every terminal as its own chip whenever there is more than one, and
    // VIEW PRICES stands down in that case (RefreshSelectionZone). This remains the picker for
    // SEND TO PLANNER, where it is not arbitrary in the same way: the planner takes exactly one
    // stop per end, so something has to choose, and "a terminal that actually has prices" is the
    // only defensible rule available. It is also still the VIEW PRICES path for the single-terminal
    // case, where there is nothing to choose between.
    private int? BestTradeTerminal(int objectId)
    {
        if (!_pins.TradeTerminalsByObject.TryGetValue(objectId, out var terms) || terms.Count == 0)
            return null;
        if (terms.Count == 1) return terms[0];

        var rows = App.Market.Snapshot?.TradePrices.Rows;
        if (rows is { Count: > 0 })
        {
            var priced = new HashSet<int>();
            foreach (var r in rows) priced.Add(r.TerminalId);
            foreach (var id in terms)
                if (priced.Contains(id)) return id;
        }
        return terms[0];
    }

    private void OnViewPrices()
    {
        if (_selection is not int id) return;
        if (BestTradeTerminal(id) is { } terminalId)
            OpenPricesRequested?.Invoke(terminalId);

        Logger.Info($"[UI] map: open TRADE prices {_catalog.ById(id)?.Name}");
    }

    private void OnOpenGuide()
    {
        if (_selection is not int id) return;
        if (!_pins.GuideIdByObject.TryGetValue(id, out var guideId)) return;

        OpenGuideRequested?.Invoke(guideId);
        Logger.Info($"[UI] map: open guide {guideId}");
    }

    // ── measure ──

    private void OnMeasureArmToggle()
    {
        _measureArmed = !_measureArmed;
        if (_measureArmed) _measureResult = null;   // arming clears any prior result; disarming keeps it

        _scene.PostJson(MapSceneBuilder.BuildMeasureArm(_measureArmed));
        RefreshMeasureZone();
        Logger.Info($"[UI] map: measure {(_measureArmed ? "armed" : "off")}");
    }

    // ── hangar status line lifecycle (F14: the shared ExecHangarStatusLine owns its own
    // 1-second ticker; this page only decides WHEN it runs, per the control's caller-owned
    // contract). Runs only while all three gates hold: page visible, hangar layer on, and the
    // chip actually shown (the hangar object selected) - the old chip ticked even while its
    // host was collapsed. ──

    private void UpdateHangarTimer()
    {
        if (IsVisible && _hangarOn && _hangarChip.Visibility == Visibility.Visible)
            _hangarLine.Start();
        else
            _hangarLine.Stop();
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Layout
    // ══════════════════════════════════════════════════════════════════════════════════════════

    private void Build()
    {
        var root = new DockPanel();
        var side = BuildSidePanel();
        DockPanel.SetDock(side, Dock.Right);
        root.Children.Add(side);
        root.Children.Add(_scene);   // fills the remaining space; never overlaps the side panel (airspace rule)
        Content = root;
    }

    private Border BuildSidePanel()
    {
        var stack = new StackPanel();
        stack.Children.Add(BuildSearchZone());
        stack.Children.Add(BuildSystemZone());
        stack.Children.Add(BuildLocationZone());
        stack.Children.Add(BuildLayersZone());
        stack.Children.Add(BuildSelectionZone());
        stack.Children.Add(BuildRouteZone());
        stack.Children.Add(BuildMeasureZone());

        var scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = stack,
        };

        return new Border
        {
            Width = 302,
            Background = Hud.Br("PageBgBrush"),
            BorderBrush = Hud.Br("NavBorderBrush"),
            BorderThickness = new Thickness(1, 0, 0, 0),
            Child = scroller,
        };
    }

    // Shared zone chrome: eyebrow header (dash + label, mirrors Hud.Header's own eyebrow
    // construction) over the zone's content, with a bottom hairline separating zones.
    private static Border Zone(string eyebrow, UIElement content, string? tag = null)
    {
        var stack = new StackPanel();
        stack.Children.Add(ZoneHeader(eyebrow, tag));
        stack.Children.Add(content);
        return new Border
        {
            Padding = new Thickness(16, 14, 16, 14),
            BorderBrush = Hud.Br("NavBorderBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = stack,
        };
    }

    private static UIElement ZoneHeader(string eyebrow, string? tag = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        row.Children.Add(new Border
        {
            Width = 16, Height = 2, Background = Hud.Br("AccentBrush"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
            Effect = new DropShadowEffect { Color = Hud.Col("AccentBrush"), BlurRadius = 7, ShadowDepth = 0, Opacity = 0.8 },
        });
        row.Children.Add(new TextBlock { Text = eyebrow, Style = (Style)Application.Current.FindResource("Eyebrow") });
        // Optional status tag beside the title (first use: ROUTE BUILDER's IN DEVELOPMENT). Amber
        // outline chip, dim enough to read as a caveat rather than a second title.
        if (tag is not null)
        {
            row.Children.Add(new Border
            {
                BorderBrush = Hud.Br("AccentStrongBrush"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3), Padding = new Thickness(5, 1, 5, 1),
                Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = tag, FontFamily = Hud.Font("UiFont"), FontSize = 8, FontWeight = FontWeights.Bold,
                    Foreground = Hud.Br("AccentBrush"),
                },
            });
        }
        return row;
    }

    // ── SEARCH zone ──

    // Location search covering all three systems (design decision c) - MapCatalog.Search itself has
    // no notion of "current system", so a result can and does name a system other than the one
    // currently shown; each row carries its own dim system tag so a cross-system hit is obvious
    // before it is clicked. Shape mirrors TradePage.Sell.cs's commodity picker: chrome built once,
    // results rebuilt on every keystroke, a suppression flag around programmatic text writes, and a
    // single commit choke point (CommitSearchResult) with its own CloseSearchResults() teardown.
    private const int SearchResultLimit = 8;

    private UIElement BuildSearchZone()
    {
        _searchGrp = new StackPanel();

        _searchBox = new TextBox
        {
            Style = (Style)Application.Current.FindResource("NexusTextBox"),
            Tag = "Search all systems...",
            ToolTip = "Search Stanton, Pyro and Nyx by name",
        };
        // Typing is ordinary text entry (fine); no key handler is ever attached here or anywhere in
        // this zone - NexusApp is mouse-driven by design, a result row commits only on click.
        _searchBox.TextChanged += (_, _) => { if (!_suppressSearchText) ShowSearchResults(); };
        _searchGrp.Children.Add(_searchBox);

        // The click-away walk anchors on the WHOLE zone (header, padding and all), not just the
        // inner group: to the user the eyebrow and its padding ARE the SEARCH zone, and a click
        // there should not read as "away".
        _searchZone = Zone("SEARCH", _searchGrp);
        return _searchZone;
    }

    // Rebuilt on every keystroke against MapCatalog.Search's case-insensitive, prefix-ranked match -
    // same search-first shape as CommodityPickerBox's suggest popup. Guards the empty/no-result
    // state itself: an empty box or an all-miss query removes any prior list rather than rendering
    // an empty results box.
    private void ShowSearchResults()
    {
        CloseSearchResults();

        var query = _searchBox.Text ?? "";
        var matches = _catalog.Search(query, SearchResultLimit);
        if (matches.Count == 0) return;

        var list = new StackPanel();
        foreach (var m in matches)
            list.Children.Add(BuildSearchResultRow(m, query));

        _searchResultsMenu = new Border
        {
            Background = Hud.Br("Bg2NavBrush"), BorderBrush = Hud.Br("NavBorderBrush"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 4, 0, 0),   // radius/gap match this page's own info boxes (_distRow, _measureOutRow)
            Child = list,
        };
        _searchGrp.Children.Add(_searchResultsMenu);
    }

    // Row content: name, a dim type label (same idiom as the SELECTION zone's _kindText), and a dim
    // system tag (TradePage.SystemTag - the codebase's one existing "dim system suffix" idiom,
    // reused rather than inventing a second one).
    private Border BuildSearchResultRow(MapObject obj, string query)
    {
        var top = new StackPanel { Orientation = Orientation.Horizontal };
        top.Children.Add(new TextBlock
        {
            Text = obj.Name, FontFamily = Hud.Font("UiFont"), FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Hud.Br("FgBrush"),
        });
        if (TradePage.SystemTag(obj.System) is { } tag) top.Children.Add(tag);

        var rowStack = new StackPanel();
        rowStack.Children.Add(top);
        rowStack.Children.Add(new TextBlock
        {
            Text = obj.Type.Replace('_', ' ').ToUpperInvariant(), FontFamily = Hud.Font("UiFont"), FontSize = 9.5,
            FontWeight = FontWeights.Bold, Foreground = Hud.Br("FgDimBrush"), Margin = new Thickness(0, 2, 0, 0),
        });

        var item = new Border { Padding = new Thickness(10, 8, 10, 8), Cursor = Cursors.Hand, Child = rowStack };
        item.MouseEnter += (_, _) => item.Background = Hud.Br("AccentFaintBrush");
        item.MouseLeave += (_, _) => item.Background = Brushes.Transparent;
        item.MouseLeftButtonUp += (_, _) => CommitSearchResult(obj, query);
        return item;
    }

    private void CloseSearchResults()
    {
        if (_searchResultsMenu is null) return;
        _searchGrp.Children.Remove(_searchResultsMenu);
        _searchResultsMenu = null;
    }

    // Containment walk for the click-away rule: visual parents where the node is a Visual, logical
    // parents otherwise (a click can originate on a Run, which is a ContentElement with no visual
    // parent chain of its own).
    private bool IsInsideSearchZone(DependencyObject? node)
    {
        while (node is not null)
        {
            if (ReferenceEquals(node, _searchZone)) return true;
            node = node is System.Windows.Media.Visual v
                ? System.Windows.Media.VisualTreeHelper.GetParent(v)
                : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }

    /// <summary>The one commit path (design decisions a/c): when the pick lives in another system,
    /// switch FIRST via the page's existing SwitchSystem - that keeps init resend/state clearing to
    /// the one implementation, rather than duplicating it here - THEN select and fly the camera
    /// through the exact same internal calls a pin double-click already uses (OnPinDoubleClicked:
    /// Select then FocusOn), so a search pick is indistinguishable from double-clicking that pin.</summary>
    /// <summary>Cross-page entry: show a specific object, switching system first when it lives in
    /// another one. Added 2026-08-01 (app review) because the Starmap was a ONE-WAY LEAF - other
    /// pages could be jumped to from it, but nothing could jump into it. Reuses the same
    /// switch-select-focus sequence the search box commits, so an arriving jump lands the camera
    /// exactly where a search for that object would.</summary>
    public void ShowObject(int objectId)
    {
        if (_catalog.ById(objectId) is not { } obj) return;   // stale id: no-op, never a half-move

        if (!string.Equals(obj.System, _system, StringComparison.OrdinalIgnoreCase))
            SwitchSystem(obj.System);

        Select(obj.Id);
        FocusOn(obj.Id);
        Logger.Info($"[UI] map: show {obj.Name} ({obj.System}) from another page");
    }

    private void CommitSearchResult(MapObject obj, string query)
    {
        if (!string.Equals(obj.System, _system, StringComparison.OrdinalIgnoreCase))
            SwitchSystem(obj.System);

        Select(obj.Id);
        FocusOn(obj.Id);

        Logger.Info($"[UI] map: search \"{query}\" -> {obj.Name} ({obj.System})");

        ClearSearchQuery();
    }

    private void ClearSearchQuery()
    {
        _suppressSearchText = true;
        try { _searchBox.Text = ""; }
        finally { _suppressSearchText = false; }
        CloseSearchResults();
    }

    // ── SYSTEM zone ──

    private UIElement BuildSystemZone()
    {
        var row = new UniformGrid { Rows = 1, Columns = 3 };
        var systems = new[] { "Stanton", "Pyro", "Nyx" };
        for (int i = 0; i < systems.Length; i++)
        {
            var pill = BuildSystemPill(systems[i]);
            pill.Margin = new Thickness(0, 0, i < systems.Length - 1 ? 6 : 0, 0);
            _systemPills[systems[i]] = pill;
            row.Children.Add(pill);
        }
        return Zone("SYSTEM", row);
    }

    private Border BuildSystemPill(string sys)
    {
        var text = new TextBlock
        {
            Text = sys.ToUpperInvariant(), FontFamily = Hud.Font("UiFont"), FontSize = 11, FontWeight = FontWeights.Bold,
            Foreground = Hud.Br("FgDimBrush"), HorizontalAlignment = HorizontalAlignment.Center,
        };
        var pill = new Border
        {
            Background = Hud.Br("Bg2NavBrush"), BorderBrush = Hud.Br("NavBorderBrush"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4), Padding = new Thickness(0, 7, 0, 7),
            Cursor = Cursors.Hand, Child = text,
        };
        // The active pill's gold styling wins over hover (matches the mock's CSS cascade: .syspill.on
        // is declared after .syspill:hover, so hovering the active pill keeps it gold).
        pill.MouseEnter += (_, _) => { if (!IsActiveSystem(sys)) { text.Foreground = Hud.Br("FgBrush"); pill.Background = Hud.Br("AccentFaintBrush"); } };
        pill.MouseLeave += (_, _) => { if (!IsActiveSystem(sys)) { text.Foreground = Hud.Br("FgDimBrush"); pill.Background = Hud.Br("Bg2NavBrush"); } };
        pill.MouseLeftButtonUp += (_, _) => SwitchSystem(sys);
        return pill;
    }

    private bool IsActiveSystem(string sys) => string.Equals(sys, _system, StringComparison.OrdinalIgnoreCase);

    private void RefreshSystemPills()
    {
        foreach (var (sys, pill) in _systemPills)
        {
            var text = (TextBlock)pill.Child;
            bool on = IsActiveSystem(sys);
            text.Foreground = on ? Hud.Br("GoldBrush") : Hud.Br("FgDimBrush");
            pill.BorderBrush = on ? Hud.Br("AccentStrongBrush") : Hud.Br("NavBorderBrush");
            // M-2 fix: Background was never restyled here, only Foreground/BorderBrush. The hover
            // handlers below only touch Background while a pill is INACTIVE (the active pill's gold
            // look is meant to win over hover), so nothing else ever put a deactivated pill's
            // Background back to neutral - it stayed whatever the last hover pass left it as (often
            // AccentFaintBrush, amber-tinted) until a later MouseEnter/MouseLeave on that same pill
            // recomputed it. Matches TradePage.RefreshScopePills, which sets Background here too.
            pill.Background = on ? Hud.Br("AccentFaintBrush") : Hud.Br("Bg2NavBrush");
        }
    }

    // ── LOCATION zone (player marker side panel, design b) ──

    // Quiet-state text (design: "an honest quiet state ... never a guess") - no button, matches the
    // house tone of _routeEmptyText/_emptyText right below it.
    private UIElement BuildLocationZone()
    {
        var stack = new StackPanel();

        _locEmptyText = new TextBlock
        {
            Text = "No live location.",
            FontFamily = Hud.Font("UiFont"), FontSize = 11, Foreground = Hud.Br("FgDimBrush"),
            TextWrapping = TextWrapping.Wrap,
        };
        stack.Children.Add(_locEmptyText);

        var content = new StackPanel { Visibility = Visibility.Collapsed };
        _locContent = content;

        _locNameRow = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(_locNameRow);

        _jumpToMeBtn = BuildActButton("JUMP TO ME", ghost: false);
        _jumpToMeBtn.Margin = new Thickness(0, 9, 0, 0);
        _jumpToMeBtn.MouseLeftButtonUp += (_, _) => OnJumpToMe();
        _jumpToMeBtn.Visibility = Visibility.Collapsed;
        content.Children.Add(_jumpToMeBtn);

        stack.Children.Add(content);
        return Zone("LOCATION", stack);
    }

    // Same-system: name only (the marker is already visible in the current view - design b calls
    // for JUMP TO ME specifically for the cross-system case). Cross-system: name + dim system tag +
    // the button. Unresolved: the quiet state above, no button - never a guess.
    private void RefreshLocationZone()
    {
        var obj = _playerLocation;
        _locEmptyText.Visibility = obj == null ? Visibility.Visible : Visibility.Collapsed;
        _locContent.Visibility = obj == null ? Visibility.Collapsed : Visibility.Visible;
        if (obj == null) return;

        bool crossSystem = !string.Equals(obj.System, _system, StringComparison.OrdinalIgnoreCase);

        _locNameRow.Children.Clear();
        _locNameRow.Children.Add(new TextBlock
        {
            Text = obj.Name, FontFamily = Hud.Font("UiFont"), FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Hud.Br("FgBrush"),
        });
        if (crossSystem && TradePage.SystemTag(obj.System) is { } tag) _locNameRow.Children.Add(tag);

        _jumpToMeBtn.Visibility = crossSystem ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── LAYERS zone ──

    private sealed class LayerRowUi
    {
        public Border Host = null!;
        public Ellipse Led = null!;
        public TextBlock Name = null!;
        public TextBlock Count = null!;
        public Border SwTrack = null!;
        public Border Knob = null!;
        public TranslateTransform KnobT = null!;
    }

    // Row visibility rule for a data layer (live-use finding, 2026-07-31): a row with nothing
    // in the active system just shows a 0, and toggling it does nothing - so it should not be there
    // to toggle. TRADE is the one exception: a 0 there can mean "market data consent is off" or "no
    // snapshot yet", which is a state the SELECTION zone's hint text ("Trade layer needs market data
    // (Settings).") exists to explain - hiding the row in that state would make the hint unreachable.
    // So TRADE stays visible whenever gated, even at count 0, and only hides once data is available
    // and the count is genuinely zero for the system. Every other row (including ASTEROID CLUSTERS,
    // which never actually hits zero - 96/158/70 across the three systems) uses the plain count rule
    // with no special-casing. Pure function of count + the existing TradeGated flag (line 244) so it
    // is unit-testable without the WPF tree - see MapPageLayerVisibilityTests.
    internal static bool LayerRowVisible(string key, int countInSystem, bool tradeGated) =>
        string.Equals(key, "trade", StringComparison.OrdinalIgnoreCase)
            ? (tradeGated || countInSystem > 0)
            : countInSystem > 0;

    private UIElement BuildLayersZone()
    {
        var stack = new StackPanel();
        stack.Children.Add(BuildLayerRow("trade", "TRADE"));
        stack.Children.Add(BuildLayerRow("guides", "GUIDES"));
        stack.Children.Add(BuildLayerRow("mining", "MINING"));
        stack.Children.Add(BuildLayerRow("hangar", "EXEC HANGAR"));
        // Live-state layers last among the data rows, above the base-map divider (app review G11):
        // they are the only two that can change on their own while the tab is open.
        stack.Children.Add(BuildLayerRow("hauls", "MY HAULS"));
        stack.Children.Add(BuildLayerRow("orders", "MY ORDERS"));
        stack.Children.Add(BuildBaseMapDivider());
        stack.Children.Add(BuildLayerRow("asteroids", "ASTEROID CLUSTERS"));
        return Zone("LAYERS", stack);
    }

    // Separates the always-on base map toggle from the four data layers above it (live-use
    // finding, 2026-07-31: ASTEROID CLUSTERS sitting last under an indent read as a child of EXEC
    // HANGAR, not as "not a data layer"). Hairline reuses NavBorderBrush, the same house separator
    // brush as this file's own Zone() bottom hairline (line 503-504) and the ROUTE BUILDER zone's
    // TOTAL-row top divider (line 1108), and as TradePage's Sep() (TradePage.cs:538-542) - no new
    // value invented. Caption style (UiFont 9.5 Bold FgDimBrush) matches _kindText, the dim caption
    // that sits above the object name in the SELECTION zone (line 925).
    private static UIElement BuildBaseMapDivider()
    {
        var stack = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        stack.Children.Add(new Border
        {
            Height = 1, Background = Hud.Br("NavBorderBrush"), Margin = new Thickness(0, 0, 0, 8),
        });
        stack.Children.Add(new TextBlock
        {
            Text = "BASE MAP", FontFamily = Hud.Font("UiFont"), FontSize = 9.5, FontWeight = FontWeights.Bold,
            Foreground = Hud.Br("FgDimBrush"), Margin = new Thickness(0, 0, 0, 4),
        });
        return stack;
    }

    private static Color LayerColor(string key) => key switch
    {
        "trade" => Hud.Col("AccentColor"),
        "guides" => Hud.Col("GoldColor"),
        "mining" => Hud.Col("OkColor"),
        "hangar" => Hud.Col("DangerColor"),
        // F14: the two LIVE personal-data layers earn their own hues instead of the base-map
        // slate they shipped in - the rail's grammar is color = layer identity, and "your data,
        // changing right now" deserves better than the asteroid toggle's neutral. Each matches
        // its own on-map tick: hauls were already cyan chevrons in the scene; the order tick
        // recolored gold -> this blue in the same pass, which also ended the collision where
        // order ticks and GUIDES both claimed gold.
        "hauls" => Hud.Col("CyanColor"),
        "orders" => Color.FromRgb(0x3B, 0x82, 0xF6),   // WorkOrder.StatusColorHex Mining blue - the queue's own family
        _ => Hud.Col("FgDimColor"),   // asteroids: a display toggle, not a data layer - stays slate
    };

    private UIElement BuildLayerRow(string key, string label)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var led = new Ellipse { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(led, 0);
        grid.Children.Add(led);

        var name = new TextBlock
        {
            Text = label, FontFamily = Hud.Font("UiFont"), FontWeight = FontWeights.Bold,
            FontSize = 11.5, Foreground = Hud.Br("FgDimBrush"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(9, 0, 9, 0),
        };
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);

        var count = new TextBlock
        {
            FontFamily = Hud.Font("MonoFont"), FontSize = 9.5, Foreground = Hud.Br("FgDimBrush"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 9, 0),
        };
        Grid.SetColumn(count, 2);
        grid.Children.Add(count);

        var knobT = new TranslateTransform();
        var knob = new Border
        {
            Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Background = Hud.Br("FgDimBrush"),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = knobT,
        };
        var swInner = new Grid();
        swInner.Children.Add(knob);
        var swTrack = new Border
        {
            Width = 26, Height = 14, CornerRadius = new CornerRadius(7),
            Background = Hud.Br("Bg3Brush"), BorderBrush = Hud.Br("BorderBrush"), BorderThickness = new Thickness(1),
            Child = swInner, VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(swTrack, 3);
        grid.Children.Add(swTrack);

        var host = new Border
        {
            Background = Brushes.Transparent, CornerRadius = new CornerRadius(3),
            Padding = new Thickness(2, 7, 2, 7), Cursor = Cursors.Hand,
            Child = grid,
        };
        host.MouseEnter += (_, _) => host.Background = Hud.Br("AccentFaintBrush");
        host.MouseLeave += (_, _) => host.Background = Brushes.Transparent;
        host.MouseLeftButtonUp += (_, _) => ToggleLayer(key);

        _layerRows[key] = new LayerRowUi { Host = host, Led = led, Name = name, Count = count, SwTrack = swTrack, Knob = knob, KnobT = knobT };
        return host;
    }

    private void RefreshLayerRowVisual(string key, bool on)
    {
        var ui = _layerRows[key];
        var color = LayerColor(key);

        if (on)
        {
            ui.Led.Fill = new SolidColorBrush(color);
            ui.Led.Stroke = null; ui.Led.StrokeThickness = 0;
            ui.Led.Effect = new DropShadowEffect { Color = color, BlurRadius = 7, ShadowDepth = 0, Opacity = 1 };
            ui.Name.Foreground = Hud.Br("FgBrush");
            ui.SwTrack.Background = Hud.Br("AccentDimBrush");
            ui.SwTrack.BorderBrush = Hud.Br("AccentStrongBrush");
            ui.Knob.Background = Hud.Br("AccentBrush");
            AnimateKnobX(ui.KnobT, 13);
        }
        else
        {
            ui.Led.Fill = Hud.Br("Bg3Brush");
            ui.Led.Stroke = Hud.Br("BorderBrush"); ui.Led.StrokeThickness = 1;
            ui.Led.Effect = null;
            ui.Name.Foreground = Hud.Br("FgDimBrush");
            ui.SwTrack.Background = Hud.Br("Bg3Brush");
            ui.SwTrack.BorderBrush = Hud.Br("BorderBrush");
            ui.Knob.Background = Hud.Br("FgDimBrush");
            AnimateKnobX(ui.KnobT, 2);
        }
    }

    private static void AnimateKnobX(TranslateTransform t, double x)
    {
        if (Motion.Reduced) { t.BeginAnimation(TranslateTransform.XProperty, null); t.X = x; return; }
        t.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(x, new Duration(TimeSpan.FromMilliseconds(Motion.HoverMs))) { EasingFunction = Motion.SlideOut });
    }

    private void RefreshLayerCounts()
    {
        var rows = _catalog.Objects.Where(o => string.Equals(o.System, _system, StringComparison.OrdinalIgnoreCase)).ToList();

        int tradeCount = rows.Count(o => _pins.TradeTerminalsByObject.ContainsKey(o.Id));
        int guidesCount = rows.Count(o => _pins.GuideIdByObject.ContainsKey(o.Id));
        int miningCount = rows.Count(o => _pins.OresByObject.ContainsKey(o.Id));
        int hangarCount = rows.Count(o => _pins.HangarObjectId == o.Id);
        int asteroidsCount = rows.Count(o => o.Type.StartsWith("Asteroid", StringComparison.OrdinalIgnoreCase));
        int haulsCount = rows.Count(o => _pins.Hauls.ContainsKey(o.Id));
        int ordersCount = rows.Count(o => _pins.Orders.ContainsKey(o.Id));

        _layerRows["trade"].Count.Text = tradeCount.ToString();
        _layerRows["guides"].Count.Text = guidesCount.ToString();
        _layerRows["mining"].Count.Text = miningCount.ToString();
        _layerRows["hangar"].Count.Text = hangarCount.ToString();
        _layerRows["asteroids"].Count.Text = asteroidsCount.ToString();
        _layerRows["hauls"].Count.Text = haulsCount.ToString();
        _layerRows["orders"].Count.Text = ordersCount.ToString();

        // Row visibility (live-use finding: EXEC HANGAR shows 0 in Stanton/Nyx and toggling it
        // does nothing there). Recomputed here because RefreshLayerCounts is the one choke point all
        // three callers already share - the ctor, SwitchSystem, and the market-data delta path
        // (RefreshMarketDelta) - so there is no second place this can drift from. Only the row's
        // Visibility changes; the on/off booleans (_tradeOn etc.) are never touched here, so a layer
        // left ON while its row is hidden keeps that state and the row reappears ON when the user
        // switches back to a system where it has data. No pin-suppression is needed to match: scene
        // pins are already system-scoped upstream in MapSceneBuilder.BuildInit's own catalog filter
        // (.Where(o => o.System == system) before the trade/guide/mine/hangar booleans are computed),
        // so a hidden-but-on layer with zero objects in this system already has zero pins to draw.
        bool gated = TradeGated;
        _layerRows["trade"].Host.Visibility = LayerRowVisible("trade", tradeCount, gated) ? Visibility.Visible : Visibility.Collapsed;
        _layerRows["guides"].Host.Visibility = LayerRowVisible("guides", guidesCount, gated) ? Visibility.Visible : Visibility.Collapsed;
        _layerRows["mining"].Host.Visibility = LayerRowVisible("mining", miningCount, gated) ? Visibility.Visible : Visibility.Collapsed;
        _layerRows["hangar"].Host.Visibility = LayerRowVisible("hangar", hangarCount, gated) ? Visibility.Visible : Visibility.Collapsed;
        _layerRows["asteroids"].Host.Visibility = LayerRowVisible("asteroids", asteroidsCount, gated) ? Visibility.Visible : Visibility.Collapsed;
        _layerRows["hauls"].Host.Visibility = LayerRowVisible("hauls", haulsCount, gated) ? Visibility.Visible : Visibility.Collapsed;
        _layerRows["orders"].Host.Visibility = LayerRowVisible("orders", ordersCount, gated) ? Visibility.Visible : Visibility.Collapsed;

        RefreshLayerRowVisual("trade", _tradeOn);
        RefreshLayerRowVisual("guides", _guidesOn);
        RefreshLayerRowVisual("mining", _miningOn);
        RefreshLayerRowVisual("hangar", _hangarOn);
        RefreshLayerRowVisual("asteroids", _asteroidsOn);
        RefreshLayerRowVisual("hauls", _haulsOn);
        RefreshLayerRowVisual("orders", _ordersOn);
    }

    // ── SELECTION zone ──

    private UIElement BuildSelectionZone()
    {
        var stack = new StackPanel();

        _hintText = new TextBlock
        {
            Text = TradeHint(App.Settings.Current.MarketDataEnabled, AppPaths.IsDemoProfile),
            FontFamily = Hud.Font("UiFont"), FontSize = 10.5, Foreground = Hud.Br("AccentBrush"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8), Visibility = Visibility.Collapsed,
        };
        stack.Children.Add(_hintText);

        _emptyText = new TextBlock
        {
            Text = "Click a pin to inspect it. Double-click to fly there.",
            FontFamily = Hud.Font("UiFont"), FontSize = 11, Foreground = Hud.Br("FgDimBrush"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4),
        };
        stack.Children.Add(_emptyText);

        var content = new StackPanel { Visibility = Visibility.Collapsed };
        _selectedContent = content;

        _kindText = new TextBlock { FontFamily = Hud.Font("UiFont"), FontSize = 9.5, FontWeight = FontWeights.Bold, Foreground = Hud.Br("FgDimBrush") };
        content.Children.Add(_kindText);

        _nameText = new TextBlock { FontFamily = Hud.Font("DisplayFont"), FontSize = 21, FontWeight = FontWeights.Bold, Foreground = Hud.Br("FgBrush"), Margin = new Thickness(0, 2, 0, 1) };
        content.Children.Add(_nameText);

        _parentRow = new TextBlock { FontFamily = Hud.Font("UiFont"), FontSize = 10.5, Foreground = Hud.Br("FgDimBrush"), Visibility = Visibility.Collapsed };
        _parentRow.Inlines.Add(new Run("in orbit of "));
        _parentNameRun = new Run("") { Foreground = Hud.Br("CyanBrush"), FontWeight = FontWeights.SemiBold };
        _parentRow.Inlines.Add(_parentNameRun);
        content.Children.Add(_parentRow);

        var distInner = new StackPanel { Orientation = Orientation.Horizontal };
        _distValue = new TextBlock { FontFamily = Hud.Font("MonoFont"), FontSize = 15, Foreground = Hud.Br("CyanBrush"), VerticalAlignment = VerticalAlignment.Bottom };
        _distLabel = new TextBlock { FontFamily = Hud.Font("UiFont"), FontSize = 9, FontWeight = FontWeights.Bold, Foreground = Hud.Br("FgDimBrush"), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(7, 0, 0, 0) };
        distInner.Children.Add(_distValue);
        distInner.Children.Add(_distLabel);
        _distRow = new Border
        {
            Background = Hud.Br("Bg2NavBrush"), BorderBrush = Hud.Br("NavBorderBrush"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 10, 0, 0),
            Child = distInner, Visibility = Visibility.Collapsed,
        };
        content.Children.Add(_distRow);

        _oreRow = new WrapPanel { Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
        content.Children.Add(_oreRow);

        // TERMINALS (app review F10). One map object routinely carries several UEX terminals - a
        // station's admin office, its cargo deck, its shops - and the panel named none of them,
        // while VIEW PRICES silently picked one. Now every terminal is a chip and the choice is the
        // user's. Only built when there is a choice to make; see RefreshSelectionZone.
        _terminalRow = new WrapPanel { Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
        content.Children.Add(_terminalRow);

        // F14: the shared ExecHangarStatusLine (compact, its fourth host) replaced a hand-rolled
        // countdown chip that sat in a PERMANENT danger-red tint, open or closed - red that never
        // changes stops meaning anything, and the one surface where the hangar is literally
        // selected on a map was the only host without the five phase lights. Neutral chip chrome;
        // the line's own phase colors (amber open / cyan counting down to open) carry the state.
        _hangarLine = new ExecHangarStatusLine(compact: true, surfaceName: "map");
        _hangarChip = new Border
        {
            Background = Hud.Br("Bg2NavBrush"), BorderBrush = Hud.Br("NavBorderBrush"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 10, 0, 0),
            Child = _hangarLine, Visibility = Visibility.Collapsed,
        };
        content.Children.Add(_hangarChip);

        var actRow = new WrapPanel { Margin = new Thickness(0, 11, 0, 0) };
        _addBtn = BuildActButton("ADD TO ROUTE", ghost: false);
        _addBtn.MouseLeftButtonUp += (_, _) => OnAddToRoute();
        _addBtn.Visibility = Visibility.Collapsed;
        actRow.Children.Add(_addBtn);

        _viewPricesBtn = BuildActButton("VIEW PRICES", ghost: true);
        _viewPricesBtn.MouseLeftButtonUp += (_, _) => OnViewPrices();
        _viewPricesBtn.Visibility = Visibility.Collapsed;
        actRow.Children.Add(_viewPricesBtn);

        _openGuideBtn = BuildActButton("OPEN GUIDE", ghost: false);
        _openGuideBtn.MouseLeftButtonUp += (_, _) => OnOpenGuide();
        _openGuideBtn.Visibility = Visibility.Collapsed;
        actRow.Children.Add(_openGuideBtn);

        _focusBtn = BuildActButton("FOCUS", ghost: true);
        _focusBtn.MouseLeftButtonUp += (_, _) => { if (_selection is int id) FocusOn(id); };
        actRow.Children.Add(_focusBtn);

        content.Children.Add(actRow);
        stack.Children.Add(content);

        return Zone("SELECTION", stack);
    }

    private static Border BuildActButton(string label, bool ghost)
    {
        var text = new TextBlock
        {
            Text = label, FontFamily = Hud.Font("UiFont"), FontSize = 10, FontWeight = FontWeights.Bold,
            Foreground = ghost ? Hud.Br("CyanBrush") : Hud.Br("AccentBrush"),
        };
        var btn = new Border
        {
            Background = ghost ? Brushes.Transparent : Hud.Br("AccentDimBrush"),
            BorderBrush = ghost ? Hud.Br("BorderBrush") : Hud.Br("AccentStrongBrush"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3),
            Padding = new Thickness(11, 6, 11, 6), Margin = new Thickness(0, 0, 7, 7),
            Cursor = Cursors.Hand, Child = text,
        };
        btn.MouseEnter += (_, _) =>
        {
            if (ghost) { btn.Background = Hud.Br("Bg2NavBrush"); text.Foreground = Hud.Br("FgBrush"); }
            else { btn.Background = Hud.Br("AccentStrongBrush"); text.Foreground = Hud.Br("OnAccentBrush"); }
        };
        btn.MouseLeave += (_, _) =>
        {
            if (ghost) { btn.Background = Brushes.Transparent; text.Foreground = Hud.Br("CyanBrush"); }
            else { btn.Background = Hud.Br("AccentDimBrush"); text.Foreground = Hud.Br("AccentBrush"); }
        };
        return btn;
    }

    private static Border BuildOreChip(string ore) => new()
    {
        Background = OreChipBg, BorderBrush = OreChipBorder, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(3), Padding = new Thickness(9, 3, 9, 3), Margin = new Thickness(0, 3, 5, 0),
        Child = new TextBlock { Text = ore, FontFamily = Hud.Font("MonoFont"), FontSize = 10, Foreground = Hud.Br("OkBrush") },
    };

    // One terminal chip (app review F10). Priced terminals read in the trade accent and unpriced
    // ones stay dim: with several terminals on one station, "which of these actually has prices" is
    // the first thing the user needs, and it is exactly what BestTradeTerminal used to decide for
    // them silently. Every chip opens ITS OWN terminal, so the arbitrary pick is gone from this path
    // entirely rather than being made a bit less arbitrary.
    private Border BuildTerminalChip(int terminalId, string name, bool priced)
    {
        var chip = new Border
        {
            Background = priced ? Hud.Br("AccentFaintBrush") : Hud.Br("Bg2NavBrush"),
            BorderBrush = priced ? Hud.Br("AccentStrongBrush") : Hud.Br("NavBorderBrush"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3),
            Padding = new Thickness(9, 3, 9, 3), Margin = new Thickness(0, 3, 5, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = priced ? $"Open prices for {name}." : $"{name} has no priced rows right now. Open it anyway.",
            Child = new TextBlock
            {
                Text = name, FontFamily = Hud.Font("UiFont"), FontSize = 10.5,
                Foreground = priced ? Hud.Br("AccentBrush") : Hud.Br("FgDimBrush"),
                MaxWidth = 210, TextTrimming = TextTrimming.CharacterEllipsis,
            },
        };
        chip.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Logger.Info($"[UI] map: open TRADE prices for terminal {terminalId}");
            OpenPricesRequested?.Invoke(terminalId);
        };
        return chip;
    }

    // Hint copy for a gated TRADE layer. Three genuinely different states hide behind the single
    // TradeGated flag, and one static string cannot serve all three: with the question unanswered
    // the one-click strip is now sitting directly above the map (B7), so pointing at Settings sends
    // the user the long way round; with consent already granted there is nothing in Settings to
    // change and the layer is simply waiting on the first fetch. Keyed off MarketNotice's own gate
    // rather than a second copy of it, so the "strip above" wording cannot outlive the strip -
    // including in the demo profile, where the strip is suppressed and Settings is right again.
    // The fourth combination (consent on, snapshot present) is unreachable: TradeGated is false
    // there and this text never shows. Pure so it is testable without the WPF tree, same idiom as
    // LayerRowVisible.
    internal static string TradeHint(bool? consent, bool isDemoProfile) =>
        MarketNotice.ShouldShowConsent(consent, isDemoProfile)
            ? "Trade layer needs live market data. Turn it on in the strip above."
            : consent != true
                ? "Trade layer needs live market data (Settings)."
                : "Trade layer is waiting for the first market fetch.";

    private void RefreshSelectionZone()
    {
        bool gated = _tradeOn && TradeGated;
        _hintText.Visibility = gated ? Visibility.Visible : Visibility.Collapsed;
        if (gated) _hintText.Text = TradeHint(App.Settings.Current.MarketDataEnabled, AppPaths.IsDemoProfile);

        var obj = _selection.HasValue ? _catalog.ById(_selection.Value) : null;
        if (obj == null)
        {
            _selectedContent.Visibility = Visibility.Collapsed;
            _emptyText.Visibility = Visibility.Visible;
            return;
        }

        _emptyText.Visibility = Visibility.Collapsed;
        _selectedContent.Visibility = Visibility.Visible;

        _kindText.Text = obj.Type.Replace('_', ' ').ToUpperInvariant();
        _nameText.Text = obj.Name;

        var parent = obj.Parent.HasValue ? _catalog.ById(obj.Parent.Value) : null;
        _parentRow.Visibility = parent != null ? Visibility.Visible : Visibility.Collapsed;
        _parentNameRun.Text = parent?.Name ?? "";

        bool showDist = _prevSelection.HasValue && _prevDistanceMeters.HasValue;
        _distRow.Visibility = showDist ? Visibility.Visible : Visibility.Collapsed;
        if (showDist)
        {
            _distValue.Text = MapCatalog.FormatGm(_prevDistanceMeters!.Value);
            var prevObj = _catalog.ById(_prevSelection!.Value);
            _distLabel.Text = ("from " + (prevObj?.Name ?? "")).ToUpperInvariant();
        }

        bool isTrade = _pins.TradeTerminalsByObject.ContainsKey(obj.Id);
        bool isGuide = _pins.GuideIdByObject.ContainsKey(obj.Id);
        bool isMine = _pins.OresByObject.ContainsKey(obj.Id);
        bool isHangar = _pins.HangarObjectId == obj.Id;

        bool showOres = isMine && _miningOn;
        _oreRow.Visibility = showOres ? Visibility.Visible : Visibility.Collapsed;
        _oreRow.Children.Clear();
        if (showOres)
            foreach (var ore in _pins.OresByObject[obj.Id])
                _oreRow.Children.Add(BuildOreChip(ore));

        bool showHangar = isHangar && _hangarOn;
        _hangarChip.Visibility = showHangar ? Visibility.Visible : Visibility.Collapsed;
        UpdateHangarTimer();   // the shared line runs only while its host is actually shown

        // TERMINALS (app review F10). Shown only when there is genuinely a choice: with one
        // terminal the VIEW PRICES button already goes to the only place it could, and a lone chip
        // beside it would be the same action twice.
        _terminalRow.Children.Clear();
        var terminalIds = isTrade && _tradeOn && _pins.TradeTerminalsByObject.TryGetValue(obj.Id, out var ids)
            ? ids : Array.Empty<int>();
        bool showTerminals = terminalIds.Count > 1;
        _terminalRow.Visibility = showTerminals ? Visibility.Visible : Visibility.Collapsed;
        if (showTerminals)
        {
            var snapshot = App.Market.Snapshot;
            var byId = snapshot?.Terminals.Rows.ToDictionary(t => t.Id);
            var priced = new HashSet<int>();
            foreach (var r in snapshot?.TradePrices.Rows ?? new List<TradePriceRow>()) priced.Add(r.TerminalId);

            foreach (var id in terminalIds)
            {
                // A terminal id with no row in the snapshot cannot be named, and an unnamed chip is
                // not a choice - skip it rather than offering "Terminal 4812".
                if (byId is null || !byId.TryGetValue(id, out var terminal) || string.IsNullOrWhiteSpace(terminal.Name))
                    continue;
                _terminalRow.Children.Add(BuildTerminalChip(id, TradeOriginResolver.LocationFirst(terminal.Name), priced.Contains(id)));
            }
            // Everything got skipped: fall back to the single button rather than an empty row.
            showTerminals = _terminalRow.Children.Count > 1;
            _terminalRow.Visibility = showTerminals ? Visibility.Visible : Visibility.Collapsed;
        }

        _addBtn.Visibility = (isTrade && _tradeOn) ? Visibility.Visible : Visibility.Collapsed;
        _viewPricesBtn.Visibility = (isTrade && _tradeOn && !showTerminals) ? Visibility.Visible : Visibility.Collapsed;
        _openGuideBtn.Visibility = (isGuide && _guidesOn) ? Visibility.Visible : Visibility.Collapsed;
        // FOCUS carries no gate; it is always available once something is selected.
    }

    // ── ROUTE BUILDER zone ──

    private UIElement BuildRouteZone()
    {
        var stack = new StackPanel();

        _routeEmptyText = new TextBlock
        {
            Text = "Turn the TRADE layer on, select a terminal, ADD TO ROUTE. The committed route stays owned by the TRADE planner.",
            FontFamily = Hud.Font("UiFont"), FontSize = 11, Foreground = Hud.Br("FgDimBrush"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4),
        };
        stack.Children.Add(_routeEmptyText);

        _routeStopsPanel = new StackPanel();
        stack.Children.Add(_routeStopsPanel);

        var totalInner = new Grid();
        totalInner.ColumnDefinitions.Add(new ColumnDefinition());
        totalInner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var totalLabel = new TextBlock { Text = "TOTAL", FontFamily = Hud.Font("UiFont"), FontSize = 9.5, FontWeight = FontWeights.Bold, Foreground = Hud.Br("FgDimBrush"), VerticalAlignment = VerticalAlignment.Bottom };
        Grid.SetColumn(totalLabel, 0);
        totalInner.Children.Add(totalLabel);
        _routeTotalValue = new TextBlock { FontFamily = Hud.Font("MonoFont"), FontSize = 13, Foreground = Hud.Br("AccentBrush"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
        Grid.SetColumn(_routeTotalValue, 1);
        totalInner.Children.Add(_routeTotalValue);
        _routeTotalRow = new Border
        {
            BorderBrush = Hud.Br("NavBorderBrush"), BorderThickness = new Thickness(0, 1, 0, 0),
            Margin = new Thickness(0, 7, 0, 0), Padding = new Thickness(0, 9, 0, 0),
            Child = totalInner, Visibility = Visibility.Collapsed,
        };
        stack.Children.Add(_routeTotalRow);

        _legendRow = BuildLegendRow();
        _legendRow.Visibility = Visibility.Collapsed;
        stack.Children.Add(_legendRow);

        var sendText = new TextBlock { Text = "SEND TO PLANNER", FontFamily = Hud.Font("UiFont"), FontSize = 11, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Hud.Br("OnAccentBrush") };
        _sendBtnText = sendText;
        _sendBtn = new Border
        {
            Background = Hud.Br("AccentBrush"), CornerRadius = new CornerRadius(3),
            Padding = new Thickness(0, 9, 0, 9), Margin = new Thickness(0, 11, 0, 0),
            Cursor = Cursors.Hand, Child = sendText,
        };
        _sendBtn.MouseEnter += (_, _) => { if (_draft.Count >= 2) _sendBtn.Background = Hud.Br("AccentHoverBrush"); };
        _sendBtn.MouseLeave += (_, _) => { if (_draft.Count >= 2) _sendBtn.Background = Hud.Br("AccentBrush"); };
        _sendBtn.MouseLeftButtonUp += (_, _) => OnSendToPlanner();
        stack.Children.Add(_sendBtn);

        // IN DEVELOPMENT tag (2026-08-01): the zone hands a multi-stop draft to a two-field
        // planner, so intermediate stops are dropped at the handoff (OnSendToPlanner's own note) -
        // the tag says out loud that this surface is not finished rather than letting that read
        // as a bug. Same amber chip language as the planner's own MARKET pill family.
        return Zone("ROUTE BUILDER", stack, tag: "IN DEVELOPMENT");
    }

    private static UIElement BuildLegendRow()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 9, 0, 0) };

        var draftItem = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 0) };
        var draftLine = new Line
        {
            X1 = 0, Y1 = 1, X2 = 22, Y2 = 1, Height = 2, Stretch = Stretch.None,
            Stroke = Hud.Br("AccentBrush"), StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 3, 2 },
            VerticalAlignment = VerticalAlignment.Center,
        };
        draftItem.Children.Add(draftLine);
        draftItem.Children.Add(new TextBlock { Text = "MAP DRAFT", FontFamily = Hud.Font("UiFont"), FontSize = 9, FontWeight = FontWeights.Bold, Foreground = Hud.Br("FgDimBrush"), Margin = new Thickness(6, 0, 0, 0) });
        row.Children.Add(draftItem);

        var plannerItem = new StackPanel { Orientation = Orientation.Horizontal };
        var plannerSwatch = new Border { Width = 22, Height = 2, Background = PlannerLegendSwatch, VerticalAlignment = VerticalAlignment.Center };
        plannerItem.Children.Add(plannerSwatch);
        plannerItem.Children.Add(new TextBlock { Text = "PLANNER (COMMITTED)", FontFamily = Hud.Font("UiFont"), FontSize = 9, FontWeight = FontWeights.Bold, Foreground = Hud.Br("FgDimBrush"), Margin = new Thickness(6, 0, 0, 0) });
        row.Children.Add(plannerItem);

        return row;
    }

    private UIElement BuildStopRow(int n, string name, int id, double? legMeters)
    {
        var grid = new Grid { Margin = new Thickness(2, 6, 2, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var circle = new Border
        {
            Width = 16, Height = 16, CornerRadius = new CornerRadius(8),
            Background = Hud.Br("AccentDimBrush"), BorderBrush = Hud.Br("AccentStrongBrush"), BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = n.ToString(), FontFamily = Hud.Font("MonoFont"), FontSize = 9, Foreground = Hud.Br("AccentBrush"),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        Grid.SetColumn(circle, 0);
        grid.Children.Add(circle);

        var nameTb = new TextBlock
        {
            Text = name, FontFamily = Hud.Font("UiFont"), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Hud.Br("FgBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(9, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(nameTb, 1);
        grid.Children.Add(nameTb);

        if (legMeters.HasValue)
        {
            var legTb = new TextBlock
            {
                Text = MapCatalog.FormatGm(legMeters.Value), FontFamily = Hud.Font("MonoFont"), FontSize = 9.5,
                Foreground = Hud.Br("FgDimBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 9, 0),
            };
            Grid.SetColumn(legTb, 2);
            grid.Children.Add(legTb);
        }

        var rm = new TextBlock
        {
            Text = "x", FontFamily = Hud.Font("MonoFont"), FontSize = 12, Foreground = Hud.Br("FgDimBrush"),
            Cursor = Cursors.Hand, Padding = new Thickness(2, 2, 5, 2), VerticalAlignment = VerticalAlignment.Center,
        };
        rm.MouseEnter += (_, _) => rm.Foreground = Hud.Br("DangerBrush");
        rm.MouseLeave += (_, _) => rm.Foreground = Hud.Br("FgDimBrush");
        rm.MouseLeftButtonUp += (_, _) => OnRemoveFromRoute(id);
        Grid.SetColumn(rm, 3);
        grid.Children.Add(rm);

        return grid;
    }

    private void RefreshRouteZone()
    {
        _routeEmptyText.Visibility = _draft.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _routeStopsPanel.Children.Clear();

        var (legs, total) = MapSceneBuilder.DraftLegs(_draft, _catalog);
        for (int i = 0; i < _draft.Count; i++)
        {
            var id = _draft[i];
            var obj = _catalog.ById(id);
            _routeStopsPanel.Children.Add(BuildStopRow(i + 1, obj?.Name ?? "", id, i > 0 ? legs[i - 1] : (double?)null));
        }

        _routeTotalRow.Visibility = _draft.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _routeTotalValue.Text = MapCatalog.FormatGm(total);

        _legendRow.Visibility = _tradeOn ? Visibility.Visible : Visibility.Collapsed;

        // M-4: SEND mirrors OnSendToPlanner's actual success condition (2+ stops AND the first
        // stop still resolves to a live trade terminal). A pins rebuild - consent revoked, a fresh
        // snapshot dropping a terminal - can pull that resolution out from under an already-built
        // draft; the delta path (RefreshMarketDelta) re-runs this zone so the button reflects it.
        bool firstStopHasTerminal = _draft.Count > 0
            && _pins.TradeTerminalsByObject.TryGetValue(_draft[0], out var firstTerms) && firstTerms.Count > 0;
        bool canSend = _draft.Count >= 2 && firstStopHasTerminal;
        _sendBtn.Background = canSend ? Hud.Br("AccentBrush") : Hud.Br("Bg3Brush");
        _sendBtn.Cursor = canSend ? Cursors.Hand : Cursors.Arrow;
        _sendBtn.IsHitTestVisible = canSend;
        _sendBtnText.Foreground = canSend ? Hud.Br("OnAccentBrush") : Hud.Br("FgDimBrush");
    }

    // ── MEASURE zone ──

    private UIElement BuildMeasureZone()
    {
        var stack = new StackPanel();

        var measText = new TextBlock { Text = "MEASURE DISTANCE", FontFamily = Hud.Font("UiFont"), FontSize = 10.5, FontWeight = FontWeights.Bold, Foreground = Hud.Br("CyanBrush"), HorizontalAlignment = HorizontalAlignment.Center };
        _measureBtnText = measText;
        _measureBtn = new Border
        {
            Background = Brushes.Transparent, BorderBrush = Hud.Br("BorderBrush"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3), Padding = new Thickness(0, 8, 0, 8), Cursor = Cursors.Hand,
            Child = measText,
        };
        _measureBtn.MouseEnter += (_, _) => { if (!_measureArmed) { _measureBtn.Background = Hud.Br("Bg2NavBrush"); measText.Foreground = Hud.Br("FgBrush"); } };
        _measureBtn.MouseLeave += (_, _) => { if (!_measureArmed) { _measureBtn.Background = Brushes.Transparent; measText.Foreground = Hud.Br("CyanBrush"); } };
        _measureBtn.MouseLeftButtonUp += (_, _) => OnMeasureArmToggle();
        stack.Children.Add(_measureBtn);

        var outInner = new Grid();
        outInner.ColumnDefinitions.Add(new ColumnDefinition());
        outInner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _measureOutLabel = new TextBlock { FontFamily = Hud.Font("UiFont"), FontSize = 9, FontWeight = FontWeights.Bold, Foreground = Hud.Br("FgDimBrush"), VerticalAlignment = VerticalAlignment.Bottom };
        Grid.SetColumn(_measureOutLabel, 0);
        outInner.Children.Add(_measureOutLabel);
        _measureOutValue = new TextBlock { FontFamily = Hud.Font("MonoFont"), FontSize = 12, Foreground = Hud.Br("CyanBrush"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
        Grid.SetColumn(_measureOutValue, 1);
        outInner.Children.Add(_measureOutValue);
        _measureOutRow = new Border
        {
            Background = Hud.Br("Bg2NavBrush"), BorderBrush = Hud.Br("NavBorderBrush"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3), Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 9, 0, 0),
            Child = outInner, Visibility = Visibility.Collapsed,
        };
        stack.Children.Add(_measureOutRow);

        return Zone("MEASURE", stack);
    }

    private void RefreshMeasureZone()
    {
        _measureBtnText.Text = _measureArmed ? "CLICK TWO PINS..." : "MEASURE DISTANCE";
        if (_measureArmed)
        {
            _measureBtn.BorderBrush = Hud.Br("CyanBrush");
            _measureBtn.Background = MeasureArmedBg;
            _measureBtn.Effect = new DropShadowEffect { Color = Hud.Col("CyanColor"), BlurRadius = 10, ShadowDepth = 0, Opacity = 0.25 };
        }
        else
        {
            _measureBtn.BorderBrush = Hud.Br("BorderBrush");
            _measureBtn.Background = Brushes.Transparent;
            _measureBtn.Effect = null;
        }
        _measureBtnText.Foreground = Hud.Br("CyanBrush");

        _measureOutRow.Visibility = _measureResult.HasValue ? Visibility.Visible : Visibility.Collapsed;
        if (_measureResult is { } r)
        {
            _measureOutLabel.Text = $"{r.A} -> {r.B}";
            _measureOutValue.Text = MapCatalog.FormatGm(r.Meters);
        }
    }
}
