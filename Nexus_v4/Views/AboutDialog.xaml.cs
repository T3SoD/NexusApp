using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Nexus_v4.Views;

public class AboutDialog : Window
{
    private static readonly (string Label, string[] Changes)[] Changelog =
    [
        ("App  4.2.1  —  Jun 03, 2026",
        [
            "Test release — verifying the Discord changelog notifier; no functional changes",
        ]),
        ("App  4.2.0  —  Jun 02, 2026",
        [
            "Recent scan history now has a filter — choose All, Exact + Close, or Exact only in both the main app and the overlay",
        ]),
        ("App  4.1.0  —  May 31, 2026",
        [
            "Auto-scan — reads RS values directly from the screen while you play; draw a region once and it updates every ~0.5 seconds automatically",
            "Fixed OCR misses on RS values in the 2,000–9,999 range where the thousands comma was read as a space",
            "Shopping cart highlights — scan result cards and history entries now show a teal IN CART / CART badge and background tint when the resource is in your shopping list",
            "Overlay redesigned with a SCAN / ORDERS / SHOPPING tab strip replacing the crowded button row",
            "Recent scan history redesigned as a clean text log — colored diamond, resource name, RS value",
            "Scan box indicator hidden by default on launch",
            "Keyboard shortcut reference removed from the sidebar",
            "Recent Scans clear button now correctly clears history (was clearing the scan input)",
        ]),
        ("App  4.0.0  —  May 30, 2026",
        [
            "Full rewrite in C# + WPF — native Windows app, no Python runtime required",
            "SQLite database backend — all resource, blueprint, location, and refinery data stored locally",
            "Windows.Media.Ocr — native WinRT OCR engine replaces Python winsdk wrapper",
            "RS multi-node matching — correctly identifies 1–6 node readings (e.g. 6800 → Lindinium ×2)",
            "Blueprint detail panel — search blueprints and view full ingredient lists with rarity-colored cards",
            "Reference tree blueprints — expand any resource to see all blueprints that use it, grouped by category",
            "Lazy-loaded blueprint subtrees — blueprint data loads on first expand, keeping navigation fast",
        ]),
        ("App  3.3.0  —  May 28, 2026",
        [
            "Work order overlay panel — slide-out panel from the right side of the overlay",
            "Complete work orders visually dimmed with strikethrough text",
            "Mining status simplified to three states: Refining, Ready to Collect, Complete",
            "Submit and Delete buttons moved to the work order editor header",
            "Editor panel now scrollable when the window is too short",
            "Progress bar starts from zero — fixed visual artifact where bar appeared pre-filled",
            "Overlay tooltip text fixed for dark mode",
        ]),
        ("App  3.2.1  —  May 25, 2026",
        [
            "Work order refinery timer — set hours and minutes per order",
            "Timer countdown displays live in H:MM:SS format",
            "Progress bar in editor shows elapsed vs total duration",
            "Progress bar in work order list — thin status-colored bar per row",
            "Work order rows highlighted by status — left border color per state",
            "Timer auto-expires — status changes to Ready to Collect at zero",
            "Timer survives app restarts",
        ]),
        ("App  3.2.0  —  May 25, 2026",
        [
            "Work Orders page — track active mining runs with label, resources, location, refinery, status, notes",
            "Work order status tracking — Mining / Refining / Ready to Collect / Complete",
            "Resource and location autocomplete in work orders",
            "Refinery dropdown — all known stations pre-loaded",
            "Work orders and shopping list persist across sessions",
        ]),
        ("App  3.1.2  —  May 24, 2026",
        [
            "Overlay shopping panel — 🛒 toggle in overlay title bar",
            "Per-item removal in the shopping list dialog",
            "Shopping list dialog redesigned with scrollable per-row layout",
            "Overlay and shopping dialog stay in sync",
        ]),
        ("App  3.1.1  —  May 24, 2026",
        [
            "Overlay results display as cards matching the main app",
            "Overlay scan shows all exact and close matches",
            "Overlay is now resizable; size persists across sessions",
        ]),
        ("App  3.1.0  —  May 23, 2026",
        [
            "Light / dark theme toggle",
            "Overlay scanner — compact always-on-top window (⧉ or Ctrl+`)",
            "Overlay history — up to 20 entries, clickable to re-run",
            "Overlay mirrors every scan to the main window",
        ]),
        ("App  3.0.0  —  May 23, 2026",
        [
            "Rebranded to Nexus — new name, new logo",
            "Complete UI overhaul: left nav sidebar",
            "RS results display as styled cards with color-coded left borders",
            "New deep navy + teal color palette throughout",
            "Added FPS and ROC resources to the Reference table",
            "Theme / accent color picker — 5 preset palettes",
            "Game version updated to Star Citizen PU v4.8.0",
        ]),
        ("App  2.7.4  —  May 23, 2026",
        [
            "Fixed blueprint results overlaying when switching between blueprints",
            "Blueprint results now show unlock requirements: faction, rep level, mission name, and system",
            "Blueprints with no known unlock source show a notice",
        ]),
        ("App  2.7.3  —  May 17, 2026",
        [
            "Refinery Yields now show the specific station name alongside the system and modifier",
            "Fixed data updater reading wrong field from data source API",
            "Refinery system percentages in blueprint results color-coded green / yellow / red",
        ]),
        ("App  2.7.0  —  May 16, 2026",
        [
            "Top-right buttons now 20% larger for easier clicking",
            "App version badge visible in the title bar",
            "Data freshness indicator works correctly in compiled .exe builds",
        ]),
        ("App  2.6.0  —  May 16, 2026",
        [
            "Confidence score shown on fuzzy RS matches",
            "RS range indicator tooltip on results table",
            "Multi-RS input: enter multiple values separated by ';'",
            "Pin/favorite resources",
            "Shopping list: accumulate blueprint ingredients",
            "Best refinery system shown in blueprint view",
        ]),
        ("App  2.5.0  —  May 2026",
        [
            "System filter (All / Stanton / Pyro / Nyx) above reference table",
            "Rarity group headers now show resource count",
            "Blueprint results show total SCU needed across all ingredients",
        ]),
        ("App  2.4.0  —  May 2026",
        [
            "Reference table now groups resources by rarity tier",
            "Blueprint search bar with autocomplete",
            "Window size and position saved between sessions",
        ]),
        ("Data  4.8.0  —  May 2026",
        [
            "Updated all mining data to Star Citizen 4.8.0 live",
            "Refreshed locations, blueprints, and refinery yield modifiers",
        ]),
        ("App  2.1.1  —  May 2026",
        [
            "Added Refineries expandable section to the reference table",
            "Refinery yield modifiers color-coded: green (+), red (−), gray (0)",
        ]),
    ];

