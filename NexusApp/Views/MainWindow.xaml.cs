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
        ApplyThemedAssets();
        ThemeService.ThemeChanged += ApplyThemedAssets;
        AppVersionText.Text = $"App v{AppInfo.Version}";
        GameVersionText.Text = $"SC PU {GameData.Version}";
        UpdateShardChip();
        if (App.Shards != null) App.Shards.Changed += () => Dispatcher.Invoke(UpdateShardChip);
        UpdateSessionFooter();
        UpdateSessionChip();
        UpdateBlueprintChip();
        if (App.GameLog != null)
        {
            App.GameLog.Marked       += _ => Dispatcher.Invoke(UpdateSessionFooter);
            App.GameLog.StateChanged += () => Dispatcher.Invoke(() => { UpdateSessionFooter(); UpdateSessionChip(); UpdateBlueprintChip(); });
            App.GameLog.SessionReset += () => Dispatcher.Invoke(UpdateSessionFooter);
        }
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

    /// <summary>Runs the welcome tour. Always shows, regardless of FirstRunComplete —
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
        TutorialTarget.RsDecoder      => Anchor("scan", RsInputBox),
        TutorialTarget.OpenOverlay    => OverlayToggleBtn,
        TutorialTarget.DrawRegion     => PrepareOverlayForTutorial()?.SetRegionTarget,
        TutorialTarget.ScanToggle     => PrepareOverlayForTutorial()?.ScanToggleTarget,
        TutorialTarget.OverlayTabs    => PrepareOverlayForTutorial()?.TabStripTarget,
        TutorialTarget.ReferenceTools => NavBlue,
        TutorialTarget.BlueprintNetwork => Anchor("network", NavNetwork),
        _                             => null,
    };

    private FrameworkElement Anchor(string page, FrameworkElement element)
    {
        SetActivePage(page);
        return element;
    }

    private void StartScanRegionSetup()
    {
        var selector = new RegionSelectorWindow();
        selector.RegionSelected += ApplyScanRegion;
        selector.ShowOnMonitorOf(this);   // draw surface opens on this window's monitor (issue #6)
    }

    /// <summary>Ensures the overlay is open, visible, and on the SCAN tab for the tour.</summary>
    private OverlayWindow? PrepareOverlayForTutorial()
    {
        EnsureOverlay();
        if (_overlay == null) return null;
        if (!_overlay.IsVisible) _overlay.Show();
        _overlay.ShowScanTabForTutorial();
        _overlay.UpdateLayout();
        return _overlay;
    }

    // ── Nav ──────────────────────────────────────────────────────────────────

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.RadioButton rb && rb.Tag is string page)
            SetActivePage(page);
    }

    private void SetActivePage(string page)
    {
        PageCommand.Visibility    = page == "command"    ? Visibility.Visible : Visibility.Collapsed;
        PageScan.Visibility       = page == "scan"       ? Visibility.Visible : Visibility.Collapsed;
        PageBlueprints.Visibility = page == "blueprints" ? Visibility.Visible : Visibility.Collapsed;
        PageReference.Visibility  = page == "reference"  ? Visibility.Visible : Visibility.Collapsed;
        PageWorkOrders.Visibility = page == "workorders" ? Visibility.Visible : Visibility.Collapsed;
        PageNetwork.Visibility    = page == "network"    ? Visibility.Visible : Visibility.Collapsed;
        PageHauling.Visibility    = page == "hauling"    ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility   = page == "settings"   ? Visibility.Visible : Visibility.Collapsed;

        NavCommand.IsChecked = page == "command";
        NavScan.IsChecked    = page == "scan";
        NavBlue.IsChecked    = page == "blueprints";
        NavRef.IsChecked     = page == "reference";
        NavWork.IsChecked    = page == "workorders";
        NavNetwork.IsChecked = page == "network";
        NavHauling.IsChecked = page == "hauling";

        Title = page switch
        {
            "command"    => "Nexus - Operations",
            "scan"       => "Nexus — RS Signal Decoder",
            "blueprints" => "Nexus — Blueprint Library",
            "reference"  => "Nexus — Mining Codex",
            "workorders" => "Nexus — Refinery Tracker",
            "network"    => "Nexus — Blueprint Network",
            "hauling"    => "Nexus - Cargo Hauling",
            "settings"   => "Nexus - Settings",
            _            => "Nexus",
        };

        if (page == "blueprints") InitBlueprintBrowse();
        if (page == "reference") { BuildFilterPills(); BuildReferenceTree(); }
        if (page == "workorders") RebuildWorkOrderList();
        if (page == "command") InitCommandPage();
        if (page == "network") InitNetworkPage();
        if (page == "hauling") InitHaulingPage();
        if (page == "settings") InitSettingsPage();
        UpdateNavBadges();
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
            ShardChipText.Text = "no shard";
            ShardDot.Fill = (System.Windows.Media.Brush)FindResource("FgDimBrush");
        }
    }

    // Live SESSION footer in the rail (blueprint count + tracking state).
    private void UpdateSessionFooter()
    {
        if (App.GameLog == null) return;
        SessionBpCount.Text = App.GameLog.Count.ToString();
        SessionStateText.Text = App.GameLog.IsRunning ? "TRACKING" : "IDLE";
    }

    // Live SESSION telemetry chip in the header status strip: tracking is always on, so this confirms
    // it's monitoring the Game.log (green) or still waiting for one to appear (red). Game.log tracking
    // is file-based and not foreground-gated, so this chip never shows the paused (yellow) state.
    private void UpdateSessionChip()
    {
        if (App.GameLog == null || SessionChipText == null) return;
        bool running = App.GameLog.IsRunning;
        SessionChipText.Text = running ? "monitoring" : "no log";
        var c = running
            ? System.Windows.Media.Color.FromRgb(0x3E, 0xD6, 0x8B)
            : System.Windows.Media.Color.FromRgb(0xE5, 0x48, 0x4D);
        SessionDot.Fill = new System.Windows.Media.SolidColorBrush(c);
        SessionChipText.Foreground = new System.Windows.Media.SolidColorBrush(c);
    }

    // Live BLUEPRINTS telemetry chip: Auto-Track Blueprints is always on, so this confirms blueprint
    // auto-collection is active (green) while the Game.log is being monitored, else off (red).
    private void UpdateBlueprintChip()
    {
        if (App.GameLog == null || BlueprintChipText == null) return;
        bool tracking = App.GameLog.IsRunning && App.GameLog.AutoMark;
        BlueprintChipText.Text = tracking ? "tracking" : "off";
        var c = tracking
            ? System.Windows.Media.Color.FromRgb(0x3E, 0xD6, 0x8B)
            : System.Windows.Media.Color.FromRgb(0xE5, 0x48, 0x4D);
        BlueprintDot.Fill = new System.Windows.Media.SolidColorBrush(c);
        BlueprintChipText.Foreground = new System.Windows.Media.SolidColorBrush(c);
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

    // Active-count badges on the Refinery + Cargo Hauling rail items.
    private void UpdateNavBadges()
    {
        int orders = App.Data.GetWorkOrders().FindAll(o => o.Status != WorkOrderStatus.Complete).Count;
        int hauls = App.Hauls.ActiveHauls.Count;
        NavWorkBadge.Text = orders > 0 ? orders.ToString() : "";
        NavHaulingBadge.Text = hauls > 0 ? hauls.ToString() : "";
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

    private void ApplyThemedAssets()
    {
        NavIcon.Source = new System.Windows.Media.Imaging.BitmapImage(new System.Uri(ThemeService.IconUri));
    }

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

    private Border? _selectedRefCard;

    private void BuildReferenceTree()
    {
        ReferenceList.Children.Clear();
        _selectedRefCard = null;
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
            ReferenceDetailPanel.Children.Clear();
            ReferenceList.Children.Add(new TextBlock
            {
                Text = "No resources match", FontSize = 12, FontStyle = FontStyles.Italic,
                Foreground = (System.Windows.Media.Brush)FindResource("FgDimBrush"),
                Margin = new Thickness(12, 12, 0, 0),
            });
            return;
        }

        foreach (var r in filtered)
            ReferenceList.Children.Add(BuildResourceCard(r));

        if (ReferenceList.Children[0] is Border first)
        {
            _selectedRefCard = first;
            first.Background  = (System.Windows.Media.Brush)FindResource("AccentDimBrush");
            first.BorderBrush = (System.Windows.Media.Brush)FindResource("AccentBrush");
        }
        ShowResourceDetail(filtered[0]);
    }

    private Border BuildResourceCard(Resource r)
    {
        var rb = RarityBrush(r.Rarity);
        var cardBrush   = (System.Windows.Media.Brush)FindResource("Bg2NavBrush");
        var borderBrush = (System.Windows.Media.Brush)FindResource("NavBorderBrush");
        var accentDim   = (System.Windows.Media.Brush)FindResource("AccentDimBrush");
        var accentBrush = (System.Windows.Media.Brush)FindResource("AccentBrush");
        var highlight   = (System.Windows.Media.Brush)FindResource("HighlightBrush");
        var headFont    = (System.Windows.Media.FontFamily)System.Windows.Application.Current.FindResource("HeadFont");

        var card = new Border
        {
            Margin = new Thickness(0, 0, 8, 6), Padding = new Thickness(12, 9, 12, 9), CornerRadius = new CornerRadius(8),
            Background = cardBrush, BorderBrush = borderBrush, BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand, Tag = r,
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var gem = new Border { Width = 11, Height = 11, CornerRadius = new CornerRadius(3), Background = rb, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        Grid.SetColumn(gem, 0); grid.Children.Add(gem);
        var name = new TextBlock { Text = r.Name, FontSize = 13, Foreground = rb, FontFamily = headFont, VerticalAlignment = VerticalAlignment.Center, TextTrimming = System.Windows.TextTrimming.CharacterEllipsis };
        Grid.SetColumn(name, 1); grid.Children.Add(name);
        var rs = new TextBlock { Text = r.Method == "ship" ? $"RS {r.BaseRs:N0}" : "—", FontSize = 12, FontFamily = headFont, Foreground = (System.Windows.Media.Brush)FindResource("GoldBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        Grid.SetColumn(rs, 2); grid.Children.Add(rs);
        card.Child = grid;

        card.MouseEnter += (s, e) => { if (!ReferenceEquals(card, _selectedRefCard)) card.Background = highlight; };
        card.MouseLeave += (s, e) => { if (!ReferenceEquals(card, _selectedRefCard)) card.Background = cardBrush; };
        card.MouseLeftButtonDown += (s, e) =>
        {
            if (_selectedRefCard != null) { _selectedRefCard.Background = cardBrush; _selectedRefCard.BorderBrush = borderBrush; }
            _selectedRefCard = card;
            card.Background = accentDim; card.BorderBrush = accentBrush;
            ShowResourceDetail(r);
        };
        return card;
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

    private void ShowResourceDetail(Resource r)
    {
        ReferenceDetailPanel.Children.Clear();
        var rb   = RarityBrush(r.Rarity);
        var dim  = (System.Windows.Media.Brush)FindResource("FgDimBrush");
        var fg   = (System.Windows.Media.Brush)FindResource("FgBrush");
        var gold = (System.Windows.Media.Brush)FindResource("GoldBrush");
        var headFont = (System.Windows.Media.FontFamily)System.Windows.Application.Current.FindResource("HeadFont");

        // hero header
        var hero = new Border
        {
            CornerRadius = new CornerRadius(12), Padding = new Thickness(20, 14, 18, 14), Margin = new Thickness(0, 0, 0, 12),
            Background = (System.Windows.Media.Brush)FindResource("Bg2NavBrush"),
            BorderBrush = (System.Windows.Media.Brush)FindResource("NavBorderBrush"), BorderThickness = new Thickness(1),
        };
        var hg = new Grid();
        hg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var ht = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
        nameRow.Children.Add(new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(4), Background = rb, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        nameRow.Children.Add(new TextBlock { Text = r.Name, FontSize = 24, FontWeight = FontWeights.Bold, FontFamily = headFont, Foreground = rb, VerticalAlignment = VerticalAlignment.Center });
        ht.Children.Add(nameRow);
        ht.Children.Add(new TextBlock { Text = $"{CapFirst(r.Rarity)}  ·  Tier {r.Tier}  ·  {MethodLabel(r.Method)}", FontSize = 12, Foreground = dim, Margin = new Thickness(0, 4, 0, 0) });
        hg.Children.Add(ht);
        var rsStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        rsStack.Children.Add(new TextBlock { Text = "RS VALUE", FontSize = 9, FontWeight = FontWeights.Bold, Foreground = dim, HorizontalAlignment = HorizontalAlignment.Right });
        rsStack.Children.Add(new TextBlock { Text = r.Method == "ship" ? $"{r.BaseRs:N0}" : "—", FontSize = 32, FontFamily = headFont, Foreground = gold, HorizontalAlignment = HorizontalAlignment.Right });
        Grid.SetColumn(rsStack, 1); hg.Children.Add(rsStack);
        hero.Child = hg;
        ReferenceDetailPanel.Children.Add(hero);

        // refinery yields — best first, top 5 + show all
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

        // locations — top 6 + show all
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

        // blueprints — full list
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
            Text = r.Method == "ship" ? $"RS {r.BaseRs:N0}" : "—", FontSize = 12,
            Foreground = r.Method == "ship" ? TierBrush(r.Tier) : (System.Windows.Media.Brush)FindResource("FgDimBrush"),
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

    private Border? _selectedOrderRow;
    private WorkOrderEditorPanel? _currentEditor;
    private System.Windows.Threading.DispatcherTimer? _listTicker;
    private readonly Dictionary<string, TextBlock?> _rowLiveRefs = new();

    private void NewWorkOrder_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedOrderRow != null) _selectedOrderRow.Background = System.Windows.Media.Brushes.Transparent;
        _selectedOrderRow = null;
        ShowWorkOrderEditor(new WorkOrder());
    }

    private void ShowWorkOrderEditor(WorkOrder wo)
    {
        _currentEditor = new WorkOrderEditorPanel(wo, _vm);
        WorkOrderEditor.Content = _currentEditor;
        WoSaveBtn.IsEnabled   = true;
        WoDeleteBtn.IsEnabled = !_currentEditor.IsNewOrder;
    }

    private void WoSave_Click(object sender, RoutedEventArgs e)
    {
        if (_currentEditor == null) return;
        _currentEditor.Save();
        WoDeleteBtn.IsEnabled = true;
    }

    private void WoDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_currentEditor == null) return;
        _currentEditor.Delete();
        _currentEditor = null;
        WorkOrderEditor.Content = null;
        WoSaveBtn.IsEnabled   = false;
        WoDeleteBtn.IsEnabled = false;
        if (_selectedOrderRow != null) _selectedOrderRow.Background = System.Windows.Media.Brushes.Transparent;
        _selectedOrderRow = null;
    }

    private void RebuildWorkOrderList()
    {
        _rowLiveRefs.Clear();
        WorkOrderListPanel.Children.Clear();
        _selectedOrderRow = null;
        foreach (var wo in _vm.WorkOrders)
            WorkOrderListPanel.Children.Add(BuildWorkOrderRow(wo));

        var hasTimers = _vm.WorkOrders.Any(w => w.HasActiveTimer);
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
            if (!_rowLiveRefs.TryGetValue(wo.Id, out var subtitle)) continue;
            if (!wo.HasActiveTimer) continue;
            anyActive = true;
            if (subtitle != null) subtitle.Text = wo.SubtitleText;
        }
        if (!anyActive)
        {
            _listTicker?.Stop();
            _listTicker = null;
        }
    }

    private Border BuildWorkOrderRow(WorkOrder wo)
    {
        var cardBrush   = (System.Windows.Media.Brush)FindResource("Bg2NavBrush");
        var borderBrush = (System.Windows.Media.Brush)FindResource("NavBorderBrush");
        var accentBrush = (System.Windows.Media.Brush)FindResource("AccentBrush");
        var accentDim   = (System.Windows.Media.Brush)FindResource("AccentDimBrush");
        var highlight   = (System.Windows.Media.Brush)FindResource("HighlightBrush");
        var fgBrush     = (System.Windows.Media.Brush)FindResource("FgBrush");
        var dimBrush    = (System.Windows.Media.Brush)FindResource("FgDimBrush");
        var chipBg      = (System.Windows.Media.Brush)FindResource("Bg3Brush");
        var headFont    = (System.Windows.Media.FontFamily)FindResource("HeadFont");

        var outer = new Border
        {
            Background = cardBrush,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(8, 8, 8, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new Border
        {
            Background = BrushFromHex(wo.StatusColorHex),
            CornerRadius = new CornerRadius(8, 0, 0, 8),
        });

        var stack = new StackPanel { Margin = new Thickness(13, 10, 10, 10) };
        Grid.SetColumn(stack, 1);

        // top row: name | status chip | delete
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        top.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(wo.Label) ? wo.Resources : wo.Label,
            FontFamily = headFont, FontSize = 14, Foreground = fgBrush,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = System.Windows.TextTrimming.CharacterEllipsis,
        });

        var chip = new Border
        {
            Background = chipBg, CornerRadius = new CornerRadius(9),
            Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = wo.StatusLabel.ToUpperInvariant(), FontSize = 9, FontWeight = FontWeights.Bold,
                Foreground = BrushFromHex(wo.StatusColorHex),
            },
        };
        Grid.SetColumn(chip, 1);
        top.Children.Add(chip);

        var deleteTb = new TextBlock { Text = "✕", FontSize = 11, Foreground = dimBrush, VerticalAlignment = VerticalAlignment.Center };
        var deleteBtn = new Border
        {
            Child = deleteTb, Padding = new Thickness(8, 0, 0, 0),
            Cursor = System.Windows.Input.Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Delete",
        };
        deleteBtn.MouseEnter += (s, _) => deleteTb.Foreground = BrushFromHex("#EF4444");
        deleteBtn.MouseLeave += (s, _) => deleteTb.Foreground = dimBrush;
        deleteBtn.MouseLeftButtonDown += (s, e) =>
        {
            e.Handled = true;
            _vm.DeleteWorkOrderCommand.Execute(wo.Id);
            if (ReferenceEquals(outer, _selectedOrderRow))
            {
                _selectedOrderRow = null;
                _currentEditor = null;
                WorkOrderEditor.Content = null;
                WoSaveBtn.IsEnabled   = false;
                WoDeleteBtn.IsEnabled = false;
            }
        };
        Grid.SetColumn(deleteBtn, 2);
        top.Children.Add(deleteBtn);

        stack.Children.Add(top);

        var subtitleTb = new TextBlock
        {
            Text = wo.SubtitleText, FontSize = 11, Margin = new Thickness(0, 5, 0, 0),
            Foreground = BrushFromHex(wo.SubtitleForeground),
        };
        stack.Children.Add(subtitleTb);

        if (!string.IsNullOrWhiteSpace(wo.Resources) && !string.IsNullOrWhiteSpace(wo.Label))
        {
            stack.Children.Add(new TextBlock
            {
                Text = wo.Resources, FontSize = 11, Margin = new Thickness(0, 2, 0, 0),
                Foreground = dimBrush, TextTrimming = System.Windows.TextTrimming.CharacterEllipsis,
            });
        }

        if (wo.HasActiveTimer)
        {
            var remaining = wo.TimerEnd!.Value - DateTime.UtcNow;
            var scale = new System.Windows.Media.ScaleTransform(wo.TimerFraction, 1);
            var barContainer = new Grid { Height = 3, Margin = new Thickness(0, 8, 0, 0) };
            barContainer.Children.Add(new Border { Background = chipBg, CornerRadius = new CornerRadius(2) });
            var fillBorder = new Border
            {
                Background = BrushFromHex(wo.StatusColorHex), CornerRadius = new CornerRadius(2),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                RenderTransform = scale, RenderTransformOrigin = new System.Windows.Point(0, 0.5),
            };
            barContainer.Children.Add(fillBorder);
            stack.Children.Add(barContainer);
            if (remaining > TimeSpan.Zero)
            {
                var anim = new System.Windows.Media.Animation.DoubleAnimation
                {
                    From = wo.TimerFraction, To = 1.0, Duration = remaining,
                    FillBehavior = System.Windows.Media.Animation.FillBehavior.HoldEnd,
                };
                scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, anim);
            }
        }

        _rowLiveRefs[wo.Id] = subtitleTb;

        grid.Children.Add(stack);
        outer.Child = grid;

        outer.MouseEnter += (s, e) => { if (!ReferenceEquals(outer, _selectedOrderRow)) outer.Background = highlight; };
        outer.MouseLeave += (s, e) => { if (!ReferenceEquals(outer, _selectedOrderRow)) outer.Background = cardBrush; };
        outer.MouseLeftButtonDown += (s, e) =>
        {
            if (_selectedOrderRow != null)
            {
                _selectedOrderRow.Background  = cardBrush;
                _selectedOrderRow.BorderBrush = borderBrush;
            }
            _selectedOrderRow = outer;
            outer.Background  = accentDim;
            outer.BorderBrush = accentBrush;
            ShowWorkOrderEditor(wo);
        };

        return outer;
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
    private Border? _selectedBpRow;
    private enum BpOwnFilter { All, Owned, NotOwned }
    private BpOwnFilter _bpOwnFilter = BpOwnFilter.All;
    private string? _detailBpName;                 // blueprint currently shown in the detail panel
    private Border? _detailOwnedToggle;            // its "Owned" toggle, kept in sync with nav checkboxes
    // Maps a blueprint name to its nav-row toggle pill so a single toggle updates that
    // one row in place instead of rebuilding the whole list (the source of the lag).
    // Maps a blueprint name to a callback that refreshes that nav row's ownership
    // visuals (left strip, ✓ tick, hover pill) in place — so one toggle updates the
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
        // A filter is a lens, not a mode switch: stay where the user is and just
        // re-filter the current level. Search results are filter-independent, so
        // toggling a filter from a search drops back to the (filtered) root.
        if (_bpLevel == "search") GoRoot();
        else { RenderBlueprintNav(); ShowBlueprintLanding(); }
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
            if (child is Border b && b.Tag is Resource cr && cr.Name == res.Name)
            {
                if (_selectedRefCard != null)
                {
                    _selectedRefCard.Background  = (System.Windows.Media.Brush)FindResource("Bg2NavBrush");
                    _selectedRefCard.BorderBrush = (System.Windows.Media.Brush)FindResource("NavBorderBrush");
                }
                _selectedRefCard = b;
                b.Background  = (System.Windows.Media.Brush)FindResource("AccentDimBrush");
                b.BorderBrush = (System.Windows.Media.Brush)FindResource("AccentBrush");
                b.BringIntoView();
                break;
            }
        }
        ShowResourceDetail(res);
    }

    private void GoRoot()
    {
        InteractionLog.Nav("Blueprint Library: Browse (root)");
        _bpLevel = "root"; _bpCat = ""; _bpSub = "";
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
    // colour list can't catch — so for armor we keep everything up to and including
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
                BlueprintCrumbHost.Content = Breadcrumb(accent, ("Browse", GoRoot), ("Results", (Action?)null));
                BlueprintNavPanel.Children.Add(NavHeader("Results", _bpSearchResults.Count, accent));
                if (_bpSearchResults.Count == 0)
                    BlueprintNavPanel.Children.Add(new TextBlock { Text = "No matches", FontSize = 12, Foreground = (System.Windows.Media.Brush)FindResource("FgDimBrush"), Margin = new Thickness(6, 8, 0, 0) });
                foreach (var bp in _bpSearchResults)
                    BlueprintNavPanel.Children.Add(BlueprintRow(bp, true));
                break;

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

    private Border CategoryCard(string cat, int count)
    {
        var col      = CategoryBrush(cat);
        var fg       = (System.Windows.Media.Brush)FindResource("FgBrush");
        var dim      = (System.Windows.Media.Brush)FindResource("FgDimBrush");
        var headFont = (System.Windows.Media.FontFamily)FindResource("HeadFont");

        var card = new Border { Background = (System.Windows.Media.Brush)FindResource("Bg2NavBrush"), BorderBrush = (System.Windows.Media.Brush)FindResource("NavBorderBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 0, 8), Cursor = System.Windows.Input.Cursors.Hand };
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(new Border { Background = col, CornerRadius = new CornerRadius(10, 0, 0, 10) });

        var stack = new StackPanel { Margin = new Thickness(16, 12, 8, 12) };
        Grid.SetColumn(stack, 1);
        stack.Children.Add(new TextBlock { Text = cat, FontFamily = headFont, FontSize = 15, Foreground = fg });
        stack.Children.Add(new TextBlock { Text = "blueprints", FontSize = 9, Foreground = dim, Margin = new Thickness(0, 2, 0, 0) });
        g.Children.Add(stack);

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
        right.Children.Add(new TextBlock { Text = count.ToString(), FontFamily = headFont, FontSize = 18, Foreground = col, VerticalAlignment = VerticalAlignment.Center });
        right.Children.Add(new TextBlock { Text = "  ›", FontSize = 15, Foreground = dim, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(right, 2);
        g.Children.Add(right);

        card.Child = g;
        card.MouseLeftButtonDown += (_, __) => EnterCategory(cat);
        return card;
    }

    private Border DrillRow(string label, int count, System.Windows.Media.Brush col, Action onClick)
    {
        var fg       = (System.Windows.Media.Brush)FindResource("FgBrush");
        var dim      = (System.Windows.Media.Brush)FindResource("FgDimBrush");
        var headFont = (System.Windows.Media.FontFamily)FindResource("HeadFont");

        var card = new Border { Background = (System.Windows.Media.Brush)FindResource("Bg2NavBrush"), BorderBrush = (System.Windows.Media.Brush)FindResource("NavBorderBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 0, 6), Cursor = System.Windows.Input.Cursors.Hand };
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(new Border { Background = col, CornerRadius = new CornerRadius(8, 0, 0, 8) });

        var name = new TextBlock { Text = label, FontFamily = headFont, FontSize = 12, Foreground = fg, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 9, 8, 9), TextTrimming = System.Windows.TextTrimming.CharacterEllipsis };
        Grid.SetColumn(name, 1); g.Children.Add(name);

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        right.Children.Add(new TextBlock { Text = count.ToString(), FontSize = 11, FontWeight = FontWeights.Bold, Foreground = col, VerticalAlignment = VerticalAlignment.Center });
        right.Children.Add(new TextBlock { Text = "  ›", FontSize = 13, Foreground = dim, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(right, 2); g.Children.Add(right);

        card.Child = g;
        card.MouseLeftButtonDown += (_, __) => onClick();
        return card;
    }

    private Border BlueprintRow(NexusApp.Models.Blueprint bp, bool showCategory)
    {
        var fg    = (System.Windows.Media.Brush)FindResource("FgBrush");
        var dim   = (System.Windows.Media.Brush)FindResource("FgDimBrush");
        var hover = (System.Windows.Media.Brush)FindResource("HighlightBrush");
        var trans = System.Windows.Media.Brushes.Transparent;

        bool owned0 = App.Settings.IsBlueprintOwned(bp.Name);

        var card = new Border { Background = trans, BorderBrush = trans, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 7, 10, 7), Margin = new Thickness(0, 0, 0, 2), Cursor = System.Windows.Input.Cursors.Hand };
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

        card.Child = rowGrid;

        // in-place refresh of this row's ownership visuals (called by OnOwnershipChanged)
        _bpRowOwned[bp.Name] = owned =>
        {
            strip.Background = owned ? _ownedGreen : trans;
            ApplyCheckVisual(pill, owned);
            tick.Visibility = owned && pill.Visibility != Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        };

        card.MouseEnter += (s, _) =>
        {
            if (card != _selectedBpRow) card.Background = hover;
            pill.Visibility = Visibility.Visible;
            tick.Visibility = Visibility.Collapsed;
        };
        card.MouseLeave += (s, _) =>
        {
            if (card != _selectedBpRow) card.Background = trans;
            pill.Visibility = Visibility.Collapsed;
            tick.Visibility = App.Settings.IsBlueprintOwned(bp.Name) ? Visibility.Visible : Visibility.Collapsed;
        };
        card.MouseLeftButtonDown += (s, _) =>
        {
            if (_selectedBpRow != null) { _selectedBpRow.Background = trans; _selectedBpRow.BorderBrush = trans; }
            _selectedBpRow = card;
            card.Background = (System.Windows.Media.Brush)FindResource("AccentDimBrush");
            card.BorderBrush = (System.Windows.Media.Brush)FindResource("AccentBrush");
            ShowBlueprintDetail(bp);
        };
        return card;
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
    // — no rebuild. In a filtered view that row no longer belongs in the list, so the
    // current drill-down level is re-rendered (cheap — one level, not the catalog).
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
        BlueprintDetailPanel.Children.Add(new TextBlock { Text = "Mark blueprints as Owned as you unlock them in-game — your manifest fills in here.", FontSize = 12, Foreground = dim, Margin = new Thickness(2, 2, 0, 18), TextWrapping = TextWrapping.Wrap, MaxWidth = 540, HorizontalAlignment = HorizontalAlignment.Left });

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
        ClearOwnFilter();
        _bpLevel = "search";
        RenderBlueprintNav();
        BlueprintSearchBox.Clear();
        ShowBlueprintLanding();
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
            ClearOwnFilter();
            _bpLevel = "search";
            RenderBlueprintNav();
            BlueprintSearchBox.Clear();
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
        specs.Children.Add(HeroSpec("TOTAL COST", $"{totalScu:0.##} SCU", fgB, dimB, monoFont));
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
        // drafting corner ticks — pulled into the card's corner gutter so they sit
        // clear of the eyebrow text rather than on top of it
        heroRoot.Children.Add(new Border { Width = 11, Height = 11, BorderBrush = heroAccent, BorderThickness = new Thickness(2, 2, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(-13, -11, 0, 0) });
        heroRoot.Children.Add(new Border { Width = 11, Height = 11, BorderBrush = heroAccent, BorderThickness = new Thickness(0, 2, 2, 0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -11, -13, 0) });

        var heroCard = new Border
        {
            CornerRadius = new CornerRadius(12), Padding = new Thickness(20, 16, 18, 16), Margin = new Thickness(0, 0, 0, 8),
            Background = (System.Windows.Media.Brush)FindResource("Bg2NavBrush"),
            BorderBrush = (System.Windows.Media.Brush)FindResource("NavBorderBrush"), BorderThickness = new Thickness(1),
            Child = heroRoot,
        };
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
            qtyWrap.Children.Add(new TextBlock { Text = $"{ing.Quantity:0.##}", FontFamily = monoFont, FontSize = 13, Foreground = fgB, Width = 50, TextAlignment = System.Windows.TextAlignment.Right });
            qtyWrap.Children.Add(new TextBlock { Text = " " + ing.Unit, FontFamily = monoFont, FontSize = 11, Foreground = dimB, Width = 38, TextAlignment = System.Windows.TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center });
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
        var totalVal = new TextBlock { Text = $"{bomTotal:0.##} SCU", FontFamily = monoFont, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = heroAccent, VerticalAlignment = VerticalAlignment.Center };
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
        _overlay.ContractBoxVisibilityToggled += visible =>
        {
            _contractBoxVisible = visible;
            EnsureContractIndicator();
            if (_contractIndicator != null)
            {
                Logger.Info($"[WIN] contract-indicator {(visible ? "shown" : "hidden")}");
                if (visible) _contractIndicator.Show();
                else         _contractIndicator.Hide();
            }
        };
        _overlay.Hidden += () => _vm.PauseScanner();
        _overlay.Shown  += () => _vm.ResumeScanner();
        _overlay.OpenMonitorRequested += ShowLogMonitor;
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
            try { _overlay?.Close(); } catch { /* discarding a broken overlay — its OnClosed detaches handlers */ }
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
    // ScanIndicatorWindow — it never touches _scanIndicator / OcrService / the RS region.
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

    private void Settings_Click(object sender, RoutedEventArgs e)
        => SetActivePage("settings");

    private LogMonitorWindow? _logMonitor;

    // BETA: opens (or re-surfaces) the floating Game.log monitor — kept modeless and un-owned
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

    // Opens (or re-surfaces) the Nexus app-log monitor — Settings → Diagnostics. Modeless so it can
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
        if (!_bpInit) return;            // not visited yet — it'll read current ownership on first open
        UpdateOwnedCount();
        RenderBlueprintNav();
        // Rebuild the manifest landing so its "You own X of Y blueprints" line, percentage and
        // category bars reflect the new count live. Only when the landing is showing — if a single
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
            Text = "— CannonActual", FontSize = 11,
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
