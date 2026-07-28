using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NexusApp.Services;

namespace NexusApp.Views;

/// <summary>
/// Preview/confirm gate shown before an "Import from SCMDB" run applies anything (issue #3).
/// Computed entirely from the plan BEFORE any ownership write - Cancel (or the zero-toImport
/// "Close") leaves Settings untouched; the accent button confirms and ScmdbImportFlow applies the
/// ToImport bucket only after this returns true. Reports all five counts: would-import, already
/// owned, unrecognized (with the raw names listed), skipped-not-completed, and malformed entries,
/// plus the mission-data and newer-version notices. Same visual chrome AND the same Cancel/"Mark N
/// owned" footer convention as ImportResultDialog (that class stays untouched). AMENDMENT 2:
/// this was briefly a post-apply summary (immediate-apply ruling); the owner reversed that, so this is
/// now a real gate, matching the Game.log import pattern.
/// </summary>
public sealed class ScmdbImportResultDialog : Window
{
    public ScmdbImportResultDialog(int toImportCount, int alreadyOwned, IReadOnlyList<string> unrecognized,
        int skippedNotCompleted, int malformedEntries, int missionCount, bool newerVersion)
    {
        Title = "Import from SCMDB";
        Width = 560; Height = 520; MinWidth = 460; MinHeight = 380;
        Background = (Brush)Application.Current.FindResource("BgBrush");
        Foreground = (Brush)Application.Current.FindResource("FgBrush");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { DialogResult = false; } };

        bool anyToImport = toImportCount > 0;

        var outer = new Grid();
        outer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 12) };

        panel.Children.Add(new TextBlock
        {
            Text = anyToImport
                ? $"Found {toImportCount} blueprint(s) to mark owned from your SCMDB export."
                : "No new blueprints to mark owned from this export.",
            FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.FindResource("FgBrush"),
            Margin = new Thickness(0, 0, 0, 4),
        });

        if (alreadyOwned > 0)
            panel.Children.Add(Line($"{alreadyOwned} were already marked owned - no change."));
        if (skippedNotCompleted > 0)
            panel.Children.Add(Line($"{skippedNotCompleted} entr{(skippedNotCompleted == 1 ? "y" : "ies")} not yet completed on SCMDB - not imported."));
        if (malformedEntries > 0)
            panel.Children.Add(Line($"{malformedEntries} entr{(malformedEntries == 1 ? "y" : "ies")} in the file couldn't be read - skipped."));
        if (missionCount > 0)
            panel.Children.Add(Line("Mission data present in the export - not imported (Nexus only imports blueprints)."));
        if (newerVersion)
            panel.Children.Add(Line("This export was made by a newer version of SCMDB's export format than this build understands - everything recognizable was still imported."));

        if (unrecognized.Count > 0)
        {
            panel.Children.Add(SectionLabel($"NOT RECOGNIZED - SKIPPED ({unrecognized.Count})"));
            panel.Children.Add(new TextBlock
            {
                Text = "These names from your SCMDB export don't match Nexus's blueprint data, so they weren't marked owned.",
                FontSize = 11, TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.FindResource("FgDimBrush"),
                Margin = new Thickness(0, 0, 0, 6),
            });
            panel.Children.Add(NamesBox(unrecognized));
        }

        outer.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel });

        var footer = new Border
        {
            BorderBrush = (Brush)Application.Current.FindResource("NavBorderBrush"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(20, 12, 20, 12),
        };
        Grid.SetRow(footer, 1);
        var footRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        if (anyToImport)
        {
            footRow.Children.Add(MakeButton("Cancel", (_, _) => { DialogResult = false; }));
            footRow.Children.Add(MakeButton($"Mark {toImportCount} owned", (_, _) => { DialogResult = true; }, accent: true, leftMargin: 8));
        }
        else
        {
            footRow.Children.Add(MakeButton("Close", (_, _) => { DialogResult = false; }));
        }
        footer.Child = footRow;
        outer.Children.Add(footer);

        Content = outer;
        DialogMotion.Attach(this);
        UiScaleService.ApplyToDialog(this, outer);   // App scale (issue #20)
    }

    private Button MakeButton(string text, RoutedEventHandler onClick, bool accent = false, double leftMargin = 0)
    {
        var b = new Button
        {
            Content = text,
            Style = (Style)Application.Current.FindResource(accent ? "AccentButton" : "NexusButton"),
            Padding = new Thickness(16, 7, 16, 7),
            Margin = new Thickness(leftMargin, 0, 0, 0),
        };
        b.Click += onClick;
        return b;
    }

    private static TextBlock Line(string text) => new()
    {
        Text = text, FontSize = 11.5, TextWrapping = TextWrapping.Wrap,
        Foreground = (Brush)Application.Current.FindResource("FgDimBrush"),
        Margin = new Thickness(0, 4, 0, 0),
    };

    private static TextBlock SectionLabel(string text) => new()
    {
        Text = text, FontSize = 11, FontWeight = FontWeights.Bold,
        Foreground = (Brush)Application.Current.FindResource("AccentBrush"),
        Margin = new Thickness(0, 14, 0, 6),
    };

    // Read-only, selectable, scrollable box of the full unrecognized-name list.
    private static UIElement NamesBox(IReadOnlyList<string> names) => new TextBox
    {
        Text = string.Join(Environment.NewLine, names),
        IsReadOnly = true,
        TextWrapping = TextWrapping.NoWrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        MaxHeight = 170,
        FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace"),
        FontSize = 12,
        Background = (Brush)Application.Current.FindResource("Bg2NavBrush"),
        Foreground = (Brush)Application.Current.FindResource("FgBrush"),
        BorderBrush = (Brush)Application.Current.FindResource("NavBorderBrush"),
        BorderThickness = new Thickness(1),
        Padding = new Thickness(8, 6, 8, 6),
    };
}
