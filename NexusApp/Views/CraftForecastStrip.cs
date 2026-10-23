using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NexusApp.Services;

namespace NexusApp.Views;

/// <summary>
/// The SET ALL presets of the Blueprint Library CRAFTING view (round 5 design 3, mock .presets):
/// one left-aligned row with no hairline, label or chips. The strip only displays; the caller
/// applies a chosen preset.
/// </summary>
public sealed class CraftForecastStrip : StackPanel
{
    private static readonly int[] PresetValues = [CraftForecast.StockQuality, 1000];
    private static readonly string[] PresetLabels = ["Q500 stock", "Q1000 pristine"];

    private readonly StackPanel _presetRow;
    private readonly List<(Border Chip, TextBlock Text, int Value)> _presets = [];
    private IReadOnlyList<int> _qualities = [];

    /// <summary>Raised with the preset quality the player chose for every controlled ore.</summary>
    public event Action<int>? PresetChosen;

    public CraftForecastStrip()
    {
        var mono = (FontFamily)Application.Current.FindResource("MonoFont");
        Margin = new Thickness(0, 12, 0, 0);

        // Mock .presets margin 12px 2px 0 (the 12 is this panel's own top margin).
        _presetRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(2, 0, 2, 0),
        };
        _presetRow.Children.Add(new TextBlock
        {
            Text = "SET ALL", FontFamily = mono, FontSize = 10, Foreground = Hud.Br("FgDimBrush"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
        });
        for (int i = 0; i < PresetValues.Length; i++)
        {
            int value = PresetValues[i];
            var text = new TextBlock { Text = PresetLabels[i], FontFamily = mono, FontSize = 11, Foreground = Hud.Br("FgDimBrush") };
            var chip = new Border
            {
                Child = text, Padding = new Thickness(10, 5, 10, 5), CornerRadius = new CornerRadius(4),
                BorderBrush = Hud.Br("NavBorderBrush"), BorderThickness = new Thickness(1),
                Background = Brushes.Transparent, Cursor = Cursors.Hand,
                Margin = new Thickness(i == 0 ? 0 : 6, 0, 0, 0),
                ToolTip = $"Set every ore on this blueprint to Q{value}",
            };
            chip.MouseEnter += (_, _) => StylePreset(chip, text, value, hover: true);
            chip.MouseLeave += (_, _) => StylePreset(chip, text, value, hover: false);
            // Under capture IsMouseOver stays true off the chip, so hover follows a bounds test instead.
            chip.MouseMove += (_, e) =>
            {
                if (chip.IsMouseCaptured) StylePreset(chip, text, value, hover: Inside(chip, e));
            };
            // Acts on release over the chip, like a button: a press that slides off does nothing.
            chip.MouseLeftButtonDown += (_, e) => { chip.CaptureMouse(); e.Handled = true; };
            chip.MouseLeftButtonUp += (s, e) =>
            {
                bool wasPressed = chip.IsMouseCaptured;
                bool inside = Inside(chip, e);
                chip.ReleaseMouseCapture();
                StylePreset(chip, text, value, hover: inside);
                if (!wasPressed) return;
                e.Handled = true;
                if (!inside) return;
                InteractionLog.Click($"forecast preset Q{value}", (DependencyObject)s);
                PresetChosen?.Invoke(value);
            };
            _presets.Add((chip, text, value));
            _presetRow.Children.Add(chip);
        }
        Children.Add(_presetRow);
    }

    /// <summary>Lights the preset every controlled ore currently sits at.</summary>
    public void Update(IReadOnlyList<int> slotQualities)
    {
        _qualities = slotQualities;
        foreach (var (chip, text, v) in _presets) StylePreset(chip, text, v, chip.IsMouseOver);
    }

    // Same release rule as ButtonBase: the pointer must still be within the chip's own bounds.
    private static bool Inside(FrameworkElement el, MouseEventArgs e)
    {
        var p = e.GetPosition(el);
        return p.X >= 0 && p.Y >= 0 && p.X <= el.ActualWidth && p.Y <= el.ActualHeight;
    }

    /// <summary>A preset is lit only when there is at least one controlled ore and every one sits at the preset.</summary>
    internal static bool PresetActive(IReadOnlyList<int> qualities, int preset) =>
        qualities.Count > 0 && qualities.All(q => q == preset);

    private void StylePreset(Border chip, TextBlock text, int value, bool hover)
    {
        bool on = PresetActive(_qualities, value);
        chip.BorderBrush = on ? Hud.Br("AccentStrongBrush") : Hud.Br("NavBorderBrush");
        chip.Background = hover ? Hud.Br("HighlightBrush") : Brushes.Transparent;
        text.Foreground = on ? Hud.Br("AccentBrush") : hover ? Hud.Br("FgBrush") : Hud.Br("FgDimBrush");
    }
}
