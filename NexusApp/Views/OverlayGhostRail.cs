using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using NexusApp.Services;

namespace NexusApp.Views;

/// <summary>
/// Ghost mode's vertical icon rail (issue #27; mock nexus-design-lab/overlay-ghost-mode,
/// whose implementation-values table is the visual contract). Top to bottom: beam cap
/// (drag handle), six tab glyphs with count badges, then a bottom-pinned settings gear and
/// close glyph. The window owns all sizing; the rail only reports clicks.
///
/// Glyph construction (crop/parts/stroke lookup, Canvas+Path+Viewbox assembly) duplicates
/// OverlayTabStrip's private builder. That builder is entangled with its TabHost's dual
/// icon/pill representation and count-chip bookkeeping, so it is not reusable here; the
/// duplication is kept local to this file rather than factored out for a single caller.
/// </summary>
public sealed class OverlayGhostRail : Grid
{
    public event Action<string>? TabSelected;
    public event Action? GearSelected;
    public event Action? CloseSelected;
    public event Action<MouseButtonEventArgs>? DragRequested;

    private sealed class RailIcon
    {
        public string Id = "";
        public Border Root = null!;
        public List<Path> Paths = [];
        public Border Badge = null!;
        public TextBlock BadgeText = null!;
        public Border EdgeBar = null!;
    }

    // Gear glyph (hand-authored, same 1.5 stroke weight as the dock/tab glyphs, from the
    // mock's ICONS map). Viewbox 0 0 24 24; paired with an EllipseGeometry center (12,12) r3.
    private const string GearPathData =
        "M12.22 2h-.44a2 2 0 0 0-2 2v.18a2 2 0 0 1-1 1.73l-.43.25a2 2 0 0 1-2 0l-.15-.08a2 2 0 0 0-2.73.73l-.22.38a2 2 0 0 0 .73 2.73l.15.1a2 2 0 0 1 1 1.72v.51a2 2 0 0 1-1 1.74l-.15.09a2 2 0 0 0-.73 2.73l.22.38a2 2 0 0 0 2.73.73l.15-.08a2 2 0 0 1 2 0l.43.25a2 2 0 0 1 1 1.73V20a2 2 0 0 0 2 2h.44a2 2 0 0 0 2-2v-.18a2 2 0 0 1 1-1.73l.43-.25a2 2 0 0 1 2 0l.15.08a2 2 0 0 0 2.73-.73l.22-.39a2 2 0 0 0-.73-2.73l-.15-.08a2 2 0 0 1-1-1.74v-.5a2 2 0 0 1 1-1.74l.15-.09a2 2 0 0 0 .73-2.73l-.22-.38a2 2 0 0 0-2.73-.73l-.15.08a2 2 0 0 1-2 0l-.43-.25a2 2 0 0 1-1-1.73V4a2 2 0 0 0-2-2z";

    private readonly Path _shell = new();
    private readonly List<RailIcon> _icons = [];
    private Border _gearRoot = null!;
    private List<Path> _gearPaths = [];
    private Border _gearEdgeBar = null!;
    private Border _closeRoot = null!;
    private string? _active;
    private bool _gearOn;

    public OverlayGhostRail()
    {
        _shell.Fill = BuildShellBrush();
        _shell.StrokeThickness = 1;
        _shell.SnapsToDevicePixels = true;
        _shell.SetResourceReference(Shape.StrokeProperty, "BorderBrush");
        Children.Add(_shell);

        var dock = new DockPanel { LastChildFill = false };

        var cap = BuildCap();
        DockPanel.SetDock(cap, Dock.Top);
        dock.Children.Add(cap);

        var close = BuildClose();
        DockPanel.SetDock(close, Dock.Bottom);
        dock.Children.Add(close);

        var gear = BuildGear();
        DockPanel.SetDock(gear, Dock.Bottom);
        dock.Children.Add(gear);

        var iconsPanel = new StackPanel();
        foreach (var id in OverlayTabs.Ids)
        {
            var icon = BuildIcon(id);
            _icons.Add(icon);
            iconsPanel.Children.Add(icon.Root);
        }
        DockPanel.SetDock(iconsPanel, Dock.Top);
        dock.Children.Add(iconsPanel);

        Children.Add(dock);

        SizeChanged += (_, _) => _shell.Data = Hud.ChamferGeometry(ActualWidth, ActualHeight, 8);
    }

    // OverlayBgTopColor/OverlayBgBotColor are raw Colors, not brushes, so the gradient has to be
    // assembled in code (same recipe as WorkOrderFlyoutWindow's panel background).
    private static LinearGradientBrush BuildShellBrush()
    {
        Color Col(string key) => Application.Current.TryFindResource(key) is Color c ? c : Colors.Transparent;
        return new LinearGradientBrush(Col("OverlayBgTopColor"), Col("OverlayBgBotColor"), new Point(0, 0), new Point(0, 1));
    }

