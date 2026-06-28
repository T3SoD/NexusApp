using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NexusApp.Models;

namespace NexusApp.Views;

/// <summary>
/// Reusable searchable, category-grouped blueprint list. The caller supplies the per-row trailing
/// content (e.g. an ownership coverage cell), an optional expand panel (e.g. the owner list), and
/// the filter chips. Built for the Blueprint Network's Blueprints tab; intended to also back the
/// Blueprint Library once that page is migrated onto it.
///
/// Rows are rendered in batches as the user scrolls so a full ~600-blueprint catalog doesn't freeze
/// the UI building every row up front, and theme brushes/fonts are cached per view rather than
/// re-resolved thousands of times.
/// </summary>
public sealed class BlueprintListView : UserControl
{
    public sealed class FilterChip
    {
        public string Label = "";
        public Func<Blueprint, bool> Match = _ => true;
    }

    private readonly IReadOnlyList<Blueprint> _all;
    private readonly Func<Blueprint, UIElement> _trailing;
    private readonly Func<Blueprint, UIElement?>? _expand;
    private readonly IReadOnlyList<FilterChip> _filters;

    private Func<Blueprint, bool> _activeFilter;
    private string _search = "";

    private readonly StackPanel _filterBar = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _list = new();
    private readonly ScrollViewer _scroll;

    private List<object> _entries = new();   // string = category header, Blueprint = a row
    private int _rendered;
    private const int Batch = 60;

    private readonly Dictionary<string, Brush> _brushCache = new();
    private Brush Br(string key) => _brushCache.TryGetValue(key, out var b) ? b : (_brushCache[key] = (Brush)Application.Current.FindResource(key));
    private FontFamily? _head;
    private FontFamily Head => _head ??= (FontFamily)Application.Current.FindResource("HeadFont");
    private FontFamily? _ui;
    private FontFamily Ui => _ui ??= (FontFamily)Application.Current.FindResource("UiFont");
    private Style St(string key) => (Style)Application.Current.FindResource(key);

