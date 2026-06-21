using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NexusApp.Views;

/// <summary>
/// The in-app user guide: a two-pane topic browser that mirrors the app's own
/// Mining Codex / Blueprint Library layout — a searchable topic list on the left,
/// the selected topic's content on the right. Each topic leads with a one-line
/// summary, a row of called-out key controls, then the detail bullets.
/// </summary>
public class HelpDialog : Window
{
    /// <summary>Set when the user clicks "Replay Tutorial" — the owner launches the tour after this dialog closes.</summary>
    public bool TutorialRequested { get; private set; }

    private sealed record HelpKey(string Glyph, string Label);
    private sealed record Topic(string Icon, string Title, string Lead, HelpKey[] Keys, string[] Items, string? Image = null);

    private static readonly Topic[] Topics =
    [
        new("⧉", "Overlay",
            "A floating window that keeps Nexus on top of your game so you can scan and plan without alt-tabbing.",
            [new("⧉", "open"), new("✕", "close")],
            [
                "Click ⧉ in the top-right of the main window to open the floating overlay.",
                "Drag the NEXUS header bar to reposition the overlay anywhere on screen.",
                "The overlay stays on top of all windows including your game.",
                "Close it with the ✕ button — position and size are saved for next time.",
                "The overlay has four tabs: STATS, SCAN, ORDERS, and SHOPPING.",
                "STATS — the Game.log blueprint session: Start/Stop watching, Auto-mark, a live count and a list of every blueprint marked this session (see Blueprint Auto-mark).",
                "SCAN — auto-scan controls, RS input, results, and the RECENT scan history (shown on this tab only).",
                "ORDERS — opens the Refinery Tracker flyout panel beside the overlay.",
                "SHOPPING — an inline view of your current shopping list.",
                "The opacity slider sits in the overlay header, so it's available from every tab.",
            ]),

        new("◎", "Auto-scan",
            "Let Nexus read RS values straight off your screen and decode them automatically as you mine.",
            [new("⊕", "draw region"), new("▶", "start"), new("■", "stop"), new("⊠", "show box"), new("⊡", "hide box")],
            [
                "Switch to the SCAN tab in the overlay to access all scan controls.",
                "Click ⊕ to draw a scan region — your cursor becomes a crosshair.",
                "Click and drag a rectangle over the RS value shown in your game.",
                "img:/Assets/RS_Signature.png",
                "Click ▶ to start scanning — the overlay reads the RS value automatically every ~0.5 seconds.",
                "While scanning the button shows ■ — click it to stop. Click ⊕ again to redraw the region.",
                "Click ⊠ to show the magenta scan box indicator on screen; click ⊡ to hide it.",
                "The scan box is hidden by default on launch.",
                "Use the opacity slider in the overlay header to adjust transparency (20–100%) — it's on every tab and the refinery flyout dims along with it.",
                "◉ Reading… appears in the status bar when a candidate value is being confirmed.",
            ]),

        new("RS", "RS Signal Decoder",
            "Type an RS value and Nexus tells you the resource, node count, tier, and how confident the match is.",
            [new("Enter", "run scan"), new("Clear", "wipe history")],
            [
                "Navigate to RS SIGNAL DECODER from the left sidebar.",
                "Type an RS value into the input box and press Enter or click SCAN.",
                "Pressing Enter runs the scan and clears the input so you can type the next value immediately.",
                "Results show the matching resource, node count, tier, and match accuracy.",
                "EXACT means the value is a perfect multiple of the resource's base RS.",
                "~X.XX% means a close match within 0.5% — the resource is still very likely correct.",
                "The left border color indicates match quality: green = exact, amber = close.",
                "Recent scans appear at the bottom as a text list — ◆ color matches the result tier.",
                "Click any recent scan entry to re-run that lookup.",
                "Click Clear next to RECENT SCANS to clear the history.",
            ]),

        new("◑", "Refinery Tracker",
            "Log your refinery jobs so you never lose track of what's cooking or when it's ready to collect.",
            [new("+ New", "create order"), new("▤", "open flyout")],
            [
                "Navigate to REFINERY TRACKER from the left sidebar.",
                "Click + New to create a work order. Fill in the label, resource, location, refinery, and status.",
                "Set a refinery timer using the Hours and Minutes fields — the countdown starts immediately.",
                "The live progress bar fills smoothly as time elapses.",
                "When the timer expires the status automatically changes to Ready to Collect.",
                "Click a work order row on the left to open it for editing.",
                "Use Save to commit changes and Delete to remove the order.",
                "Work orders and their timers survive app restarts.",
                "In the overlay, switch to the ORDERS tab and click ▤ Open Refinery Tracker to open the flyout panel.",
                "The flyout has a hide-completed toggle (☐/☑) and a side-swap button (⇄) in its header.",
            ]),

        new("▣", "Blueprint Library",
            "Search any craftable item to see the exact resources it needs and the best places to mine them.",
            [new("Enter", "search"), new("Add All", "to shopping list"), new("Owned", "toggle")],
            [
                "Navigate to BLUEPRINT LIBRARY from the left sidebar.",
                "Start typing a blueprint name — autocomplete suggestions appear as you type.",
                "Select a suggestion or press Enter to search.",
                "The left panel lists matching blueprints. Click one to see its full ingredient list on the right.",
                "Each ingredient card shows the resource name, quantity, unit, and rarity color.",
                "Click 🛒 on an ingredient to add it to your shopping list.",
                "Click Add All to Shopping List to add every ingredient at once.",
                "A WHERE TO MINE section below the ingredients ranks the most efficient mining locations to gather all required resources.",
                "The first recommended location covers the most ingredients; subsequent entries cover what remains.",
                "Resources with no known mining location are listed separately at the bottom.",
                "Mark a blueprint as Owned with its toggle — a manifest (You own X of Y blueprints) and per-category progress appear at the top of the library.",
                "Filter the library by All, Owned, or Not owned; the owned count updates live as you mark blueprints.",
                "Let Nexus mark these for you automatically as you play — see Blueprint Auto-mark.",
            ]),

        new("✓", "Blueprint Auto-mark",
            "Let Nexus read Star Citizen's Game.log to mark blueprints you receive as Owned — live as you play, or in bulk from past logs. (Beta)",
            [new("Start", "watch"), new("Auto-mark", "toggle"), new("Import", "past logs")],
            [
                "Open it from the Settings (cog) button in the top-right › Game.log › Open Game.log Monitor.",
                "Click Start to begin tailing your Game.log, Stop to end. The path defaults to the LIVE install — use Browse for PTU/EPTU.",
                "Turn on Auto-mark so each 'Received Blueprint' event marks that blueprint Owned in your Blueprint Library automatically.",
                "A toast appears each time a blueprint is auto-marked, and the Blueprint Library's owned count updates live.",
                "Click Import owned from past logs to scan your current log plus the logbackups folder and mark everything you've already received (after a preview and confirmation).",
                "The overlay's STATS tab mirrors this with Start/Stop, Auto-mark, a live session count, and a list of each blueprint marked this session.",
                "Nexus only reads the log file — it never writes to game files or touches the game process.",
            ]),

        new("◆", "Mining Codex",
            "The full reference table of every resource Nexus knows — filter and group it to plan a route before you undock.",
            [new("✕", "clear search"), new("Expand All", ""), new("Reset Sort", "")],
            [
                "Navigate to MINING CODEX from the left sidebar.",
                "Search by resource name, location, or blueprint — the table filters in real time.",
                "Click ✕ to clear the search.",
                "System pills (All / Stanton / Pyro / Nyx) filter resources by where they are found.",
                "Method pills (All / Ship / ROC / FPS) filter by how the resource is mined.",
                "Multiple pills can be active at once — they combine as AND filters. Click All to clear a row.",
                "Click Group: Resource / Group: Location to toggle between rarity-grouped and location-grouped views.",
                "In location view, resources are grouped by system (Stanton / Pyro / Nyx) then by mining site.",
                "Click Expand All to open every row. The button becomes Collapse All.",
                "Click Reset Sort to clear all filters, the search, and return to resource grouping.",
                "Expand a resource node to see its Locations, Refinery Yields, and Blueprints.",
                "Under Ship Components, blueprints are further grouped by subcategory (Cooler, Mining Laser, Shield, etc.).",
                "Blueprints are lazy-loaded on first expand to keep navigation fast.",
                "Rarity: Legendary (gold) · Epic (purple) · Rare (blue) · Uncommon (green) · Common (gray).",
                "Refinery: Bonus yield (green) · No modifier (gray) · Reduced yield (red).",
            ]),

        new("▤", "Shopping List",
            "A running list of the resources you're after this run, highlighted everywhere they appear.",
            [new("−", "remove item")],
            [
                "Add items from RS Signal Decoder results, Blueprint Library ingredients, or the Mining Codex.",
                "Click 🛒 in the main toolbar to open the shopping list dialog.",
                "Each row shows the resource name, quantity, and unit.",
                "Click − next to an item to remove it.",
                "In the overlay, switch to the SHOPPING tab to view the same list inline.",
                "Shopping list data is saved and persists across sessions.",
                "Resources already in your list are highlighted with a teal background in scan results and recent scan history.",
                "An IN CART badge appears on matching scan result cards; a CART badge appears on matching history entries.",
                "Highlights update automatically when you add or remove items from the list.",
            ]),

        new("≡", "Settings",
            "The cog button in the top-right of the main window opens Settings — your theme, the Game.log blueprint watcher, and a reset for saved data all live here.",
            [new("Settings", "cog, top-right")],
            [
                "Click the cog (Settings) button in the top-right of the main window to open it.",
                "Appearance — switch between the Luxury Gold and Classic themes (applies on restart).",
                "Game.log (Beta) — opens the Game.log Monitor that auto-marks blueprints you receive as owned (see Blueprint Auto-mark).",
                "Data — Clear saved data wipes your owned blueprints, shopping cart, work orders and pinned resources after a confirmation; your theme and the mining reference data are left untouched.",
            ]),

        new("◐", "Appearance",
            "Choose how Nexus looks — pick a theme on first launch, or switch it anytime.",
            [new("Settings", "› Appearance")],
            [
                "On first launch, a welcome picker lets you choose your look: Luxury Gold or Classic teal.",
                "Luxury Gold is the default near-black-and-gold theme; Classic is the original slate-and-teal style.",
                "Switch themes anytime from the Settings (cog) button in the top-right › Appearance — the change applies on restart.",
                "Replay this guided tour anytime with the Replay Tutorial button below.",
            ]),
    ];