    // Beam cap: drag handle at the top of the rail.
    private FrameworkElement BuildCap()
    {
        var beam = new NexusBeamIcon
        {
            Width = 16,
            Height = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var hairline = new Border
        {
            Height = 1,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        hairline.SetResourceReference(Border.BackgroundProperty, "AccentHairlineBrush");
        var cap = new Grid
        {
            Height = 34,
            Background = Brushes.Transparent,   // hit-testable across the whole drag zone
            Cursor = Cursors.SizeAll,
        };
        cap.Children.Add(beam);
        cap.Children.Add(hairline);
        cap.MouseLeftButtonDown += (_, e) => DragRequested?.Invoke(e);
        return cap;
    }

    // Bottom-pinned close glyph: the same "X" character the overlay header's close button uses.
    private Border BuildClose()
    {
        var text = new TextBlock
        {
            Text = "✕",
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "FgDimBrush");

        _closeRoot = new Border
        {
            Width = 26,
            Height = 26,
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = text,
            ToolTip = BuildHoverChip("CLOSE OVERLAY"),
        };
        ToolTipService.SetInitialShowDelay(_closeRoot, 150);
        ToolTipService.SetPlacement(_closeRoot, System.Windows.Controls.Primitives.PlacementMode.Right);
        _closeRoot.MouseEnter += (_, _) => text.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");
        _closeRoot.MouseLeave += (_, _) => text.SetResourceReference(TextBlock.ForegroundProperty, "FgDimBrush");
        _closeRoot.MouseLeftButtonUp += (_, _) => CloseSelected?.Invoke();
        return _closeRoot;
    }

    // Bottom-pinned settings gear, sitting directly above the close glyph.
    private Border BuildGear()
    {
        var canvas = new Canvas { Width = 24, Height = 24 };

        var gearPath = new Path
        {
            Data = Geometry.Parse(GearPathData),
            StrokeThickness = 1.5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        };
        gearPath.SetResourceReference(Shape.StrokeProperty, "FgDimBrush");
        canvas.Children.Add(gearPath);
        _gearPaths.Add(gearPath);

        var gearDot = new Path
        {
            Data = new EllipseGeometry(new Point(12, 12), 3, 3),
            StrokeThickness = 1.5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        };
        gearDot.SetResourceReference(Shape.StrokeProperty, "FgDimBrush");
        canvas.Children.Add(gearDot);
        _gearPaths.Add(gearDot);

        var glyph = new Viewbox { Width = 16, Height = 16, Child = canvas };

        _gearEdgeBar = new Border
        {
            Width = 2,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(-6, 6, 0, 6),
            Visibility = Visibility.Collapsed,
        };
        _gearEdgeBar.SetResourceReference(Border.BackgroundProperty, "AccentBrush");

        var content = new Grid { ClipToBounds = false };
        content.Children.Add(glyph);
        content.Children.Add(_gearEdgeBar);

        _gearRoot = new Border
        {
            Width = 32,
            Height = 32,
            HorizontalAlignment = HorizontalAlignment.Center,
            CornerRadius = new CornerRadius(5),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = content,
            ToolTip = BuildHoverChip("SETTINGS"),
        };
        ToolTipService.SetInitialShowDelay(_gearRoot, 150);
        ToolTipService.SetPlacement(_gearRoot, System.Windows.Controls.Primitives.PlacementMode.Right);
        _gearRoot.MouseEnter += (_, _) => { if (!_gearOn) SetGearBrush("FgBrush"); };
        _gearRoot.MouseLeave += (_, _) => { if (!_gearOn) SetGearBrush("FgDimBrush"); };
        _gearRoot.MouseLeftButtonUp += (_, _) => GearSelected?.Invoke();
        return _gearRoot;
    }

    // One 32x32 tab button: 16px glyph (cropped/stroked per TabGlyphSpecs), top-right count
    // badge, and a left-edge active bar. Mirrors OverlayTabStrip.BuildGlyphWithBadge, minus
    // the pill/dual-representation bookkeeping this rail has no use for.
    private RailIcon BuildIcon(string id)
    {
        var icon = new RailIcon { Id = id };

        var (x, y, w, h) = TabGlyphSpecs.CropFor(id);
        var canvas = new Canvas { Width = w, Height = h };
        foreach (var part in TabGlyphSpecs.PartsFor(id))
        {
            var geometry = BuildGeometry(part);
            if (geometry is null) continue;
            var path = new Path
            {
                Data = geometry,
                StrokeThickness = TabGlyphSpecs.StrokeFor(id),
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                RenderTransform = new TranslateTransform(-x, -y),
            };
            path.SetResourceReference(Shape.StrokeProperty, "FgDimBrush");
            icon.Paths.Add(path);
            canvas.Children.Add(path);
        }
        var glyph = new Viewbox { Width = 16, Height = 16, Child = canvas };

        icon.BadgeText = new TextBlock { FontSize = 8, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center };
        icon.BadgeText.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        icon.BadgeText.SetResourceReference(TextBlock.ForegroundProperty, "OnAccentBrush");
        icon.Badge = new Border
        {
            CornerRadius = new CornerRadius(3),
            MinWidth = 11,
            Height = 11,
            Padding = new Thickness(2, 0, 2, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -3, -6, 0),
            Visibility = Visibility.Collapsed,
            Child = icon.BadgeText,
        };
        icon.Badge.SetResourceReference(Border.BackgroundProperty, "AccentBrush");

        icon.EdgeBar = new Border
        {
            Width = 2,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(-6, 6, 0, 6),
            Visibility = Visibility.Collapsed,
        };
        icon.EdgeBar.SetResourceReference(Border.BackgroundProperty, "AccentBrush");

        var content = new Grid { ClipToBounds = false };
        content.Children.Add(glyph);
        content.Children.Add(icon.Badge);
        content.Children.Add(icon.EdgeBar);

        icon.Root = new Border
        {
            Width = 32,
            Height = 32,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
            CornerRadius = new CornerRadius(5),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = content,
            ToolTip = BuildHoverChip(OverlayTabs.LabelFor(id)),
        };
        ToolTipService.SetInitialShowDelay(icon.Root, 150);
        ToolTipService.SetPlacement(icon.Root, System.Windows.Controls.Primitives.PlacementMode.Right);
        icon.Root.MouseLeftButtonUp += (_, _) => TabSelected?.Invoke(id);
        icon.Root.MouseEnter += (_, _) => { if (_active != id) SetIconBrush(icon, "FgBrush"); };
        icon.Root.MouseLeave += (_, _) => { if (_active != id) SetIconBrush(icon, "FgDimBrush"); };
        return icon;
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

    // Explicit ToolTip with its chrome zeroed out (OverlayTabStrip.BuildHoverChip's pattern). A
    // bare element gets wrapped in a default-styled ToolTip (near-white fill, grey hairline,
    // 6,4 padding), which would halo this dark HUD chip.
    private ToolTip BuildHoverChip(string label)
    {
        var text = new TextBlock { Text = label, FontSize = 8.5 };
        text.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");
        var chip = new Border { Padding = new Thickness(7, 2, 7, 2), BorderThickness = new Thickness(1), Child = text };
        chip.SetResourceReference(Border.BackgroundProperty, "Bg3Brush");
        chip.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        return new ToolTip
        {
            Content = chip,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            HasDropShadow = false,
        };
    }

    private static void SetIconBrush(RailIcon icon, string brushKey)
    {
        foreach (var p in icon.Paths) p.SetResourceReference(Shape.StrokeProperty, brushKey);
    }

    private void SetGearBrush(string brushKey)
    {
        foreach (var p in _gearPaths) p.SetResourceReference(Shape.StrokeProperty, brushKey);
    }

    public void SetActive(string? id)
    {
        _active = id;
        foreach (var icon in _icons)
        {
            bool on = icon.Id == id;
            icon.EdgeBar.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (on)
            {
                icon.Root.SetResourceReference(Border.BackgroundProperty, "AccentActiveFillBrush");
            }
            else
            {
                icon.Root.ClearValue(Border.BackgroundProperty);
                icon.Root.Background = Brushes.Transparent;
            }
            foreach (var p in icon.Paths)
                p.SetResourceReference(Shape.StrokeProperty, on ? "AccentBrush" : "FgDimBrush");
            ToolTipService.SetIsEnabled(icon.Root, !on);
        }
    }

    public void SetGearActive(bool on)
    {
        _gearOn = on;
        _gearEdgeBar.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on)
        {
            _gearRoot.SetResourceReference(Border.BackgroundProperty, "AccentActiveFillBrush");
        }
        else
        {
            _gearRoot.ClearValue(Border.BackgroundProperty);
            _gearRoot.Background = Brushes.Transparent;
        }
        foreach (var p in _gearPaths)
            p.SetResourceReference(Shape.StrokeProperty, on ? "AccentBrush" : "FgDimBrush");
        ToolTipService.SetIsEnabled(_gearRoot, !on);
    }

    public void SetBadge(string id, int count)
    {
        var icon = _icons.Find(i => i.Id == id);
        if (icon is null) return;
        var vis = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        icon.Badge.Visibility = vis;
        icon.BadgeText.Text = count.ToString();
    }

    public void SetExpandDirection(GhostExpandDirection dir)
    {
        var side = dir == GhostExpandDirection.Left
            ? System.Windows.Controls.Primitives.PlacementMode.Left
            : System.Windows.Controls.Primitives.PlacementMode.Right;
        foreach (var icon in _icons) ToolTipService.SetPlacement(icon.Root, side);
        ToolTipService.SetPlacement(_gearRoot, side);
        ToolTipService.SetPlacement(_closeRoot, side);
    }
}
