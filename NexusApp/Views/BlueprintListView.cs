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

    private static Brush Br(string key) => (Brush)Application.Current.FindResource(key);
    private static FontFamily Head => (FontFamily)Application.Current.FindResource("HeadFont");

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
            Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 0, 0, 10),
            Background = Br("Bg2NavBrush"), Foreground = Br("FgBrush"),
            BorderBrush = Br("NavBorderBrush"), BorderThickness = new Thickness(1),
            CaretBrush = Br("AccentBrush"), ToolTip = "Search blueprints…",
        };
        search.TextChanged += (_, _) => { _search = search.Text ?? ""; Render(); };
        Grid.SetRow(search, 0); root.Children.Add(search);

        _filterBar.Margin = new Thickness(0, 0, 0, 10);
        Grid.SetRow(_filterBar, 1); root.Children.Add(_filterBar);
        BuildFilters();

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _list };
        Grid.SetRow(scroll, 2); root.Children.Add(scroll);

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
                Text = f.Label, FontFamily = Head, FontSize = 11, FontWeight = FontWeights.Bold,
                Foreground = active ? Br("BgBrush") : Br("FgDimBrush"),
            };
            var chip = new Border
            {
                Padding = new Thickness(11, 6, 11, 6), Margin = new Thickness(0, 0, 7, 0), CornerRadius = new CornerRadius(13),
                Background = active ? Br("AccentBrush") : Br("Bg2NavBrush"),
                BorderBrush = Br("NavBorderBrush"), BorderThickness = new Thickness(1), Cursor = Cursors.Hand, Child = tb,
            };
            var match = f.Match;
            chip.MouseLeftButtonUp += (_, _) => { _activeFilter = match; BuildFilters(); Render(); };
            _filterBar.Children.Add(chip);
        }
    }

    private void Render()
    {
        _list.Children.Clear();
        var items = _all.Where(b => _activeFilter(b) && Matches(b));
        var any = false;
        foreach (var group in items.GroupBy(b => string.IsNullOrEmpty(b.Category) ? "Other" : b.Category).OrderBy(g => g.Key))
        {
            _list.Children.Add(Header(group.Key));
            foreach (var b in group.OrderBy(x => x.Name))
            {
                _list.Children.Add(RowFor(b));
                any = true;
            }
        }
        if (!any)
            _list.Children.Add(new TextBlock { Text = "No blueprints match.", Foreground = Br("FgDimBrush"), Margin = new Thickness(4, 20, 0, 0), FontSize = 13 });
    }

    private bool Matches(Blueprint b)
    {
        if (string.IsNullOrWhiteSpace(_search)) return true;
        var s = _search.Trim();
        return Has(b.Name, s) || Has(b.Category, s) || Has(b.SubCategory, s);
    }

    private static bool Has(string? hay, string needle) =>
        !string.IsNullOrEmpty(hay) && hay.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private UIElement Header(string text) => new TextBlock
    {
        Text = text.ToUpperInvariant(), FontFamily = Head, FontSize = 11, FontWeight = FontWeights.Bold,
        Foreground = Br("AccentBrush"), Margin = new Thickness(4, 11, 0, 6),
    };

    private UIElement RowFor(Blueprint b)
    {
        var stack = new StackPanel();

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // strip
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });  // name
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // trailing

        var strip = new Border { Width = 3, Background = Br("AccentBrush"), CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 10, 0) };
        Grid.SetColumn(strip, 0); top.Children.Add(strip);

        var info = new StackPanel { Margin = new Thickness(0, 8, 0, 8), VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock { Text = b.Name, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Br("FgBrush") });
        if (!string.IsNullOrEmpty(b.SubCategory))
            info.Children.Add(new TextBlock { Text = b.SubCategory, FontSize = 10.5, Foreground = Br("FgDimBrush"), Margin = new Thickness(0, 1, 0, 0) });
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
                            Child = content, BorderBrush = Br("NavBorderBrush"), BorderThickness = new Thickness(0, 1, 0, 0),
                            Padding = new Thickness(13, 10, 13, 12),
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
            CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 0, 6), Child = stack,
        };
    }
}
