using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using NexusApp.Services;

namespace NexusApp.Views;

/// <summary>
/// The CRAFTING view's FORECAST vs STOCK CRAFT list: one row per affected stat with a centred
/// bar (better grows one way, worse the other) and the delta, then a legend. Values follow
/// library-redesign round 5 design 3 (.stat, .bar, .delta, .legend). Display only; the caller
/// computes the forecast.
/// </summary>
public sealed class CraftStatRows : StackPanel
{
    private const double NameWidth = 140;
    private const double DeltaWidth = 86;
    private const double ColumnGap = 12;

    // Mock .bar .rail: cyan at 6 %.
    private static readonly SolidColorBrush RailBrush = Frozen(Color.FromArgb(0x0F, 0x7F, 0xE9, 0xE0));

    private sealed class RowParts
    {
        public required Grid Bar;
        public required Border Seg;
        public required TextBlock From;
        public required TextBlock Delta;
        public required TextBlock Word;
        public double Fraction;
        public bool Right = true;
    }

    private readonly Dictionary<string, RowParts> _rows = [];
    private string _keys = "";

    public CraftStatRows() { }

    /// <summary>Shows the rows. A new stat set rebuilds them; the same set updates in place.</summary>
    public void Update(IReadOnlyList<StatForecast> rows, bool animate = true)
    {
        var keys = string.Join("|", rows.Select(r => r.Key));
        if (keys != _keys)
        {
            // A different stat set means a different blueprint: rebuild without motion.
            _keys = keys;
            Children.Clear();
            _rows.Clear();
            foreach (var r in rows) Children.Add(BuildRow(r));
            Children.Add(BuildLegend());
            foreach (var r in rows) Apply(_rows[r.Key], r, animate: false);
            return;
        }
        foreach (var r in rows) Apply(_rows[r.Key], r, animate && !Motion.Reduced);
    }

    /// <summary>
    /// The segment's share of half the bar: |value - base| over the wider side of the stat's
    /// range, clamped to 1. Base is 1 for a factor and 0 for an additive step; a zero span gives 0.
    /// </summary>
    internal static double BarFraction(StatForecast r)
    {
        double b = r.IsAdditive ? 0.0 : 1.0;
        double v = r.IsAdditive ? r.Additive : r.Factor;
        double span = Math.Max(Math.Abs(r.Max - b), Math.Abs(r.Min - b));
        if (span <= 0) return 0.0;
        return Math.Min(1.0, Math.Abs(v - b) / span);
    }

    /// <summary>True when the value sits above stock (Factor over 1, or a positive additive step).</summary>
    internal static bool BarGrowsRight(StatForecast r) =>
        r.IsAdditive ? r.Additive > 0 : r.Factor > 1.0;

