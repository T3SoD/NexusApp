using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using NexusApp.Services;

namespace NexusApp.Views;

/// <summary>
/// The overlay's tab strip, "Active Pill" concept (mock: nexus-design-lab/overlay-redesign,
/// Concept 02): one 34px row of 15px glyph buttons where the active tab expands into a chamfered
/// amber icon-plus-label pill. Inactive tabs show their label in a 150ms hover chip below the
/// icon. Count badges ride the icon's top-right corner (they no longer widen any column).
/// Space-between layout: Auto columns for tabs, Star columns for the elastic gaps, 6px edge pads.
/// </summary>
public sealed class OverlayTabStrip : Grid
{
    public event Action<string>? TabSelected;

    private sealed class TabHost
    {
        public string Id = "";
        public Border Root = null!;          // hit target + hover events
        public FrameworkElement IconOnly = null!;
        public ChamferPanel Pill = null!;
        public Border LabelClip = null!;     // width-animated label window inside the pill
        public TextBlock Label = null!;
        public List<Path> IconPaths = [];    // both representations, recolored on state change
        public List<Border> BadgeChips = []; // one chip per representation, updated together
        public List<TextBlock> BadgeTexts = [];

        // Bumped on every state change. Measured WPF behavior: an animation removed with
        // BeginAnimation(prop, null) detaches from the property but its clock keeps running, and it
        // still raises Completed at the ORIGINAL end time. Every Completed handler must therefore
        // compare its generation against this, or a superseded clock will tear down the live state.
        public int MotionGen;
    }

    private readonly List<TabHost> _hosts = [];
    private string? _active;