    public BlueprintListView(IReadOnlyList<Blueprint> all, Func<Blueprint, UIElement> trailing,
        Func<Blueprint, UIElement?>? expand, IReadOnlyList<FilterChip> filters)
    {
        _all = all;
        _trailing = trailing;
        _expand = expand;
        _filters = filters;
        _activeFilter = filters.Count > 0 ? filters[0].Match : (_ => true);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // search
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // filters
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });  // list

        var search = new TextBox
        {
            Style = St("NexusTextBox"),
            Tag = "Search blueprints by name, category or type",
            Margin = new Thickness(0, 0, 0, 12),
            ToolTip = "Search blueprints",
        };
        search.TextChanged += (_, _) => { _search = search.Text ?? ""; Render(); };
        Grid.SetRow(search, 0); root.Children.Add(search);

        _filterBar.Margin = new Thickness(0, 0, 0, 12);
        Grid.SetRow(_filterBar, 1); root.Children.Add(_filterBar);
        BuildFilters();

        _scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _list };
        _scroll.ScrollChanged += (_, _) => MaybeLoadMore();   // fires on scroll AND on extent change as batches add
        _scroll.SizeChanged += (_, _) => MaybeLoadMore();     // a taller viewport (resize/maximize) may need more rows
        Grid.SetRow(_scroll, 2); root.Children.Add(_scroll);

        Content = root;
        Render();
    }

    private void BuildFilters()
    {
        _filterBar.Children.Clear();
        foreach (var f in _filters)
        {
            var active = _activeFilter == f.Match;
            var tb = new TextBlock
            {
                Text = f.Label, FontFamily = Ui, FontSize = 11, FontWeight = FontWeights.SemiBold,
                Foreground = active ? Br("OnAccentBrush") : Br("FgDimBrush"),
            };
            var chip = new Border
            {
                Padding = new Thickness(13, 6, 13, 6), Margin = new Thickness(0, 0, 8, 0), CornerRadius = new CornerRadius(4),
                Background = active ? Br("AccentBrush") : Br("Bg2NavBrush"),
                BorderBrush = active ? Br("AccentBrush") : Br("NavBorderBrush"), BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand, Child = tb,
            };
            var match = f.Match;
            chip.MouseLeftButtonUp += (_, _) => { _activeFilter = match; BuildFilters(); Render(); };
            _filterBar.Children.Add(chip);
        }
    }

    private void Render()
    {
        _list.Children.Clear();
        _entries = BuildEntries();
        _rendered = 0;
        if (_entries.Count == 0)
        {
            _list.Children.Add(new TextBlock { Text = "No blueprints match.", FontFamily = Ui, Foreground = Br("FgDimBrush"), Margin = new Thickness(4, 24, 0, 0), FontSize = 13 });
            return;
        }
        RenderMore();
        _scroll.ScrollToTop();
    }

    // Flatten the filtered, category-grouped catalog into an ordered list of headers + rows; the
    // actual visuals are built lazily a batch at a time in RenderMore.
    private List<object> BuildEntries()
    {
        var entries = new List<object>();
        var items = _all.Where(b => _activeFilter(b) && Matches(b));
        foreach (var group in items.GroupBy(b => string.IsNullOrEmpty(b.Category) ? "Other" : b.Category).OrderBy(g => g.Key))
        {
            entries.Add(group.Key);
            foreach (var b in group.OrderBy(x => x.Name)) entries.Add(b);
        }
        return entries;
    }

    private void RenderMore()
    {
        var end = Math.Min(_rendered + Batch, _entries.Count);
        for (; _rendered < end; _rendered++)
        {
            var e = _entries[_rendered];
            _list.Children.Add(e is string cat ? Header(cat) : RowFor((Blueprint)e));
        }
    }

    // Load the next batch when scrolled near the bottom, or when the content doesn't yet fill the
    // viewport (so a tall window never strands the unrendered tail).
    private void MaybeLoadMore()
    {
        if (_rendered < _entries.Count && _scroll.VerticalOffset >= _scroll.ScrollableHeight - 400)
            RenderMore();
    }

    private bool Matches(Blueprint b)
    {
        if (string.IsNullOrWhiteSpace(_search)) return true;
        var s = _search.Trim();
        return Has(b.Name, s) || Has(b.Category, s) || Has(b.SubCategory, s);
    }

    private static bool Has(string? hay, string needle) =>
        !string.IsNullOrEmpty(hay) && hay.Contains(needle, StringComparison.OrdinalIgnoreCase);

    // Command-center section divider for each category (small dim uppercase SectionLabel).
    private UIElement Header(string text) => new TextBlock
    {
        Style = St("SectionLabel"),
        Text = text.ToUpperInvariant(), Margin = new Thickness(4, 16, 0, 7),
    };

    private UIElement RowFor(Blueprint b)
    {
        var stack = new StackPanel();

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // strip
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });  // name
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // trailing

        var strip = new Border { Width = 3, Background = Br("AccentBrush"), CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 7, 11, 7) };
        Grid.SetColumn(strip, 0); top.Children.Add(strip);

        var info = new StackPanel { Margin = new Thickness(0, 9, 0, 9), VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock { Text = b.Name, FontFamily = Ui, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Br("FgBrush") });
        if (!string.IsNullOrEmpty(b.SubCategory))
            info.Children.Add(new TextBlock { Text = b.SubCategory, FontFamily = Ui, FontSize = 10.5, Foreground = Br("FgDimBrush"), Margin = new Thickness(0, 2, 0, 0) });
        Grid.SetColumn(info, 1); top.Children.Add(info);

        var trail = new ContentControl { Content = _trailing(b), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 10, 0) };
        Grid.SetColumn(trail, 2); top.Children.Add(trail);

        stack.Children.Add(top);

        if (_expand != null)
        {
            top.Cursor = Cursors.Hand;
            Border? panel = null;
            var shown = false;
            top.MouseLeftButtonUp += (_, _) =>
            {
                if (!shown)
                {
                    if (panel == null)
                    {
                        var content = _expand(b);
                        if (content == null) return;
                        panel = new Border
                        {
                            Child = content, Background = Br("BgBrush"),
                            BorderBrush = Br("NavBorderBrush"), BorderThickness = new Thickness(0, 1, 0, 0),
                            CornerRadius = new CornerRadius(0, 0, 4, 4),
                            Padding = new Thickness(14, 11, 14, 13),
                        };
                        stack.Children.Add(panel);
                    }
                    panel.Visibility = Visibility.Visible;
                    shown = true;
                }
                else
                {
                    if (panel != null) panel.Visibility = Visibility.Collapsed;
                    shown = false;
                }
            };
        }

        return new Border
        {
            Background = Br("Bg2NavBrush"), BorderBrush = Br("NavBorderBrush"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(4, 0, 0, 0), Child = stack,
        };
    }
}
