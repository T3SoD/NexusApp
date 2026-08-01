using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NexusApp.Models;
using NexusApp.Services;
using NexusApp.Services.Map;
using NexusApp.ViewModels;
using static NexusApp.Views.UiHelpers;

namespace NexusApp.Views;

// Split from MainWindow.xaml.cs (app review, Task 13): Mining Codex / Reference Tree dossier.
public partial class MainWindow
{
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

    // Codex list sort: "default" (rarity rank then RS desc), "sellDesc", "sellAsc". Only the sell
    // column is sortable, and only while that column exists - the gate in BuildSellHits resets this
    // to "default" the moment live market prices, the column toggle or the snapshot goes away.
    private string _refSort = "default";

    // The Codex list's sell column is opt-in (owner ruling 2026-07-27, live run 5): the toggle row
    // above the list only exists while live market data is on, and the column itself needs the
    // toggle ON as well. Built once here; its visibility is driven per rebuild by BuildReferenceTree.
    private Hud.ToggleSwitch? _codexSellToggle;

    private void InitCodexSellToggle()
    {
        CodexSellToggleLabel.Text = MarketNotice.CodexColumnToggle;
        _codexSellToggle = new Hud.ToggleSwitch(App.Settings.Current.CodexSellColumn)
        {
            OnToggled = on =>
            {
                App.Settings.Current.CodexSellColumn = on;
                App.Settings.Save();
                InteractionLog.Toggle("codex sell column " + (on ? "on" : "off"), _codexSellToggle!);
                // Rebuild in place: no stagger (this is not a page entrance) and the open dossier
                // is carried across, so turning the column on does not throw the reader back to the
                // first ore - same contract as the hourly market publish's rebuild.
                BuildReferenceTree(staggerEntry: false,
                                   preserveSelection: (_selectedRefCard?.Tag as Resource)?.Name);
            },
        };
        CodexSellToggleHost.Content = _codexSellToggle;
    }

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
    // Shapes and stroke come from the frozen codex-motion mock values. Brush and geometries are
    // parsed once and frozen here so BuildResourceCard doesn't re-parse/re-allocate per card per rebuild.
    private static readonly System.Windows.Media.SolidColorBrush ClassGlyphBrush =
        FrozenBrush(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xC0, 0xFF, 0xB2, 0x3E)));
    private static readonly System.Windows.Media.Geometry ClassGlyphMetalGeometry =
        FrozenGeometry("M 5,0 L 10,5 L 5,10 L 0,5 Z");
    private static readonly System.Windows.Media.Geometry ClassGlyphGemGeometry =
        FrozenGeometry("M 2.5,0.7 L 7.5,0.7 L 10,5 L 7.5,9.3 L 2.5,9.3 L 0,5 Z");
    private static readonly System.Windows.Media.Geometry ClassGlyphMineralGeometry =
        FrozenGeometry("M 2,7 A 1.6,1.6 0 1 0 2,7.01 M 7,3 A 2,2 0 1 0 7,3.01 M 7.5,8 A 1.3,1.3 0 1 0 7.5,8.01");

    private static System.Windows.Media.SolidColorBrush FrozenBrush(System.Windows.Media.SolidColorBrush b) { b.Freeze(); return b; }

    private static System.Windows.Media.Geometry FrozenGeometry(string data)
    {
        var g = System.Windows.Media.Geometry.Parse(data);
        g.Freeze();
        return g;
    }

    private static FrameworkElement? ClassGlyph(string oreClass)
    {
        System.Windows.Media.Geometry? geometry = oreClass.ToLowerInvariant() switch
        {
            "metal"   => ClassGlyphMetalGeometry,
            "gem"     => ClassGlyphGemGeometry,
            "mineral" => ClassGlyphMineralGeometry,
            _ => null,
        };
        if (geometry is null) return null;
        return new System.Windows.Shapes.Path
        {
            Data = geometry,
            Stroke = ClassGlyphBrush, StrokeThickness = 1.2, Width = 10, Height = 10,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0),
        };
    }

    // preserveSelection names the resource whose dossier should stay open across the rebuild
    // (remembered UI state, same contract as _expandedName). Null - every user-driven caller -
    // keeps the original behavior of auto-selecting the first card.
    private void BuildReferenceTree(bool staggerEntry = false, string? preserveSelection = null)
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

        // Sell prices are resolved once per rebuild, here: the sort keys and the cards' price cells
        // both read this dictionary, so a rebuild never queries the snapshot twice for one ore.
        // Null = the column does not exist this rebuild (feature off, column toggle off, or no
        // snapshot yet). The toggle ROW is a separate gate: it appears with the feature, whether or
        // not the column is switched on.
        CodexSellToggleRow.Visibility = App.Settings.Current.MarketDataEnabled == true
            ? Visibility.Visible : Visibility.Collapsed;
        _codexSellToggle?.SetOnSilently(App.Settings.Current.CodexSellColumn);

        var sellHits = BuildSellHits(filtered);
        if (sellHits != null && _refSort != "default")
        {
            // Unmapped ores (no dictionary entry, or no priced row) sink to the bottom in BOTH
            // directions. OrderBy is stable, so the rarity/RS order above survives as the tiebreak.
            double Price(Resource r) => sellHits.TryGetValue(r.Name, out var h) && h != null ? h.Display : -1;
            filtered = (_refSort == "sellDesc"
                    ? filtered.OrderBy(r => Price(r) < 0 ? 1 : 0).ThenByDescending(Price)
                    : filtered.OrderBy(r => Price(r) < 0 ? 1 : 0).ThenBy(Price))
                .ToList();
        }
        UpdateReferenceSortHeader(sellHits != null && filtered.Count > 0);

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
            var card = BuildResourceCard(filtered[i], sellHits, out var select);
            ReferenceList.Children.Add(card);
            _refSelectByName[filtered[i].Name] = select;
            if (i == 0) selectFirst = select;
        }
        // Exactly one selection runs per rebuild, so the dossier is built once and never flashes
        // through the first resource's detail on its way back to the one the reader had open. A
        // preserved name that no longer survives the current filters falls through to the first card.
        if (preserveSelection is not null && _refSelectByName.TryGetValue(preserveSelection, out var reselect))
            reselect();
        else
            selectFirst?.Invoke();   // auto-select the first card (shows its detail)

        if (staggerEntry)
        {
            CascadeIn(ReferenceList.Children, maxAnimated: 14);
        }
        else
        {
            // Filter/search rebuilds happen per keystroke (debounced) - a per-card stagger would
            // be jank and noise, so the rebuilt list gets one quick settle fade instead.
            if (!Motion.Reduced)
            {
                ReferenceList.BeginAnimation(UIElement.OpacityProperty,
                    new System.Windows.Media.Animation.DoubleAnimation(0.55, 1,
                        System.TimeSpan.FromMilliseconds(120))
                    {
                        EasingFunction = new System.Windows.Media.Animation.QuadraticEase
                        { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
                    });
            }
        }
    }

    /// <summary>
    /// Best REFINED sell per resource for one Codex rebuild, or null when the sell column must not
    /// exist at all: live market prices off, the reader's own column toggle off, or on with no
    /// snapshot fetched yet. Gated off, the sort state goes back to "default" too, so the list is
    /// byte-for-byte the pre-feature codex. Refined and not raw for the same reason every other
    /// price surface is: UEX's raw ore-sales dataset has had no reports since patch 4.8.
    /// </summary>
    private Dictionary<string, PriceHit?>? BuildSellHits(List<Resource> resources)
    {
        if (App.Settings.Current.MarketDataEnabled != true
            || !App.Settings.Current.CodexSellColumn
            || App.Market.Snapshot is not { } snap)
        {
            _refSort = "default";
            return null;
        }

        var hits = new Dictionary<string, PriceHit?>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in resources) hits[r.Name] = MarketQueries.BestRefinedSell(snap, r.Name);
        return hits;
    }

    // Shows/hides the sticky sell header and paints its state: amber label plus a direction caret
    // while a sell sort is active, the inherited dim/hover treatment otherwise.
    private void UpdateReferenceSortHeader(bool show)
    {
        if (ReferenceSortHeader == null) return;
        ReferenceSortHeader.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ReferenceSortCaret.Text = _refSort switch { "sellDesc" => "▾", "sellAsc" => "▴", _ => "" };
        if (_refSort == "default")
            ReferenceSortHeader.ClearValue(System.Windows.Documents.TextElement.ForegroundProperty);
        else
            System.Windows.Documents.TextElement.SetForeground(ReferenceSortHeader, (System.Windows.Media.Brush)FindResource("AccentBrush"));
    }

    // The Codex's one sortable column. Click cycles codex order -> highest sell -> lowest sell ->
    // codex order. The rebuild's own 120ms settle fade is the sort animation; there is deliberately
    // no per-row reorder choreography.
    private void ReferenceSortHeader_Click(object sender, MouseButtonEventArgs e)
    {
        _refSort = _refSort switch
        {
            "default"  => "sellDesc",
            "sellDesc" => "sellAsc",
            _          => "default",
        };
        InteractionLog.Click("codex sort: " + _refSort, ReferenceSortHeader);
        BuildReferenceTree(staggerEntry: false, preserveSelection: (_selectedRefCard?.Tag as Resource)?.Name);
    }

    // One Codex row's price: the best refined sell, a BARE number - the column header states the
    // unit once (owner ruling 2026-07-27) so 39 rows do not repeat it. When the freshest priced row
    // for that ore comes from an older game patch the number goes dim and carries the patch tag, so
    // a stale price is never signalled by colour alone.
    private static FrameworkElement SellCell(PriceHit hit)
    {
        var value = new TextBlock
        {
            Text = hit.Display.ToString("n0"),
            FontSize = 12,
            FontFamily = Hud.Font("HeadFont"),
            Foreground = Hud.Br(hit.Stale ? "FgDimBrush" : "GoldBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        if (!hit.Stale)
        {
            value.Margin = new Thickness(12, 0, 0, 0);
            return value;
        }

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(12, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(value);
        row.Children.Add(PatchTagChip(hit.GameVersion));
        return row;
    }

    private FrameworkElement BuildResourceCard(Resource r, IReadOnlyDictionary<string, PriceHit?>? sellHits, out Action select)
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
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });   // fixed so ore names don't zig-zag when a row has no class glyph
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

        // Sell column: added only when the column is switched on with the feature on and a snapshot
        // loaded (sellHits != null), so otherwise the card keeps its original four columns exactly.
        // An ore the snapshot has no price for keeps the column but leaves the cell empty, never a
        // placeholder.
        if (sellHits != null)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            sellHits.TryGetValue(r.Name, out var hit);
            if (hit != null)
            {
                var cell = SellCell(hit);
                Grid.SetColumn(cell, 4); grid.Children.Add(cell);
            }
        }

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

    // Stale-price marker in the dock count-chip geometry: mono 9 bold on a 0x1F tint of the dim
    // text colour, 0x66 border, radius 3. Used by the dossier price rows, the work order sell
    // line, and the Codex sell column cells (the column returned as an off-by-default toggle
    // preference after first being removed for clutter; both were owner rulings on 2026-07-27).
    private static Border PatchTagChip(string gameVersion)
    {
        var c = ((System.Windows.Media.SolidColorBrush)Hud.Br("FgDimBrush")).Color;
        return new Border
        {
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x1F, c.R, c.G, c.B)),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x66, c.R, c.G, c.B)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 1, 4, 1),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = MarketNotice.PatchTag(gameVersion),
                FontFamily = Hud.Font("MonoFont"),
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                Foreground = Hud.Br("FgDimBrush"),
            },
        };
    }

    // Column header above the dossier price rows (Task 11 fix round, mock CSS .colhdr @
    // nexus-design-lab/market-data/index.html:172-175): dim, bold, 9px, uppercase labels in the
    // SAME 4-column layout as MarketPriceRow (Star/128/116/132, 10px gaps) so the header lines up
    // with the numeric columns below it. Callers only add this alongside actual rows (see the
    // gate in ShowResourceDetail) - it never renders alone over an empty section.
    //
    // Both numeric columns were widened when their values gained currency units (owner rulings
    // 2026-07-27), sized by measuring the widest realistic string in the rendering font:
    // week avg 104 -> 128 ("999,999 aUEC/SCU" is 115.4px in the inherited UiFont at 13, plus the
    // cell's 10px left gap), now 96 -> 116 ("999,999 aUEC" is 77.9px at 12, plus the 10px gap, the
    // 5px label gap and the 18.4px "now" label). Both this header and MarketPriceRow below must be
    // changed together or the columns drift apart.
    private FrameworkElement MarketPriceColumnHeader()
    {
        var dimBrush = (System.Windows.Media.Brush)FindResource("FgDimBrush");

        var g = new Grid { Margin = new Thickness(12, 0, 12, 5) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(128) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(116) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(132) });

        TextBlock Hdr(string text, Thickness margin) => new TextBlock
        {
            Text = text, FontSize = 9, FontWeight = FontWeights.Bold, Foreground = dimBrush,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = margin,
        };

        var term = Hdr("TERMINAL", new Thickness(0));
        term.HorizontalAlignment = HorizontalAlignment.Left;
        g.Children.Add(term);
        var avg = Hdr("WEEK AVG", new Thickness(10, 0, 0, 0));
        Grid.SetColumn(avg, 1); g.Children.Add(avg);
        var now = Hdr("NOW", new Thickness(10, 0, 0, 0));
        Grid.SetColumn(now, 2); g.Children.Add(now);
        var upd = Hdr("UPDATED", new Thickness(10, 0, 0, 0));
        Grid.SetColumn(upd, 3); g.Children.Add(upd);

        return g;
    }

    // One row in the dossier's MARKET PRICES section (Task 11; restructured in the fix round to
    // match the approved mock, nexus-design-lab/market-data/index.html:592-623 markup / :157-175
    // CSS - see task-11-report.md for the full mapping): a single-line 4-column grid (Star/128
    // week avg/116 now/132 updated, matching MarketPriceColumnHeader) instead of the original
    // 3-column stacked layout. The mock's 104/96 widths grew to 128/116 to fit the currency units;
    // keep both builders in step with MarketPriceColumnHeader. Week average falls back to instant per PriceHit.Display so a
    // week-less row never shows a bare 0; the raw instant price still shows separately as "now".
    // Either the row's age or - when its price is from an older game patch - the shared patch-tag
    // chip (PatchTagChip above) fills the Updated column. A stale row goes fully dim: every
    // text element drops to FgDimBrush so staleness
    // reads at a glance rather than depending on the small chip alone.
    private Border MarketPriceRow(PriceHit hit)
    {
        var dimBrush  = (System.Windows.Media.Brush)FindResource("FgDimBrush");
        var nameBrush = hit.Stale ? dimBrush : (System.Windows.Media.Brush)FindResource("FgBrush");
        var goldBrush = hit.Stale ? dimBrush : (System.Windows.Media.Brush)FindResource("GoldBrush");
        var cyanBrush = hit.Stale ? dimBrush : (System.Windows.Media.Brush)FindResource("CyanBrush");

        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });   // terminal name
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(128) });                    // week avg (fits "999,999 aUEC/SCU")
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(116) });                    // now (fits "999,999 aUEC" + the label)
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(132) });                    // updated / patch tag

        // Terminal name keeps TextWrapping.Wrap (brief's explicit, deliberate override of the
        // mock's single-line ellipsis - see task-11-report.md fix-round mapping).
        // App review 2026-08-01: the dossier could only ever name a terminal, because PriceHit
        // carried no id. With the id back, the shared helper appends the system and (with a live
        // session in the same system) the distance, and the row clicks through to that terminal's
        // full price board - the same jump the Starmap already makes. Silence rule: an unresolvable
        // terminal renders exactly as before, with no click affordance.
        var where = PriceLocationLabel.Describe(hit.TerminalId, App.Market.Snapshot?.Terminals.Rows,
                                                App.Map, App.Player.Current);
        var name = new TextBlock
        {
            Text = where is null ? hit.TerminalName : $"{hit.TerminalName}  ({where})",
            FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = nameBrush,
            TextWrapping = System.Windows.TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
        };
        if (hit.TerminalId > 0)
        {
            name.Cursor = System.Windows.Input.Cursors.Hand;
            name.ToolTip = $"Show all prices at {hit.TerminalName}.";
            name.MouseLeftButtonUp += (_, _) =>
            {
                SetActivePage("trade");
                _tradePage?.ShowPricesForTerminal(hit.TerminalId);
            };
        }
        g.Children.Add(name);

        // Both value cells carry a unit (owner rulings 2026-07-27): the week average spells out
        // "aUEC/SCU", the instant price beside it takes the shorter "aUEC" because its own dim
        // "now" label already says what it is and a second "/SCU" on the same row is clutter.
        var avg = new TextBlock
        {
            Text = $"{hit.Display:n0} aUEC/SCU", FontSize = 13, Foreground = goldBrush,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };
        Grid.SetColumn(avg, 1);
        g.Children.Add(avg);

        // "now": value then the dim "now" label, baseline-together, right-justified as one block -
        // mock markup order is value-first (.pnowval) then label (.pnowlbl), not "now <value>".
        var nowRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
        };
        nowRow.Children.Add(new TextBlock { Text = $"{hit.Instant:n0} aUEC", FontSize = 12, Foreground = cyanBrush, VerticalAlignment = VerticalAlignment.Bottom });
        nowRow.Children.Add(new TextBlock { Text = "now", FontSize = 10, Foreground = dimBrush, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(5, 0, 0, 0) });
        Grid.SetColumn(nowRow, 2);
        g.Children.Add(nowRow);

        FrameworkElement updated;
        if (hit.Stale)
        {
            var chip = PatchTagChip(hit.GameVersion);
            chip.HorizontalAlignment = HorizontalAlignment.Right;
            updated = chip;
        }
        else
        {
            updated = new TextBlock
            {
                Text = MarketNotice.FormatAge(DateTime.UtcNow - hit.ModifiedUtc), FontSize = 10,
                Foreground = dimBrush, HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
            };
        }
        Grid.SetColumn(updated, 3);
        g.Children.Add(updated);

        return Hud.RowCard(g);
    }

    private TextBlock RefSectionLabel(string text) => new TextBlock
    {
        Text = text, FontSize = 9, FontWeight = FontWeights.Bold,
        Foreground = (System.Windows.Media.Brush)FindResource("FgDimBrush"),
        Margin = new Thickness(0, 4, 0, 8),
    };

    private Border YieldRow(NexusApp.Models.RefineryYield y)
    {
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
        return Hud.RowCard(rg);
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

        var row = Hud.RowCard(g);
        row.Cursor = System.Windows.Input.Cursors.Hand;
        row.ToolTip = $"Open {f.Ore} in the Codex  ·  carried by {f.Variants} {f.Ore} rock type{(f.Variants == 1 ? "" : "s")}";
        row.MouseLeftButtonDown += (s, e) => NavigateToResource(f.Ore);
        return row;
    }

    // Compact number: drop a trailing ".0" but keep real decimals.
    private static string FmtD(double v) => v == System.Math.Floor(v) ? ((long)v).ToString() : v.ToString("0.##");

    // Severity colour for the mining gauges: lo = green (easy/good), md = amber, hi = red (hard/risky).
    private System.Windows.Media.Brush LevelBrush(string level) => level switch
    {
        "hi" => (System.Windows.Media.Brush)FindResource("DangerBrush"),
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

    // Dossier expander state (amendment 2026-07-27 item 6b/6c). Session-scoped and GLOBAL rather
    // than per resource: a reader who opened the price detail (or the blueprint list) once is
    // comparing ores, and re-clicking the same expander on every ore would be the restructure
    // working against them. Fields, not settings: nothing here survives an app restart, and every
    // dossier rebuild we cause ourselves (the hourly market publish rebuilds the Codex) reads them
    // back so an open section is never yanked shut under the user - the same survives-a-rebuild
    // contract as _expandedName and _expandedWorkOrderSells.
    private bool _dossierValueExpanded;
    private bool _dossierBlueprintsExpanded;
    private bool _dossierFoundInExpanded;

    // The always-visible line of the VALUE section (mock .vsum): the top refined terminal and its
    // price on the left, the seed's best refinery and its yield modifier on the right, in the
    // dossier's own row chrome (YieldRow: radius 6, padding 12,7,12,7, Bg2Nav on a NavBorder
    // hairline). Either half may be absent - market data off or unpriced ore leaves the left side
    // out, an ore with no refinery entries leaves the right side out - and the row is only ever
    // built when at least one of them is present.
    private Border ValueSummaryRow(PriceHit? top, NexusApp.Models.RefineryYield? bestYield)
    {
        var dim  = (System.Windows.Media.Brush)FindResource("FgDimBrush");
        var fg   = (System.Windows.Media.Brush)FindResource("FgBrush");
        var gold = (System.Windows.Media.Brush)FindResource("GoldBrush");

        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (top is not null)
        {
            // Grid, not a horizontal StackPanel: a StackPanel measures with infinite width, so the
            // terminal name would never trim (same reason the hero name row is a Grid).
            var left = new Grid();
            left.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            left.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            left.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            left.Children.Add(new TextBlock
            {
                Text = top.TerminalName, FontSize = 13, FontWeight = FontWeights.SemiBold,
                Foreground = top.Stale ? dim : fg, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = System.Windows.TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 8, 0),
            });
            var price = new TextBlock
            {
                Text = $"{top.Display:n0} aUEC/SCU", FontSize = 13, Foreground = top.Stale ? dim : gold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(price, 1);
            left.Children.Add(price);
            // The hero line above carries this row's age, but a price from an older patch still
            // says so here: staleness never rides on the dim colour alone.
            if (top.Stale)
            {
                var chip = PatchTagChip(top.GameVersion);
                Grid.SetColumn(chip, 2);
                left.Children.Add(chip);
            }
            g.Children.Add(left);
        }

        if (bestYield is not null)
        {
            var right = new StackPanel
            {
                Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
            };
            right.Children.Add(new TextBlock
            {
                Text = MarketNotice.BestRefineryLabel, FontSize = 11, Foreground = dim,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0),
            });
            right.Children.Add(new TextBlock
            {
                Text = bestYield.Station, FontSize = 12, Foreground = fg, MaxWidth = 230,
                TextTrimming = System.Windows.TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0),
            });
            right.Children.Add(new TextBlock
            {
                Text = $"{(bestYield.ModifierPct > 0 ? "+" : "")}{bestYield.ModifierPct}%", FontSize = 13,
                FontWeight = FontWeights.SemiBold, Foreground = ModifierBrush(bestYield.ModifierPct),
                VerticalAlignment = VerticalAlignment.Center,
            });
            Grid.SetColumn(right, 1);
            g.Children.Add(right);
        }

        return Hud.RowCard(g, marginBottom: 6);
    }

    // ToggleLink's chrome (11 bold amber + caret, padding 12,5, radius 8, 1px NavBorder) with the
    // three things the dossier restructure needs and the plain helper does not have: the app's
    // drill-down reveal motion, a remembered state the caller owns, and a logged click. Mouse only.
    private Border DossierExpanderLink(string showText, string hideText, FrameworkElement target,
                                       bool expanded, string logLabel, Action<bool> onToggled)
    {
        var tb = new TextBlock
        {
            Text = (expanded ? hideText : showText) + (expanded ? "  ▴" : "  ▾"),
            FontSize = 11, FontWeight = FontWeights.Bold,
            Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush"),
        };
        var btn = new Border
        {
            Child = tb, Padding = new Thickness(12, 5, 12, 5), CornerRadius = new CornerRadius(8),
            BorderBrush = (System.Windows.Media.Brush)FindResource("NavBorderBrush"), BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 8),
            Cursor = System.Windows.Input.Cursors.Hand, Background = System.Windows.Media.Brushes.Transparent,
        };
        bool open = expanded;
        btn.MouseLeftButtonDown += (s, e) =>
        {
            e.Handled = true;
            open = !open;
            onToggled(open);
            InteractionLog.Click($"dossier {logLabel} {(open ? "expand" : "collapse")}", btn);
            if (open) ExpandRows(target, animate: !Motion.Reduced);
            else CollapseRows(target, animate: !Motion.Reduced);
            tb.Text = (open ? hideText : showText) + (open ? "  ▴" : "  ▾");
        };
        return btn;
    }

    // A long-tail section that is collapsed by default (mock .tailhdr): the RefSectionLabel
    // treatment plus the count in parentheses, a caret, and a hairline running out to the right
    // edge, with the whole row clickable. The body expands in place on the app's reveal motion.
    private Border TailSectionHeader(string label, int count, FrameworkElement body,
                                     bool expanded, string logLabel, Action<bool> onToggled)
    {
        var dim = (System.Windows.Media.Brush)FindResource("FgDimBrush");
        var fg  = (System.Windows.Media.Brush)FindResource("FgBrush");

        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var text = new TextBlock
        {
            Text = $"{label} ({count})", FontSize = 9, FontWeight = FontWeights.Bold, Foreground = dim,
            VerticalAlignment = VerticalAlignment.Center,
        };
        g.Children.Add(text);

        var caret = new TextBlock
        {
            Text = expanded ? "▴" : "▾", FontSize = 9,
            Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
        };
        Grid.SetColumn(caret, 1);
        g.Children.Add(caret);

        var rule = new Border
        {
            Height = 1, Background = (System.Windows.Media.Brush)FindResource("NavBorderBrush"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
        };
        Grid.SetColumn(rule, 2);
        g.Children.Add(rule);

        var header = new Border
        {
            Child = g, Margin = new Thickness(0, 4, 0, 0), Padding = new Thickness(0, 6, 0, 6),
            Background = System.Windows.Media.Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand,
        };
        header.MouseEnter += (s, e) => text.Foreground = fg;
        header.MouseLeave += (s, e) => text.Foreground = dim;

        bool open = expanded;
        header.MouseLeftButtonDown += (s, e) =>
        {
            e.Handled = true;
            open = !open;
            onToggled(open);
            InteractionLog.Click($"dossier {logLabel} {(open ? "expand" : "collapse")}", header);
            if (open) ExpandRows(body, animate: !Motion.Reduced);
            else CollapseRows(body, animate: !Motion.Reduced);
            caret.Text = open ? "▴" : "▾";
        };
        return header;
    }

    // Puts a freshly built expander body into the state its remembered flag says it is in, without
    // animating: a dossier rebuild we cause (an hourly market publish) must not replay the reveal
    // on a section the user already has open. Same static-on-rebuild handling the work order sell
    // block uses.
    private static void ApplyExpanderState(FrameworkElement body, bool expanded)
    {
        body.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        body.Height = expanded ? double.NaN : 0;
        body.Opacity = expanded ? 1 : 0;
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

        // Live REFINED prices for this ore, resolved ONCE for the whole dossier so the hero's
        // best-sell line and the VALUE section under it can never disagree (both read this ranked,
        // best-first list). Gated on three things together - feature on, a snapshot landed, and
        // this ore actually having priced rows - so an unmapped or unpriced ore adds nothing at
        // all, matching every price surface's silence-over-placeholder rule. Refined and not raw
        // because UEX's raw ore-sales dataset has had no reports since patch 4.8 (amendment
        // 2026-07-27).
        var marketSnap = App.Settings.Current.MarketDataEnabled == true ? App.Market.Snapshot : null;
        var priceHits = marketSnap is null
            ? new List<PriceHit>()
            : MarketQueries.TopRefinedSells(marketSnap, r.Name, 3);
        var topHit = priceHits.Count > 0 ? priceHits[0] : null;

        // hero header
        var hg = new Grid();
        hg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // hologram column, filled in once comp is available below
        var ht = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        // Grid (not StackPanel) so the name column is width-constrained and CharacterEllipsis actually engages;
        // a horizontal StackPanel measures children with infinite width and never trims.
        var nameRow = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
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

        // One best-sell line inside the hero (amendment 2026-07-27 item 6a, mock .dbest): the same
        // hairline-then-line treatment the decoder hero's sell line uses, so the ore's headline
        // worth is answered before the reader scrolls at all. Silent when there is no priced row.
        var heroContent = new StackPanel();
        heroContent.Children.Add(hg);
        if (topHit is not null)
        {
            heroContent.Children.Add(new Border
            {
                Height = 1, Background = (System.Windows.Media.Brush)FindResource("NavBorderBrush"),
                Margin = new Thickness(0, 12, 0, 11),
            });
            // Age, or the patch the price was captured in when the row is stale: a price never
            // renders without one of the two.
            var heroAge = topHit.Stale
                ? MarketNotice.PatchTag(topHit.GameVersion)
                : MarketNotice.FormatAge(DateTime.UtcNow - topHit.ModifiedUtc);
            // Segmented like the decoder line (owner ruling 2026-07-27): label dim, value gold,
            // terminal Fg, age dim, and the whole line dim when the price is stale.
            var heroSell = new TextBlock
            {
                FontSize = 12, TextTrimming = System.Windows.TextTrimming.CharacterEllipsis,
            };
            SellLineRuns(heroSell, MarketNotice.DossierHeroLabel, topHit, heroAge);
            heroContent.Children.Add(heroSell);
        }

        // Chamfered HUD hero panel with corner brackets (matches the Blueprint detail hero).
        var hero = Hud.Panel(heroContent, chamfer: HeroChamfer, brackets: true,
            bg: (System.Windows.Media.Brush)FindResource("Bg2NavBrush"),
            border: (System.Windows.Media.Brush)FindResource("NavBorderBrush"),
            padding: new Thickness(20, 14, 18, 14));
        hero.Margin = new Thickness(0, 0, 0, 12);
        ReferenceDetailPanel.Children.Add(hero);

        // ── VALUE: live prices and refinery yields as one section, directly under the hero ──
        // (amendment 2026-07-27 item 6b, mock section 07A). It replaces the separate MARKET PRICES
        // and REFINERY YIELDS sections that used to sit at the very bottom of the dossier.
        //
        // The two halves are gated INDEPENDENTLY, and that is the whole point: the price half is
        // market data and comes and goes with the feature, the yield half is SEED data and must
        // never disappear with it. So the section renders in three forms - both halves; prices
        // only (an ore with no refinery entries); yields only (market data off, no snapshot, or no
        // priced row for this ore) - and is skipped entirely only when the ore has neither.
        var yields = r.Refineries.OrderByDescending(x => x.ModifierPct).ToList();
        if (topHit is not null || yields.Count > 0)
        {
            ReferenceDetailPanel.Children.Add(RefSectionLabel(MarketNotice.ValueSection));
            ReferenceDetailPanel.Children.Add(ValueSummaryRow(topHit, yields.Count > 0 ? yields[0] : null));

            var valueDetails = new StackPanel { ClipToBounds = true };   // clips mid-reveal, like the work order sell rows
            if (priceHits.Count > 0)
            {
                valueDetails.Children.Add(MarketPriceColumnHeader());
                foreach (var hit in priceHits)
                    valueDetails.Children.Add(MarketPriceRow(hit));

                valueDetails.Children.Add(new TextBlock
                {
                    Text = MarketNotice.DossierFooter, FontSize = 10.5, Foreground = dim,
                    Margin = new Thickness(0, 2, 0, 10),
                });

                // The dossier is the one surface that carries the snapshot-age note (the decoder
                // line and the overlay card line are single-line and show age per row instead) -
                // only when the price data as a whole, not just this ore's rows, is more than a
                // day old. Measured off the RefinedPrices stamp alone, not the snapshot's
                // NewestFetchUtc: that one spans all five datasets, including the daily reference
                // sets and the permanently frozen RawPrices, so a daily refresh could report the
                // prices as fresh while the hourly price leg had been failing for a day. The
                // commodity catalogue is excluded for the same reason - it is identity data, and a
                // partial cycle that lands the catalogue but fails the refined leg (the exact case
                // the ShouldFetch rewrite was written for) would otherwise hide this note while
                // day-old prices sit on screen (amendment 2026-07-27).
                var snapshotAge = DateTime.UtcNow - marketSnap!.RefinedPrices.FetchedUtc;
                if (snapshotAge > TimeSpan.FromHours(24))
                {
                    valueDetails.Children.Add(new TextBlock
                    {
                        Text = MarketNotice.SnapshotAgeNote(snapshotAge), FontSize = 10.5, FontFamily = monoFont,
                        Foreground = dim, Margin = new Thickness(0, 0, 0, 10),
                    });
                }
            }

            if (yields.Count > 0)
            {
                // Every refinery, not the old first-page-of-five plus a nested "Show all": the
                // rows are already behind one click here, and a second expander inside the first
                // would hide seed data the dossier shows today.
                valueDetails.Children.Add(RefSectionLabel($"REFINERY YIELDS  ·  {yields.Count}"));
                foreach (var y in yields)
                    valueDetails.Children.Add(YieldRow(y));
            }

            ApplyExpanderState(valueDetails, _dossierValueExpanded);
            ReferenceDetailPanel.Children.Add(DossierExpanderLink(
                MarketNotice.ValueDetailsShow, MarketNotice.ValueDetailsHide, valueDetails,
                _dossierValueExpanded, "value details", open => _dossierValueExpanded = open));
            ReferenceDetailPanel.Children.Add(valueDetails);
        }

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
        // Scheme D (approved 2026-07-10): the primary ring segment takes the ore's own rarity
        // color, opaque. rb is built from RarityBrush(r.Rarity), which always returns a
        // SolidColorBrush (BrushFromHex) - fall back to the amber-bright default if that ever
        // stops holding true.
        var primaryColor = rb is System.Windows.Media.SolidColorBrush rbSolid
            ? rbSolid.Color
            : System.Windows.Media.Color.FromRgb(0xFF, 0xD0, 0x89);
        _codexHologram.Show(r.Name, profile?.Class ?? "Metal", pcts, primaryColor);
        if (!IsActive) _codexHologram.Pause();   // deactivation already happened - do not run under the game
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

        // ── Long tail: the two counted, collapsed-by-default sections, then LOCATIONS ──
        // Order per mock section 07 (amendment 2026-07-27 item 6c): the two collapsed headers sit
        // together and LOCATIONS closes the dossier as the last open block. Each collapsed body
        // keeps its own first-page-plus-"Show all" pagination exactly as it was, so nothing the
        // dossier shows today is unreachable.

        // blueprints - collapsed behind a counted header
        var bps = App.Data.GetBlueprintsForResource(r.Name);
        if (bps.Count == 0)
        {
            ReferenceDetailPanel.Children.Add(RefSectionLabel("USED IN BLUEPRINTS  ·  0"));
            ReferenceDetailPanel.Children.Add(new TextBlock { Text = "None", FontSize = 12, Foreground = dim });
        }
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
            var bpBody = new StackPanel { ClipToBounds = true, Margin = new Thickness(0, 8, 0, 0) };
            const int bpShow = 8;
            for (int i = 0; i < System.Math.Min(bpShow, bpList.Count); i++)
                bpBody.Children.Add(BpRow(bpList[i].Name));
            if (bpList.Count > bpShow)
            {
                var moreBp = new StackPanel { Visibility = Visibility.Collapsed };
                for (int i = bpShow; i < bpList.Count; i++) moreBp.Children.Add(BpRow(bpList[i].Name));
                bpBody.Children.Add(moreBp);
                bpBody.Children.Add(ToggleLink($"Show all {bpList.Count}", "Show fewer", moreBp));
            }
            ApplyExpanderState(bpBody, _dossierBlueprintsExpanded);
            ReferenceDetailPanel.Children.Add(TailSectionHeader("USED IN BLUEPRINTS", bpList.Count, bpBody,
                _dossierBlueprintsExpanded, "blueprints", open => _dossierBlueprintsExpanded = open));
            ReferenceDetailPanel.Children.Add(bpBody);
        }

        // found in other deposits - byproduct sourcing (datamined). Only shown when this ore
        // actually appears in another ore's rock; headline-only ores and hand/vehicle gems have
        // none. Collapsed behind a counted header, same treatment as USED IN BLUEPRINTS above.
        var found = App.Data.GetFoundInForResource(r.Name);
        if (found.Count > 0)
        {
            var foundBody = new StackPanel { ClipToBounds = true };
            foundBody.Children.Add(new TextBlock
            {
                Text = $"Other ores whose rock also yields {r.Name} - its share of that rock, and how often the rock carries it.",
                FontSize = 11, Foreground = dim, TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 8),
            });
            const int fShow = 6;
            for (int i = 0; i < System.Math.Min(fShow, found.Count); i++)
                foundBody.Children.Add(FoundInRow(found[i]));
            if (found.Count > fShow)
            {
                var moreF = new StackPanel { Visibility = Visibility.Collapsed };
                for (int i = fShow; i < found.Count; i++) moreF.Children.Add(FoundInRow(found[i]));
                foundBody.Children.Add(moreF);
                foundBody.Children.Add(ToggleLink($"Show all {found.Count}", "Show fewer", moreF));
            }
            ApplyExpanderState(foundBody, _dossierFoundInExpanded);
            ReferenceDetailPanel.Children.Add(TailSectionHeader("FOUND IN OTHER DEPOSITS", found.Count, foundBody,
                _dossierFoundInExpanded, "found in", open => _dossierFoundInExpanded = open));
            ReferenceDetailPanel.Children.Add(foundBody);
        }

        // locations - top 6 + show all. Stays open, and closes the dossier per the mock.
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

        // Refinery yields and market prices used to close the dossier here as two separate
        // sections; both now live in the VALUE section under the hero (amendment 2026-07-27).

        CascadeIn(ReferenceDetailPanel.Children, maxAnimated: 8);
    }

    // Fade + rise the first few children in sequence - the MOBIGLAS dossier reveal.
    // Capped so long dossiers do not animate below the fold.
    private static void CascadeIn(UIElementCollection children, int maxAnimated)
    {
        if (Motion.Reduced) return;   // elements are already at final opacity/position - only the entrance is skipped
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

}
