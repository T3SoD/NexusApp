using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NexusApp.Views;

// Shown after a Game.log "import owned from past logs" scan. Lists the blueprints that
// will be marked owned, plus any names that weren't recognized — with Copy / Export so the
// user can send them to the maintainer to get the mapping fixed. ShowDialog() returns
// true when the user chose to mark the matched blueprints owned.
public sealed class ImportResultDialog : Window
{
    private readonly string _reportPayload;

    public ImportResultDialog(IReadOnlyList<string> matched, IReadOnlyList<string> unmatched, int filesScanned, DateTime? earliestUtc, string reportPayload)
    {
        _reportPayload = reportPayload;

        Title = "Import owned blueprints (Beta)";
        Width = 560; Height = 560; MinWidth = 460; MinHeight = 400;
        Background = (Brush)Application.Current.FindResource("BgBrush");
        Foreground = (Brush)Application.Current.FindResource("FgBrush");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { DialogResult = false; } };

        bool anyMatched = matched.Count > 0;

        var outer = new Grid();
        outer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 12) };

        panel.Children.Add(new TextBlock
        {
            Text = anyMatched
                ? $"Found {matched.Count} blueprint(s) across {filesScanned} log file(s)."
                : $"No known blueprints found across {filesScanned} log file(s).",
            FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.FindResource("FgBrush"),
            Margin = new Thickness(0, 0, 0, 4),
        });

        // How far back the scan could actually see. Star Citizen overwrites old logs as you play, so
        // anything received before this point simply isn't in the files; this sets that expectation.
        if (earliestUtc.HasValue)
        {
            var oldest = earliestUtc.Value.ToString("d MMM yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture);
            panel.Children.Add(new TextBlock
            {
                Text = $"Oldest log data read: {oldest} UTC. Blueprints received before then aren't in these logs " +
                       "(Star Citizen overwrites older logs as you play).",
                FontSize = 11, TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.FindResource("FgDimBrush"),
                Margin = new Thickness(0, 0, 0, 4),
            });
        }

        if (anyMatched)
        {
            panel.Children.Add(SectionLabel("WILL BE MARKED OWNED"));
            panel.Children.Add(NamesBox(matched));
        }

        if (unmatched.Count > 0)
        {
            panel.Children.Add(SectionLabel($"NOT RECOGNIZED — SKIPPED ({unmatched.Count})"));
            panel.Children.Add(new TextBlock
            {
                Text = "These names from your log don't match Nexus's blueprint data, so they can't be marked owned. " +
                       "Copy or export them and send them to T3SoD on Discord to get them added.",
                FontSize = 11, TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.FindResource("FgDimBrush"),
                Margin = new Thickness(0, 0, 0, 6),
            });
            panel.Children.Add(NamesBox(unmatched));

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            actions.Children.Add(MakeButton("Copy", (_, _) => CopyToClipboard()));
            actions.Children.Add(MakeButton("Export…", (_, _) => Export(), leftMargin: 8));
            panel.Children.Add(actions);
        }

        outer.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel });

        // Footer
        var footer = new Border
        {
            BorderBrush = (Brush)Application.Current.FindResource("NavBorderBrush"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(20, 12, 20, 12),
        };
        Grid.SetRow(footer, 1);
        var footRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        if (anyMatched)
        {
            footRow.Children.Add(MakeButton("Cancel", (_, _) => { DialogResult = false; }));
            footRow.Children.Add(MakeButton($"Mark {matched.Count} owned", (_, _) => { DialogResult = true; }, accent: true, leftMargin: 8));
        }
        else
        {
            footRow.Children.Add(MakeButton("Close", (_, _) => { DialogResult = false; }));
        }
        footer.Child = footRow;
        outer.Children.Add(footer);

        Content = outer;
    }

    private static TextBlock SectionLabel(string text) => new()
    {
        Text = text, FontSize = 11, FontWeight = FontWeights.Bold,
        Foreground = (Brush)Application.Current.FindResource("AccentBrush"),
        Margin = new Thickness(0, 14, 0, 6),
    };

    // A read-only, selectable, scrollable box of the full list of names (so the user can hand-pick too).
    private static UIElement NamesBox(IReadOnlyList<string> names)
    {
        return new TextBox
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

    private void CopyToClipboard()
    {
        try
        {
            Clipboard.SetText(_reportPayload);
            MessageBox.Show(this, "Unrecognized names copied — paste them into a Discord message to T3SoD.",
                "Copied", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't copy to the clipboard: {ex.Message}", "Copy failed",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Export()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Text (*.txt)|*.txt",
            FileName = $"nexus_unrecognized_blueprints_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, _reportPayload);
            MessageBox.Show(this, $"Saved to {dlg.FileName}", "Exported", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Export failed: {ex.Message}", "Export failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