    public AboutDialog()
    {
        Title = "About Nexus";
        Width = 600; Height = 660;
        PreviewKeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
        Background = (Brush)Application.Current.FindResource("BgBrush");
        Foreground = (Brush)Application.Current.FindResource("FgBrush");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;

        var outer = new Grid();
        outer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var tabs = new TabControl { Margin = new Thickness(0) };

        // ── About tab ────────────────────────────────────────────────────────
        var aboutTab = new TabItem { Header = "ABOUT" };
        var aboutPanel = new StackPanel { Margin = new Thickness(28, 24, 28, 16) };

        // Logo
        aboutPanel.Children.Add(new Image
        {
            Source = new System.Windows.Media.Imaging.BitmapImage(
                new Uri("pack://application:,,,/Assets/nexus_logo.png")),
            Width = 160, Height = 80, Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 12),
        });

        aboutPanel.Children.Add(new TextBlock
        {
            Text = "NEXUS", FontSize = 28, FontWeight = FontWeights.Bold,
            Foreground = (Brush)Application.Current.FindResource("AccentBrush"),
        });

        var verRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        var ver = Changelog[0].Label.Split("  ", StringSplitOptions.RemoveEmptyEntries)[1];
        AddBadge(verRow, $"v{ver}");
        verRow.Children.Add(new TextBlock
        {
            Text = "  ·  Star Citizen Industry Companion",
            FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.FindResource("FgDimBrush"),
        });
        aboutPanel.Children.Add(verRow);