    public OverlayTabStrip()
    {
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1) });

        var row = new Grid();
        SetRow(row, 0);
        Children.Add(row);

        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        for (var i = 0; i < OverlayTabs.Ids.Length; i++)
        {
            if (i > 0)
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var host = BuildTab(OverlayTabs.Ids[i]);
            SetColumn(host.Root, 1 + i * 2);
            row.Children.Add(host.Root);
            _hosts.Add(host);
        }
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });

        // Bottom hairline, preserving the shipped strip's silhouette.
        var hairline = new Border { Height = 1 };
        hairline.SetResourceReference(Border.BackgroundProperty, "NavBorderBrush");
        SetRow(hairline, 1);
        Children.Add(hairline);
    }

    private TabHost BuildTab(string id)
    {
        var host = new TabHost { Id = id };

        // Inactive representation: bare 15px glyph, vertically centered in the 26px hit target.
        host.IconOnly = BuildGlyphWithBadge(host, 15);

        // Active representation: chamfered pill, icon + width-clipped label.
        host.Label = new TextBlock
        {
            Text = OverlayTabs.LabelFor(id),
            FontSize = 9.5,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.None,   // the clip Border crops the label: trimming re-trims
                                                // every animation frame (growing ellipsis instead of
                                                // a wipe), so a hard clip at extreme narrow widths is
                                                // accepted below ~300px.
        };
        host.Label.SetResourceReference(TextBlock.FontFamilyProperty, "UiFont");
        host.Label.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        host.LabelClip = new Border
        {
            Child = host.Label,
            ClipToBounds = true,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var pillContent = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        pillContent.Children.Add(BuildGlyphWithBadge(host, 15));
        pillContent.Children.Add(host.LabelClip);
        host.Pill = new ChamferPanel
        {
            Chamfer = 6,
            Height = 26,
            Padding = new Thickness(10, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Content = pillContent,
            Visibility = Visibility.Collapsed,
        };
        host.Pill.SetResourceReference(Control.BackgroundProperty, "AccentActiveFillBrush");
        host.Pill.SetResourceReference(Control.BorderBrushProperty, "AccentStrongBrush");

        var stack = new Grid { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(host.IconOnly);
        stack.Children.Add(host.Pill);

        host.Root = new Border
        {
            Background = Brushes.Transparent,   // hit-testable while visually empty
            Height = 26,
            MinWidth = 27,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            Child = stack,
            ToolTip = BuildHoverChip(id),
        };
        ToolTipService.SetInitialShowDelay(host.Root, 150);
        ToolTipService.SetPlacement(host.Root, System.Windows.Controls.Primitives.PlacementMode.Bottom);
        host.Root.MouseLeftButtonUp += (_, _) => TabSelected?.Invoke(id);
        host.Root.MouseEnter += (_, _) => { if (_active != id) SetIconBrush(host, "FgBrush"); };
        host.Root.MouseLeave += (_, _) => { if (_active != id) SetIconBrush(host, "FgDimBrush"); };
        return host;
    }

    // 15px glyph plus its top-right count chip. Built once per representation; the chip lists on
    // the host keep both copies in sync through SetBadge.
    private FrameworkElement BuildGlyphWithBadge(TabHost host, double size)
    {
        var (x, y, w, h) = TabGlyphSpecs.CropFor(host.Id);
        var canvas = new Canvas { Width = w, Height = h };
        foreach (var part in TabGlyphSpecs.PartsFor(host.Id))
        {
            var geometry = BuildGeometry(part);
            if (geometry is null) continue;
            var path = new Path
            {
                Data = geometry,
                StrokeThickness = TabGlyphSpecs.StrokeFor(host.Id),
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                RenderTransform = new TranslateTransform(-x, -y),
            };
            path.SetResourceReference(Shape.StrokeProperty, "FgDimBrush");
            host.IconPaths.Add(path);
            canvas.Children.Add(path);
        }
        var glyph = new Viewbox { Width = size, Height = size, Child = canvas };

        var badgeText = new TextBlock { FontSize = 8, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center };
        badgeText.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        badgeText.SetResourceReference(TextBlock.ForegroundProperty, "OnAccentBrush");
        var chip = new Border
        {
            CornerRadius = new CornerRadius(3),
            MinWidth = 11,
            Height = 11,
            Padding = new Thickness(2, 0, 2, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -3, -6, 0),
            Visibility = Visibility.Collapsed,
            Child = badgeText,
        };
        chip.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        host.BadgeChips.Add(chip);
        host.BadgeTexts.Add(badgeText);

        return new Grid { Children = { glyph, chip }, ClipToBounds = false };
    }

    private static Geometry? BuildGeometry(GlyphPart part)
    {
        double N(string key) => double.Parse(part.Attrs[key], System.Globalization.CultureInfo.InvariantCulture);
        return part.El switch
        {
            "path" => Geometry.Parse(part.Attrs["d"]),
            "line" => new LineGeometry(new Point(N("x1"), N("y1")), new Point(N("x2"), N("y2"))),
            "circle" => new EllipseGeometry(new Point(N("cx"), N("cy")), N("r"), N("r")),
            _ => null,
        };
    }

    private ToolTip BuildHoverChip(string id)
    {
        var text = new TextBlock { Text = OverlayTabs.LabelFor(id), FontSize = 8.5 };
        text.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");
        var chip = new Border { Padding = new Thickness(7, 2, 7, 2), BorderThickness = new Thickness(1), Child = text };
        chip.SetResourceReference(Border.BackgroundProperty, "Bg3Brush");
        chip.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        // Explicit ToolTip with its chrome zeroed out. A bare element gets wrapped in a default-styled
        // ToolTip (near-white fill, grey hairline, 6,4 padding), which would halo this dark HUD chip.
        return new ToolTip
        {
            Content = chip,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            HasDropShadow = false,
        };
    }

    private void SetIconBrush(TabHost host, string brushKey)
    {
        foreach (var p in host.IconPaths) p.SetResourceReference(Shape.StrokeProperty, brushKey);
    }

    public void SetActive(string id, bool animate)
    {
        if (id == _active) return;
        var old = _active;
        _active = id;
        foreach (var host in _hosts)
        {
            if (host.Id == id) ShowPill(host, animate);
            else if (host.Id == old) ShowIcon(host, animate);
            ToolTipService.SetIsEnabled(host.Root, host.Id != id);   // active tab shows its label already
        }
    }

    private void ShowPill(TabHost host, bool animate)
    {
        var gen = ++host.MotionGen;
        SetIconBrush(host, "AccentBrush");
        host.IconOnly.Visibility = Visibility.Collapsed;
        host.Pill.Visibility = Visibility.Visible;
        host.LabelClip.BeginAnimation(WidthProperty, null);
        if (!animate || Motion.Reduced)
        {
            host.LabelClip.Width = double.NaN;
            return;
        }
        // Measure-before-animate: measure unconstrained FIRST, then pin 0 and animate to the
        // measured width. Pinning 0 before measuring clamps DesiredSize and animates 0 to 0.
        host.Label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var target = host.Label.DesiredSize.Width;
        host.LabelClip.Width = 0;
        var expand = new DoubleAnimation(0, target, TimeSpan.FromMilliseconds(Motion.PillInMs))
        {
            EasingFunction = Motion.Settle,
        };
        expand.Completed += (_, _) =>
        {
            if (gen != host.MotionGen) return;   // superseded by a later switch; this clock is orphaned
            host.LabelClip.BeginAnimation(WidthProperty, null);
            host.LabelClip.Width = double.NaN;
        };
        host.LabelClip.BeginAnimation(WidthProperty, expand);
    }

    private void ShowIcon(TabHost host, bool animate)
    {
        var gen = ++host.MotionGen;
        SetIconBrush(host, host.Root.IsMouseOver ? "FgBrush" : "FgDimBrush");
        host.LabelClip.BeginAnimation(WidthProperty, null);
        if (!animate || Motion.Reduced)
        {
            host.Pill.Visibility = Visibility.Collapsed;
            host.IconOnly.Visibility = Visibility.Visible;
            return;
        }
        var from = host.LabelClip.ActualWidth;
        host.LabelClip.Width = from;
        var collapse = new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(Motion.PillOutMs))
        {
            EasingFunction = Motion.SlideOut,
        };
        collapse.Completed += (_, _) =>
        {
            if (gen != host.MotionGen) return;   // superseded by a later switch; this clock is orphaned
            host.LabelClip.BeginAnimation(WidthProperty, null);
            host.Pill.Visibility = Visibility.Collapsed;
            host.IconOnly.Visibility = Visibility.Visible;
        };
        host.LabelClip.BeginAnimation(WidthProperty, collapse);
    }

    public void SetBadge(string id, int count)
    {
        var host = _hosts.Find(h => h.Id == id);
        if (host is null) return;
        var vis = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var chip in host.BadgeChips) chip.Visibility = vis;
        foreach (var text in host.BadgeTexts) text.Text = count.ToString();
    }
}
