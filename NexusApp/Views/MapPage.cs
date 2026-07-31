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
    private static readonly SolidColorBrush HangarChipBg = Frozen(Color.FromArgb(0x12, 0xFF, 0x6B, 0x6B));      // rgba(255,107,107,0.07)
    private static readonly SolidColorBrush HangarChipBorder = Frozen(Color.FromArgb(0x59, 0xFF, 0x6B, 0x6B));  // rgba(255,107,107,0.35)
    private static readonly SolidColorBrush OreChipBg = Frozen(Color.FromArgb(0x1A, 0x66, 0xE6, 0xA6));         // rgba(102,230,166,0.10)
    private static readonly SolidColorBrush OreChipBorder = Frozen(Color.FromArgb(0x59, 0x66, 0xE6, 0xA6));     // rgba(102,230,166,0.35)
    private static readonly SolidColorBrush MeasureArmedBg = Frozen(Color.FromArgb(0x14, 0x7F, 0xE9, 0xE0));    // rgba(127,233,224,0.08)
    private static readonly SolidColorBrush PlannerLegendSwatch = Frozen(Color.FromArgb(0x73, 0xFF, 0xB2, 0x3E)); // rgba(255,178,62,0.45)

    private static SolidColorBrush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    private readonly MapCatalog _catalog = MapCatalog.LoadEmbedded();
    private readonly IReadOnlyList<Resource> _resources;
    private readonly MapWebView _scene = new();

    // ── runtime state (mirrors the mock's chromeStore) ──
    private string _system = "Stanton";
    private bool _tradeOn, _guidesOn, _miningOn, _hangarOn;
    private bool _asteroidsOn = true;
    private int? _selection;
    private int? _prevSelection;
    private double? _prevDistanceMeters;
    private readonly List<int> _draft = new();
    private List<int> _plannerIds = new();
    private bool _measureArmed;
    private (string A, string B, double Meters)? _measureResult;
    private bool _sceneReady;

    private MapLayerPins _pins = null!;
    private MarketSnapshot? _lastSnapshotRef;
    private bool _lastConsent;

    private DispatcherTimer? _hangarTimer;

    // ── side panel element refs (built once, repainted live) ──
    private readonly Dictionary<string, Border> _systemPills = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LayerRowUi> _layerRows = new(StringComparer.OrdinalIgnoreCase);

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
    private Border _hangarChip = null!;
    private TextBlock _hangarValue = null!;
    private TextBlock _hangarLabel = null!;
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

    public event Action<int>? OpenPlannerRequested;    // SEND TO PLANNER (first draft stop's terminal id)
    public event Action<int>? OpenPricesRequested;     // VIEW PRICES (terminal id)
    public event Action<string>? OpenGuideRequested;   // OPEN GUIDE (GuideCatalog id)

    public MapPage(IReadOnlyList<Resource> resources)
    {
        _resources = resources;
        RebuildPins();
        _lastSnapshotRef = App.Market.Snapshot;
        _lastConsent = App.Settings.Current.MarketDataEnabled == true;

        Build();

        _scene.Ready += OnSceneReady;
        _scene.PinClicked += OnPinClicked;
        _scene.PinDoubleClicked += OnPinDoubleClicked;
        _scene.MeasurePicked += OnMeasurePicked;

        // Lazy-singleton page, visibility-toggled by MainWindow, never Loaded/Unloaded - the hangar
        // timer's only lifecycle signal (precedent: GuidesPage.cs:71-77).
        IsVisibleChanged += (_, _) => UpdateHangarTimer();

        RefreshSystemPills();
        RefreshLayerCounts();
        RefreshSelectionZone();
        RefreshRouteZone();
        RefreshMeasureZone();
        UpdateHangarTimer();
    }

    /// <summary>Called by MainWindow every time the dock activates this page.</summary>
    public void Activate()
    {
        Logger.Info("[UI] map: tab open");

        var snapshot = App.Market.Snapshot;
        var consent = App.Settings.Current.MarketDataEnabled == true;
        if (!ReferenceEquals(snapshot, _lastSnapshotRef) || consent != _lastConsent)
        {
            RebuildPins();
            _lastSnapshotRef = snapshot;
            _lastConsent = consent;
            if (_sceneReady) SendInit();
        }

        RefreshLayerCounts();
        RefreshSelectionZone();
        UpdateHangarTimer();
    }

    // Portable self-swap: release the embedded browser's handles on Web\map before files are renamed.
    internal void ShutdownWebViewForUpdate() => _scene.ShutdownForUpdate();

    // ── planner route pinning (MainWindow forwards TradePage's pinned buy/sell terminals) ──

    public void SetPlannerRoute(int buyTerminalId, int sellTerminalId)
    {
        var buyObj = _catalog.ResolveTerminal(FindTerminal(buyTerminalId));
        var sellObj = _catalog.ResolveTerminal(FindTerminal(sellTerminalId));
        if (buyObj == null || sellObj == null)
        {
            ClearPlannerRoute();
            Logger.Info("[UI] map: planner route unresolved");
            return;
        }

        _plannerIds = new List<int> { buyObj.Id, sellObj.Id };
        _scene.PostJson(MapSceneBuilder.BuildPlanner(_plannerIds));
        Logger.Info($"[UI] map: planner route shown ({buyObj.Name} -> {sellObj.Name})");
    }

    public void ClearPlannerRoute()
    {
        _plannerIds = new List<int>();
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
            MapLayers.HangarObject(_catalog));
    }

    private static bool TradeGated => App.Market.Snapshot == null || App.Settings.Current.MarketDataEnabled != true;

    // ── scene wiring ──

    private void OnSceneReady() => SendInit();

    private void SendInit()
    {
        _sceneReady = true;
        _scene.PostJson(MapSceneBuilder.BuildInit(_catalog, _system, _pins,
            _tradeOn, _guidesOn, _miningOn, _hangarOn, _asteroidsOn,
            _selection, _draft, _plannerIds, Motion.Reduced));
    }

    private void OnPinClicked(int id) => Select(id);

    private void OnPinDoubleClicked(int id)
    {
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
            default: return;
        }

        _scene.PostJson(MapSceneBuilder.BuildLayerToggle(key, on));
        RefreshLayerRowVisual(key, on);
        RefreshSelectionZone();   // action-button gating and the trade-gated hint depend on layer state
        if (key == "trade") RefreshRouteZone();   // the draft/planner legend only shows while trade is on
        if (key == "hangar") UpdateHangarTimer();

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

    private void OnSendToPlanner()
    {
        if (_draft.Count < 2) return;

        var (_, total) = MapSceneBuilder.DraftLegs(_draft, _catalog);
        if (_pins.TradeTerminalsByObject.TryGetValue(_draft[0], out var terms) && terms.Count > 0)
            OpenPlannerRequested?.Invoke(terms[0]);

        Logger.Info($"[UI] map: route send -> planner ({_draft.Count} stops, {MapCatalog.FormatGm(total)})");
    }

    private void OnViewPrices()
    {
        if (_selection is not int id) return;
        if (_pins.TradeTerminalsByObject.TryGetValue(id, out var terms) && terms.Count > 0)
            OpenPricesRequested?.Invoke(terms[0]);

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

    // ── hangar countdown timer (visible AND the hangar layer on, only) ──

    private void UpdateHangarTimer()
    {
        bool shouldRun = IsVisible && _hangarOn;
        if (shouldRun)
        {
            if (_hangarTimer != null) return;   // already running - never stack a second timer
            _hangarTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _hangarTimer.Tick += (_, _) => RefreshHangarChip();
            _hangarTimer.Start();
            RefreshHangarChip();
        }
        else
        {
            _hangarTimer?.Stop();
            _hangarTimer = null;
        }
    }

    private void RefreshHangarChip()
    {
        var snap = ExecHangarCycle.At(DateTime.UtcNow, App.Settings.Current.ExecHangarAnchorOverrideUtc);
        _hangarValue.Text = ExecHangarCycle.FormatCountdown(snap.TimeToTransition);
        _hangarLabel.Text = snap.IsOpen ? "EXEC HANGAR CLOSES" : "EXEC HANGAR OPENS";
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
        stack.Children.Add(BuildSystemZone());
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
    private static Border Zone(string eyebrow, UIElement content)
    {
        var stack = new StackPanel();
        stack.Children.Add(ZoneHeader(eyebrow));
        stack.Children.Add(content);
        return new Border
        {
            Padding = new Thickness(16, 14, 16, 14),
            BorderBrush = Hud.Br("NavBorderBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = stack,
        };
    }

    private static UIElement ZoneHeader(string eyebrow)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        row.Children.Add(new Border
        {
            Width = 16, Height = 2, Background = Hud.Br("AccentBrush"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
            Effect = new DropShadowEffect { Color = Hud.Col("AccentBrush"), BlurRadius = 7, ShadowDepth = 0, Opacity = 0.8 },
        });
        row.Children.Add(new TextBlock { Text = eyebrow, Style = (Style)Application.Current.FindResource("Eyebrow") });
        return row;
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
        }
    }

    // ── LAYERS zone ──

    private sealed class LayerRowUi
    {
        public Ellipse Led = null!;
        public TextBlock Name = null!;
        public TextBlock Count = null!;
        public Border SwTrack = null!;
        public Border Knob = null!;
        public TranslateTransform KnobT = null!;
    }

    private UIElement BuildLayersZone()
    {
        var stack = new StackPanel();
        stack.Children.Add(BuildLayerRow("trade", "TRADE", sub: false));
        stack.Children.Add(BuildLayerRow("guides", "GUIDES", sub: false));
        stack.Children.Add(BuildLayerRow("mining", "MINING", sub: false));
        stack.Children.Add(BuildLayerRow("hangar", "EXEC HANGAR", sub: false));
        stack.Children.Add(BuildLayerRow("asteroids", "ASTEROID CLUSTERS", sub: true));
        return Zone("LAYERS", stack);
    }

    private static Color LayerColor(string key) => key switch
    {
        "trade" => Hud.Col("AccentColor"),
        "guides" => Hud.Col("GoldColor"),
        "mining" => Hud.Col("OkColor"),
        "hangar" => Hud.Col("DangerColor"),
        _ => Hud.Col("FgDimColor"),   // asteroids: a display toggle, not a data layer - stays slate
    };

    private UIElement BuildLayerRow(string key, string label, bool sub)
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
            Text = label, FontFamily = Hud.Font("UiFont"), FontWeight = sub ? FontWeights.SemiBold : FontWeights.Bold,
            FontSize = sub ? 10 : 11.5, Foreground = Hud.Br("FgDimBrush"),
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
            Margin = new Thickness(sub ? 16 : 0, 0, 0, 0),
            Child = grid,
        };
        host.MouseEnter += (_, _) => host.Background = Hud.Br("AccentFaintBrush");
        host.MouseLeave += (_, _) => host.Background = Brushes.Transparent;
        host.MouseLeftButtonUp += (_, _) => ToggleLayer(key);

        _layerRows[key] = new LayerRowUi { Led = led, Name = name, Count = count, SwTrack = swTrack, Knob = knob, KnobT = knobT };
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

        _layerRows["trade"].Count.Text = rows.Count(o => _pins.TradeTerminalsByObject.ContainsKey(o.Id)).ToString();
        _layerRows["guides"].Count.Text = rows.Count(o => _pins.GuideIdByObject.ContainsKey(o.Id)).ToString();
        _layerRows["mining"].Count.Text = rows.Count(o => _pins.OresByObject.ContainsKey(o.Id)).ToString();
        _layerRows["hangar"].Count.Text = rows.Count(o => _pins.HangarObjectId == o.Id).ToString();
        _layerRows["asteroids"].Count.Text = rows.Count(o => o.Type.StartsWith("Asteroid", StringComparison.OrdinalIgnoreCase)).ToString();

        RefreshLayerRowVisual("trade", _tradeOn);
        RefreshLayerRowVisual("guides", _guidesOn);
        RefreshLayerRowVisual("mining", _miningOn);
        RefreshLayerRowVisual("hangar", _hangarOn);
        RefreshLayerRowVisual("asteroids", _asteroidsOn);
    }

    // ── SELECTION zone ──

    private UIElement BuildSelectionZone()
    {
        var stack = new StackPanel();

        _hintText = new TextBlock
        {
            Text = "Trade layer needs market data (Settings).",
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

        var hangInner = new StackPanel { Orientation = Orientation.Horizontal };
        _hangarValue = new TextBlock { FontFamily = Hud.Font("MonoFont"), FontSize = 14, Foreground = Hud.Br("DangerBrush") };
        _hangarLabel = new TextBlock { FontFamily = Hud.Font("UiFont"), FontSize = 9, FontWeight = FontWeights.Bold, Foreground = Hud.Br("FgDimBrush"), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        hangInner.Children.Add(_hangarValue);
        hangInner.Children.Add(_hangarLabel);
        _hangarChip = new Border
        {
            Background = HangarChipBg, BorderBrush = HangarChipBorder, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3), Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 10, 0, 0),
            Child = hangInner, Visibility = Visibility.Collapsed,
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

    private void RefreshSelectionZone()
    {
        _hintText.Visibility = (_tradeOn && TradeGated) ? Visibility.Visible : Visibility.Collapsed;

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
        if (showHangar) RefreshHangarChip();

        _addBtn.Visibility = (isTrade && _tradeOn) ? Visibility.Visible : Visibility.Collapsed;
        _viewPricesBtn.Visibility = (isTrade && _tradeOn) ? Visibility.Visible : Visibility.Collapsed;
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

        return Zone("ROUTE BUILDER", stack);
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

        bool canSend = _draft.Count >= 2;
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
