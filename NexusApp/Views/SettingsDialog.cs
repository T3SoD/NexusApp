using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NexusApp.Services;

namespace NexusApp.Views;

// Central settings, opened from the header gear button: appearance/theme, the Game.log
// blueprint monitor (Beta), and the destructive "clear saved data" action. The theme-card
// and clear-data logic moved here out of AboutDialog, which is now just info + changelog.
public class SettingsDialog : Window
{
    private readonly Action _openLogMonitor;

    public SettingsDialog(Action openLogMonitor)
    {
        _openLogMonitor = openLogMonitor;

        Title = "Settings";
        Width = 600; Height = 520;
        PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };
        Background = (Brush)Application.Current.FindResource("BgBrush");
        Foreground = (Brush)Application.Current.FindResource("FgBrush");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;

        var outer = new Grid();
        outer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var panel = new StackPanel { Margin = new Thickness(28, 24, 28, 16) };

        // ── Appearance ────────────────────────────────────────────────────────
        panel.Children.Add(SectionHeader("APPEARANCE"));
        panel.Children.Add(SectionBlurb(
            "Switch between the refreshed Nexus look and the classic v4 style. Applies on restart."));
        var themeRow = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(themeRow);
        BuildThemeOptions(themeRow);

        panel.Children.Add(Divider());

