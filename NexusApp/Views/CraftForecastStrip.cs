using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using NexusApp.Services;

namespace NexusApp.Views;

/// <summary>
/// The Blueprint Library hero's forecast strip: one chip per affected stat, read against a
/// stock (Q500) craft, plus the SET ALL presets. Values and layout follow library-redesign
/// direction 2 (.heroFore, .schip, .presets). The strip only displays; the caller computes
/// the forecast and applies a chosen preset.
/// </summary>
public sealed class CraftForecastStrip : StackPanel
{
    private static readonly int[] PresetValues = [CraftForecast.StockQuality, 1000];
    private static readonly string[] PresetLabels = ["Q500 stock", "Q1000 pristine"];

    private readonly WrapPanel _chips;
    private readonly List<(Border Chip, TextBlock Text, int Value)> _presets = [];
    private readonly Dictionary<string, (TextBlock Value, TextBlock From)> _chipByKey = [];
    private string _chipKeys = "";
    private IReadOnlyList<int> _qualities = [];

    /// <summary>Raised with the preset quality the player chose for every controlled ore.</summary>
    public event Action<int>? PresetChosen;

    public CraftForecastStrip()
    {
        var mono = (FontFamily)Application.Current.FindResource("MonoFont");
        Margin = new Thickness(0, 12, 0, 0);

        Children.Add(new Border { Height = 1, Background = Hud.Br("NavBorderBrush") });
        Children.Add(new TextBlock
        {
            Text = "FORECAST AT YOUR QUALITIES  ·  vs stock craft",
            FontFamily = mono, FontSize = 9.5, Foreground = Hud.Br("AccentBrush"),
            Margin = new Thickness(0, 12, 0, 0),
        });
        // Chip gap is 6 px both ways; the trailing 6 px under the last row comes off the presets margin.
        _chips = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        Children.Add(_chips);

        var presets = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 4, 2, 0),
        };
        presets.Children.Add(new TextBlock
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
            presets.Children.Add(chip);
        }
        Children.Add(presets);
    }


    /// <summary>Shows the forecast rows and lights the preset every controlled ore currently sits at.</summary>
    public void Update(IReadOnlyList<StatForecast> rows, IReadOnlyList<int> slotQualities, bool fade = true)
    {
        _qualities = slotQualities;
        var keys = string.Join("|", rows.Select(r => r.Key));
        if (keys != _chipKeys)
        {
            // A different stat set means a different blueprint: rebuild, no cross-fade.
            _chipKeys = keys;
            _chips.Children.Clear();
            _chipByKey.Clear();
            foreach (var r in rows) _chips.Children.Add(BuildChip(r));
        }
        else
        {
            foreach (var r in rows)
            {
                var (value, from) = _chipByKey[r.Key];
                from.Text = string.Join(" + ", r.Slots);
                var brush = VerdictBrush(r.Verdict);
                if (value.Text == r.DeltaText && value.Foreground == brush) continue;
                value.Text = r.DeltaText;
                value.Foreground = brush;
                if (fade) CrossFade(value);   // a drag preview swaps text and colour without pulsing
            }
        }
        foreach (var (chip, text, v) in _presets) StylePreset(chip, text, v, chip.IsMouseOver);
    }

    private Border BuildChip(StatForecast r)
    {
        var mono = (FontFamily)Application.Current.FindResource("MonoFont");
        var ui = (FontFamily)Application.Current.FindResource("UiFont");
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = r.Name.ToUpperInvariant(), FontFamily = ui, FontSize = 9, Foreground = Hud.Br("FgDimBrush") });
        var value = new TextBlock
        {
            Text = r.DeltaText, FontFamily = mono, FontSize = 13, FontWeight = FontWeights.SemiBold,
            Foreground = VerdictBrush(r.Verdict), Margin = new Thickness(0, 1, 0, 0),
        };
        sp.Children.Add(value);
        var from = new TextBlock
        {
            Text = string.Join(" + ", r.Slots), FontFamily = ui, FontSize = 9, Foreground = Hud.Br("FgDimBrush"),
            Margin = new Thickness(0, 1, 0, 0),
        };
        sp.Children.Add(from);
        _chipByKey[r.Key] = (value, from);
        return new Border
        {
            Child = sp, Background = Hud.Br("Bg3Brush"), BorderBrush = Hud.Br("NavBorderBrush"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 6, 10, 6), MinWidth = 104, Margin = new Thickness(0, 0, 6, 6),
        };
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

    private static Brush VerdictBrush(string verdict) => verdict switch
    {
        "better" => Hud.Br("OkBrush"),
        "worse" => Hud.Br("DangerBrush"),
        _ => Hud.Br("FgDimBrush"),
    };

    // The mock rolls the number; a short fade-in of the new value carries the same "this changed" cue.
    // A drag updates on every mouse step; restarting the fade each step would hold the value
    // dim, so a fade already in flight is left to finish and only the text changes.
    private static void CrossFade(FrameworkElement e)
    {
        if (Motion.Reduced) { e.BeginAnimation(OpacityProperty, null); e.Opacity = 1; return; }
        if (e.Tag is DateTime started && DateTime.UtcNow - started < TimeSpan.FromMilliseconds(Motion.QuickRevealMs)) return;
        e.Tag = DateTime.UtcNow;
        e.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(Motion.QuickRevealMs)) { EasingFunction = Motion.Settle });
    }

    private void StylePreset(Border chip, TextBlock text, int value, bool hover)
    {
        bool on = PresetActive(_qualities, value);
        chip.BorderBrush = on ? Hud.Br("AccentStrongBrush") : Hud.Br("NavBorderBrush");
        chip.Background = hover ? Hud.Br("HighlightBrush") : Brushes.Transparent;
        text.Foreground = on ? Hud.Br("AccentBrush") : hover ? Hud.Br("FgBrush") : Hud.Br("FgDimBrush");
    }
}