    private Border BuildRow(StatForecast r)
    {
        var ui = Hud.Font("UiFont");
        var mono = Hud.Font("MonoFont");

        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(NameWidth + ColumnGap) });   // the gap rides inside the column so the name keeps its 140
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(DeltaWidth) });

        var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, ColumnGap, 0) };
        name.Children.Add(new TextBlock { Text = r.Name, FontFamily = ui, FontSize = 13, Foreground = Hud.Br("FgBrush"), TextWrapping = TextWrapping.Wrap });
        var from = new TextBlock
        {
            FontFamily = ui, FontSize = 10, Foreground = Hud.Br("FgDimBrush"),
            Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap,
        };
        name.Children.Add(from);
        Grid.SetColumn(name, 0); g.Children.Add(name);

        // Bar cell, 18 tall: rail, the two halves the segment grows into, then the zero line on top.
        var bar = new Grid { Height = 18, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, ColumnGap, 0) };
        bar.Children.Add(new Border { Height = 2, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 8, 0, 0), Background = RailBrush });
        var halves = new Grid();
        halves.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        halves.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var seg = new Border
        {
            Height = 8, Width = 0, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 0, 0),
            CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left,
        };
        Grid.SetColumn(seg, 1);
        halves.Children.Add(seg);
        bar.Children.Add(halves);
        bar.Children.Add(new Border
        {
            Width = 1, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 1, 0, 1),
            Background = Hud.Br("BorderBrush"),
        });
        Grid.SetColumn(bar, 1); g.Children.Add(bar);

        var deltaCell = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        var delta = new TextBlock { FontFamily = mono, FontSize = 15, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Right, HorizontalAlignment = HorizontalAlignment.Right };
        var word = new TextBlock { FontFamily = mono, FontSize = 10, Foreground = Hud.Br("FgDimBrush"), TextAlignment = TextAlignment.Right, HorizontalAlignment = HorizontalAlignment.Right };
        deltaCell.Children.Add(delta);
        deltaCell.Children.Add(word);
        Grid.SetColumn(deltaCell, 2); g.Children.Add(deltaCell);

        var parts = new RowParts { Bar = bar, Seg = seg, From = from, Delta = delta, Word = word };
        _rows[r.Key] = parts;
        // A resize keeps the fraction: the segment follows half the bar's new width at once.
        bar.SizeChanged += (_, e) =>
        {
            if (!e.WidthChanged) return;
            parts.Seg.BeginAnimation(WidthProperty, null);
            parts.Seg.Width = SegmentWidth(parts);
        };

        return new Border
        {
            Child = g, Padding = new Thickness(2, 10, 2, 10),
            BorderBrush = Hud.Br("NavBorderBrush"), BorderThickness = new Thickness(0, 0, 0, 1),
        };
    }

    private static double SegmentWidth(RowParts p) => p.Fraction * Math.Max(0, p.Bar.ActualWidth) / 2;

    private static void Apply(RowParts p, StatForecast r, bool animate)
    {
        p.From.Text = "from " + string.Join(" + ", r.Slots);
        p.Delta.Text = r.DeltaText;
        p.Delta.Foreground = VerdictBrush(r.Verdict);
        p.Word.Text = r.Verdict;
        p.Seg.Background = VerdictBrush(r.Verdict);

        // Stock has zero width, as in the mock (scaleX 0 when flat).
        p.Fraction = r.Verdict == "stock" ? 0.0 : BarFraction(r);
        bool right = BarGrowsRight(r);
        if (right != p.Right)
        {
            // The segment changes side: it restarts from the zero line on the new side.
            p.Right = right;
            p.Seg.BeginAnimation(WidthProperty, null);
            p.Seg.Width = 0;
            Grid.SetColumn(p.Seg, right ? 1 : 0);
            p.Seg.HorizontalAlignment = right ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        }

        double target = SegmentWidth(p);
        if (!animate)
        {
            p.Seg.BeginAnimation(WidthProperty, null);
            p.Seg.Width = target;
            return;
        }
        // No From: the animation starts at the current (possibly mid-flight) width. The base value
        // is set after, so the width holds when the animation is later cleared.
        p.Seg.BeginAnimation(WidthProperty,
            new DoubleAnimation(target, TimeSpan.FromMilliseconds(Motion.QuickRevealMs)) { EasingFunction = Motion.Settle });
        p.Seg.Width = target;
    }

    private static WrapPanel BuildLegend()
    {
        var mono = Hud.Font("MonoFont");
        var legend = new WrapPanel { Margin = new Thickness(2, 10, 2, 0) };
        void Item(FrameworkElement swatch, string text, bool last)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, last ? 0 : 14, 0) };
            swatch.VerticalAlignment = VerticalAlignment.Center;
            swatch.Margin = new Thickness(0, 0, 6, 0);
            sp.Children.Add(swatch);
            sp.Children.Add(new TextBlock { Text = text, FontFamily = mono, FontSize = 10, Foreground = Hud.Br("FgDimBrush"), VerticalAlignment = VerticalAlignment.Center });
            legend.Children.Add(sp);
        }
        Item(new Border { Width = 1, Height = 10, Background = Hud.Br("BorderBrush") }, "stock craft, all slots Q500", last: false);
        Item(new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(2), Background = Hud.Br("OkBrush") }, "better", last: false);
        Item(new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(2), Background = Hud.Br("DangerBrush") }, "worse", last: true);
        return legend;
    }

    private static Brush VerdictBrush(string verdict) => verdict switch
    {
        "better" => Hud.Br("OkBrush"),
        "worse" => Hud.Br("DangerBrush"),
        _ => Hud.Br("FgDimBrush"),
    };

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
