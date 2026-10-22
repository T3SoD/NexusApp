using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NexusApp.Models;
using NexusApp.Services;
using static NexusApp.Views.UiHelpers;

namespace NexusApp.Views;

// Split from MainWindow.xaml.cs (app review, Task 13): Blueprint Library browse + detail.
public partial class MainWindow
{
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
    private string? _detailSegment;                // SOURCING or CRAFTING view of that blueprint; null = its default
    private string? _detailSegmentBp;              // the blueprint _detailSegment belongs to
    // Drill-down depth trackers for the directional slide (motion pass Item 8). Nav and content
    // rebuild independently (clicking a row shows detail without RenderBlueprintNav running), so
    // each needs its own last-rendered depth to compare against - sharing one field would have the
    // nav's write clobber the value the content panel needs to compare against in the same action.
    // Nav: root=0, category=1, subgroup=2, family=3 (its own switch already branches on these).
    // Content: landing collapses subgroup/family into one "subcategory" tier (root=0, category=1,
    // subgroup-or-family=2) since ShowBlueprintLanding renders identically at either; detail is
    // always 3. "search" is not part of the hierarchy at either tracker - see PlayDrillSlide.
    private int _bpNavDepth;
    private int _bpDetailDepth;
    // Maps a blueprint name to its nav-row toggle pill so a single toggle updates that
    // one row in place instead of rebuilding the whole list (the source of the lag).
    // Maps a blueprint name to a callback that refreshes that nav row's ownership
    // visuals (left strip, ✓ tick, hover pill) in place - so one toggle updates the
    // row without rebuilding the whole list.
    private readonly Dictionary<string, Action<bool>> _bpRowOwned = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string[] _bpCategories = ["Armor", "Weapons", "Ship Components", "Ammo"];

