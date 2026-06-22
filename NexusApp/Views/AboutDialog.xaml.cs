using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using NexusApp.Services;

namespace NexusApp.Views;

public class AboutDialog : Window
{
    private static readonly (string Label, string[] Changes)[] Changelog =
    [
        ("App  5.4.0  —  Jun 21, 2026",
        [
            "New Session Tracking (Beta) — Nexus can read Star Citizen's Game.log to automatically mark blueprints Owned the moment you receive them in-game, so your library fills in as you play",
            "Import owned blueprints from past logs — scan your previous sessions in one pass and mark everything you've already collected, after a preview and confirmation",
            "New STATS tab in the overlay — a live THIS SESSION tally of blueprints collected, with a feed of what was just added; start or stop tracking right there",
            "New Settings dialog — a cog button in the top-right now gathers your theme, the Game.log monitor, and the clear-saved-data action in one place",
            "Reads the log read-only — Nexus never writes to or modifies any game file and reads no game memory, so it stays EAC-safe",
        ]),
        ("App  5.3.2  —  Jun 20, 2026",
        [
            "Armor blueprints group cleaner — every paint and skin variant of a piece now collapses into one entry (for example, all Antium Helmet skins), instead of some grouping and some appearing as separate rows",
            "Browse owned and not-owned blueprints by category — the Owned and Not owned filters now use the same Browse → category → blueprint drill-down as All, rather than one long flat list",
            "The breadcrumb trail now stays pinned at the top of the list as you scroll, so you can always see where you are",
        ]),
        ("App  5.3.1  —  Jun 20, 2026",
        [
            "Faster startup — the app no longer fully parses its mining database on every launch, only when the data actually changes",
            "Smoother Mining Codex filtering — the list now updates when you pause typing instead of rebuilding on every keystroke",
            "Friendlier error handling — unexpected errors show a clear message and are written to a local log file (%AppData%\\NexusApp\\logs) for troubleshooting",
            "Updated app icon — the taskbar and installed-app icons now use the teal Nexus logo",
        ]),
        ("App  5.3.0  —  Jun 20, 2026",
        [
            "Redesigned the Blueprint Library — a selected blueprint now opens as a schematic sheet, with a category eyebrow, large title, and quick read-outs for ingredient count and total cost",
            "Ingredients are now a clean Bill of Materials — rarity-coded, with right-aligned quantities and a running total in SCU",
            "New Blueprint Manifest on the landing view — see how many blueprints you own overall and a completion bar for each category",
            "Clearer drill-down — a clickable breadcrumb trail replaces the single back button, carrying each category's colour as you navigate",
            "Calmer ownership in the list — owned blueprints show a quiet green tick at rest, with the full Owned toggle appearing on hover; the detail panel keeps the prominent toggle",
            "Fixed blueprint unlock missions running off the edge — long mission lines now wrap correctly",
        ]),
        ("App  5.2.0  —  Jun 20, 2026",
        [
            "Track which blueprints you own — mark any blueprint as Owned right in the Blueprint Library, with a toggle on each row and a button in its detail panel, so you no longer have to check in-game",
            "Filter the Blueprint Library by All, Owned, or Not owned, with a live owned count; the filtered view groups your blueprints under their categories",
            "New Clear saved data button under About — wipes your owned blueprints, shopping cart, work orders and pinned resources after a confirmation prompt (your theme and the mining reference data are left untouched)",
        ]),
        ("App  5.1.2  —  Jun 19, 2026",
        [
            "Updated for Star Citizen patch 4.8.2 — mining reference data verified current for the new patch",
        ]),
        ("App  5.1.1  —  Jun 11, 2026",
        [
            "Redesigned the welcome tour — a caption now sits beside each control it points at, instead of a centered dialog, in a shorter and clearer walkthrough",
            "Redesigned the in-app User Guide — a searchable two-panel browser with each topic's key controls called out up top",
            "User Guide fixes — corrected the start/stop scanning buttons, updated the Refinery Tracker control reference, and added an Appearance section covering themes",
        ]),
        ("App  5.1.0  —  Jun 11, 2026",
        [
            "Choose your look on first launch — a new welcome picker lets you start in Luxury Gold or Classic teal before the app opens, with no restart",
            "The Refinery Tracker flyout now follows your theme — its panel was previously stuck on the gold look while in the Classic theme",
            "Fixed the Classic theme logo and app icon showing a dark square behind them",
        ]),
        ("App  5.0.1  —  Jun 10, 2026",
        [
            "Housekeeping — the project and its downloads dropped the version number from their names (the portable download and its program are now NexusApp); your existing settings, work orders and history carry over automatically",
        ]),
        ("App  5.0.0  —  Jun 10, 2026",
        [
            "New Luxury Gold theme — a refreshed near-black and warm-gold look with the Outfit typeface throughout",
            "Classic theme preserved — switch between the new Luxury Gold and the original v4 slate-and-teal style under About > Appearance (applies on restart)",
            "Every screen redesigned — the RS Signal Decoder leads with a results dashboard, the Refinery Tracker shows work orders as status cards, the Mining Codex is a two-panel browser, and the Blueprint Library has a cleaner layout",
            "Blueprint Library reorganized — browse blueprints by category instead of scrolling one long flat list",
            "Click-through navigation — jump from a resource to the blueprints that use it and on to each of their ingredients",
            "Floating overlay restyled to match the new look",
            "Mining data — added Pressurized Ice to the cooler and ship-weapon recipes that were missing it",
        ]),
        ("App  4.4.1  —  Jun 10, 2026",
        [
            "Updated mining data — removed duplicate blueprint entries and restored missing crafting ingredients so blueprint recipes and resource lists read accurately",
            "Corrected several blueprint recipes and ingredient units",
        ]),
        ("App  4.4.0  —  Jun 06, 2026",
        [
            "The welcome tour is now a full-app guided tutorial — it walks you through the RS Signal Decoder, scan results and history, the floating overlay and auto-scan, the shopping list, the Blueprint Library, the Mining Codex, and the Refinery Tracker, with a pulsing ring pointing out each control as you go",
            "Replay the full tour any time from the Help (?) window",
        ]),
        ("App  4.3.0  —  Jun 05, 2026",
        [
            "New first-run welcome tour — interactive overlay coach marks walk you through the app the first time you open it",
        ]),
        ("App  4.2.2  —  Jun 04, 2026",
        [
            "Minor framework changes and behind-the-scenes maintenance",
        ]),
        ("App  4.2.1  —  Jun 03, 2026",
        [
            "About dialog now links to the Nexus source on GitHub — click the Source line to open the repo in your browser",
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
            Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(ThemeService.LogoUri)),
            Width = 160, Height = 80, Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 12),
        });

        aboutPanel.Children.Add(new TextBlock
        {
            Text = "NEXUS", FontSize = 28, FontWeight = FontWeights.Bold, FontFamily = (System.Windows.Media.FontFamily)System.Windows.Application.Current.FindResource("HeadFont"),
            Foreground = (Brush)Application.Current.FindResource("AccentBrush"),
        });

        var verRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        AddBadge(verRow, $"v{AppInfo.Version}");
        verRow.Children.Add(new TextBlock
        {
            Text = "  ·  Star Citizen Companion",
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

        AddInfoLine(aboutPanel, "Created by", "T3SoD");
        AddInfoLine(aboutPanel, "Game Data",  $"Star Citizen PU v{GameData.Version}");
        AddInfoLine(aboutPanel, "Mining Data", $"v{App.Data.MiningDataVersion}");
        AddLinkLine(aboutPanel, "Source", "github.com/T3SoD/NexusApp",
            "https://github.com/T3SoD/NexusApp");

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
            "Nexus reads pixel data from your screen (screen capture) and displays reference information " +
            "from a local database. The optional Session Tracking feature (Beta) additionally reads — " +
            "read-only — the Game.log text file Star Citizen writes to disk, to auto-collect blueprints. " +
            "Nexus does not read game memory, inject code into any process, modify any game files, or " +
            "communicate with game servers in any way.");

        AddLegalSection("Anti-Cheat Compatibility",
            "Nexus does not interact with the game process in any way and is EAC-Safe (Easy Anti-Cheat " +
            "compatible). It operates entirely outside the game — similar to having a browser or " +
            "spreadsheet open alongside Star Citizen. Reading the Game.log is an out-of-process, " +
            "read-only file read and never modifies game files.");

        AddLegalSection("Use at Your Own Risk",
            "While every effort has been made to ensure this tool is safe and compliant with Star " +
            "Citizen's Terms of Service, the author makes no guarantees. Game policies can change. " +
            "You use this tool at your own discretion.");

        legalScroll.Content = legalStack;
        legalTab.Content = legalScroll;
        tabs.Items.Add(legalTab);

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

    private static void AddLinkLine(Panel parent, string label, string text, string url)
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

        var link = new Hyperlink { NavigateUri = new Uri(url) };
        link.Inlines.Add(text);
        link.Foreground = (Brush)Application.Current.FindResource("AccentBrush");
        link.RequestNavigate += (s, e) =>
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        };

        var linkText = new TextBlock { FontSize = 12 };
        linkText.Inlines.Add(link);
        row.Children.Add(linkText);

        parent.Children.Add(row);
    }
}
