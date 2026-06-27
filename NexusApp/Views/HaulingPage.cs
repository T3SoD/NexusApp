using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NexusApp.Models;
using NexusApp.Services;

namespace NexusApp.Views;

/// <summary>
/// The Cargo Hauling page (code-built, like NetworkPage). Reads App.Hauls and renders three
/// sections: active hauls (per-leg load/drop rows), a load/drop consolidation view, and finished
/// hauls with their outcome. Rebuilds itself whenever the tracker raises Changed.
/// </summary>
public sealed class HaulingPage : UserControl
{
    private readonly StackPanel _body = new();

    private readonly Dictionary<string, Brush> _brushCache = new();
    private Brush Br(string key) => _brushCache.TryGetValue(key, out var b) ? b : (_brushCache[key] = (Brush)Application.Current.FindResource(key));
    private FontFamily? _head, _mono;
    private FontFamily Head => _head ??= (FontFamily)Application.Current.FindResource("HeadFont");
    private FontFamily Mono => _mono ??= (FontFamily)Application.Current.FindResource("MonoFont");

    public HaulingPage()
    {
        Build();
        Refresh();
        InteractionLog.Nav("Cargo Hauling");
        App.Hauls.Changed += () => Dispatcher.Invoke(Refresh);
    }

    /// <summary>Rebuild every section from the current App.Hauls state.</summary>
    public void Refresh()
    {
        _body.Children.Clear();

        if (App.Hauls.AllHauls.Count == 0)
        {
            _body.Children.Add(Placeholder("No active hauls. Accept a hauling contract in-game."));
            return;
        }

        RenderActive();
        RenderConsolidation();
        RenderFinished();
    }

    // -- layout --------------------------------------------------------------------