        // ── Game.log (Beta) ───────────────────────────────────────────────────
        panel.Children.Add(SectionHeader("GAME.LOG (BETA)"));
        panel.Children.Add(SectionBlurb(
            "Track your session from Star Citizen's Game.log: auto-collect blueprints you receive " +
            "(they're marked Owned in your library), or import the ones you already own from past logs."));
        var openLogBtn = new Button
        {
            Content = "Open Game.log Monitor",
            Style = (Style)Application.Current.FindResource("NexusButton"),
            Padding = new Thickness(16, 8, 16, 8),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        openLogBtn.Click += (s, e) => { _openLogMonitor?.Invoke(); Close(); };
        panel.Children.Add(openLogBtn);

        panel.Children.Add(Divider());

        // ── Data ──────────────────────────────────────────────────────────────
        panel.Children.Add(SectionHeader("DATA"));
        panel.Children.Add(SectionBlurb(
            "Clear everything you've saved in Nexus — owned blueprints, shopping cart, " +
            "work orders and pinned resources. Your theme and the mining reference data are not affected."));

        var danger = new SolidColorBrush(Color.FromRgb(0xE5, 0x53, 0x53));
        var clearBtn = new Border
        {
            Background = Brushes.Transparent,
            BorderBrush = danger, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(16, 8, 16, 8),
            HorizontalAlignment = HorizontalAlignment.Left, Cursor = Cursors.Hand,
            Child = new TextBlock { Text = "Clear saved data…", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = danger },
        };
        clearBtn.MouseEnter += (s, e) => clearBtn.Background = new SolidColorBrush(Color.FromArgb(0x18, 0xE5, 0x53, 0x53));
        clearBtn.MouseLeave += (s, e) => clearBtn.Background = Brushes.Transparent;
        clearBtn.MouseLeftButtonUp += (s, e) => ClearSavedData();
        panel.Children.Add(clearBtn);

        scroll.Content = panel;
        outer.Children.Add(scroll);

        // Footer close button
        var footer = new Border
        {
            BorderBrush = (Brush)Application.Current.FindResource("NavBorderBrush"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(20, 12, 20, 12),
        };
        Grid.SetRow(footer, 1);
        var closeBtn = new Button
        {
            Content = "Close",
            Style = (Style)Application.Current.FindResource("NexusButton"),
            Padding = new Thickness(20, 8, 20, 8),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        closeBtn.Click += (s, e) => Close();
        footer.Child = closeBtn;
        outer.Children.Add(footer);

        Content = outer;
    }

    private static TextBlock SectionHeader(string text) => new()
    {
        Text = text, FontSize = 11, FontWeight = FontWeights.Bold,
        Foreground = (Brush)Application.Current.FindResource("AccentBrush"),
        Margin = new Thickness(0, 0, 0, 6),
    };

    private static TextBlock SectionBlurb(string text) => new()
    {
        Text = text, FontSize = 11, TextWrapping = TextWrapping.Wrap,
        Foreground = (Brush)Application.Current.FindResource("FgDimBrush"),
        Margin = new Thickness(0, 0, 0, 12),
    };

    private static Border Divider() => new()
    {
        Height = 1, Margin = new Thickness(0, 18, 0, 16),
        Background = (Brush)Application.Current.FindResource("NavBorderBrush"),
    };

    // ── Saved data ────────────────────────────────────────────────────────────
    private void ClearSavedData()
    {
        var confirm = MessageBox.Show(
            this,
            "This permanently deletes all of your saved data:\n\n" +
            "    •  Owned blueprints\n" +
            "    •  Shopping cart\n" +
            "    •  Work orders\n" +
            "    •  Pinned resources\n\n" +
            "Your theme/window settings and the mining reference data are kept.\n\n" +
            "This cannot be undone. Are you sure?",
            "Clear all saved data?",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;

        App.Data.ClearShoppingList();
        App.Data.ClearWorkOrders();
        App.Data.ClearAllPins();
        App.Settings.ClearOwnedBlueprints();
        App.Settings.ClearPinnedResources();

        var restart = MessageBox.Show(
            this,
            "All saved data has been cleared.\n\nNexus needs to restart to refresh. Restart now?",
            "Data cleared",
            MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.Yes);
        if (restart == MessageBoxResult.Yes)
            ThemeService.RestartApp();
    }

    // ── Appearance helpers ──────────────────────────────────────────────────────
    private static void BuildThemeOptions(StackPanel row)
    {
        row.Children.Clear();
        row.Children.Add(ThemeCard(row, "luxury", "Luxury Gold", "v5 — near-black + warm gold",
            new[] { "#0E0E13", "#C9A24B", "#D9B25C", "#ECE7DD" }));
        row.Children.Add(ThemeCard(row, "classic", "Classic", "v4 — slate + teal + amber",
            new[] { "#0D1117", "#00C9A7", "#E8A23A", "#E6EDF3" }));
    }

    private static Border ThemeCard(StackPanel row, string key, string title, string subtitle, string[] swatches)
    {
        bool active = ThemeService.Current == key;
        bool pending = ThemeService.Pending == key;
        var card = new Border
        {
            Width = 184, Margin = new Thickness(0, 0, 12, 0), Padding = new Thickness(14, 12, 14, 12),
            CornerRadius = new CornerRadius(10),
            Background = (Brush)Application.Current.FindResource("Bg2NavBrush"),
            BorderBrush = (Brush)Application.Current.FindResource(active || pending ? "AccentBrush" : "NavBorderBrush"),
            BorderThickness = new Thickness(active || pending ? 2 : 1),
            Cursor = Cursors.Hand,
        };
        var sp = new StackPanel();

        var titleRow = new Grid();
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleRow.Children.Add(new TextBlock
        {
            Text = title, FontFamily = (FontFamily)Application.Current.FindResource("HeadFont"),
            FontSize = 15, Foreground = (Brush)Application.Current.FindResource("FgBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (active || pending)
        {
            var badge = new Border
            {
                Background = (Brush)Application.Current.FindResource("AccentBrush"),
                CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = active ? "ACTIVE" : "ON RESTART", FontSize = 8, FontWeight = FontWeights.Bold, Foreground = (Brush)Application.Current.FindResource("OnAccentBrush") },
            };
            Grid.SetColumn(badge, 1); titleRow.Children.Add(badge);
        }
        sp.Children.Add(titleRow);

        sp.Children.Add(new TextBlock
        {
            Text = subtitle, FontSize = 10, TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.FindResource("FgDimBrush"),
            Margin = new Thickness(0, 3, 0, 10),
        });

        var sw = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var hex in swatches)
            sw.Children.Add(new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 5, 0),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
                BorderBrush = (Brush)Application.Current.FindResource("NavBorderBrush"), BorderThickness = new Thickness(1),
            });
        sp.Children.Add(sw);

        card.Child = sp;
        card.MouseLeftButtonUp += (s, e) =>
        {
            if (key == ThemeService.Current && ThemeService.Pending == null) return;
            ThemeService.SelectForRestart(key);
            BuildThemeOptions(row);
            var res = MessageBox.Show(
                "The theme changes the next time Nexus starts.\n\nRestart now to apply it?",
                "Restart required", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (res == MessageBoxResult.Yes)
                ThemeService.RestartApp();
        };
        return card;
    }
}
