using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Nexus_v4.Models;
using Nexus_v4.Services;
using Nexus_v4.ViewModels;

namespace Nexus_v4.Views;

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
        AppVersionText.Text = $"App v{AppInfo.Version}";
        GameVersionText.Text = $"Star Citizen PU v{GameData.Version}";
        _vm = new MainViewModel();
        DataContext = _vm;
        _vm.OcrValueReceived    += v => { _overlay?.ReceiveOcrValue(v); _scanIndicator?.FlashGreen(); };
        _vm.OcrPhaseReceived    += p => _overlay?.ReceiveScanPhase(p);
        _vm.OcrProgressReceived += c => _overlay?.ReceiveScanProgress(c);

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
    /// Opens the overlay and points a pulsing ring at the button each step explains.</summary>
    public void ShowTutorial()
    {
        var highlight = new HighlightWindow();
        var wizard = new WelcomeWizardWindow { Owner = this };
        wizard.TargetChanged += t => ApplyTutorialHighlight(t, highlight);

        // Reflect the wizard's opening step before it goes modal.
        ApplyTutorialHighlight(wizard.CurrentTarget, highlight);
        wizard.ShowDialog();

        highlight.Close();

        if (wizard.SetupRegionRequested)
        {
            var selector = new RegionSelectorWindow();
            selector.RegionSelected += ApplyScanRegion;
            selector.Show();
        }
    }

    private void ApplyTutorialHighlight(TutorialTarget t, HighlightWindow highlight)
    {
        FrameworkElement? target = null;
        switch (t)
        {
            case TutorialTarget.RsDecoder:
                SetActivePage("scan");
                target = RsInputBox;
                break;
            case TutorialTarget.ScanHistory:
                SetActivePage("scan");
                target = ScanHistorySection;
                break;
            case TutorialTarget.OpenOverlay:
                target = OverlayToggleBtn;
                break;
            case TutorialTarget.ShowBox:
                target = PrepareOverlayForTutorial()?.BoxToggleTarget;
                break;
            case TutorialTarget.DrawRegion:
                target = PrepareOverlayForTutorial()?.SetRegionTarget;
                break;
            case TutorialTarget.ScanToggle:
                target = PrepareOverlayForTutorial()?.ScanToggleTarget;
                break;
            case TutorialTarget.OverlayTabs:
                target = PrepareOverlayForTutorial()?.TabStripTarget;
                break;
            case TutorialTarget.ShoppingTab:
                {
                    var overlay = PrepareOverlayForTutorial();
                    overlay?.ShowShoppingTabForTutorial();
                    overlay?.UpdateLayout();
                    target = overlay?.ShoppingTabTarget;
                }
                break;
            case TutorialTarget.Blueprints:
                SetActivePage("blueprints");
                target = BlueprintSearchBox;
                break;
            case TutorialTarget.Reference:
                SetActivePage("reference");
                target = GroupByBtn;
                break;
            case TutorialTarget.WorkOrders:
                SetActivePage("workorders");
                target = WoNewBtn;
                break;
            case TutorialTarget.DataVersion:
                target = AppVersionBadge;
                break;
        }

        if (target == null) { highlight.HideRing(); return; }

        // Defer until layout settles (overlay may have just been shown / tab-switched).
        var captured = target;
        Dispatcher.BeginInvoke(new Action(() => highlight.HighlightControl(captured)),
            System.Windows.Threading.DispatcherPriority.Loaded);
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
    private bool _groupByLocation;

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
            var pill = MakePill(label, _systemFilter.Contains(k));
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

    private void RefClear_Click(object sender, RoutedEventArgs e)
    {
        _vm.RefFilter = "";
        BuildReferenceTree();
    }

    private bool _treeExpanded;

    private void ExpandAll_Click(object sender, RoutedEventArgs e)
    {
        _treeExpanded = !_treeExpanded;
        ExpandAllBtn.Content = _treeExpanded ? "Collapse All" : "Expand All";
        foreach (var item in ReferenceTree.Items.OfType<TreeViewItem>())
            SetTreeExpanded(item, _treeExpanded ? 1 : int.MaxValue, expand: _treeExpanded);
    }

    private void GroupBy_Click(object sender, RoutedEventArgs e)
    {
        _groupByLocation = !_groupByLocation;
        GroupByBtn.Content = _groupByLocation ? "Group: Location" : "Group: Resource";
        BuildReferenceTree();
    }

    private void ResetSort_Click(object sender, RoutedEventArgs e)
    {
        _vm.RefFilter = "";
        _systemFilter.Clear();
        _methodFilter.Clear();
        _groupByLocation = false;
        if (GroupByBtn != null) GroupByBtn.Content = "Group: Resource";
        BuildFilterPills();
        BuildReferenceTree();
    }

    private static void SetTreeExpanded(TreeViewItem item, int depth, bool expand)
    {
        item.IsExpanded = expand;
        if (!expand || depth <= 0) return;
        foreach (var child in item.Items.OfType<TreeViewItem>())
            SetTreeExpanded(child, depth - 1, expand);
    }

    private void BuildReferenceTree()
    {
        _treeExpanded = false;
        if (ExpandAllBtn != null) ExpandAllBtn.Content = "Expand All";
        ReferenceTree.Items.Clear();
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
            .ToList();

        if (_groupByLocation)
            BuildLocationTree(filtered);
        else
            BuildResourceTree(filtered);
    }

    private void BuildResourceTree(List<Resource> filtered)
    {
        if (RefCol1Header != null) RefCol1Header.Text = "Resource";
        var grouped = filtered
            .GroupBy(r => r.Rarity)
            .OrderBy(g => Array.IndexOf(["legendary", "epic", "rare", "uncommon", "common"], g.Key));

        foreach (var group in grouped)
        {
            var groupNode = new TreeViewItem
            {
                Header = $"{CapFirst(group.Key)}  ·  {group.Count()}",
                FontSize = 12,
                Foreground = RarityBrush(group.Key),
                IsExpanded = true,
            };
            foreach (var r in group.OrderBy(x => x.BaseRs))
                groupNode.Items.Add(BuildResourceNode(r, includeLocations: true));
            ReferenceTree.Items.Add(groupNode);
        }
    }

    private void BuildLocationTree(List<Resource> filtered)
    {
        if (RefCol1Header != null) RefCol1Header.Text = "Location";
        var locToResources = new Dictionary<string, List<Resource>>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in filtered)
        {
            foreach (var loc in r.Locations)
            {
                if (_systemFilter.Count > 0 && !_systemFilter.Contains(GetSystem(loc))) continue;
                if (!locToResources.TryGetValue(loc, out var list))
                    locToResources[loc] = list = new List<Resource>();
                list.Add(r);
            }
        }

        foreach (var sys in new[] { "Stanton", "Pyro", "Nyx" })
        {
            var sysLocs = locToResources
                .Where(kv => GetSystem(kv.Key) == sys)
                .OrderBy(kv => kv.Key)
                .ToList();
            if (sysLocs.Count == 0) continue;

            var sysNode = new TreeViewItem
            {
                Header = $"{sys}  ·  {sysLocs.Count}",
                FontSize = 12,
                Foreground = SystemBrush(sys),
                IsExpanded = true,
            };
            foreach (var (loc, resources) in sysLocs)
            {
                var locNode = new TreeViewItem
                {
                    Header = $"{loc}  ·  {resources.Count}",
                    FontSize = 12,
                    Foreground = SystemBrush(sys),
                    IsExpanded = false,
                };
                foreach (var r in resources.OrderBy(x => x.BaseRs))
                    locNode.Items.Add(BuildResourceNode(r, includeLocations: false));
                sysNode.Items.Add(locNode);
            }
            ReferenceTree.Items.Add(sysNode);
        }
    }

    private TreeViewItem BuildResourceNode(Resource r, bool includeLocations)
    {
        var resNode = new TreeViewItem { Header = BuildResourceHeader(r), IsExpanded = false };

        if (includeLocations && r.Locations.Count > 0)
        {
            var locNode = new TreeViewItem
            {
                Header = $"Locations  ·  {r.Locations.Count}",
                Foreground = (System.Windows.Media.Brush)FindResource("FgDimBrush"),
                FontSize = 12,
            };
            foreach (var loc in r.Locations)
                locNode.Items.Add(new TreeViewItem { Header = loc, Foreground = SystemBrush(GetSystem(loc)), FontSize = 12 });
            resNode.Items.Add(locNode);
        }

        if (r.Refineries.Count > 0)
        {
            var refNode = new TreeViewItem
            {
                Header = $"Refinery Yields  ·  {r.Refineries.Count}",
                Foreground = (System.Windows.Media.Brush)FindResource("FgDimBrush"),
                FontSize = 12,
            };
            foreach (var y in r.Refineries)
            {
                var sign = y.ModifierPct > 0 ? "+" : "";
                refNode.Items.Add(new TreeViewItem
                {
                    Header = $"{y.Station}  ({y.System})  {sign}{y.ModifierPct}%",
                    Foreground = ModifierBrush(y.ModifierPct),
                    FontSize = 12,
                });
            }
            resNode.Items.Add(refNode);
        }

        var bpNode = new TreeViewItem
        {
            Header = "Blueprints  ·  …",
            Foreground = (System.Windows.Media.Brush)FindResource("FgDimBrush"),
        };
        bpNode.Items.Add(new TreeViewItem { Header = "Loading…" });
        bool bpLoaded = false;
        var capturedName = r.Name;
        bpNode.Expanded += (s, e) =>
        {
            if (bpLoaded) return;
            bpLoaded = true;
            bpNode.Items.Clear();
            var bps = App.Data.GetBlueprintsForResource(capturedName);
            bpNode.Header = $"Blueprints  ·  {bps.Count}";
            if (bps.Count == 0)
            {
                bpNode.Items.Add(new TreeViewItem
                {
                    Header = "No blueprints use this resource",
                    FontSize = 12,
                    Foreground = (System.Windows.Media.Brush)FindResource("FgDimBrush"),
                });
                return;
            }
            foreach (var catGroup in bps.GroupBy(b => b.Category).OrderBy(g => g.Key))
            {
                var catNode = new TreeViewItem
                {
                    Header = catGroup.Key, FontSize = 12,
                    Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush"),
                };

                // Ship Components: add an extra subcategory level
                if (catGroup.Key == "Ship Components")
                {
                    foreach (var subGroup in catGroup.GroupBy(b => b.SubCategory ?? "Other").OrderBy(g => g.Key))
                    {
                        var subNode = new TreeViewItem
                        {
                            Header = subGroup.Key, FontSize = 12,
                            Foreground = (System.Windows.Media.Brush)FindResource("FgBrush"),
                        };
                        foreach (var bp in subGroup.OrderBy(b => b.Name))
                        {
                            var bpItem = new TreeViewItem { Header = bp.Name, FontSize = 12 };
                            foreach (var ing in bp.Ingredients)
                                bpItem.Items.Add(new TreeViewItem
                                {
                                    Header = $"{ing.ResourceName}  ×{ing.Quantity:0.##} {ing.Unit}",
                                    FontSize = 11,
                                    Foreground = (System.Windows.Media.Brush)FindResource("FgDimBrush"),
                                });
                            subNode.Items.Add(bpItem);
                        }
                        catNode.Items.Add(subNode);
                    }
                }
                else
                {
                    foreach (var bp in catGroup.OrderBy(b => b.Name))
                    {
                        var bpItem = new TreeViewItem { Header = bp.Name, FontSize = 12 };
                        foreach (var ing in bp.Ingredients)
                            bpItem.Items.Add(new TreeViewItem
                            {
                                Header = $"{ing.ResourceName}  ×{ing.Quantity:0.##} {ing.Unit}",
                                FontSize = 11,
                                Foreground = (System.Windows.Media.Brush)FindResource("FgDimBrush"),
                            });
                        catNode.Items.Add(bpItem);
                    }
                }
                bpNode.Items.Add(catNode);
            }
        };
        resNode.Items.Add(bpNode);

        return resNode;
    }

    private void RefSearch_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) BuildReferenceTree(); }
    private void RefSearch_KeyUp(object sender, KeyEventArgs e) => BuildReferenceTree();
    private void RefSearch_Click(object sender, RoutedEventArgs e) => BuildReferenceTree();

    private StackPanel BuildResourceHeader(Resource r)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock
        {
            Text = r.Name, FontSize = 13, FontWeight = FontWeights.SemiBold,
            Foreground = RarityBrush(r.Rarity), Width = 140,
        });
        panel.Children.Add(new TextBlock
        {
            Text = r.Method == "ship" ? $"RS {r.BaseRs:N0}" : "—",
            FontSize = 12,
            Foreground = r.Method == "ship"
                ? TierBrush(r.Tier)
                : (System.Windows.Media.Brush)FindResource("FgDimBrush"),
            Width = 90,
        });
        panel.Children.Add(new TextBlock
        {
            Text = MethodLabel(r.Method), FontSize = 11,
            Foreground = (System.Windows.Media.Brush)FindResource("FgDimBrush"),
            Width = 65,
        });
        if (r.IsPinned)
            panel.Children.Add(new TextBlock { Text = "★", Foreground = AccentBrush(), FontSize = 12 });
        return panel;
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
        var outer = new Border { Background = System.Windows.Media.Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new Border { Background = BrushFromHex(wo.StatusColorHex) });

        var stack = new StackPanel();
        Grid.SetColumn(stack, 1);

        var inner = new Grid { Margin = new Thickness(8, 6, 8, 4) };
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        textStack.Children.Add(new TextBlock
        {
            Text = wo.Label, FontWeight = FontWeights.SemiBold, FontSize = 12,
            Foreground = (System.Windows.Media.Brush)FindResource("FgBrush"),
        });
        var subtitleTb = new TextBlock
        {
            Text = wo.SubtitleText, FontSize = 9, Margin = new Thickness(0, 2, 0, 0),
            Foreground = BrushFromHex(wo.SubtitleForeground),
        };
        textStack.Children.Add(subtitleTb);
        inner.Children.Add(textStack);

        var dot = new TextBlock
        {
            Text = "●", FontSize = 10, VerticalAlignment = VerticalAlignment.Center,
            Foreground = BrushFromHex(wo.StatusColorHex), ToolTip = wo.StatusLabel,
            Margin = new Thickness(0, 0, 4, 0),
        };
        Grid.SetColumn(dot, 1);
        inner.Children.Add(dot);

        var dimBrush  = (System.Windows.Media.Brush)FindResource("FgDimBrush");
        var deleteTb  = new TextBlock
        {
            Text = "✕", FontSize = 10,
            Foreground = dimBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var deleteBtn = new Border
        {
            Child = deleteTb,
            Padding = new Thickness(4, 0, 0, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Delete",
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
        inner.Children.Add(deleteBtn);

        stack.Children.Add(inner);

        if (wo.HasActiveTimer)
        {
            var remaining = wo.TimerEnd!.Value - DateTime.UtcNow;
            var scale = new System.Windows.Media.ScaleTransform(wo.TimerFraction, 1);
            var barContainer = new Grid { Height = 3 };
            var fillBorder = new Border
            {
                Background = BrushFromHex(wo.StatusColorHex),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                RenderTransform = scale,
                RenderTransformOrigin = new System.Windows.Point(0, 0.5),
            };
            barContainer.Children.Add(fillBorder);
            stack.Children.Add(barContainer);

            if (remaining > TimeSpan.Zero)
            {
                var anim = new System.Windows.Media.Animation.DoubleAnimation
                {
                    From = wo.TimerFraction,
                    To = 1.0,
                    Duration = remaining,
                    FillBehavior = System.Windows.Media.Animation.FillBehavior.HoldEnd,
                };
                scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, anim);
            }
        }

        _rowLiveRefs[wo.Id] = subtitleTb;

        grid.Children.Add(stack);
        outer.Child = grid;

        var highlight = (System.Windows.Media.Brush)FindResource("HighlightBrush");
        var accent    = (System.Windows.Media.Brush)FindResource("AccentDimBrush");
        outer.MouseEnter        += (s, e) => { if (!ReferenceEquals(outer, _selectedOrderRow)) outer.Background = highlight; };
        outer.MouseLeave        += (s, e) => { if (!ReferenceEquals(outer, _selectedOrderRow)) outer.Background = System.Windows.Media.Brushes.Transparent; };
        outer.MouseLeftButtonDown += (s, e) =>
        {
            if (_selectedOrderRow != null) _selectedOrderRow.Background = System.Windows.Media.Brushes.Transparent;
            _selectedOrderRow = outer;
            outer.Background = accent;
            ShowWorkOrderEditor(wo);
        };

        return outer;
    }

    // ── Blueprints ───────────────────────────────────────────────────────────

    private void BlueprintSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { BlueprintSuggestPopup.IsOpen = false; return; }
        if (e.Key == Key.Enter)
        {
            BlueprintSuggestPopup.IsOpen = false;
            _vm.SearchBlueprintsCommand.Execute(null);
            BlueprintSearchBox.Clear();
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
            _vm.SearchBlueprintsCommand.Execute(null);
            BlueprintSearchBox.Clear();
            _suppressAutocomplete = false;

            // Auto-select the first (and likely only) result to immediately show ingredients
            Dispatcher.InvokeAsync(() =>
            {
                var match = _vm.BlueprintResults.FirstOrDefault(b => b.Name == name)
                            ?? _vm.BlueprintResults.FirstOrDefault();
                if (match != null) BlueprintList.SelectedItem = match;
            }, System.Windows.Threading.DispatcherPriority.Loaded);
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

    private void BlueprintList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BlueprintList.SelectedItem is not Nexus_v4.Models.Blueprint selected) return;
        var full = App.Data.GetBlueprintFull(selected.Name);
        BlueprintDetailPanel.Children.Clear();
        if (full == null) return;

        BlueprintDetailPanel.Children.Add(new TextBlock
        {
            Text = full.Name, FontSize = 15, FontWeight = FontWeights.Bold,
            Foreground = (System.Windows.Media.Brush)FindResource("FgBrush"),
            Margin = new Thickness(0, 0, 0, 4),
        });
        BlueprintDetailPanel.Children.Add(new TextBlock
        {
            Text = full.Category, FontSize = 11,
            Foreground = (System.Windows.Media.Brush)FindResource("FgDimBrush"),
            Margin = new Thickness(0, 0, 0, 12),
        });

        // ── HOW TO UNLOCK ────────────────────────────────────────────────────
        BlueprintDetailPanel.Children.Add(new TextBlock
        {
            Text = "HOW TO UNLOCK",
            FontSize = 10, FontWeight = FontWeights.Bold,
            Foreground = (System.Windows.Media.Brush)FindResource("FgDimBrush"),
            Margin = new Thickness(0, 0, 0, 6),
        });

        if (full.UnlockEntries.Count == 0)
        {
            BlueprintDetailPanel.Children.Add(new TextBlock
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
                BlueprintDetailPanel.Children.Add(new TextBlock
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
                        BlueprintDetailPanel.Children.Add(row);
                    }
                }
                else
                {
                    var typeLabel = mtype != null ? $" {mtype}" : "";
                    BlueprintDetailPanel.Children.Add(new TextBlock
                    {
                        Text = $"  ·  Any{typeLabel} mission  ({missions.Count} available)",
                        FontSize = 11,
                        Foreground = (System.Windows.Media.Brush)FindResource("FgDimBrush"),
                        Margin = new Thickness(8, 1, 0, 1),
                    });
                }
            }

            BlueprintDetailPanel.Children.Add(new Border
            {
                Height = 1, Margin = new Thickness(0, 10, 0, 4),
                Background = (System.Windows.Media.Brush)FindResource("NavBorderBrush"),
            });
        }

        var ingHeader = new TextBlock
        {
            Text = $"INGREDIENTS  ·  {full.Ingredients.Count}",
            FontSize = 10, FontWeight = FontWeights.Bold,
            Foreground = (System.Windows.Media.Brush)FindResource("FgDimBrush"),
            Margin = new Thickness(0, 8, 0, 4),
        };
        BlueprintDetailPanel.Children.Add(ingHeader);

        // Add All to Shopping List button
        var allIngs = full.Ingredients.ToList();
        var addAllBtn = new Button
        {
            Content = "🛒  Add All to Shopping List",
            Style = (Style)FindResource("NexusButton"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 0, 0, 8),
        };
        addAllBtn.Click += (s, e) =>
        {
            foreach (var i in allIngs) _vm.AddToShoppingCommand.Execute(i);
        };
        BlueprintDetailPanel.Children.Add(addAllBtn);

        foreach (var ing in full.Ingredients)
        {
            var rarity = _vm.AllResources.FirstOrDefault(r => r.Name == ing.ResourceName)?.Rarity ?? "common";
            var card = new Border
            {
                Margin = new Thickness(0, 2, 0, 2),
                Padding = new Thickness(10, 7, 10, 7),
                CornerRadius = new CornerRadius(4),
                Background = (System.Windows.Media.Brush)FindResource("Bg2NavBrush"),
                BorderBrush = (System.Windows.Media.Brush)FindResource("NavBorderBrush"),
                BorderThickness = new Thickness(1),
            };
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var bar = new Border { Background = RarityBrush(rarity), CornerRadius = new CornerRadius(2) };
            Grid.SetColumn(bar, 0);
            row.Children.Add(bar);

            var name = new TextBlock
            {
                Text = ing.ResourceName, FontSize = 13,
                Foreground = RarityBrush(rarity),
                Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(name, 1);
            row.Children.Add(name);

            var qty = new TextBlock
            {
                Text = $"{ing.Quantity:0.##} {ing.Unit}", FontSize = 12,
                Foreground = (System.Windows.Media.Brush)FindResource("FgDimBrush"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(qty, 2);
            row.Children.Add(qty);

            var ingCopy = ing;
            var addBtn = new Button
            {
                Content = "🛒", Style = (Style)FindResource("NexusButton"),
                Padding = new Thickness(7, 2, 7, 2), Margin = new Thickness(6, 0, 0, 0),
                FontSize = 12, ToolTip = "Add to shopping list", Tag = ingCopy,
            };
            addBtn.Click += (s, e) => _vm.AddToShoppingCommand.Execute(((Button)s).Tag);
            Grid.SetColumn(addBtn, 3);
            row.Children.Add(addBtn);

            card.Child = row;
            BlueprintDetailPanel.Children.Add(card);
        }

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
            BlueprintDetailPanel.Children.Add(new Border
            {
                Height = 1, Margin = new Thickness(0, 14, 0, 10),
                Background = (System.Windows.Media.Brush)FindResource("NavBorderBrush"),
            });
            BlueprintDetailPanel.Children.Add(new TextBlock
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

                BlueprintDetailPanel.Children.Add(new Border
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
                BlueprintDetailPanel.Children.Add(new TextBlock
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

    private void ApplyScanRegion(Nexus_v4.Models.ScanRegion r)
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

    private void ShowScanIndicator(Nexus_v4.Models.ScanRegion r)
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
    private static System.Windows.Media.Brush AccentBrush() => BrushFromHex("#00C9A7");
    private static System.Windows.Media.SolidColorBrush BrushFromHex(string hex)
    {
        var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
        return new System.Windows.Media.SolidColorBrush(c);
    }
}
