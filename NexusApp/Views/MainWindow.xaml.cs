using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NexusApp.Models;
using NexusApp.Services;
using NexusApp.ViewModels;

namespace NexusApp.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private OverlayWindow? _overlay;
    private ScanIndicatorWindow? _scanIndicator;
    private bool _boxVisible = false;

    private bool _suppressAutocomplete;

    public MainWindow()
    {
        InitializeComponent();
        ApplyThemedAssets();
        ThemeService.ThemeChanged += ApplyThemedAssets;
        AppVersionText.Text = $"App v{AppInfo.Version}";
        GameVersionText.Text = $"Star Citizen PU v{GameData.Version}";
        _vm = new MainViewModel();
        DataContext = _vm;
        _vm.OcrValueReceived    += v => { _overlay?.ReceiveOcrValue(v); _scanIndicator?.FlashGreen(); };
        _vm.OcrPhaseReceived    += p => _overlay?.ReceiveScanPhase(p);
        _vm.OcrProgressReceived += c => _overlay?.ReceiveScanProgress(c);

        KeyPopup.Closed += (_, __) => _keyPopupClosedAt = DateTime.UtcNow;

        RestoreWindowPosition();
        SetActivePage("scan");
        Closing += (s, e) => { SaveWindowPosition(); _vm.StopScanner(); _listTicker?.Stop(); _scanIndicator?.Close(); };

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
        selector.Show();
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
        PageScan.Visibility       = page == "scan"       ? Visibility.Visible : Visibility.Collapsed;
        PageBlueprints.Visibility = page == "blueprints" ? Visibility.Visible : Visibility.Collapsed;
        PageReference.Visibility  = page == "reference"  ? Visibility.Visible : Visibility.Collapsed;
        PageWorkOrders.Visibility = page == "workorders" ? Visibility.Visible : Visibility.Collapsed;

        NavScan.IsChecked  = page == "scan";
        NavBlue.IsChecked  = page == "blueprints";
        NavRef.IsChecked   = page == "reference";
        NavWork.IsChecked  = page == "workorders";

        Title = page switch
        {
            "scan"       => "Nexus — RS Signal Decoder",
            "blueprints" => "Nexus — Blueprint Library",
            "reference"  => "Nexus — Mining Codex",
            "workorders" => "Nexus — Refinery Tracker",
            _            => "Nexus",
        };

        if (page == "blueprints") InitBlueprintBrowse();
        if (page == "reference") { BuildFilterPills(); BuildReferenceTree(); }
        if (page == "workorders") RebuildWorkOrderList();
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
        NavLogo.Source = new System.Windows.Media.Imaging.BitmapImage(new System.Uri(ThemeService.LogoUri));
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

    private void RefSearch_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) BuildReferenceTree(); }
    private void RefSearch_KeyUp(object sender, KeyEventArgs e) => BuildReferenceTree();
    private void RefSearch_Click(object sender, RoutedEventArgs e) => BuildReferenceTree();

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
    private readonly Dictionary<string, Border> _bpPills = new(StringComparer.OrdinalIgnoreCase);
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
    private void BpChipAll_Click(object sender, MouseButtonEventArgs e)      => SetBpOwnFilter(BpOwnFilter.All);
    private void BpChipOwned_Click(object sender, MouseButtonEventArgs e)    => SetBpOwnFilter(BpOwnFilter.Owned);
    private void BpChipNotOwned_Click(object sender, MouseButtonEventArgs e) => SetBpOwnFilter(BpOwnFilter.NotOwned);

    private void SetBpOwnFilter(BpOwnFilter filter)
    {
        _bpOwnFilter = filter;
        UpdateOwnedChips();
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

    private int CatCount(string cat) => _allBlueprints?.Count(b => b.Category == cat) ?? 0;

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
        _bpLevel = "root"; _bpCat = ""; _bpSub = "";
        RenderBlueprintNav();
        ShowBlueprintLanding();
    }

    private void EnterCategory(string cat)
    {
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

    // family = name with quoted skins / parentheticals / trailing colour words removed (collapses variants)
    private static string FamilyKey(string name)
    {
        var s = System.Text.RegularExpressions.Regex.Replace(name, "\"[^\"]*\"", "");
        s = System.Text.RegularExpressions.Regex.Replace(s, "\\([^)]*\\)", "");
        s = System.Text.RegularExpressions.Regex.Replace(s, "\\s+", " ").Trim();
        var parts = s.Split(' ').ToList();
        while (parts.Count > 0 && _variantWords.Contains(parts[^1])) parts.RemoveAt(parts.Count - 1);
        return parts.Count > 0 ? string.Join(" ", parts) : (s.Length > 0 ? s : name);
    }

    private void RenderBlueprintNav()
    {
        BlueprintNavPanel.Children.Clear();
        _selectedBpRow = null;
        _bpPills.Clear();
        if (_allBlueprints == null) return;
        var accent = (System.Windows.Media.Brush)FindResource("AccentBrush");
        var catCol = CategoryBrush(_bpCat);

        // Ownership filter takes over the nav with a category-grouped matching list.
        if (_bpOwnFilter != BpOwnFilter.All)
        {
            var wantOwned = _bpOwnFilter == BpOwnFilter.Owned;
            var matches = _allBlueprints
                .Where(b => App.Settings.IsBlueprintOwned(b.Name) == wantOwned)
                .OrderBy(b => b.Category).ThenBy(b => b.Name).ToList();
            var headFont = (System.Windows.Media.FontFamily)FindResource("HeadFont");
            var hdr = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 2, 0, 8) };
            hdr.Children.Add(new TextBlock { Text = wantOwned ? "Owned" : "Not owned", FontFamily = headFont, FontSize = 16, Foreground = accent, VerticalAlignment = VerticalAlignment.Center });
            hdr.Children.Add(new TextBlock { Text = $"  ·  {matches.Count}", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = (System.Windows.Media.Brush)FindResource("FgDimBrush"), VerticalAlignment = VerticalAlignment.Center });
            BlueprintNavPanel.Children.Add(hdr);
            if (matches.Count == 0)
            {
                BlueprintNavPanel.Children.Add(new TextBlock
                {
                    Text = wantOwned ? "Nothing marked owned yet" : "Every blueprint is marked owned",
                    FontSize = 12, Foreground = (System.Windows.Media.Brush)FindResource("FgDimBrush"),
                    Margin = new Thickness(6, 8, 0, 0), TextWrapping = TextWrapping.Wrap,
                });
                return;
            }
            // Group matches under colour-coded category headers. Cap total realized
            // rows — the flat list isn't virtualized, so rendering the whole "Not
            // owned" catalog froze the UI thread; search narrows past the cap.
            const int filterRenderCap = 150;
            var rendered = 0;
            var groups = matches
                .GroupBy(b => b.Category)
                .OrderBy(g => { var i = Array.IndexOf(_bpCategories, g.Key); return i < 0 ? int.MaxValue : i; })
                .ThenBy(g => g.Key);
            foreach (var grp in groups)
            {
                if (rendered >= filterRenderCap) break;
                BlueprintNavPanel.Children.Add(FilterCategoryHeader(grp.Key, grp.Count()));
                foreach (var bp in grp.OrderBy(b => b.Name))
                {
                    if (rendered >= filterRenderCap) break;
                    BlueprintNavPanel.Children.Add(BlueprintRow(bp, false));
                    rendered++;
                }
            }
            if (matches.Count > rendered)
                BlueprintNavPanel.Children.Add(new TextBlock
                {
                    Text = $"Showing first {rendered} of {matches.Count}. Use search above to find a specific blueprint.",
                    FontSize = 11, FontStyle = FontStyles.Italic,
                    Foreground = (System.Windows.Media.Brush)FindResource("FgDimBrush"),
                    Margin = new Thickness(6, 10, 6, 4), TextWrapping = TextWrapping.Wrap,
                });
            return;
        }

        switch (_bpLevel)
        {
            case "category":
            {
                BlueprintNavPanel.Children.Add(BackRow("All categories", GoRoot));
                BlueprintNavPanel.Children.Add(NavHeader(_bpCat, CatCount(_bpCat), catCol));
                var inCat = _allBlueprints.Where(b => b.Category == _bpCat).ToList();
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
                BlueprintNavPanel.Children.Add(BackRow(_bpCat, () => { _bpLevel = "category"; _bpSub = ""; RenderBlueprintNav(); ShowBlueprintLanding(); }));
                var items = _allBlueprints.Where(b => b.Category == _bpCat && Subgroup(b) == _bpSub).ToList();
                BlueprintNavPanel.Children.Add(NavHeader(_bpSub, items.Count, catCol));
                RenderLeafGroup(items, catCol);
                break;
            }

            case "family":
            {
                Action backToLeaf = _bpSub.Length > 0
                    ? () => { _bpLevel = "subgroup"; _bpFam = ""; RenderBlueprintNav(); ShowBlueprintLanding(); }
                    : () => { _bpLevel = "category"; _bpFam = ""; RenderBlueprintNav(); ShowBlueprintLanding(); };
                BlueprintNavPanel.Children.Add(BackRow(_bpSub.Length > 0 ? _bpSub : _bpCat, backToLeaf));
                var variants = _allBlueprints
                    .Where(b => b.Category == _bpCat && (_bpSub.Length == 0 ? Subgroup(b) == null : Subgroup(b) == _bpSub) && FamilyKey(b.Name) == _bpFam)
                    .OrderBy(b => b.Name).ToList();
                BlueprintNavPanel.Children.Add(NavHeader(_bpFam, variants.Count, catCol));
                foreach (var bp in variants)
                    BlueprintNavPanel.Children.Add(BlueprintRow(bp, false));
                break;
            }

            case "search":
                BlueprintNavPanel.Children.Add(BackRow("Browse", GoRoot));
                BlueprintNavPanel.Children.Add(NavHeader("Results", _bpSearchResults.Count, accent));
                if (_bpSearchResults.Count == 0)
                    BlueprintNavPanel.Children.Add(new TextBlock { Text = "No matches", FontSize = 12, Foreground = (System.Windows.Media.Brush)FindResource("FgDimBrush"), Margin = new Thickness(6, 8, 0, 0) });
                foreach (var bp in _bpSearchResults)
                    BlueprintNavPanel.Children.Add(BlueprintRow(bp, true));
                break;

            default: // root
                foreach (var cat in _bpCategories)
                    BlueprintNavPanel.Children.Add(CategoryCard(cat, CatCount(cat)));
                break;
        }
    }

    // within a leaf set: families with >1 variant become drill rows; singles become blueprint rows
    private void RenderLeafGroup(System.Collections.Generic.IEnumerable<NexusApp.Models.Blueprint> items, System.Windows.Media.Brush col)
    {
        var fams = items.GroupBy(b => FamilyKey(b.Name)).OrderBy(g => g.Key).ToList();
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
        _bpSub = sub; _bpFam = ""; _bpLevel = "subgroup";
        RenderBlueprintNav();
        ShowBlueprintLanding();
    }

    private void EnterFamily(string fam)
    {
        _bpFam = fam; _bpLevel = "family";
        RenderBlueprintNav();
        ShowBlueprintLanding();
    }

    private Border BackRow(string label, Action onClick)
    {
        var tb = new TextBlock { FontSize = 11, FontWeight = FontWeights.Bold, Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush"), VerticalAlignment = VerticalAlignment.Center };
        tb.Inlines.Add(new System.Windows.Documents.Run("‹  " + label));
        var b = new Border { Child = tb, Padding = new Thickness(6, 6, 6, 6), Margin = new Thickness(0, 0, 0, 4), Cursor = System.Windows.Input.Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Left };
        b.MouseLeftButtonDown += (_, __) => onClick();
        return b;
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

        var card = new Border { Background = trans, BorderBrush = trans, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 7, 10, 7), Margin = new Thickness(0, 0, 0, 2), Cursor = System.Windows.Input.Cursors.Hand };
        var rowGrid = new Grid();
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var chk = OwnedCheckbox(bp);
        _bpPills[bp.Name] = chk;
        Grid.SetColumn(chk, 0);
        rowGrid.Children.Add(chk);
        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(sp, 1);
        sp.Children.Add(new TextBlock { Text = bp.Name, FontWeight = FontWeights.SemiBold, Foreground = fg, TextTrimming = System.Windows.TextTrimming.CharacterEllipsis });
        if (showCategory)
            sp.Children.Add(new TextBlock { Text = bp.Category + (string.IsNullOrEmpty(bp.SubCategory) ? "" : " · " + bp.SubCategory), FontSize = 10, Foreground = dim, Margin = new Thickness(0, 2, 0, 0) });
        rowGrid.Children.Add(sp);
        card.Child = rowGrid;

        card.MouseEnter += (s, _) => { if (card != _selectedBpRow) card.Background = hover; };
        card.MouseLeave += (s, _) => { if (card != _selectedBpRow) card.Background = trans; };
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
    // detail toggle. In the drill-down (All) view it updates just the toggled row's
    // pill in place — no rebuild. In a filtered view the matching set and its category
    // grouping change, so it re-renders the (capped) filtered list, which is cheap.
    private void OnOwnershipChanged(string name, bool nowOwned)
    {
        UpdateOwnedCount();

        if (_detailBpName != null && string.Equals(_detailBpName, name, StringComparison.OrdinalIgnoreCase)
            && _detailOwnedToggle != null)
            ApplyOwnedToggleVisual(_detailOwnedToggle, nowOwned);

        if (_bpOwnFilter == BpOwnFilter.All)
        {
            if (_bpPills.TryGetValue(name, out var pill))
                ApplyCheckVisual(pill, nowOwned);
            return;
        }

        RenderBlueprintNav();
    }

    // Colour-coded category section header for the grouped ownership filter list.
    private UIElement FilterCategoryHeader(string category, int count)
    {
        var col = CategoryBrush(category);
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 12, 0, 4) };
        sp.Children.Add(new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = col, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) });
        sp.Children.Add(new TextBlock { Text = category, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = col, VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(new TextBlock { Text = $"  {count}", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = (System.Windows.Media.Brush)FindResource("FgDimBrush"), VerticalAlignment = VerticalAlignment.Center });
        return sp;
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
        var headFont = (System.Windows.Media.FontFamily)FindResource("HeadFont");
        var total    = _allBlueprints?.Count ?? 0;

        BlueprintDetailPanel.Children.Add(new TextBlock { Text = "Blueprint Library", FontFamily = headFont, FontSize = 24, Foreground = fg, Margin = new Thickness(4, 4, 0, 0) });
        BlueprintDetailPanel.Children.Add(new TextBlock { Text = $"{total} blueprints across {_bpCategories.Length} categories", FontSize = 12, Foreground = dim, Margin = new Thickness(4, 6, 0, 2) });
        BlueprintDetailPanel.Children.Add(new TextBlock { Text = "Pick a category on the left to drill in, or search above. Choose a blueprint to see its recipe and where to unlock it.", FontSize = 12, Foreground = dim, Margin = new Thickness(4, 0, 0, 16), TextWrapping = TextWrapping.Wrap, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left });

        var wrap = new System.Windows.Controls.WrapPanel();
        foreach (var cat in _bpCategories)
            wrap.Children.Add(CategoryStatChip(cat, CatCount(cat)));
        BlueprintDetailPanel.Children.Add(wrap);
    }

    private Border CategoryStatChip(string cat, int count)
    {
        var col = CategoryBrush(cat);
        var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(new System.Windows.Shapes.Ellipse { Width = 9, Height = 9, Fill = col, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) });
        sp.Children.Add(new TextBlock { Text = cat, FontSize = 11, Foreground = (System.Windows.Media.Brush)FindResource("FgBrush"), VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(new TextBlock { Text = $"  {count}", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = col, VerticalAlignment = VerticalAlignment.Center });
        var chip = new Border { Child = sp, Background = (System.Windows.Media.Brush)FindResource("Bg2NavBrush"), BorderBrush = (System.Windows.Media.Brush)FindResource("NavBorderBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 6, 14, 6), Margin = new Thickness(0, 0, 10, 10), Cursor = System.Windows.Input.Cursors.Hand };
        chip.MouseLeftButtonDown += (_, __) => EnterCategory(cat);
        return chip;
    }

    private static System.Windows.Media.Brush CategoryBrush(string cat) => cat switch
    {
        "Weapons"         => BrushFromHex("#EF6B52"),
        "Armor"           => BrushFromHex("#5BA0EB"),
        "Ammo"            => BrushFromHex("#D4AA5A"),
        "Ship Components" => BrushFromHex("#78C8A0"),
        _                 => BrushFromHex("#8C887F"),
    };

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

    private void ShowBlueprintDetail(NexusApp.Models.Blueprint selected)
    {
        var full = App.Data.GetBlueprintFull(selected.Name);
        BlueprintDetailPanel.Children.Clear();
        _detailBpName = null;
        _detailOwnedToggle = null;
        if (full == null) return;
        _detailBpName = full.Name;

        var heroCard = new Border
        {
            CornerRadius = new CornerRadius(12), Padding = new Thickness(20, 13, 14, 13), Margin = new Thickness(0, 0, 0, 6),
            Background = (System.Windows.Media.Brush)FindResource("Bg2NavBrush"),
            BorderBrush = (System.Windows.Media.Brush)FindResource("NavBorderBrush"), BorderThickness = new Thickness(1),
        };
        var heroGrid = new Grid();
        heroGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heroGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var heroText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        heroText.Children.Add(new TextBlock
        {
            Text = full.Name, FontSize = 22, FontWeight = FontWeights.Bold,
            FontFamily = (System.Windows.Media.FontFamily)System.Windows.Application.Current.FindResource("HeadFont"),
            Foreground = (System.Windows.Media.Brush)FindResource("FgBrush"),
        });
        heroText.Children.Add(new TextBlock
        {
            Text = full.Category, FontSize = 12, Margin = new Thickness(0, 2, 0, 0),
            Foreground = (System.Windows.Media.Brush)FindResource("FgDimBrush"),
        });
        heroGrid.Children.Add(heroText);
        var heroActions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        heroActions.Children.Add(OwnedToggle(full.Name));
        var heroAddBtn = new Button
        {
            Content = "+ Add all to cart", Style = (Style)FindResource("AccentButton"),
            Padding = new Thickness(14, 7, 14, 7), VerticalAlignment = VerticalAlignment.Center,
        };
        heroAddBtn.Click += (s, e) => { foreach (var i in full.Ingredients) _vm.AddToShoppingCommand.Execute(i); };
        heroActions.Children.Add(heroAddBtn);
        Grid.SetColumn(heroActions, 1);
        heroGrid.Children.Add(heroActions);
        heroCard.Child = heroGrid;
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

                        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 1, 0, 1) };
                        row.Children.Add(new TextBlock
                        {
                            Text = "·  ", FontSize = 11,
                            Foreground = (System.Windows.Media.Brush)FindResource("FgDimBrush"),
                            VerticalAlignment = VerticalAlignment.Top,
                        });
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
        var ingHeader = new TextBlock
        {
            Text = $"INGREDIENTS  ·  {full.Ingredients.Count}",
            FontSize = 10, FontWeight = FontWeights.Bold,
            Foreground = (System.Windows.Media.Brush)FindResource("FgDimBrush"),
            Margin = new Thickness(0, 8, 0, 4),
        };
        host.Children.Add(ingHeader);

        double maxQty = full.Ingredients.Count > 0 ? full.Ingredients.Max(i => i.Quantity) : 1;
        if (maxQty <= 0) maxQty = 1;

        foreach (var ing in full.Ingredients)
        {
            var rarity = _vm.AllResources.FirstOrDefault(r => r.Name == ing.ResourceName)?.Rarity ?? "common";
            var rb = RarityBrush(rarity);
            var card = new Border
            {
                Margin = new Thickness(0, 3, 0, 3), Padding = new Thickness(12, 8, 12, 9), CornerRadius = new CornerRadius(8),
                Background = (System.Windows.Media.Brush)FindResource("Bg2NavBrush"),
                BorderBrush = (System.Windows.Media.Brush)FindResource("NavBorderBrush"), BorderThickness = new Thickness(1),
            };
            var ingStack = new StackPanel();
            var top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var gem = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(3), Background = rb, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            Grid.SetColumn(gem, 0); top.Children.Add(gem);
            var name = new TextBlock { Text = ing.ResourceName, FontSize = 13, Foreground = rb, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(name, 1); top.Children.Add(name);
            var qty = new TextBlock { Text = $"{ing.Quantity:0.##} {ing.Unit}", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = (System.Windows.Media.Brush)FindResource("FgBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            Grid.SetColumn(qty, 2); top.Children.Add(qty);
            var ingCopy = ing;
            var addBtn = new Button { Content = "+", Style = (Style)FindResource("NexusButton"), Padding = new Thickness(9, 2, 9, 2), FontSize = 13, FontWeight = FontWeights.Bold, ToolTip = "Add to shopping list", Tag = ingCopy, VerticalAlignment = VerticalAlignment.Center };
            addBtn.Click += (s, e) => _vm.AddToShoppingCommand.Execute(((Button)s).Tag);
            Grid.SetColumn(addBtn, 3); top.Children.Add(addBtn);
            ingStack.Children.Add(top);

            double frac = System.Math.Min(1.0, ing.Quantity / maxQty);
            var barGrid = new Grid { Height = 5, Margin = new Thickness(0, 7, 0, 0) };
            barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(frac, GridUnitType.Star) });
            barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - frac, GridUnitType.Star) });
            var trackBar = new Border { Background = (System.Windows.Media.Brush)FindResource("Bg3Brush"), CornerRadius = new CornerRadius(2) };
            Grid.SetColumnSpan(trackBar, 2); barGrid.Children.Add(trackBar);
            var fillBar = new Border { Background = rb, CornerRadius = new CornerRadius(2) };
            Grid.SetColumn(fillBar, 0); barGrid.Children.Add(fillBar);
            ingStack.Children.Add(barGrid);

            card.Child = ingStack;

            // cross-link: clicking an ingredient that exists as a resource opens it in the Codex
            if (_vm.AllResources.Any(r => r.Name.Equals(ing.ResourceName, StringComparison.OrdinalIgnoreCase)))
            {
                card.Cursor = System.Windows.Input.Cursors.Hand;
                card.ToolTip = "Open in Mining Codex";
                var navBorder = (System.Windows.Media.Brush)FindResource("NavBorderBrush");
                var accentB   = (System.Windows.Media.Brush)FindResource("AccentBrush");
                card.MouseEnter += (s, _) => card.BorderBrush = accentB;
                card.MouseLeave += (s, _) => card.BorderBrush = navBorder;
                card.MouseLeftButtonDown += (s, _) => NavigateToResource(ingCopy.ResourceName);
            }

            host.Children.Add(card);
        }

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
            if (visible) _scanIndicator.Show();
            else         _scanIndicator.Hide();
        };
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
            _overlay = null;
        }
    }

    private void ApplyScanRegion(NexusApp.Models.ScanRegion r)
    {
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
            if (_boxVisible) _scanIndicator.Show();
        }
        _scanIndicator.SetRegion(r, System.Windows.Media.VisualTreeHelper.GetDpi(this));
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

    // ── Easter egg (app version badge) ───────────────────────────────────────
    private int _eggClicks;
    private System.Windows.Threading.DispatcherTimer? _eggTimer;
    private static readonly string[] _eggWarnings =
        ["I wouldn't do that...", "Don't.", "Great! Now you've pissed off CannonActual!"];

    private void AppBadge_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
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

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string GetSystem(string location)
    {
        if (location.StartsWith("Pyro") || location == "Akiro Cluster") return "Pyro";
        if (location is "Glaciem Ring" or "Keeger Belt" or "Levski" or "Breaker Stations Interior" or "Breaker Stations Large Geode") return "Nyx";
        return "Stanton";
    }

    private static string MethodLabel(string method) => method switch
    {
        "ship" => "Ship", "vehicle" => "ROC", "fps" => "FPS", "fps+vehicle" => "FPS · ROC", _ => method,
    };

    private static string CapFirst(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s[1..];

    private static System.Windows.Media.Brush RarityBrush(string rarity) => rarity switch
    {
        "legendary" => BrushFromHex("#FFD700"), "epic"   => BrushFromHex("#A855F7"),
        "rare"      => BrushFromHex("#3B82F6"), "uncommon" => BrushFromHex("#22C55E"),
        _ => BrushFromHex("#9E9E9E"),
    };
    private static System.Windows.Media.Brush TierBrush(string tier) => tier switch
    {
        "S" => BrushFromHex("#FFD700"), "A" => BrushFromHex("#4CAF50"),
        "B" => BrushFromHex("#29B6F6"), _   => System.Windows.Media.Brushes.White,
    };
    private static System.Windows.Media.Brush SystemBrush(string sys) => sys switch
    {
        "Pyro" => BrushFromHex("#F97316"), "Nyx" => BrushFromHex("#A855F7"),
        _ => BrushFromHex("#3B82F6"),
    };
    private static System.Windows.Media.Brush ModifierBrush(int mod) =>
        mod > 0 ? BrushFromHex("#22C55E") : mod < 0 ? BrushFromHex("#EF4444") : BrushFromHex("#8B949E");
    private static System.Windows.Media.Brush AccentBrush() => (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("AccentBrush");
    private static System.Windows.Media.SolidColorBrush BrushFromHex(string hex)
    {
        var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
        return new System.Windows.Media.SolidColorBrush(c);
    }
}