        // Divider
        aboutPanel.Children.Add(new Border
        {
            Height = 1, Margin = new Thickness(0, 16, 0, 16),
            Background = (Brush)Application.Current.FindResource("NavBorderBrush"),
        });

        AddInfoLine(aboutPanel, "Created by", "TurboV1RG1N");
        AddInfoLine(aboutPanel, "Game Data",  "Star Citizen PU v4.8.0");
        aboutTab.Content = aboutPanel;
        tabs.Items.Add(aboutTab);

        // ── Changelog tab ────────────────────────────────────────────────────
        var changeTab = new TabItem { Header = "CHANGELOG" };

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(24, 8, 24, 8),
        };

        var changeStack = new StackPanel();
        foreach (var (label, changes) in Changelog)
        {
            changeStack.Children.Add(new TextBlock
            {
                Text = label, FontSize = 12, FontWeight = FontWeights.Bold,
                Foreground = (Brush)Application.Current.FindResource("AccentBrush"),
                Margin = new Thickness(0, 14, 0, 5),
            });
            foreach (var c in changes)
            {
                var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var dot = new TextBlock
                {
                    Text = "·  ", FontSize = 12,
                    Foreground = (Brush)Application.Current.FindResource("FgDimBrush"),
                    VerticalAlignment = VerticalAlignment.Top,
                };
                var text = new TextBlock
                {
                    Text = c, FontSize = 12, TextWrapping = TextWrapping.Wrap,
                    Foreground = (Brush)Application.Current.FindResource("FgBrush"),
                };
                Grid.SetColumn(dot, 0);
                Grid.SetColumn(text, 1);
                row.Children.Add(dot);
                row.Children.Add(text);
                changeStack.Children.Add(row);
            }
        }
        scroll.Content = changeStack;
        changeTab.Content = scroll;
        tabs.Items.Add(changeTab);

        // ── Legal tab ────────────────────────────────────────────────────────
        var legalTab = new TabItem { Header = "LEGAL" };
        var legalScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(28, 20, 28, 16),
        };
        var legalStack = new StackPanel();

        void AddLegalSection(string heading, string body)
        {
            legalStack.Children.Add(new TextBlock
            {
                Text = heading, FontSize = 11, FontWeight = FontWeights.Bold,
                Foreground = (Brush)Application.Current.FindResource("AccentBrush"),
                Margin = new Thickness(0, 14, 0, 4),
            });
            legalStack.Children.Add(new TextBlock
            {
                Text = body, FontSize = 11, TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.FindResource("FgBrush"),
                LineHeight = 18,
            });
        }

        // Warning banner
        var warnBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x18, 0xF5, 0x9E, 0x0B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 0xF5, 0x9E, 0x0B)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 0, 0, 6),
        };
        warnBorder.Child = new TextBlock
        {
            Text = "UNOFFICIAL FAN TOOL — Not affiliated with or endorsed by Cloud Imperium Games.",
            FontSize = 11, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)),
        };
        legalStack.Children.Add(warnBorder);

        AddLegalSection("Non-Affiliation",
            "Nexus is an unofficial, fan-made tool created independently. It is not affiliated with, " +
            "endorsed by, or sponsored by Cloud Imperium Games (CIG) or Roberts Space Industries (RSI). " +
            "Star Citizen® is a registered trademark of Cloud Imperium Games Corporation.");

        AddLegalSection("How This App Works",
            "Nexus only reads pixel data from your screen (screen capture) and displays reference " +
            "information from a local database. It does not read game memory, inject code into any " +
            "process, modify game files, or communicate with game servers in any way.");

        AddLegalSection("Anti-Cheat Compatibility",
            "Nexus does not interact with the game process in any way and is EAC-Safe (Easy Anti-Cheat " +
            "compatible). It operates entirely outside the game — similar to having a browser or " +
            "spreadsheet open alongside Star Citizen.");

        AddLegalSection("Use at Your Own Risk",
            "While every effort has been made to ensure this tool is safe and compliant with Star " +
            "Citizen's Terms of Service, the author makes no guarantees. Game policies can change. " +
            "You use this tool at your own discretion.");

        legalScroll.Content = legalStack;
        legalTab.Content = legalScroll;
        tabs.Items.Add(legalTab);

        // ── Appearance tab ───────────────────────────────────────────────────
        var appearTab = new TabItem { Header = "APPEARANCE" };
        var appearPanel = new StackPanel { Margin = new Thickness(28, 20, 28, 16) };

        appearPanel.Children.Add(new TextBlock
        {
            Text = "Accent Color", FontSize = 13, FontWeight = FontWeights.Bold,
            Foreground = (Brush)Application.Current.FindResource("FgBrush"),
            Margin = new Thickness(0, 0, 0, 10),
        });

        var swatchRow = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (name, hex, _) in _accentPalette)
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            var swatch = new Border
            {
                Width = 36, Height = 36, CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 0, 8, 0),
                Background = new SolidColorBrush(c),
                Cursor = Cursors.Hand,
                ToolTip = name,
                Tag = name,
            };
            swatch.MouseLeftButtonUp += (s, e) => ApplyAccent(((Border)s).Tag.ToString()!);
            swatchRow.Children.Add(swatch);
        }
        appearPanel.Children.Add(swatchRow);
        appearTab.Content = appearPanel;
        tabs.Items.Add(appearTab);

        outer.Children.Add(tabs);

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

    // ── Appearance helpers ────────────────────────────────────────────────────

    private static readonly (string Name, string Hex, string DimHex)[] _accentPalette =
    [
        ("Teal",   "#FF00C9A7", "#FF0D3028"),
        ("Blue",   "#FF3B82F6", "#FF0D1B3E"),
        ("Amber",  "#FFF59E0B", "#FF2D1F00"),
        ("Purple", "#FFA855F7", "#FF1A0D2E"),
        ("Rose",   "#FFF43F5E", "#FF2E0D13"),
    ];

    private static void ApplyAccent(string name)
    {
        var match = _accentPalette.FirstOrDefault(a => a.Name == name);
        if (match.Hex == null) return;
        var res = Application.Current.Resources;
        res["AccentBrush"]    = new SolidColorBrush((Color)ColorConverter.ConvertFromString(match.Hex));
        res["AccentDimBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(match.DimHex));
        App.Settings.Current.AccentTheme = name.ToLower();
        App.Settings.Save();
    }

    private static void AddBadge(Panel parent, string text)
    {
        var b = new Border
        {
            Padding = new Thickness(6, 2, 6, 2), CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            BorderBrush = (Brush)Application.Current.FindResource("AccentDimBrush"),
            BorderThickness = new Thickness(1),
        };
        b.Child = new TextBlock
        {
            Text = text, FontSize = 11, FontWeight = FontWeights.Bold,
            Foreground = (Brush)Application.Current.FindResource("AccentBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        parent.Children.Add(b);
    }

    private static void AddInfoLine(Panel parent, string label, string value)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 3, 0, 3),
        };
        row.Children.Add(new TextBlock
        {
            Text = label + ":", Width = 110, FontSize = 12,
            Foreground = (Brush)Application.Current.FindResource("FgDimBrush"),
        });
        row.Children.Add(new TextBlock
        {
            Text = value, FontSize = 12,
            Foreground = (Brush)Application.Current.FindResource("FgBrush"),
        });
        parent.Children.Add(row);
    }
}