    private static Brush R(string key) => (Brush)Application.Current.FindResource(key);

    private readonly TextBox _search = new();
    private readonly TextBlock _searchHint = new() { Text = "Search topics…", IsHitTestVisible = false };
    private readonly StackPanel _topicList = new();
    private readonly Border _contentHost = new();
    private readonly System.Collections.Generic.List<(Topic Topic, Border Row)> _rows = new();
    private Topic? _selected;

    public HelpDialog()
    {
        Title = "Help — Nexus";
        Width = 880; Height = 600; MinWidth = 720; MinHeight = 480;
        Background = R("BgBrush");
        Foreground = R("FgBrush");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        PreviewKeyDown += OnKeyDown;

        var outer = new Grid();
        outer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var panes = new Grid();
        panes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(248) });
        panes.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        panes.Children.Add(BuildLeftPane());

        var contentScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(28, 22, 28, 16),
            Content = _contentHost,
        };
        Grid.SetColumn(contentScroll, 1);
        panes.Children.Add(contentScroll);

        Grid.SetRow(panes, 0);
        outer.Children.Add(panes);
        outer.Children.Add(BuildFooter());

        Content = outer;

        BuildTopicRows();
        SelectTopic(Topics[0]);
    }

    private UIElement BuildLeftPane()
    {
        var left = new Border
        {
            Background = R("NavBgBrush"),
            BorderBrush = R("NavBorderBrush"),
            BorderThickness = new Thickness(0, 0, 1, 0),
        };
        Grid.SetColumn(left, 0);

        var col = new Grid();
        col.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // title
        col.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // search
        col.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // list

        var title = new TextBlock
        {
            Text = "USER GUIDE",
            FontSize = 11, FontWeight = FontWeights.Bold,
            Foreground = R("FgDimBrush"),
            Margin = new Thickness(18, 18, 18, 10),
        };
        Grid.SetRow(title, 0);
        col.Children.Add(title);

        // Search box with a faint placeholder.
        _search.Background = Brushes.Transparent;
        _search.BorderThickness = new Thickness(0);
        _search.Foreground = R("FgBrush");
        _search.CaretBrush = R("AccentBrush");
        _search.VerticalContentAlignment = VerticalAlignment.Center;
        _search.FontSize = 12.5;
        _search.TextChanged += (_, __) => ApplyFilter(_search.Text);
        _searchHint.Foreground = R("FgDimBrush");
        _searchHint.FontSize = 12.5;
        _searchHint.Margin = new Thickness(2, 0, 0, 0);
        _searchHint.VerticalAlignment = VerticalAlignment.Center;

        var searchInner = new Grid();
        searchInner.Children.Add(_searchHint);
        searchInner.Children.Add(_search);

        var searchBox = new Border
        {
            Background = R("Bg2NavBrush"),
            BorderBrush = R("NavBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 7, 10, 7),
            Margin = new Thickness(14, 0, 14, 10),
            Child = searchInner,
        };
        Grid.SetRow(searchBox, 1);
        col.Children.Add(searchBox);

        var listScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(10, 0, 10, 12),
            Content = _topicList,
        };
        Grid.SetRow(listScroll, 2);
        col.Children.Add(listScroll);

        left.Child = col;
        return left;
    }

    private void BuildTopicRows()
    {
        foreach (var topic in Topics)
        {
            var bar = new Border { Width = 3, CornerRadius = new CornerRadius(2), Background = Brushes.Transparent };
            var icon = new TextBlock
            {
                Text = topic.Icon, FontSize = 12, MinWidth = 18,
                Foreground = R("FgDimBrush"), VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
            };
            var label = new TextBlock
            {
                Text = topic.Title, FontSize = 12.5, Margin = new Thickness(8, 0, 0, 0),
                Foreground = R("FgBrush"), VerticalAlignment = VerticalAlignment.Center,
            };

            var inner = new Grid();
            inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(bar, 0); Grid.SetColumn(icon, 1); Grid.SetColumn(label, 2);
            bar.Margin = new Thickness(0, 0, 8, 0);
            inner.Children.Add(bar); inner.Children.Add(icon); inner.Children.Add(label);

            var row = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 9, 8, 9),
                Margin = new Thickness(0, 1, 0, 1),
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
                Child = inner,
            };
            var captured = topic;
            row.MouseLeftButtonUp += (_, __) => SelectTopic(captured);
            _rows.Add((topic, row));
            _topicList.Children.Add(row);
        }
    }

    private void ApplyFilter(string query)
    {
        _searchHint.Visibility = string.IsNullOrEmpty(query) ? Visibility.Visible : Visibility.Collapsed;
        query = query.Trim();
        Topic? firstVisible = null;

        foreach (var (topic, row) in _rows)
        {
            bool match = query.Length == 0 || Matches(topic, query);
            row.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
            if (match) firstVisible ??= topic;
        }

        // Keep the selection if it's still visible; otherwise jump to the first match.
        if (firstVisible != null && (_selected == null || !IsVisibleTopic(_selected)))
            SelectTopic(firstVisible);
    }

    private static bool Matches(Topic t, string q)
    {
        var c = StringComparison.OrdinalIgnoreCase;
        if (t.Title.Contains(q, c) || t.Lead.Contains(q, c)) return true;
        if (t.Items.Any(i => i.Contains(q, c))) return true;
        return t.Keys.Any(k => k.Label.Contains(q, c) || k.Glyph.Contains(q, c));
    }

    private bool IsVisibleTopic(Topic t) =>
        _rows.Any(r => ReferenceEquals(r.Topic, t) && r.Row.Visibility == Visibility.Visible);

    private void SelectTopic(Topic topic)
    {
        _selected = topic;
        foreach (var (t, row) in _rows)
        {
            bool sel = ReferenceEquals(t, topic);
            row.Background = sel ? R("Bg2NavBrush") : Brushes.Transparent;
            var inner = (Grid)row.Child;
            ((Border)inner.Children[0]).Background = sel ? R("AccentBrush") : Brushes.Transparent;          // accent bar
            ((TextBlock)inner.Children[1]).Foreground = sel ? R("AccentBrush") : R("FgDimBrush");           // icon
            ((TextBlock)inner.Children[2]).Foreground = sel ? R("AccentBrush") : R("FgBrush");              // label
            ((TextBlock)inner.Children[2]).FontWeight = sel ? FontWeights.SemiBold : FontWeights.Normal;
        }
        _contentHost.Child = BuildContent(topic);
    }

    private UIElement BuildContent(Topic topic)
    {
        var stack = new StackPanel();

        // Header: icon + title
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new TextBlock
        {
            Text = topic.Icon, FontSize = 18, FontWeight = FontWeights.Bold,
            Foreground = R("AccentBrush"), VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        });
        header.Children.Add(new TextBlock
        {
            Text = topic.Title, FontSize = 18, FontWeight = FontWeights.Bold,
            Foreground = R("FgBrush"), VerticalAlignment = VerticalAlignment.Center,
        });
        stack.Children.Add(header);

        // Lead summary
        stack.Children.Add(new TextBlock
        {
            Text = topic.Lead, FontSize = 13.5, TextWrapping = TextWrapping.Wrap, LineHeight = 20,
            Foreground = R("FgDimBrush"), Margin = new Thickness(0, 10, 0, 0),
        });

        // Key controls
        if (topic.Keys.Length > 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "KEY CONTROLS", FontSize = 10, FontWeight = FontWeights.Bold,
                Foreground = R("FgDimBrush"), Margin = new Thickness(0, 18, 0, 8),
            });
            var chips = new WrapPanel();
            foreach (var k in topic.Keys) chips.Children.Add(BuildKeyChip(k));
            stack.Children.Add(chips);
        }

        // Divider
        stack.Children.Add(new Border
        {
            Height = 1, Background = R("NavBorderBrush"), Margin = new Thickness(0, 18, 0, 14),
        });

        // Detail bullets
        foreach (var item in topic.Items)
        {
            if (item.StartsWith("img:"))
            {
                stack.Children.Add(new Border
                {
                    BorderBrush = R("NavBorderBrush"), BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4), Margin = new Thickness(18, 5, 0, 9),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Child = new Image
                    {
                        Source = new BitmapImage(new Uri($"pack://application:,,,{item[4..]}", UriKind.Absolute)),
                        Stretch = Stretch.Uniform, Width = 90,
                    },
                });
                continue;
            }

            var row = new Grid { Margin = new Thickness(2, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var dot = new TextBlock
            {
                Text = "·  ", FontSize = 13, Foreground = R("AccentBrush"),
                VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0),
            };
            var text = new TextBlock
            {
                Text = item, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, LineHeight = 19,
                Foreground = R("FgBrush"), Margin = new Thickness(0, 1, 0, 0),
            };
            Grid.SetColumn(dot, 0); Grid.SetColumn(text, 1);
            row.Children.Add(dot); row.Children.Add(text);
            stack.Children.Add(row);
        }

        return stack;
    }

    private UIElement BuildKeyChip(HelpKey k)
    {
        var inner = new StackPanel { Orientation = Orientation.Horizontal };
        inner.Children.Add(new TextBlock
        {
            Text = k.Glyph, FontSize = 12, FontWeight = FontWeights.Bold,
            Foreground = R("AccentBrush"), VerticalAlignment = VerticalAlignment.Center,
        });
        if (!string.IsNullOrEmpty(k.Label))
            inner.Children.Add(new TextBlock
            {
                Text = "  " + k.Label, FontSize = 11.5,
                Foreground = R("FgDimBrush"), VerticalAlignment = VerticalAlignment.Center,
            });

        return new Border
        {
            Background = R("Bg3Brush"),
            BorderBrush = R("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(9, 5, 9, 5),
            Margin = new Thickness(0, 0, 8, 8),
            Child = inner,
        };
    }

    private UIElement BuildFooter()
    {
        var footer = new Border
        {
            BorderBrush = R("NavBorderBrush"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(20, 12, 20, 12),
        };
        Grid.SetRow(footer, 1);

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var closeBtn = new Button
        {
            Content = "Close",
            Style = (Style)Application.Current.FindResource("NexusButton"),
            Padding = new Thickness(20, 8, 20, 8),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        closeBtn.Click += (_, __) => Close();
        Grid.SetColumn(closeBtn, 0);

        var tutorialBtn = new Button
        {
            Content = "▶  Replay Tutorial",
            Style = (Style)Application.Current.FindResource("AccentButton"),
            Padding = new Thickness(20, 8, 20, 8),
            HorizontalAlignment = HorizontalAlignment.Right,
            ToolTip = "Replay the guided welcome tour",
        };
        tutorialBtn.Click += (_, __) => { TutorialRequested = true; Close(); };
        Grid.SetColumn(tutorialBtn, 2);

        row.Children.Add(closeBtn);
        row.Children.Add(tutorialBtn);
        footer.Child = row;
        return footer;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (!string.IsNullOrEmpty(_search.Text)) { _search.Clear(); _search.Focus(); }
        else Close();
    }
}
