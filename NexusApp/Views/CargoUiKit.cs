using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NexusApp.Models.Cargo;

namespace NexusApp.Views;

// Shared MOBIGLAS styling for the cargo module screens (Cargo Planner + Grid Studio): the amber-on-void
// palette and the small styled-control builders both pages use. Pulled out of the two pages (which held
// byte-identical copies) so the look is single-sourced. Pages pull these in with
// `using static NexusApp.Views.CargoUiKit;`, so call sites stay terse (Btn(...), Eyebrow(...), Amber).
internal static class CargoUiKit
{
    public static readonly Color Bg = Color.FromRgb(0x05, 0x07, 0x0A);   // deepest "void" background
    public static readonly Color Panel = Color.FromRgb(0x0B, 0x10, 0x17);
    public static readonly Color Line = Color.FromRgb(0x1C, 0x28, 0x36);
    public static readonly Color Amber = Color.FromRgb(0xFF, 0xB2, 0x3E);
    public static readonly Color Cyan = Color.FromRgb(0x7F, 0xE9, 0xE0);
    public static readonly Color Fg = Color.FromRgb(0xEA, 0xF1, 0xF6);
    public static readonly Color Dim = Color.FromRgb(0x7C, 0x8A, 0x99);
    public static readonly Color Warn = Color.FromRgb(0xFF, 0x8A, 0x3D);

    public static FontFamily Mono => (FontFamily)Application.Current.FindResource("MonoFont");
    public static FontFamily Head => (FontFamily)Application.Current.FindResource("HeadFont");

    public static TextBlock Eyebrow(string t) => new()
    {
        Text = t, Foreground = new SolidColorBrush(Amber), FontFamily = Head, FontSize = 10.5,
        FontWeight = FontWeights.SemiBold,
    };

    public static Button Btn(string text, RoutedEventHandler onClick, bool primary = false)
    {
        var b = new Button
        {
            Content = text, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 6, 0),
            Cursor = Cursors.Hand, FontFamily = Mono, FontSize = 11,
            Foreground = new SolidColorBrush(primary ? Bg : Fg),
            Background = new SolidColorBrush(primary ? Amber : Color.FromRgb(0x14, 0x1B, 0x24)),
            BorderBrush = new SolidColorBrush(primary ? Amber : Line),
            BorderThickness = new Thickness(1),
        };
        b.Click += onClick;
        return b;
    }

    public static void StyleCombo(ComboBox c)
    {
        // Use the app's themed dark ComboBox (fully templated popup + readable items); the default
        // WPF template ignores a plain Background, which is why raw styling was invisible.
        if (Application.Current.TryFindResource("NexusComboBox") is Style s) c.Style = s;
        c.FontFamily = Mono;
        c.FontSize = 11.5;
    }

    public static void StyleBox(TextBox t)
    {
        t.Foreground = new SolidColorBrush(Fg);
        t.Background = new SolidColorBrush(Color.FromRgb(0x0E, 0x14, 0x1C));
        t.BorderBrush = new SolidColorBrush(Line);
        t.BorderThickness = new Thickness(1);
        t.Padding = new Thickness(6, 4, 6, 4);
        t.FontFamily = Mono; t.FontSize = 11.5;
        t.CaretBrush = new SolidColorBrush(Cyan);
    }

    public static void StyleGhost(Button b)
    {
        b.Foreground = new SolidColorBrush(Dim);
        b.Background = Brushes.Transparent;
        b.BorderBrush = new SolidColorBrush(Line);
        b.BorderThickness = new Thickness(1);
        b.FontFamily = Mono; b.FontSize = 10;
    }

    public static Border RailBorder(UIElement child) => new()
    {
        Background = new SolidColorBrush(Panel),
        BorderBrush = new SolidColorBrush(Line),
        BorderThickness = new Thickness(1),
        Child = child,
    };
}

// One row in a ship-picker combo: the ship plus an optional status marker prefix. Shared by both
// cargo pages (its ToString is what the ComboBox renders).
internal sealed class ShipRow
{
    public ShipCargoDef Ship { get; }
    private readonly string _marker;   // e.g. "[OK] " signed off, "[!] " flagged, "" unreviewed
    public ShipRow(ShipCargoDef ship, string marker = "") { Ship = ship; _marker = marker; }
    public override string ToString() => $"{_marker}{Ship.DisplayName}  ({Ship.TotalScu} SCU)";
}