    private void InitBlueprintBrowse()
    {
        if (_bpInit) return;
        _bpInit = true;
        _allBlueprints = App.Data.GetAllBlueprints();
        UpdateOwnedChips();
        UpdateOwnedCount();
        _bpNavCollapsed = App.Settings.Current.LibraryNavCollapsed;
        ApplyNavCollapse(animate: false);
        GoRoot();
        // Repaint the rendered nav and any open detail the moment the component label
        // setting changes; without this a parked Library keeps the old naming until the
        // user navigates. Subscribed once, inside the _bpInit guard, after panels exist;
        // MainWindow is app-lifetime, so the subscription is never detached.
        App.ComponentLabelsChanged += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            RenderBlueprintNav();
            if (_detailBpName is { } shown) RenderBlueprintDetail(shown);
        });
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

    // Local mirror of the CountUp attached behavior (Views/CountUp.cs), retargeted at a fixed
    // 300ms roll for the Blueprint Library's "N owned" count (frozen value; CountUp.cs's own
    // duration is fixed via Motion.CountUpMs at 900ms, tuned for the RS Decoder's lock-on read,
    // so this is a small local copy rather than a change to that shared behavior - same pattern
    // as CommandPage.cs's local Run-count-up mirror from the Operations entrance task).
    private static readonly DependencyProperty OwnedCountCurrentProperty = DependencyProperty.RegisterAttached(
        "OwnedCountCurrent", typeof(double), typeof(MainWindow), new PropertyMetadata(0.0, OnOwnedCountCurrentChanged));

    private static void OnOwnedCountCurrentChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is not TextBlock tb) return;
        int n = (int)System.Math.Round((double)e.NewValue);
        tb.Text = n == 1 ? "1 owned" : $"{n} owned";
    }

    private double _ownedCountShown = double.NaN;   // last roll target; used only for the no-op skip below

    private void UpdateOwnedCount()
    {
        var n = App.Settings.OwnedBlueprintCount;

        // First paint, or reduced motion: snap straight to the count - nothing to roll from.
        // The base value is stored too, so a later From-less roll hands off from here rather
        // than from the attached property's 0.0 default.
        if (Motion.Reduced || double.IsNaN(_ownedCountShown))
        {
            BpOwnedCount.BeginAnimation(OwnedCountCurrentProperty, null);
            BpOwnedCount.SetValue(OwnedCountCurrentProperty, (double)n);
            BpOwnedCount.Text = n == 1 ? "1 owned" : $"{n} owned";
            _ownedCountShown = n;
            return;
        }

        if (n == _ownedCountShown) return;   // no change - do not restart the roll

        // No explicit From: WPF's snapshot-and-replace then continues from the LIVE animated
        // value, so a retrigger mid-roll keeps rolling smoothly instead of snapping to the
        // previous target first.
        var roll = new System.Windows.Media.Animation.DoubleAnimation(n, System.TimeSpan.FromMilliseconds(300))
        { EasingFunction = Motion.Settle };
        BpOwnedCount.BeginAnimation(OwnedCountCurrentProperty, roll);
        _ownedCountShown = n;
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

    // Directional slide for the blueprint drill-down (frozen in
    // docs/superpowers/specs/2026-07-10-motion-pass-values.md Item 8): the freshly rebuilt
    // container slides in from +12px (descending to a deeper level) or -12px (backing out to a
    // shallower one), fading 0 to 1 over Motion.DrillMs on the reveal curve. Same depth (e.g.
    // switching between sibling rows at the same tier) plays no slide. newDepth < 0 means the
    // current state ("search") is not part of the drill-down hierarchy - skipped entirely, and
    // the last real depth is left untouched so the next real hierarchy move still compares
    // correctly. Returns the depth to store back into the caller's tracker field.
    private int PlayDrillSlide(Panel container, int oldDepth, int newDepth)
    {
        if (newDepth < 0) return oldDepth;
        if (Motion.Reduced || newDepth == oldDepth) return newDepth;

        double fromX = newDepth > oldDepth ? 12 : -12;
        var slide = new System.Windows.Media.TranslateTransform(fromX, 0);
        container.RenderTransform = slide;
        container.Opacity = 0;
        var dur = System.TimeSpan.FromMilliseconds(Motion.DrillMs);
        var fade = new System.Windows.Media.Animation.DoubleAnimation(0, 1, dur) { EasingFunction = Motion.Reveal };
        var move = new System.Windows.Media.Animation.DoubleAnimation(fromX, 0, dur) { EasingFunction = Motion.Reveal };
        container.BeginAnimation(UIElement.OpacityProperty, fade);
        slide.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, move);
        return newDepth;
    }

    /// <summary>The AUTO-TRACK status chip on the Library's filter row - the old BLUEPRINTS
    /// header chip, moved home (F14: "session AND a Settings toggle" is a single-feature
    /// derivation, so it lives with its feature). Three honest states instead of the old two:
    /// green breathing "tracking" when collecting; dim "no session" when the toggle is on but
    /// Star Citizen is closed; dim "off in Settings" when the user disabled it. Never red - not
    /// tracking is a state of the world or a choice, not a failure. Repainted on
    /// App.GameLog.StateChanged (wired in the MainWindow ctor) and on every nav render.</summary>
    internal void RefreshBlueprintTrackingLine()
    {
        if (BpAutoTrackText == null || App.GameLog == null) return;
        bool tracking = App.GameLog.IsSessionLive && App.GameLog.AutoMark;
        BpAutoTrackText.Text = tracking ? "tracking"
            : App.GameLog.AutoMark ? "no session" : "off in Settings";
        var brush = tracking ? Hud.Br("OkBrush") : Hud.Br("FgDimBrush");
        BpAutoTrackDot.Fill = brush;
        BpAutoTrackText.Foreground = brush;
        Hud.PulseDot(BpAutoTrackDot, tracking);
    }

    private void RenderBlueprintNav()
    {
        RefreshBlueprintNetworkCounts();   // one grouped read per render, never one per row (G12)
        RefreshBlueprintTrackingLine();
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
                var groups = inCat.Where(b => BlueprintFamilyGrouping.Subgroup(b) != null)
                    .GroupBy(b => BlueprintFamilyGrouping.Subgroup(b)!).OrderBy(g => g.Key).ToList();
                if (groups.Count > 0)
                {
                    foreach (var grp in groups)
                    {
                        var sub = grp.Key;
                        BlueprintNavPanel.Children.Add(DrillRow(sub, grp.Count(), catCol, () => EnterSubgroup(sub)));
                    }
                    RenderLeafGroup(inCat.Where(b => BlueprintFamilyGrouping.Subgroup(b) == null), catCol);
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
                var items = src.Where(b => b.Category == _bpCat && BlueprintFamilyGrouping.Subgroup(b) == _bpSub).ToList();
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
                    .Where(b => b.Category == _bpCat && (_bpSub.Length == 0 ? BlueprintFamilyGrouping.Subgroup(b) == null : BlueprintFamilyGrouping.Subgroup(b) == _bpSub) && BlueprintFamilyGrouping.FamilyKeyOf(b) == _bpFam)
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
                _bpRailText = RailLabel("Browse", cards);
                if (cards == 0)
                    BlueprintNavPanel.Children.Add(NavEmptyNote(
                        _bpOwnFilter == BpOwnFilter.Owned ? "Nothing marked owned yet" : "Every blueprint is marked owned"));
                break;
            }
        }

        int navDepth = _bpLevel switch
        {
            "root" => 0,
            "category" => 1,
            "subgroup" => 2,
            "family" => 3,
            _ => -1,   // "search" - not part of the drill-down hierarchy, no slide
        };
        _bpNavDepth = PlayDrillSlide(BlueprintNavPanel, _bpNavDepth, navDepth);

        // The collapse toggle sits at the right end of the breadcrumb row on every level.
        WrapCrumb(BlueprintCrumbHost, () => NavToggleButton(collapsed: false));
        EnsureNavRail();
        _bpRailLabel!.Text = _bpRailText;
    }

    private TextBlock NavEmptyNote(string text = "Nothing here in this filter") => new()
    {
        Text = text, FontSize = 12, Foreground = (System.Windows.Media.Brush)FindResource("FgDimBrush"),
        Margin = new Thickness(6, 8, 0, 0), TextWrapping = TextWrapping.Wrap,
    };

    // within a leaf set: families with >1 variant become drill rows; singles become blueprint rows
    private void RenderLeafGroup(System.Collections.Generic.IEnumerable<NexusApp.Models.Blueprint> items, System.Windows.Media.Brush col)
    {
        var fams = items.GroupBy(BlueprintFamilyGrouping.FamilyKeyOf).OrderBy(g => g.Key).ToList();
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
        _bpRailText = RailLabel(text, count);   // the collapsed rail names the same list
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
        Hud.Hoverable(host, on => frame.Fill = on ? highlight : bg2);
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
        Hud.Hoverable(host, on => frame.Fill = on ? highlight : bg2);
        host.MouseLeftButtonDown += (_, __) => onClick();
        return host;
    }

    // ── Network ownership on Library rows (app review G12) ─────────────────────────────────────
    // The Library had zero references to App.Network: every ownership read was
    // App.Settings.IsBlueprintOwned, which only ever answers "do I have this". The moment a player
    // cares who else owns a blueprint is precisely when looking at one they do NOT own, and this
    // list has a NOT OWNED filter built into it. The question was answerable, but only by leaving
    // for the Network tab and coming back.
    //
    // Counts are batched ONCE per render into this field rather than queried per row: the Library
    // renders hundreds of rows and OwnerCounts is a single grouped SQL read. Empty for a solo user,
    // which makes every chip below vanish with no extra branch at the call sites.
    private IReadOnlyDictionary<string, int> _bpNetworkCounts =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    private void RefreshBlueprintNetworkCounts()
        => _bpNetworkCounts = App.Network.MemberCount == 0
            ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            : App.Network.OwnerCounts();

    /// <summary>"2 in network" for a blueprint other members own, or null for silence. Null covers
    /// the solo user, a blueprint nobody else has, and a failed store read - the same absent-not-
    /// placeholder rule the rest of the app follows. Deliberately counts OTHER members only: the
    /// row already says whether the player owns it, twice (the accent strip and the tick).</summary>
    private FrameworkElement? NetworkOwnerChip(string blueprintName)
    {
        if (!_bpNetworkCounts.TryGetValue(blueprintName, out var others) || others <= 0) return null;

        var text = new TextBlock
        {
            Text = $"{others} in network", FontFamily = Hud.Font("UiFont"), FontSize = 9.5,
            Foreground = Hud.Br("CyanBrush"), VerticalAlignment = VerticalAlignment.Center,
        };
        return new Border
        {
            Child = text, Padding = new Thickness(6, 1, 6, 1), CornerRadius = new CornerRadius(3),
            BorderThickness = new Thickness(1), BorderBrush = Hud.Br("CyanLineBrush"),
            Background = Hud.Br("CyanDimBrush"), VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            ToolTip = others == 1
                ? "1 other person in your blueprint network owns this."
                : $"{others} other people in your blueprint network own this.",
        };
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
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // network owners (app review G12)
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // marker (tick / pill)

        var strip = new Border { Width = 3, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 1, 11, 1), Background = owned0 ? _ownedGreen : trans };
        Grid.SetColumn(strip, 0); rowGrid.Children.Add(strip);

        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(sp, 1);
        // Display text only: ownership maps, network chips, and click handlers keep bp.Name.
        sp.Children.Add(new TextBlock { Text = ComponentLabels.Library(bp.Name), FontWeight = FontWeights.SemiBold, Foreground = fg, TextTrimming = System.Windows.TextTrimming.CharacterEllipsis });
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
        if (NetworkOwnerChip(bp.Name) is { } netChip) { Grid.SetColumn(netChip, 2); rowGrid.Children.Add(netChip); }
        Grid.SetColumn(marker, 3); rowGrid.Children.Add(marker);

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
            PlayOwnedTick(pill);
        };
        return pill;
    }

    // One-shot scale-in on the toggled ownership pill/toggle (frozen: 0.97 to 1.0, 150ms,
    // settle, origin center-left). Motion Item 8's "owned tick".
    private static void PlayOwnedTick(FrameworkElement row)
    {
        if (Motion.Reduced) return;
        var scale = new System.Windows.Media.ScaleTransform(0.97, 0.97);
        row.RenderTransform = scale;
        row.RenderTransformOrigin = new Point(0, 0.5);
        var tick = new System.Windows.Media.Animation.DoubleAnimation(0.97, 1.0, System.TimeSpan.FromMilliseconds(150))
        { EasingFunction = Motion.Settle };
        scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, tick);
        scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, tick);
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
            PlayOwnedTick(toggle);
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
        int pct   = UiHelpers.PctOf(owned, total);

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

        // Landing renders identically regardless of level (no _bpLevel reference above), so
        // subgroup and family collapse into one "subcategory" tier here - detail (below) owns depth 3.
        int landingDepth = _bpLevel switch
        {
            "root" => 0,
            "category" => 1,
            "subgroup" or "family" => 2,
            _ => -1,   // "search" - not part of the drill-down hierarchy, no slide
        };
        _bpDetailDepth = PlayDrillSlide(BlueprintDetailPanel, _bpDetailDepth, landingDepth);
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
        ExpandNavForSearch();
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
        // The popup sits in a separate visual tree that does not inherit the App-scale transform,
        // so its content is scaled in ApplyUiScale. Match the popup width to the on-screen (scaled)
        // search box so the scaled suggestions are never clipped to the unscaled logical width.
        BlueprintSuggestPopup.Width = BlueprintSearchBox.ActualWidth * UiScaleService.AppScale;
        BlueprintSuggestPopup.IsOpen = true;
    }

    private void BlueprintSuggest_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (BlueprintSuggestList.SelectedItem is System.Windows.Controls.ListBoxItem li && li.Tag is string name)
        {
            _suppressAutocomplete = true;
            _vm.BlueprintSearch = name;
            BlueprintSuggestPopup.IsOpen = false;
            ExpandNavForSearch();
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
        var sp = new StackPanel { Margin = new Thickness(0, 0, 24, 0) };
        sp.Children.Add(new TextBlock { Text = label, FontFamily = mono, FontSize = 9, Foreground = dim });
        sp.Children.Add(new TextBlock { Text = value, FontSize = 14, Foreground = fg, Margin = new Thickness(0, 2, 0, 0) });
        return sp;
    }

    // ── Craft quality forecast: slot matching and row copy ──────────────────
    // A slot without modifiers shapes no stat, so it never earns a quality control; the seed
    // carries such slots on purpose and their ore keeps the plain one-line row.

    /// <summary>Every slot with modifiers that accepts this ore (any of its resource options), in slot order.</summary>
    internal static IReadOnlyList<BlueprintSlot> SlotsForIngredient(NexusApp.Models.Blueprint bp, string resourceName) =>
        bp.Slots
            .Where(s => s.Modifiers.Count > 0
                && s.Resources.Any(r => string.Equals(r, resourceName, StringComparison.OrdinalIgnoreCase)))
            .ToList();

    /// <summary>The first slot with modifiers that accepts this ore, or null.</summary>
    internal static BlueprintSlot? SlotForIngredient(NexusApp.Models.Blueprint bp, string resourceName) =>
        SlotsForIngredient(bp, resourceName).FirstOrDefault();

    /// <summary>"6 cSCU  ·  FRAME + BARREL slots": a quantity before the slot label.</summary>
    internal static string SlotLine(double qty, string unit, IReadOnlyList<BlueprintSlot> slots) =>
        $"{CraftAmount.Format(qty, unit)}  ·  {SlotLabel(slots)}";

    /// <summary>Second line of a CRAFTING view row: "FRAME slot" or "FRAME + BARREL slots".</summary>
    internal static string SlotLabel(IReadOnlyList<BlueprintSlot> slots)
    {
        var names = string.Join(" + ", slots.Select(s => s.Name.ToUpperInvariant()));
        return $"{names} {(slots.Count == 1 ? "slot" : "slots")}";
    }

    /// <summary>The small tier text of a SOURCING row: "Common", or "Common · Frame + Stock" when the ore feeds slots.</summary>
    internal static string TierLine(string tier, IReadOnlyList<BlueprintSlot> slots)
    {
        if (slots.Count == 0) return tier;
        var names = string.Join(" + ", slots.Select(s => s.Name));
        return tier.Length > 0 ? $"{tier} · {names}" : names;
    }

    // ── Two-view detail (library-redesign round 5 design 3, .seg) ──────────
    internal const string SegmentSourcing = "sourcing";
    internal const string SegmentCrafting = "crafting";

    /// <summary>An owned blueprint opens on CRAFTING (the player can craft it now), anything else on SOURCING.</summary>
    internal static string DefaultSegment(bool owned) => owned ? SegmentCrafting : SegmentSourcing;

    /// <summary>The segment bar shows only when there is something to set and something to forecast.</summary>
    internal static bool ShowSegments(bool anyControlled, int forecastRows) => anyControlled && forecastRows > 0;

    /// <summary>"3 ingredients", "1 stat".</summary>
    internal static string CountText(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    // One segment button (mock .seg button): mono 11 label, an 8 px gap, then the count at 10 px.
    // Mouse only, like the preset chips: a press captures, a release inside acts. The press also
    // clears keyboard focus, so a typed quality commits (on blur) before the body swaps. Each
    // button owns its brushes so hover and active ease over Motion.HoverMs.
    private static (Border Button, Action Restyle) SegmentButton(string label, string count, Func<bool> isActive, Action<Border> act)
    {
        var mono = Hud.Font("MonoFont");
        var fill = new System.Windows.Media.SolidColorBrush();
        var line = new System.Windows.Media.SolidColorBrush();
        var labelFg = new System.Windows.Media.SolidColorBrush();
        var countFg = new System.Windows.Media.SolidColorBrush();
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = label, FontFamily = mono, FontSize = 11, Foreground = labelFg, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = count, FontFamily = mono, FontSize = 10, Foreground = countFg, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) });
        var btn = new Border
        {
            Child = row, Padding = new Thickness(12, 6, 12, 6), CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1), BorderBrush = line, Background = fill, Cursor = Cursors.Hand,
        };

        bool hover = false;
        void Restyle(bool animate)
        {
            bool on = isActive();
            // The mock's --amber-faint and --highlight are the same colour, so active and hover share the fill.
            var hl = Hud.Col("HighlightBrush");
            Ease(fill, on || hover ? hl : System.Windows.Media.Color.FromArgb(0, hl.R, hl.G, hl.B), animate);
            Ease(line, Hud.Col(on ? "AccentStrongBrush" : "NavBorderBrush"), animate);
            Ease(labelFg, Hud.Col(on ? "AccentBrush" : hover ? "FgBrush" : "FgDimBrush"), animate);
            Ease(countFg, Hud.Col(on ? "AccentBrush" : "FgDimBrush"), animate);
        }
        void SetHover(bool value)
        {
            if (hover == value) return;
            hover = value;
            Restyle(animate: true);
        }
        static bool Inside(FrameworkElement el, MouseEventArgs e)
        {
            var p = e.GetPosition(el);
            return p.X >= 0 && p.Y >= 0 && p.X <= el.ActualWidth && p.Y <= el.ActualHeight;
        }

        Restyle(animate: false);
        btn.MouseEnter += (_, _) => SetHover(true);
        btn.MouseLeave += (_, _) => SetHover(false);
        // Under capture IsMouseOver stays true off the button, so hover follows a bounds test instead.
        btn.MouseMove += (_, e) => { if (btn.IsMouseCaptured) SetHover(Inside(btn, e)); };
        btn.MouseLeftButtonDown += (_, e) =>
        {
            Keyboard.ClearFocus();
            btn.CaptureMouse();
            e.Handled = true;
        };
        btn.MouseLeftButtonUp += (_, e) =>
        {
            bool wasPressed = btn.IsMouseCaptured;
            bool inside = Inside(btn, e);
            btn.ReleaseMouseCapture();
            SetHover(inside);
            if (!wasPressed) return;
            e.Handled = true;
            if (inside) act(btn);
        };
        return (btn, () => Restyle(animate: true));
    }

    private static void Ease(System.Windows.Media.SolidColorBrush brush, System.Windows.Media.Color to, bool animate)
    {
        if (!animate || Motion.Reduced)
        {
            brush.BeginAnimation(System.Windows.Media.SolidColorBrush.ColorProperty, null);
            brush.Color = to;
            return;
        }
        brush.BeginAnimation(System.Windows.Media.SolidColorBrush.ColorProperty,
            new System.Windows.Media.Animation.ColorAnimation(to, TimeSpan.FromMilliseconds(Motion.HoverMs)) { EasingFunction = Motion.Settle });
        brush.Color = to;   // the base value matches, so clearing the animation later keeps the colour
    }

    // ── Results column collapse (library-redesign round 4: .navBtn, .navRail) ──
    // The toggle collapses the 300 px results column to a 34 px rail so the detail panel takes
    // the width. The detail re-renders through the SizeChanged hook below (HookDetailResize):
    // the column width change resizes BlueprintDetailPanel like a window resize does.
    private const double NavExpandedWidth = 300;
    private const double NavCollapsedWidth = 34;
    private const double NavContentInset = 13;   // the right hairline (1) plus the list's right margin (12)

    internal static double NavColumnWidth(bool collapsed) => collapsed ? NavCollapsedWidth : NavExpandedWidth;

    /// <summary>The rotated rail caption for the current list: "RIFLE · 7".</summary>
    internal static string RailLabel(string listName, int count) => $"{listName.ToUpperInvariant()} · {count}";

    private bool _bpNavCollapsed;
    private string _bpRailText = "";
    private TextBlock? _bpRailLabel;

    // Animation target for the nav column; a GridLength cannot take a DoubleAnimation directly.
    private static readonly DependencyProperty NavColumnWidthAnimProperty = DependencyProperty.Register(
        "NavColumnWidthAnim", typeof(double), typeof(MainWindow),
        new PropertyMetadata(NavExpandedWidth, (o, e) =>
        {
            if (o is MainWindow w) w.BlueprintNavColumn.Width = new GridLength((double)e.NewValue);
        }));

    // Same anatomy as the quality control's graph button: 26x22, NavBorder 1 px, radius 4, a
    // 12 px chevron (stroke 1.8, round caps); hover = AccentStrong border, Highlight fill, amber glyph.
    private Button NavToggleButton(bool collapsed)
    {
        var glyph = new System.Windows.Shapes.Polyline
        {
            Points = System.Windows.Media.PointCollection.Parse(collapsed ? "4,2 8,6 4,10" : "8,2 4,6 8,10"),
            Stroke = (System.Windows.Media.Brush)FindResource("FgDimBrush"), StrokeThickness = 1.8,
            StrokeStartLineCap = System.Windows.Media.PenLineCap.Round,
            StrokeEndLineCap = System.Windows.Media.PenLineCap.Round,
            StrokeLineJoin = System.Windows.Media.PenLineJoin.Round,
        };
        var box = new Canvas { Width = 12, Height = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        box.Children.Add(glyph);
        var face = new Border
        {
            Width = 26, Height = 22, CornerRadius = new CornerRadius(4),
            BorderBrush = (System.Windows.Media.Brush)FindResource("NavBorderBrush"), BorderThickness = new Thickness(1),
            Background = System.Windows.Media.Brushes.Transparent,
            Child = box,
        };
        var btn = new Button
        {
            Template = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) },
            Content = face, Cursor = Cursors.Hand, Focusable = false,
            VerticalAlignment = VerticalAlignment.Top,
            ToolTip = collapsed ? "Show results" : "Hide results",
        };
        btn.MouseEnter += (_, _) =>
        {
            face.BorderBrush = (System.Windows.Media.Brush)FindResource("AccentStrongBrush");
            face.Background = (System.Windows.Media.Brush)FindResource("HighlightBrush");
            glyph.Stroke = (System.Windows.Media.Brush)FindResource("AccentBrush");
        };
        btn.MouseLeave += (_, _) =>
        {
            face.BorderBrush = (System.Windows.Media.Brush)FindResource("NavBorderBrush");
            face.Background = System.Windows.Media.Brushes.Transparent;
            glyph.Stroke = (System.Windows.Media.Brush)FindResource("FgDimBrush");
        };
        btn.Click += (_, _) => SetLibraryNavCollapsed(!_bpNavCollapsed, btn);
        return btn;
    }

    // The breadcrumb (or nothing, at the root) on the left, the collapse toggle on the right.
    // The host's current content is the breadcrumb; it moves into the wrapper grid. The host
    // owns it as a logical child, so it is released first or the grid refuses it.
    internal static UIElement WrapCrumb(ContentControl host, Func<UIElement> makeToggle)
    {
        var crumb = host.Content as UIElement;
        host.Content = null;
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (crumb != null) g.Children.Add(crumb);
        var btn = makeToggle();
        if (crumb == null && btn is FrameworkElement fe) fe.Margin = new Thickness(0, 0, 0, 8);
        Grid.SetColumn(btn, 1);
        g.Children.Add(btn);
        host.Content = g;
        return g;
    }

    private void EnsureNavRail()
    {
        if (_bpRailLabel != null) return;
        var btn = NavToggleButton(collapsed: true);
        btn.HorizontalAlignment = HorizontalAlignment.Left;
        _bpRailLabel = new TextBlock
        {
            FontFamily = (System.Windows.Media.FontFamily)FindResource("MonoFont"), FontSize = 10,
            Foreground = (System.Windows.Media.Brush)FindResource("FgDimBrush"),
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(6, 10, 0, 0),
            LayoutTransform = new System.Windows.Media.RotateTransform(-90),   // reads bottom to top
        };
        BlueprintNavRail.Children.Add(btn);
        BlueprintNavRail.Children.Add(_bpRailLabel);
    }

    private void SetLibraryNavCollapsed(bool collapsed, DependencyObject source)
    {
        if (collapsed == _bpNavCollapsed) return;
        InteractionLog.Toggle(collapsed ? "library nav collapsed" : "library nav expanded", source);
        _bpNavCollapsed = collapsed;
        App.Settings.Current.LibraryNavCollapsed = collapsed;
        App.Settings.Save();
        ApplyNavCollapse(animate: !Motion.Reduced);
    }

    // Only the column width animates. The list keeps its expanded width while the column moves,
    // so the border clips it instead of reflowing it every frame; the rail shows once collapsed.
    private void ApplyNavCollapse(bool animate)
    {
        EnsureNavRail();
        double target = NavColumnWidth(_bpNavCollapsed);
        if (!animate)
        {
            BeginAnimation(NavColumnWidthAnimProperty, null);
            SetValue(NavColumnWidthAnimProperty, target);
            ShowNavState();
            return;
        }
        double from = (double)GetValue(NavColumnWidthAnimProperty);   // the live value, if mid-slide
        BlueprintNavRail.Visibility = Visibility.Collapsed;
        BlueprintNavContent.Visibility = Visibility.Visible;
        BlueprintNavContent.HorizontalAlignment = HorizontalAlignment.Left;
        BlueprintNavContent.Width = NavExpandedWidth - NavContentInset;
        var slide = new System.Windows.Media.Animation.DoubleAnimation(from, target, TimeSpan.FromMilliseconds(Motion.SlideMs))
        {
            EasingFunction = Motion.Settle,
            FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop,
        };
        bool endsCollapsed = _bpNavCollapsed;
        slide.Completed += (_, _) => { if (endsCollapsed == _bpNavCollapsed) ShowNavState(); };
        BeginAnimation(NavColumnWidthAnimProperty, slide);
        SetValue(NavColumnWidthAnimProperty, target);   // base value; the slide holds the effective value until it stops
    }

    // A search fills the results list, so a collapsed list opens first. The width snaps (no
    // slide) so the results render into the expanded column; clearing a search never collapses.
    private void ExpandNavForSearch()
    {
        if (!_bpNavCollapsed) return;
        InteractionLog.Toggle("library nav expanded (search)", BlueprintSearchBox);
        _bpNavCollapsed = false;
        App.Settings.Current.LibraryNavCollapsed = false;
        App.Settings.Save();
        ApplyNavCollapse(animate: false);
    }

    private void ShowNavState()
    {
        BlueprintNavContent.Width = double.NaN;
        BlueprintNavContent.HorizontalAlignment = HorizontalAlignment.Stretch;
        BlueprintNavContent.Visibility = _bpNavCollapsed ? Visibility.Collapsed : Visibility.Visible;
        BlueprintNavRail.Visibility = _bpNavCollapsed ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Width-aware detail layout (library-redesign round 3 container rules) ──
    // The detail panel is half the page, and its two-column split halves it again. Fixed
    // columns that fit a 1340 px help shot leave a 400 px column with no room for the name
    // block, and a star column at zero width wraps its text one glyph per line.
    internal enum DetailLayout { Wide, Stacked }
    private const double DetailSplitMin = 700;     // below this the split becomes one column
    private const double RowStackBelow = 560;      // below this a materials row puts its control under the name
    private const double RowNameMin = 160;         // the name block never gets less than this
    private const double RowControlWidth = 250;    // the control column in the one-row shape
    private const double DetailSplitGap = 24;

    internal static DetailLayout LayoutFor(double detailWidth) =>
        detailWidth >= DetailSplitMin ? DetailLayout.Wide : DetailLayout.Stacked;

    internal static bool StackRowControl(double rowWidth) => rowWidth < RowStackBelow;

    /// <summary>
    /// The column width a materials row actually gets: half the split when Wide (both views
    /// split 1fr 24px 1fr), the whole panel when Stacked.
    /// </summary>
    internal static double RowWidthFor(double detailWidth) =>
        LayoutFor(detailWidth) == DetailLayout.Wide ? (detailWidth - DetailSplitGap) / 2 : detailWidth;

    // One-row shape only when the fixed control column still leaves the name block its minimum.
    private static bool RowsStacked(double rowWidth) =>
        StackRowControl(rowWidth) || rowWidth < RowControlWidth + RowNameMin + 60;

    private DetailLayout _detailRenderedLayout;
    private bool _detailRenderedRowsStacked;
    private bool _detailSizeHooked;
    private System.Windows.Threading.DispatcherTimer? _detailResizeTimer;

    private double DetailWidth()
    {
        double w = BlueprintDetailPanel.ActualWidth;
        if (w <= 0 && BlueprintDetailPanel.Parent is ScrollViewer sv) w = sv.ViewportWidth;
        return w;
    }

    // Re-render the open detail only when a resize crosses a layout threshold, debounced so a
    // drag-resize does not rebuild the panel on every pixel.
    private void HookDetailResize()
    {
        if (_detailSizeHooked) return;
        _detailSizeHooked = true;
        _detailResizeTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _detailResizeTimer.Tick += (_, _) =>
        {
            _detailResizeTimer.Stop();
            if (_detailBpName is { } name && DetailLayoutChanged()) RenderBlueprintDetail(name);
        };
        BlueprintDetailPanel.SizeChanged += (_, e) =>
        {
            if (!e.WidthChanged || _detailBpName == null || !DetailLayoutChanged()) return;
            _detailResizeTimer.Stop();
            // A first layout pass (no width before) is not a drag: correct it at once, after layout.
            if (e.PreviousSize.Width <= 0)
                Dispatcher.BeginInvoke(() => { if (_detailBpName is { } n && DetailLayoutChanged()) RenderBlueprintDetail(n); });
            else
                _detailResizeTimer.Start();
        };
    }

    private bool DetailLayoutChanged()
    {
        double w = DetailWidth();
        return LayoutFor(w) != _detailRenderedLayout || RowsStacked(RowWidthFor(w)) != _detailRenderedRowsStacked;
    }

    internal static string CraftTimeText(int seconds) =>
        seconds < 60 ? $"{seconds} s" : $"{(int)Math.Round(seconds / 60.0, MidpointRounding.AwayFromZero)} min";

    // A CRAFTING view row for an ore that feeds one or more slots with modifiers (library-redesign
    // round 5 design 3, lean .bomQ): the name, the slot it fills, a third line naming the stats it
    // shapes, then the 250 px quality control. No quantity and no add button: those live in the
    // SOURCING view. The Codex cross-link sits on the name block so a press on the control never
    // navigates away.
    private Border BuildForecastBomRow(
        NexusApp.Models.Blueprint bp, BlueprintIngredient ing, IReadOnlyList<BlueprintSlot> slots,
        System.Windows.Media.Brush rarityBrush, string tier, IReadOnlyList<StatForecast> forecast,
        System.Windows.Media.Brush line, System.Windows.Media.Brush softFg,
        IReadOnlyList<QualityBand> bands, bool stacked, Action<string, QualityControl> wire)
    {
        var fgB = (System.Windows.Media.Brush)FindResource("FgBrush");
        var dimB = (System.Windows.Media.Brush)FindResource("FgDimBrush");
        var monoFont = (System.Windows.Media.FontFamily)FindResource("MonoFont");
        var uiFont = (System.Windows.Media.FontFamily)FindResource("UiFont");

        // Stacked (mock @container 560): dot | name on row one, the control under the name on
        // row two. One-row: dot | name | 250. The name column carries a MinWidth in both shapes
        // so it can never reach zero.
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                                          // dot
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = RowNameMin }); // name block
        if (!stacked)
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(RowControlWidth) });                      // quality control
        if (stacked)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        var dot = new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(2), Background = rarityBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 11, 0) };
        Grid.SetColumn(dot, 0); g.Children.Add(dot);

        var who = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, stacked ? 0 : 14, 0), Background = System.Windows.Media.Brushes.Transparent };
        var nameLine = new StackPanel { Orientation = Orientation.Horizontal };
        nameLine.Children.Add(new TextBlock { Text = ing.ResourceName, FontSize = 13.5, Foreground = rarityBrush, VerticalAlignment = VerticalAlignment.Center });
        if (tier.Length > 0)
            nameLine.Children.Add(new TextBlock { Text = "   " + tier, FontSize = 10, Foreground = dimB, VerticalAlignment = VerticalAlignment.Center });
        who.Children.Add(nameLine);

        // Mock lean meta: <b>FRAME slot</b>, no quantity before it.
        who.Children.Add(new TextBlock
        {
            Text = SlotLabel(slots), FontFamily = monoFont, FontSize = 11, Foreground = fgB, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap,
        });

        // Stat names come from the forecast rows so the Mining Laser name override reads the same here and in the bars.
        var slotNames = slots.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var shaped = forecast.Where(r => r.Slots.Any(slotNames.Contains)).Select(r => r.Name).Distinct().ToList();
        if (shaped.Count > 0)
        {
            var mods = new TextBlock { FontFamily = uiFont, FontSize = 10.5, Foreground = dimB, Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap };
            mods.Inlines.Add(new System.Windows.Documents.Run("Shapes "));
            for (int k = 0; k < shaped.Count; k++)
            {
                mods.Inlines.Add(new System.Windows.Documents.Run(shaped[k]) { Foreground = softFg });
                if (k < shaped.Count - 1) mods.Inlines.Add(new System.Windows.Documents.Run(", "));
            }
            who.Children.Add(mods);
        }
        Grid.SetColumn(who, 1); g.Children.Add(who);

        var ore = ing.ResourceName;
        var ctl = new QualityControl(ore, bands, App.Settings.Current.HeldQualityOf(ore))
        {
            VerticalAlignment = VerticalAlignment.Center,
        };
        // The bands popup hooks in here; until then the request is logged so the click is traceable.
        ctl.GraphRequested += () => InteractionLog.Click($"forecast bands {ore}", ctl);
        wire(ore, ctl);
        if (stacked)
        {
            ctl.Margin = new Thickness(0, 6, 0, 0);   // mock row-gap 6
            Grid.SetRow(ctl, 1); Grid.SetColumn(ctl, 1);
        }
        else Grid.SetColumn(ctl, 2);
        g.Children.Add(ctl);

        var rowBorder = new Border { Child = g, Background = System.Windows.Media.Brushes.Transparent, Padding = new Thickness(2, 9, 2, 9), BorderBrush = line, BorderThickness = new Thickness(0, 0, 0, 1) };

        if (_vm.AllResources.Any(r => r.Name.Equals(ore, StringComparison.OrdinalIgnoreCase)))
        {
            who.Cursor = System.Windows.Input.Cursors.Hand;
            who.ToolTip = "Open in Mining Codex";
            var hov = (System.Windows.Media.Brush)FindResource("HighlightBrush");
            who.MouseEnter += (s, _) => rowBorder.Background = hov;
            who.MouseLeave += (s, _) => rowBorder.Background = System.Windows.Media.Brushes.Transparent;
            who.MouseLeftButtonDown += (s, _) => NavigateToResource(ore);
        }
        return rowBorder;
    }

    private void ShowBlueprintDetail(NexusApp.Models.Blueprint selected)
    {
        InteractionLog.Nav($"Blueprint Library: open {selected.Name}");
        _detailSegment = null;   // a new selection opens on its default view
        RenderBlueprintDetail(selected.Name);
    }

    // Body split from ShowBlueprintDetail so a component-label change can repaint the open
    // detail without logging a navigation that did not happen.
    private void RenderBlueprintDetail(string selectedName)
    {
        var full = App.Data.GetBlueprintFull(selectedName);
        BlueprintDetailPanel.Children.Clear();
        _detailBpName = null;
        _detailOwnedToggle = null;
        if (full == null) return;
        _detailBpName = full.Name;

        HookDetailResize();
        double detailWidth = DetailWidth();
        var detailLayout = LayoutFor(detailWidth);
        bool anyControlled = full.Ingredients.Any(i => SlotsForIngredient(full, i.ResourceName).Count > 0);
        double rowWidth = RowWidthFor(detailWidth);
        bool rowsStacked = RowsStacked(rowWidth);
        _detailRenderedLayout = detailLayout;
        _detailRenderedRowsStacked = rowsStacked;
        bool heroStacked = detailWidth < RowStackBelow;

        // ── Schematic hero: drafting-sheet header (round 5 .heroC) ─────────────
        var heroAccent = (System.Windows.Media.Brush)FindResource("AccentBrush");
        var fgB      = (System.Windows.Media.Brush)FindResource("FgBrush");
        var dimB     = (System.Windows.Media.Brush)FindResource("FgDimBrush");
        var monoFont = (System.Windows.Media.FontFamily)FindResource("MonoFont");
        var headFont = (System.Windows.Media.FontFamily)System.Windows.Application.Current.FindResource("HeadFont");
        var navBorder0 = (System.Windows.Media.Brush)FindResource("NavBorderBrush");

        var eyebrow = full.SubCategory is { Length: > 0 }
            ? $"{full.Category} · {full.SubCategory}".ToUpperInvariant()
            : full.Category.ToUpperInvariant();
        double totalScu = full.Ingredients.Sum(i => i.Quantity);
        var heroContent = new StackPanel();

        // Name row: eyebrow + name on the left, the actions at the right, aligned top.
        // Narrow panel (mock @container 560): the actions drop under the name block, left-aligned.
        var heroTop = new Grid();
        heroTop.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heroTop.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var heroTitle = new StackPanel();
        heroTitle.Children.Add(new TextBlock { Text = eyebrow, FontFamily = monoFont, FontSize = 11, Foreground = heroAccent });
        heroTitle.Children.Add(new TextBlock
        {
            Text = ComponentLabels.Library(full.Name), FontFamily = headFont, FontSize = 22, FontWeight = FontWeights.SemiBold,
            Foreground = fgB, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap,
        });
        heroTop.Children.Add(heroTitle);

        // Mock .heroC .btn.outline / .btn.accent.sm: 32 px tall, padding 5 12, 12.5 px.
        var heroActions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        var ownedToggle = OwnedToggle(full.Name);
        ownedToggle.Height = 32;
        ownedToggle.Padding = new Thickness(12, 5, 12, 5);
        if (ownedToggle.Child is TextBlock ownedLabel) ownedLabel.FontSize = 12.5;
        heroActions.Children.Add(ownedToggle);
        var heroAddBtn = new Button
        {
            Content = "+ Add all to cart", Style = (Style)FindResource("AccentButton"),
            Padding = new Thickness(12, 5, 12, 5), FontSize = 12.5, Height = 32, VerticalAlignment = VerticalAlignment.Center,
        };
        heroAddBtn.Click += (s, e) => { foreach (var i in full.Ingredients) _vm.AddToShoppingCommand.Execute(i); };
        heroActions.Children.Add(heroAddBtn);
        if (heroStacked)
        {
            heroTop.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            heroTop.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            heroActions.HorizontalAlignment = HorizontalAlignment.Left;
            heroActions.Margin = new Thickness(0, 10, 0, 0);
            Grid.SetRow(heroActions, 1); Grid.SetColumnSpan(heroActions, 2);
        }
        else
        {
            heroActions.Margin = new Thickness(16, 0, 0, 0);   // mock .heroC .top gap 16
            Grid.SetColumn(heroActions, 1);
        }
        heroTop.Children.Add(heroActions);
        heroContent.Children.Add(heroTop);
        heroContent.Children.Add(new Border { Height = 1, Background = heroAccent, Opacity = 0.6, Margin = new Thickness(0, 10, 0, 0) });

        var specs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        specs.Children.Add(HeroSpec("INGREDIENTS", full.Ingredients.Count.ToString(), fgB, dimB, monoFont));
        specs.Children.Add(HeroSpec("TOTAL COST", CraftAmount.Format(totalScu, "SCU"), fgB, dimB, monoFont));
        // Network ownership as a third hero spec (app review G12). The detail panel is where the
        // player decides whether they need to go get this blueprint, and until now it could not say
        // that somebody they already share with has it. Counts are read fresh here rather than from
        // the nav render's batch: one detail panel is one lookup, and the panel can be reached
        // without a nav render having happened first.
        RefreshBlueprintNetworkCounts();
        if (_bpNetworkCounts.TryGetValue(full.Name, out var netOwners) && netOwners > 0)
            specs.Children.Add(HeroSpec("IN NETWORK", netOwners.ToString(), fgB, dimB, monoFont));
        if (full.CraftSeconds is int craftSeconds)
            specs.Children.Add(HeroSpec("CRAFT TIME", CraftTimeText(craftSeconds), fgB, dimB, monoFont));
        heroContent.Children.Add(specs);

        // ── Craft quality forecast ─────────────────────────────────────────────
        // Held quality is per ore and shared by every blueprint that uses it. The engine asks
        // by slot name, so each slot reads the quality of the ore the bill of materials lists
        // for it (the first of its options when none is listed).
        var craftStats = App.Data.GetCraftStats();
        var bandCache = new Dictionary<string, IReadOnlyList<QualityBand>>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<QualityBand> BandsOf(string resource) =>
            bandCache.TryGetValue(resource, out var cached) ? cached : bandCache[resource] = App.Data.GetQualityBands(resource);
        var slotOre = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var slot in full.Slots.Where(s => s.Modifiers.Count > 0))
        {
            var ore = slot.Resources.FirstOrDefault(r => full.Ingredients.Any(i => i.ResourceName.Equals(r, StringComparison.OrdinalIgnoreCase)))
                      ?? slot.Resources.FirstOrDefault();
            if (ore != null) slotOre.TryAdd(slot.Name, ore);
        }
        // Filled only when the CRAFTING view is built; until then every slot reads its saved quality.
        var qualityControls = new List<(string Ore, QualityControl Control)>();
        // A control's live value wins over the saved one, so a drag preview reads the knob, not the file.
        int HeldOfSlot(string slotName)
        {
            if (!slotOre.TryGetValue(slotName, out var ore)) return CraftForecast.StockQuality;
            foreach (var (o, c) in qualityControls)
                if (o.Equals(ore, StringComparison.OrdinalIgnoreCase)) return c.Value;
            return App.Settings.Current.HeldQualityOf(ore);
        }

        bool forecastBulk = false;   // a preset moves every control: save and refresh once, not per ore
        IReadOnlyList<StatForecast> forecast = [];
        CraftStatRows? statRows = null;            // built with the CRAFTING view
        CraftForecastStrip? presetStrip = null;    // built with the CRAFTING view
        void RefreshForecast(bool fade = true)
        {
            forecast = CraftForecast.Forecast(full, HeldOfSlot, BandsOf, craftStats);
            statRows?.Update(forecast, animate: fade);
            presetStrip?.Update(forecast, qualityControls.Select(c => c.Control.Value).ToList(), fade);
        }
        RefreshForecast();
        // No forecast rows means nothing honest to show: the SOURCING body alone, no segment bar.
        bool segmented = ShowSegments(anyControlled, forecast.Count);

        var heroRoot = new Grid();
        heroRoot.Children.Add(heroContent);

        // Chamfered HUD hero panel with amber corner brackets (replaces the rounded drafting card).
        var heroCard = Hud.Panel(heroRoot, chamfer: HeroChamfer, brackets: true,
            bg: (System.Windows.Media.Brush)FindResource("Bg2NavBrush"),
            border: (System.Windows.Media.Brush)FindResource("NavBorderBrush"),
            padding: new Thickness(20, 12, 18, 14));
        heroCard.Margin = new Thickness(0, 0, 0, 8);
        BlueprintDetailPanel.Children.Add(heroCard);

        // ── Shared pieces of the two views ─────────────────────────────────────
        // Split rule (mock @container 700): Wide is 1fr 24px 1fr, Stacked is two rows.
        (Grid Split, StackPanel Left, StackPanel Right) NewSplit()
        {
            var split = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            var left = new StackPanel();
            var right = new StackPanel();
            if (detailLayout == DetailLayout.Wide)
            {
                split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(DetailSplitGap) });
                split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                Grid.SetColumn(left, 0);
                Grid.SetColumn(right, 2);
            }
            else
            {
                split.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                split.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                right.Margin = new Thickness(0, 18, 0, 0);
                Grid.SetRow(right, 1);
            }
            split.Children.Add(left);
            split.Children.Add(right);
            return (split, left, right);
        }

        // Mock .secHead: mono 11, label left, note right, hairline under, padding-bottom 8, margin top 0 2.
        Border SecHead(string label, string note, System.Windows.Media.Brush color, double top)
        {
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(new TextBlock { Text = label, FontFamily = monoFont, FontSize = 11, Foreground = color });
            var noteText = new TextBlock { Text = note, FontFamily = monoFont, FontSize = 11, Foreground = color, Margin = new Thickness(10, 0, 0, 0) };
            Grid.SetColumn(noteText, 1); head.Children.Add(noteText);
            return new Border { Child = head, BorderBrush = navBorder0, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 0, 0, 8), Margin = new Thickness(0, top, 0, 2) };
        }

        // The mock's lab-fg (#B9C4D0) has no app key; FgBrush at 0.75 reads the same on the panel.
        var softFg = Hud.WithOpacity(fgB, 0.75);

        string TierOf(BlueprintIngredient ing, out System.Windows.Media.Brush rarityBrush)
        {
            var rarity = _vm.AllResources.FirstOrDefault(r => r.Name == ing.ResourceName)?.Rarity ?? "common";
            rarityBrush = RarityBrush(rarity);
            return rarity.Length > 0 ? char.ToUpper(rarity[0]) + rarity.Substring(1) : "";
        }

        // ── SOURCING body: unlock + bill of materials | where to mine ──────────
        FrameworkElement BuildSourcingBody()
        {
            var (split, left, right) = NewSplit();
            BuildUnlock(left);
            left.Children.Add(SecHead("BILL OF MATERIALS", "QTY", dimB, 14));

            foreach (var ing in full.Ingredients)
            {
                var ingCopy = ing;
                var tier = TierLine(TierOf(ing, out var rb), SlotsForIngredient(full, ing.ResourceName));

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

                left.Children.Add(rowBorder);
            }

            // Total footer
            var totalGrid = new Grid { Margin = new Thickness(2, 11, 2, 0) };
            totalGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            totalGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            totalGrid.Children.Add(new TextBlock { Text = "TOTAL", FontFamily = monoFont, FontSize = 11, Foreground = dimB, VerticalAlignment = VerticalAlignment.Center });
            var totalVal = new TextBlock { Text = CraftAmount.Format(totalScu, "SCU"), FontFamily = monoFont, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = heroAccent, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(totalVal, 1); totalGrid.Children.Add(totalVal);
            left.Children.Add(new Border { Child = totalGrid, BorderBrush = navBorder0, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 10, 0, 0), Margin = new Thickness(0, 2, 0, 0) });

            BuildWhereToMine(right);
            return split;
        }

        // ── CRAFTING body: quality you hold | forecast vs stock craft ──────────
        FrameworkElement BuildCraftingBody()
        {
            var (split, left, right) = NewSplit();
            left.Children.Add(SecHead("QUALITY YOU HOLD", "Q 0 - 1000", heroAccent, 8));
            foreach (var ing in full.Ingredients)
            {
                var ingSlots = SlotsForIngredient(full, ing.ResourceName);
                if (ingSlots.Count == 0) continue;
                var tier = TierOf(ing, out var rb);
                left.Children.Add(BuildForecastBomRow(full, ing, ingSlots, rb, tier, forecast, navBorder0, softFg, BandsOf(ing.ResourceName), rowsStacked, (ore, ctl) =>
                {
                    qualityControls.Add((ore, ctl));
                    // Preview only: the bars follow the knob; save and log wait for the release.
                    ctl.ValueChanging += _ => RefreshForecast(fade: false);
                    ctl.QualityChanged += (q, source) =>
                    {
                        App.Settings.Current.SetHeldQuality(ore, q);
                        Logger.Info($"[UI] forecast q {full.Name} {ore} {q} {source}");
                        if (forecastBulk) return;
                        App.Settings.Save();
                        RefreshForecast();
                    };
                }));
            }

            presetStrip = new CraftForecastStrip(presetsOnly: true);
            presetStrip.PresetChosen += v =>
            {
                forecastBulk = true;
                try { foreach (var (_, c) in qualityControls) c.SetValue(v, "preset"); }
                finally { forecastBulk = false; }
                App.Settings.Save();
                RefreshForecast();
            };
            left.Children.Add(presetStrip);

            right.Children.Add(SecHead("FORECAST vs STOCK CRAFT", CountText(forecast.Count, "stat"), heroAccent, 8));
            statRows = new CraftStatRows();
            right.Children.Add(statRows);

            // Every control now exists: fill the bars and light the matching preset, without motion.
            RefreshForecast(fade: false);
            return split;
        }

        // ── Segment bar (mock .seg) and the body host ──────────────────────────
        // The host swaps one body for the other; the hero is never rebuilt by a switch.
        var bodyHost = new Grid();
        FrameworkElement? sourcingBody = null, craftingBody = null, shownBody = null;
        void ShowBody(string key, bool animate)
        {
            var body = key == SegmentCrafting
                ? craftingBody ??= BuildCraftingBody()
                : sourcingBody ??= BuildSourcingBody();
            var old = shownBody;
            if (ReferenceEquals(old, body)) return;
            shownBody = body;
            bool move = animate && !Motion.Reduced;
            var dur = TimeSpan.FromMilliseconds(Motion.QuickRevealMs);

            // A body still fading out is already a child: take it back rather than add it twice.
            // A fresh body starts transparent so the fade-in below has somewhere to start from;
            // a body taken back mid-fade keeps its current opacity and the fade-in continues from there.
            if (!bodyHost.Children.Contains(body))
            {
                if (move) body.Opacity = 0;
                bodyHost.Children.Add(body);
            }
            body.IsHitTestVisible = true;
            if (old != null)
            {
                old.IsHitTestVisible = false;
                if (move)
                {
                    var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(0, dur) { EasingFunction = Motion.Settle };
                    fadeOut.Completed += (_, _) =>
                    {
                        if (ReferenceEquals(shownBody, old)) return;
                        old.BeginAnimation(UIElement.OpacityProperty, null);
                        bodyHost.Children.Remove(old);
                    };
                    old.BeginAnimation(UIElement.OpacityProperty, fadeOut);
                }
                else
                {
                    old.BeginAnimation(UIElement.OpacityProperty, null);
                    bodyHost.Children.Remove(old);
                }
            }
            if (move)
            {
                // Mock: opacity 0 to 1, y 6 to 0, settle ease.
                var rise = new System.Windows.Media.TranslateTransform(0, 6);
                body.RenderTransform = rise;
                body.BeginAnimation(UIElement.OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(1, dur) { EasingFunction = Motion.Settle });
                rise.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, new System.Windows.Media.Animation.DoubleAnimation(6, 0, dur) { EasingFunction = Motion.Settle });
            }
            else
            {
                body.BeginAnimation(UIElement.OpacityProperty, null);
                body.Opacity = 1;
                body.RenderTransform = null;
            }
        }

        string shownSegment = SegmentSourcing;
        if (segmented)
        {
            // Per selection, not persisted: a width-driven re-render keeps the view, a new blueprint resets it.
            if (_detailSegment == null || !string.Equals(_detailSegmentBp, full.Name, StringComparison.OrdinalIgnoreCase))
                _detailSegment = DefaultSegment(App.Settings.IsBlueprintOwned(full.Name));
            _detailSegmentBp = full.Name;
            shownSegment = _detailSegment;

            var segRow = new StackPanel { Orientation = Orientation.Horizontal };
            var restyles = new List<Action>();
            void AddSegment(string key, string label, string count)
            {
                var (btn, restyle) = SegmentButton(label, count, () => _detailSegment == key, source =>
                {
                    if (_detailSegment == key) return;
                    InteractionLog.Click($"library view {key} {full.Name}", source);
                    _detailSegment = key;
                    foreach (var r in restyles) r();
                    ShowBody(key, animate: true);
                });
                btn.Margin = new Thickness(segRow.Children.Count == 0 ? 0 : 6, 0, 0, 0);
                restyles.Add(restyle);
                segRow.Children.Add(btn);
            }
            AddSegment(SegmentSourcing, "SOURCING", CountText(full.Ingredients.Count, "ingredient"));
            AddSegment(SegmentCrafting, "CRAFTING", CountText(forecast.Count, "stat"));
            BlueprintDetailPanel.Children.Add(new Border
            {
                Child = segRow, Margin = new Thickness(0, 6, 0, 0), Padding = new Thickness(0, 0, 0, 10),
                BorderBrush = navBorder0, BorderThickness = new Thickness(0, 0, 0, 1),
            });
            bodyHost.Margin = new Thickness(0, 8, 0, 0);   // mock .detail gap 8 under the bar
        }
        BlueprintDetailPanel.Children.Add(bodyHost);
        ShowBody(shownSegment, animate: false);

        // The shipped unlock block, now the top of the SOURCING left column.
        void BuildUnlock(System.Windows.Controls.Panel host)
        {
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
        }

        // The shipped WHERE TO MINE block, now the SOURCING right column.
        void BuildWhereToMine(System.Windows.Controls.Panel host)
        {
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
                host.Children.Add(new TextBlock
                {
                    Text = $"WHERE TO MINE  ·  {rankedLocations.Count} location{(rankedLocations.Count == 1 ? "" : "s")}",
                    FontSize = 10, FontWeight = FontWeights.Bold,
                    Foreground = (System.Windows.Media.Brush)FindResource("FgDimBrush"),
                    Margin = new Thickness(0, 0, 0, 6),
                });

                // ── Byproduct sourcing note (issue #12) ──────────────────────────
                // Ingredients like Aslarite are collected inside host rocks, not as dedicated
                // deposits at each ranked location. This reuses the same datamined found-in rows
                // the Mining Codex shows; probability and variants are intentionally omitted.
                var byproductOres = new List<(string Name, string Rarity, List<NexusApp.Models.FoundInSource> Sources)>();
                foreach (var ing in full.Ingredients)
                {
                    var sources = App.Data.GetFoundInForResource(ing.ResourceName);
                    if (sources.Count == 0) continue;
                    var rarity = _vm.AllResources.FirstOrDefault(r => r.Name == ing.ResourceName)?.Rarity ?? "common";
                    byproductOres.Add((ing.ResourceName, rarity, sources));
                }

                if (byproductOres.Count > 0)
                {
                    var noteFg   = (System.Windows.Media.Brush)FindResource("FgBrush");
                    var noteCyan = (System.Windows.Media.Brush)FindResource("CyanBrush");
                    var bandTrackBrush = BrushFromHex("#147FE9E0");   // faint cyan track (line .078)
                    var bandFillBrush = new System.Windows.Media.LinearGradientBrush(
                        System.Windows.Media.Color.FromArgb(0x8C, 0x7F, 0xE9, 0xE0),   // cyan .55
                        System.Windows.Media.Color.FromRgb(0x7F, 0xE9, 0xE0),          // cyan
                        0);   // 0deg = left-to-right, matching the mock's 90deg gradient

                    var noteInner = new StackPanel { Margin = new Thickness(13, 11, 13, 9) };
                    noteInner.Children.Add(new TextBlock
                    {
                        Text = "BYPRODUCT SOURCING",
                        FontSize = 9, FontWeight = FontWeights.Bold,
                        Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush"),
                        Margin = new Thickness(0, 0, 0, 4),
                    });
                    noteInner.Children.Add(new TextBlock
                    {
                        Text = "Host share per rock. Longer bar means a richer cut when you scan that rock.",
                        FontSize = 11, Foreground = dimB, TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 0, 0, 10),
                    });

                    // Section C band-bar rows: rarity dot + ore name, then one bar per composition
                    // band (richest band first, filling the track). Bar width scales each band's
                    // upper bound against the ore's richest band; label reads "<hosts> up to <max>%".
                    const double trackW = 104, trackInnerW = 102;   // 1px border each side
                    bool firstBandRow = true;
                    foreach (var (oreName, oreRarity, sources) in byproductOres)
                    {
                        var chd = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
                        chd.Children.Add(new Border
                        {
                            Width = 9, Height = 9, CornerRadius = new CornerRadius(3),
                            Background = RarityBrush(oreRarity), VerticalAlignment = VerticalAlignment.Center,
                            Margin = new Thickness(0, 0, 9, 0),
                        });
                        chd.Children.Add(new TextBlock
                        {
                            Text = oreName, FontFamily = headFont, FontWeight = FontWeights.SemiBold,
                            FontSize = 13, Foreground = noteFg, VerticalAlignment = VerticalAlignment.Center,
                        });

                        var groups = ByproductNote.Groups(sources);
                        double oreMax = groups.Count > 0 ? groups.Max(x => x.Max) : 0;
                        var ordered = groups.OrderByDescending(x => x.Max).ToList();   // richest band on top

                        var bands = new StackPanel { Margin = new Thickness(18, 0, 0, 0) };   // indent past the dot
                        for (int bi = 0; bi < ordered.Count; bi++)
                        {
                            var grp = ordered[bi];
                            double frac = ByproductNote.BarFraction(grp.Max, oreMax);

                            var fill = new Border
                            {
                                HorizontalAlignment = HorizontalAlignment.Left,
                                Width = trackInnerW * frac, Background = bandFillBrush,
                            };
                            var track = new Border
                            {
                                Width = trackW, Height = 9, CornerRadius = new CornerRadius(3),
                                Background = bandTrackBrush, BorderBrush = navBorder0,
                                BorderThickness = new Thickness(1), ClipToBounds = true,
                                VerticalAlignment = VerticalAlignment.Center, Child = fill,
                            };

                            var label = new TextBlock
                            {
                                FontFamily = monoFont, FontSize = 10.5, Foreground = dimB,
                                VerticalAlignment = VerticalAlignment.Center,
                                Margin = new Thickness(9, 0, 0, 0), TextWrapping = TextWrapping.Wrap,
                            };
                            for (int hi = 0; hi < grp.Hosts.Count; hi++)
                            {
                                if (hi > 0) label.Inlines.Add(new System.Windows.Documents.Run(", ") { Foreground = dimB });
                                label.Inlines.Add(new System.Windows.Documents.Run(grp.Hosts[hi]) { Foreground = noteFg });
                            }
                            label.Inlines.Add(new System.Windows.Documents.Run(" up to ") { Foreground = dimB });
                            label.Inlines.Add(new System.Windows.Documents.Run(ByproductNote.Percent(grp.Max)) { Foreground = noteCyan });

                            var bandRow = new StackPanel
                            {
                                Orientation = Orientation.Horizontal,
                                Margin = new Thickness(0, 0, 0, bi == ordered.Count - 1 ? 0 : 5),
                            };
                            bandRow.Children.Add(track);
                            bandRow.Children.Add(label);
                            bands.Children.Add(bandRow);
                        }

                        var crow = new StackPanel();
                        crow.Children.Add(chd);
                        crow.Children.Add(bands);

                        // Hairline separator between rows only (not before the first).
                        noteInner.Children.Add(new Border
                        {
                            Child = crow, Padding = new Thickness(0, 8, 0, 8),
                            BorderBrush = navBorder0,
                            BorderThickness = new Thickness(0, firstBandRow ? 0 : 1, 0, 0),
                        });
                        firstBandRow = false;
                    }

                    // 2px amber accent bar (AccentStrongBrush) + faint amber wash, cyan hairline border.
                    var noteGrid = new Grid();
                    noteGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2) });
                    noteGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    noteGrid.Children.Add(new Border { Background = (System.Windows.Media.Brush)FindResource("AccentStrongBrush") });
                    Grid.SetColumn(noteInner, 1); noteGrid.Children.Add(noteInner);

                    host.Children.Add(new Border
                    {
                        Margin = new Thickness(0, 0, 0, 4),
                        CornerRadius = new CornerRadius(4),
                        Background = BrushFromHex("#07FFB23E"),
                        BorderBrush = navBorder0,
                        BorderThickness = new Thickness(1),
                        ClipToBounds = true,
                        Child = noteGrid,
                    });

                    Logger.Info($"[UI] blueprint byproducts: {byproductOres.Count} ores");
                }

                // Treatment B: "via <host>" chips on each covers line. A byproduct-sourced ore is
                // tagged only with the hosts that actually spawn at that location (host presence read
                // from each host ore's own Locations); a dim "no host here" chip marks an ore whose
                // hosts are all absent. Chips are non-interactive reference markers (no logging).
                var oreToSources = new Dictionary<string, List<NexusApp.Models.FoundInSource>>(StringComparer.OrdinalIgnoreCase);
                foreach (var b in byproductOres) oreToSources[b.Name] = b.Sources;
                var hostLocations = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var res in _vm.AllResources) hostLocations[res.Name] = res.Locations;

                var chipAmber     = heroAccent;                                                    // amber text
                var chipAmberLine = (System.Windows.Media.Brush)FindResource("AccentStrongBrush"); // amber-line border
                var chipCyan      = (System.Windows.Media.Brush)FindResource("CyanBrush");          // "+N" text
                var chipCyanLine  = (System.Windows.Media.Brush)FindResource("CyanDimBrush");       // "+N" border
                Border ViaChip(string text, System.Windows.Media.Brush textBrush, System.Windows.Media.Brush borderBrush) => new()
                {
                    Child = new TextBlock { Text = text, FontFamily = monoFont, FontSize = 8.5, Foreground = textBrush },
                    BorderBrush = borderBrush, BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3), Padding = new Thickness(5, 1, 5, 1),
                    Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                };

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

                    var coversPanel = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
                    coversPanel.Children.Add(new TextBlock
                    {
                        Text = $"{covers.Count}/{ingredientNames.Count} ingredients ·",
                        FontSize = 10, Foreground = dimB,
                        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0),
                    });
                    for (int ci = 0; ci < covers.Count; ci++)
                    {
                        if (ci > 0)
                            coversPanel.Children.Add(new TextBlock
                            {
                                Text = ",", FontSize = 10, Foreground = dimB,
                                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0),
                            });
                        coversPanel.Children.Add(new TextBlock
                        {
                            Text = covers[ci], FontSize = 10, FontWeight = FontWeights.SemiBold,
                            Foreground = (System.Windows.Media.Brush)FindResource("FgBrush"),
                            VerticalAlignment = VerticalAlignment.Center,
                        });

                        // Byproduct ores get "via <host>" chips for the hosts present here; ores with
                        // found-in rows but no host at this location get a dim "no host here" chip;
                        // ores without found-in rows get nothing.
                        oreToSources.TryGetValue(covers[ci], out var oreSources);
                        var chips = ByproductNote.HostsPresentAt(oreSources, hostLocations, location);
                        if (chips.HasHosts)
                        {
                            if (chips.Present.Count == 0)
                                coversPanel.Children.Add(ViaChip("no host here", dimB, navBorder0));
                            else
                            {
                                foreach (var h in chips.Present)
                                    coversPanel.Children.Add(ViaChip($"via {h}", chipAmber, chipAmberLine));
                                if (chips.Overflow > 0)
                                    coversPanel.Children.Add(ViaChip($"+{chips.Overflow}", chipCyan, chipCyanLine));
                            }
                        }
                    }
                    locContent.Children.Add(coversPanel);

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

        // Detail is always the deepest tier (3); switching between sibling blueprints while
        // already viewing one plays no slide (same depth), matching a real drill-in.
        _bpDetailDepth = PlayDrillSlide(BlueprintDetailPanel, _bpDetailDepth, 3);
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

}
