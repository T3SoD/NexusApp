using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using NexusApp.Services;

namespace NexusApp.Views;

/// <summary>One setting on the chip bar: what it is called, what it currently says, whether it is
/// actually constraining the results, and the control that edits it.</summary>
internal sealed class FilterChipDef
{
    public string Key { get; init; } = "";
    /// <summary>Current value, read fresh on every Refresh so a chip never shows a stale figure.</summary>
    public Func<string> Value { get; init; } = () => "";
    /// <summary>True when this setting is narrowing the results. Drives the amber treatment: the
    /// whole point of the bar is that "ANY" and "C2 Hercules" do not look alike.</summary>
    public Func<bool> IsSet { get; init; } = () => false;
    /// <summary>The existing field group, moved into this chip's popover. Never rebuilt: the
    /// controls keep their identity, their handlers and their focus behaviour.</summary>
    public FrameworkElement Content { get; init; } = null!;
    public double PopoverWidth { get; init; } = 264;
}

/// <summary>
/// The Trade filter bar (concept A, approved 2026-08-10). Replaces the collapsible FILTERS shelf:
/// every setting is a chip carrying its own value, and clicking one opens a popover holding just
/// that control.
///
/// <para>WHY THIS SHAPE. The shelf's collapsed line was a comma-joined string, so it could not say
/// WHICH setting a value belonged to, nor which settings were actually constraining the results.
/// That is the job of a summary that stands in for hidden controls, and it is the defect this
/// fixes: a chip names its setting, shows its value, and goes amber only when it is doing
/// something. There is no expand or collapse at all, because nothing is ever hidden.</para>
///
/// <para>POPUPS ARE StaysOpen = TRUE ON PURPOSE. This page already has an open defect where a
/// CommodityPickerBox dropdown closes inside the click that opened it, and three of these popovers
/// contain exactly that control. A self-closing popup nested around a self-closing popup makes that
/// worse and is untestable headless. Dismissal is therefore explicit and owned here: clicking the
/// chip again, opening another chip, or pressing the mouse anywhere outside both. No keyboard
/// dismissal, per the house rule against shortcuts.</para>
/// </summary>
internal sealed class FilterChipBar : UserControl
{
    private readonly List<(FilterChipDef Def, Border Chip, TextBlock Value, Popup Pop, RotateTransform Arrow)> _chips = new();
    private readonly string _flowName;
    private Popup? _open;

    public FilterChipBar(IReadOnlyList<FilterChipDef> defs, string flowName)
    {
        _flowName = flowName;

        var row = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        foreach (var def in defs) row.Children.Add(BuildChip(def));
        Content = row;

        // Outside-press dismissal. Handled on the PREVIEW tunnel at the window level so it runs
        // before the click reaches whatever was clicked, and only closes when the press landed
        // outside both the open popover and its chip.
        Loaded += (_, _) =>
        {
            if (Window.GetWindow(this) is { } w)
                w.PreviewMouseDown += OnWindowPreviewMouseDown;
        };
        Unloaded += (_, _) =>
        {
            if (Window.GetWindow(this) is { } w)
                w.PreviewMouseDown -= OnWindowPreviewMouseDown;
        };
    }

    private Border BuildChip(FilterChipDef def)
    {
        var key = new TextBlock
        {
            Text = def.Key, FontFamily = Hud.Font("MonoFont"), FontSize = 8,
            Foreground = Hud.Br("FgDimBrush"), VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        };
        var value = new TextBlock
        {
            FontFamily = Hud.Font("UiFont"), FontSize = 10.5, FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center, MaxWidth = 190,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var arrowT = new RotateTransform(0);
        var arrow = new System.Windows.Shapes.Path
        {
            Width = 8, Height = 8, Data = Geometry.Parse("M5,3 L11,8 L5,13"),   // the app's own chevron
            Stroke = Hud.Br("FgDimBrush"), StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            Stretch = Stretch.Uniform, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = arrowT,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0),
            IsHitTestVisible = false,
        };

        var inner = new StackPanel { Orientation = Orientation.Horizontal };
        inner.Children.Add(key);
        inner.Children.Add(value);
        inner.Children.Add(arrow);

        var chip = new Border
        {
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3),
            Padding = new Thickness(9, 3, 9, 3), Margin = new Thickness(0, 0, 6, 6),
            Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = inner,
        };