    private void Build()
    {
        var root = new Grid { Margin = new Thickness(20, 16, 20, 16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // header
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });  // content

        var titleStack = new StackPanel();
        titleStack.Children.Add(new TextBlock { Text = "CARGO HAULING", FontFamily = Head, FontSize = 21, Foreground = Br("FgBrush") });
        titleStack.Children.Add(new TextBlock { Text = "Hauling contracts read from your game log", FontSize = 12, Foreground = Br("FgDimBrush"), Margin = new Thickness(0, 2, 0, 0) });
        Grid.SetRow(titleStack, 0); root.Children.Add(titleStack);

        var scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 14, 0, 0),
            Content = _body,
        };
        Grid.SetRow(scroller, 1); root.Children.Add(scroller);

        Content = root;
    }

    // -- active hauls --------------------------------------------------------------

    private void RenderActive()
    {
        var active = App.Hauls.ActiveHauls;
        _body.Children.Add(SectionHeader($"Active hauls · {active.Count}"));
        if (active.Count == 0)
        {
            _body.Children.Add(MutedLine("No active hauls right now."));
            return;
        }
        foreach (var h in active)
            _body.Children.Add(ActiveHaulCard(h));
    }

    private UIElement ActiveHaulCard(Haul h)
    {
        var inner = new StackPanel { Margin = new Thickness(12, 10, 12, 10) };

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(new TextBlock
        {
            Text = CompanyOf(h), FontFamily = Head, FontSize = 14, FontWeight = FontWeights.SemiBold,
            Foreground = Br("FgBrush"), VerticalAlignment = VerticalAlignment.Center,
        });
        titleRow.Children.Add(new TextBlock
        {
            Text = h.Topology, FontSize = 11, FontWeight = FontWeights.SemiBold,
            Foreground = Br("AccentBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
        });
        inner.Children.Add(titleRow);

        if (!string.IsNullOrWhiteSpace(h.RouteTitle))
            inner.Children.Add(new TextBlock
            {
                Text = h.RouteTitle, FontSize = 11, Foreground = Br("FgDimBrush"),
                Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap,
            });

        foreach (var leg in h.Legs)
            inner.Children.Add(LegRow(leg));

        return Card(inner);
    }

    private UIElement LegRow(HaulLeg leg)
    {
        var role = leg.Role == HaulRole.Pickup ? "Load" : "Drop";

        // Pickup legs often carry no commodity/SCU/destination of their own (those live on the
        // sibling dropoff). Show whatever fields are present, skipping the empties.
        var segs = new List<string>();
        if (leg.TargetScu > 0) segs.Add($"{leg.TargetScu} SCU");
        if (!string.IsNullOrWhiteSpace(leg.Commodity)) segs.Add(leg.Commodity);
        if (!string.IsNullOrWhiteSpace(leg.Destination)) segs.Add($"-> {leg.Destination}");
        var desc = string.Join(" ", segs);

        var text = leg.Completed ? $"{role}: {desc}  [done]" : $"{role}: {desc}";
        return new TextBlock
        {
            Text = text, FontFamily = Mono, FontSize = 12,
            Foreground = leg.Completed ? Br("FgDimBrush") : Br("FgBrush"),
            Margin = new Thickness(2, 5, 0, 0), TextWrapping = TextWrapping.Wrap,
        };
    }

    // -- consolidation -------------------------------------------------------------

    private void RenderConsolidation()
    {
        var con = App.Hauls.BuildConsolidation();
        _body.Children.Add(SectionHeader("Load / drop consolidation"));

        if (con.Pickups.Count == 0 && con.Dropoffs.Count == 0)
        {
            _body.Children.Add(MutedLine("Nothing to consolidate yet."));
            return;
        }

        _body.Children.Add(SubHeader("Load at"));
        if (con.Pickups.Count == 0) _body.Children.Add(MutedLine("No pickups pending."));
        else foreach (var s in con.Pickups) _body.Children.Add(StopCard(s));

        _body.Children.Add(SubHeader("Drop at"));
        if (con.Dropoffs.Count == 0) _body.Children.Add(MutedLine("No dropoffs pending."));
        else foreach (var s in con.Dropoffs) _body.Children.Add(StopCard(s));
    }

    private UIElement StopCard(ConsolidationStop stop)
    {
        var inner = new StackPanel { Margin = new Thickness(12, 9, 12, 9) };

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(new TextBlock
        {
            Text = stop.Location, FontFamily = Head, FontSize = 13, FontWeight = FontWeights.SemiBold,
            Foreground = Br("FgBrush"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var total = new TextBlock
        {
            Text = $"{stop.TotalScu} SCU", FontFamily = Mono, FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = Br("AccentBrush"), VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(total, 1); head.Children.Add(total);
        inner.Children.Add(head);

        foreach (var item in stop.Items)
        {
            var commodity = string.IsNullOrWhiteSpace(item.Commodity) ? "Cargo" : item.Commodity;
            inner.Children.Add(new TextBlock
            {
                Text = $"{item.Scu} SCU {commodity}", FontFamily = Mono, FontSize = 11.5,
                Foreground = Br("FgDimBrush"), Margin = new Thickness(2, 4, 0, 0), TextWrapping = TextWrapping.Wrap,
            });
        }
        return Card(inner);
    }

    // -- finished hauls ------------------------------------------------------------

    private void RenderFinished()
    {
        var finished = App.Hauls.FinishedHauls;
        if (finished.Count == 0) return;

        _body.Children.Add(SectionHeader($"Finished hauls · {finished.Count}"));
        foreach (var h in finished)
            _body.Children.Add(FinishedRow(h));
    }

    private UIElement FinishedRow(Haul h)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var info = new StackPanel { Margin = new Thickness(12, 8, 12, 8) };
        info.Children.Add(new TextBlock { Text = CompanyOf(h), FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Br("FgBrush") });
        var sub = string.IsNullOrWhiteSpace(h.RouteTitle) ? h.Topology : $"{h.Topology}  ·  {h.RouteTitle}";
        info.Children.Add(new TextBlock { Text = sub, FontSize = 10.5, Foreground = Br("FgDimBrush"), Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(info, 0); grid.Children.Add(info);

        var outcome = new TextBlock
        {
            Text = h.Outcome.ToString(), FontSize = 11, FontWeight = FontWeights.SemiBold,
            Foreground = h.Outcome == HaulOutcome.Complete ? Br("AccentBrush") : Br("FgDimBrush"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0),
        };
        Grid.SetColumn(outcome, 1); grid.Children.Add(outcome);

        return Card(grid);
    }

    // -- helpers -------------------------------------------------------------------

    private static string CompanyOf(Haul h) => string.IsNullOrWhiteSpace(h.Company) ? "Unknown company" : h.Company;

    private UIElement Card(UIElement child) => new Border
    {
        Background = Br("Bg2NavBrush"), BorderBrush = Br("NavBorderBrush"), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 0, 6), Child = child,
    };

    private UIElement SectionHeader(string text) => new TextBlock
    {
        Text = text.ToUpperInvariant(), FontFamily = Head, FontSize = 10, FontWeight = FontWeights.Bold,
        Foreground = Br("FgDimBrush"), Margin = new Thickness(2, 12, 0, 8),
    };

    private UIElement SubHeader(string text) => new TextBlock
    {
        Text = text, FontFamily = Head, FontSize = 12, FontWeight = FontWeights.SemiBold,
        Foreground = Br("FgBrush"), Margin = new Thickness(2, 6, 0, 6),
    };

    private UIElement MutedLine(string text) => new TextBlock
    {
        Text = text, FontSize = 12, Foreground = Br("FgDimBrush"),
        Margin = new Thickness(2, 2, 0, 6), TextWrapping = TextWrapping.Wrap,
    };

    private UIElement Placeholder(string text) => new TextBlock
    {
        Text = text, Foreground = Br("FgDimBrush"), FontSize = 13, TextWrapping = TextWrapping.Wrap,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(40),
    };
}