        // The popover: the field group itself, on the house chamfered panel.
        var body = new Border
        {
            Background = Hud.Br("Bg2NavBrush"), BorderBrush = Hud.Br("AccentStrongBrush"),
            BorderThickness = new Thickness(1), Padding = new Thickness(12, 10, 12, 12),
            Width = def.PopoverWidth, Child = def.Content,
        };
        var pop = new Popup
        {
            PlacementTarget = chip, Placement = PlacementMode.Bottom, HorizontalOffset = 0, VerticalOffset = 4,
            StaysOpen = true,   // see the class comment: dismissal is owned here, not by WPF
            AllowsTransparency = true, Child = body,
        };

        chip.MouseLeftButtonUp += (_, _) => Toggle(pop, arrowT, def.Key);
        _chips.Add((def, chip, value, pop, arrowT));
        return chip;
    }

    private void Toggle(Popup pop, RotateTransform arrow, string key)
    {
        if (pop.IsOpen) { Close(pop, arrow); return; }
        CloseOpen();
        pop.IsOpen = true;
        _open = pop;
        Spin(arrow, 90);
        Logger.Info($"[UI] trade filter opened: {key} ({_flowName})");
    }

    private void Close(Popup pop, RotateTransform arrow)
    {
        pop.IsOpen = false;
        Spin(arrow, 0);
        if (ReferenceEquals(_open, pop)) _open = null;
    }

    private void CloseOpen()
    {
        if (_open is null) return;
        foreach (var c in _chips)
            if (ReferenceEquals(c.Pop, _open)) { Close(c.Pop, c.Arrow); return; }
        _open.IsOpen = false;
        _open = null;
    }

    private static void Spin(RotateTransform t, double to)
    {
        if (Motion.Reduced) { t.BeginAnimation(RotateTransform.AngleProperty, null); t.Angle = to; return; }
        t.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(to, TimeSpan.FromMilliseconds(Motion.ChipFadeMs)) { EasingFunction = Motion.Settle });
    }

    private void OnWindowPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_open is null || e.OriginalSource is not DependencyObject src) return;
        // Inside the open popover: leave it alone. Popup content lives in its own visual tree, so
        // this walks up from the pressed element and checks for the popup's own child.
        if (IsWithin(src, (Visual?)_open.Child)) return;
        // On a chip: its own handler owns the toggle, and closing here first would make a
        // second click on the SAME chip look like a no-op (close then immediately reopen).
        foreach (var c in _chips)
            if (IsWithin(src, c.Chip)) return;
        CloseOpen();
    }

    private static bool IsWithin(DependencyObject? node, Visual? ancestor)
    {
        if (ancestor is null) return false;
        while (node is not null)
        {
            if (ReferenceEquals(node, ancestor)) return true;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }

    /// <summary>Re-reads every chip's value and set-state. Called from each flow's own rebuild, in
    /// place of the shelf's old summary refresh, so a snapshot-driven correction (an unresolved
    /// start, a commodity the hourly refresh dropped) reaches the bar immediately.</summary>
    public void Refresh()
    {
        foreach (var (def, chip, value, _, _) in _chips)
        {
            var text = def.Value();
            var set = def.IsSet();
            value.Text = string.IsNullOrWhiteSpace(text) ? "ANY" : text;
            value.Foreground = set ? Hud.Br("AccentBrush") : Hud.Br("FgDimBrush");
            chip.BorderBrush = set ? Hud.Br("AccentStrongBrush") : Hud.Br("NavBorderBrush");
            chip.Background = set ? Hud.Br("AccentFaintBrush") : Brushes.Transparent;
            chip.ToolTip = $"{def.Key}: {value.Text}";
        }
    }
}
