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
/// Mining Codex / Blueprint Library layout - a searchable topic list on the left,
/// the selected topic's content on the right. Each topic leads with a one-line
/// summary, a row of called-out key controls, then the detail bullets.
/// </summary>
public class HelpDialog : Window
{
    /// <summary>Set when the user clicks "Replay Tutorial" - the owner launches the tour after this dialog closes.</summary>
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
                "Close it with the ✕ button - position and size are saved for next time.",
                "The overlay has five tabs: HUB, SCAN, REFINERY, SHOPPING, and HAULING - it opens on the HUB the first time, then reopens on the tab you last used.",
                "HUB - a read-only glance: SCAN STATUS lights (green = on, yellow = paused, red = off), a live BLUEPRINTS COLLECTED count, the SERVER / SHARD panel, and the Collection Log feed (see Session Tracking).",
                "SCAN - the Auto-scan RS switch, RS input, results, and the RECENT scan history (shown on this tab only).",
                "REFINERY - your work orders at a glance, plus ▤ Open Refinery Tracker for the flyout panel.",
                "SHOPPING - an inline view of your current shopping list.",
                "HAULING - the contract scan switches, your active hauls, and the consolidated STOPS plan (see Cargo Hauling).",
                "The opacity slider sits in the overlay header, so it's available from every tab.",
            ]),

        new("◎", "Auto-scan",
            "Let Nexus read RS values straight off your screen and decode them automatically as you mine.",
            [new("⊕", "set RS region"), new("Auto-scan RS", "switch"), new("Show/Hide box", "switch")],
            [
                "Switch to the SCAN tab in the overlay to access all RS scan controls.",
                "Click ⊕ Set RS detection region - your cursor becomes a crosshair.",
                "Click and drag a rectangle over the RS value shown in your game.",
                "img:/Assets/RS_Signature.png",
                "Turn on the Auto-scan RS switch - Nexus reads the region several times a second and confirms a value after two matching reads.",
                "Auto-scan starts off on launch - Nexus only captures the screen while the switch is on.",
                "Scanning pauses on its own while both Nexus and Star Citizen are in the background, and resumes when either returns to the front - the HUB light shows yellow while paused.",
                "Use the Show/Hide RS detection box switch to see the magenta scan box on screen - it's hidden by default.",
                "Contract scanning for Cargo Hauling uses its own separate detection box - see Cargo Hauling.",
                "Use the opacity slider in the overlay header to adjust transparency (20-100%) - it's on every tab and the refinery flyout dims along with it.",
                "◉ Reading… appears while a candidate value is being confirmed.",
            ]),

        new("dock:rs", "RS Signal Decoder",
            "Type an RS value and Nexus tells you the resource, node count, rarity color, and how confident the match is.",
            [new("Enter", "run scan"), new("Clear", "wipe history")],
            [
                "Open the RS Decoder module in the app dock.",
                "Type an RS value into the input box and press Enter or click SCAN.",
                "Pressing Enter runs the scan and clears the input so you can type the next value immediately.",
                "BEST MATCH shows the top result as a hero card; OTHER MATCHES lists the rest.",
                "EXACT means the value is a perfect multiple of the resource's base RS.",
                "A close match within 0.5% is still very likely correct.",
                "Click + Add to cart on the best match to put it on your shopping list.",
                "RECENT SCANS keeps your history - filter it with the pills, and click any entry to re-run that lookup.",
                "Click Clear next to RECENT SCANS to clear the history.",
            ]),

        new("dock:refinery", "Refinery Tracker",
            "Log your refinery jobs so you never lose track of what's cooking or when it's ready to collect.",
            [new("+ Add work order", "create"), new("▤", "open flyout")],
            [
                "Open the Refinery module in the app dock.",
                "Click + Add work order - a popup editor takes the label, resource, location, refinery, and status.",
                "Set a refinery timer using the Hours and Minutes fields - the countdown starts immediately.",
                "Work orders show as cards with a live progress bar and timer.",
                "When the timer expires while the order is open in the editor (or the next time you open it), the status changes to Ready to Collect.",
                "Click a card to reopen it in the editor. Save commits changes, Delete removes the order.",
                "Work orders and their timers survive app restarts.",
                "In the overlay, the REFINERY tab gives a quick status view - click ▤ Open Refinery Tracker there to open the flyout panel.",
                "The flyout has a hide-completed toggle (☐/☑) and a side-swap button (⇄) in its header.",
            ]),

        new("dock:blueprint", "Blueprint Library",
            "Search any craftable item to see the exact resources it needs and the best places to mine them.",
            [new("Enter", "search"), new("+ Add all to cart", "every ingredient"), new("Owned", "toggle")],
            [
                "Open the Blueprint Library module in the app dock.",
                "Browse by category and drill in - the breadcrumb trail stays pinned at the top as you scroll and jumps you back up.",
                "Start typing a blueprint name - autocomplete suggestions appear as you type.",
                "Select a suggestion or press Enter to search.",
                "The left panel lists matching blueprints. Click one to see its full ingredient list on the right.",
                "Each ingredient card shows the resource name, quantity, unit, and rarity color.",
                "Click + on an ingredient row to add it to your shopping list.",
                "Click + Add all to cart to add every ingredient at once.",
                "A WHERE TO MINE section beside the ingredient list ranks the most efficient mining locations to gather all required resources.",
                "The first recommended location covers the most ingredients; subsequent entries cover what remains.",
                "Resources with no known mining location are listed separately at the bottom.",
                "Mark a blueprint as Owned with its toggle - a manifest (You own X of Y blueprints) and per-category progress appear at the top of the library.",
                "Filter the library by All, Owned, or Not owned; the owned count updates live as you mark blueprints.",
                "Click Import owned from logs… to scan your Game.log and its backups and mark everything you've already received as Owned - or let Session Tracking collect them live as you play.",
            ]),

        new("✓", "Session Tracking",
            "Nexus reads Star Citizen's Game.log and auto-collects blueprints you receive - marking them Owned live as you play, or in bulk from past logs. Always on.",
            [new("GAME SESSION", "header pill"), new("Import", "past logs")],
            [
                "Session tracking and blueprint auto-collect are always on - there's nothing to switch on.",
                "The header pills show the live state - GAME SESSION reads monitoring while Star Citizen runs and offline once it's closed; BLUEPRINTS reads tracking or off.",
                "Each 'Received Blueprint' event marks that blueprint Owned in your Blueprint Library and bumps the session count - quietly, with no popups.",
                "The overlay HUB shows a live BLUEPRINTS COLLECTED count and the Collection Log feed; counts reset when Star Citizen starts a new session.",
                "For more control, open the advanced monitor from Settings › Game.log Paths › Open Game.log Monitor - a raw log view, snapshot export, and Reset session.",
                "Import owned from logs… in the Blueprint Library scans your current log plus the logbackups folder and collects everything you've already received (after a preview and confirmation).",
                "If Nexus can't find your Game.log, set its path in Settings › Game.log Paths.",
                "Nexus only reads the log file - it never writes to game files or touches the game process.",
            ]),

        new("dock:codex", "Mining Codex",
            "The full reference table of every resource Nexus knows - search and filter it to plan a route before you undock.",
            [new("✕", "clear search"), new("Key ▾", "color key"), new("Reset filters", "")],
            [
                "Open the Mining Codex module in the app dock.",
                "Search by resource name, location, or blueprint - the list filters in real time.",
                "Click ✕ to clear the search.",
                "System pills (All / Stanton / Pyro / Nyx) filter resources by where they are found.",
                "Method pills (All / Ship / ROC / FPS) filter by how the resource is mined.",
                "Multiple pills in a row broaden the match (Stanton + Pyro shows resources in either); the System and Method rows combine. Click All to clear a row.",
                "Click a resource to open its detail panel - Locations, Refinery Yields, and Blueprints.",
                "Click Key ▾ for the color key: rarity tiers and refinery yield colors.",
                "Rarity: Legendary (gold) · Epic (purple) · Rare (blue) · Uncommon (green) · Common (gray).",
                "Refinery: Bonus yield (green) · No modifier (gray) · Reduced yield (red).",
                "Click Reset filters to clear the search and every pill.",
            ]),

        new("cart", "Shopping List",
            "A running list of the resources you're after this run, highlighted everywhere they appear.",
            [new("−", "remove item")],
            [
                "Add items from RS Decoder results (+ Add to cart on the best match, Add on other matches) or Blueprint Library ingredients (+ on an ingredient, or + Add all to cart).",
                "Click the cart button in the main window header to open the shopping list dialog.",
                "Each row shows the resource name, quantity, and unit.",
                "Click − next to an item to remove it.",
                "In the overlay, switch to the SHOPPING tab to view the same list inline.",
                "Shopping list data is saved and persists across sessions.",
                "Resources already in your list are highlighted with a faint amber tint in recent scan history and the overlay's results.",
                "An IN CART badge appears on matching scan result cards; a CART badge appears on matching history entries.",
                "Highlights update automatically when you add or remove items from the list.",
            ]),

        new("dock:network", "Blueprint Network",
            "Share which blueprints you own with friends or your org, and see who in your group has what.",
            [new("Export", "your library"), new("Import", "a teammate's"), new("Groups…", "organize")],
            [
                "Open the Network module in the app dock - its dock label reads BLUEPRINT SHARING.",
                "Click Export to save your owned blueprints to a .nexuslib file - share it as your RSI handle or a nickname.",
                "Send that file to friends however you like (Discord, a shared drive); they Import it to see your library.",
                "Click Import to load a teammate's .nexuslib file - each person becomes a member of your network.",
                "Re-importing someone's newer file updates them in place - no duplicates.",
                "A coordinator can import everyone, then Export one combined roster the whole group imports just once.",
                "Members tab - everyone you've imported; use Groups… to add a member to a group, or + New group to create one (Friends, your org, …).",
                "Overview tab - your group's coverage: percent owned, blueprints nobody has yet (farm targets), and ones only a single person holds.",
                "Blueprints tab - every blueprint with how many of you own it; filter to Nobody owns / Single owner / I'm missing, and expand a row to see exactly who has it.",
                "The group switcher scopes coverage to a group or to everyone; you're always counted in coverage.",
                "Scope any tab to a single member with the person switcher - see exactly what one friend owns or is missing.",
                "Settings › Blueprint Network can detect your RSI handle (read-only, from Game.log) so exports come pre-filled - or just use a nickname at export.",
                "No server, no account - Nexus uploads nothing on its own; your library leaves your PC only when you export a file and share it yourself.",
            ]),

        new("dock:settings", "Settings",
            "One module for everything Nexus needs configured - file paths, identity, diagnostics, motion, and data.",
            [new("Settings", "app dock, bottom")],
            [
                "Click the Settings module at the bottom of the app dock to open it.",
                "Game.log Paths - set your Game.log location (used by Session Tracking, Cargo Hauling, and server / shard tracking) and an optional global.ini path that translates blueprint names renamed by a localization mod. Open Game.log Monitor lives here too.",
                "Blueprint Network - detect your RSI handle (read-only, from Game.log) so library exports come pre-filled.",
                "Diagnostics - the App Log Monitor shows Nexus's own activity live, and a snapshot bundles app info and the log into one file to attach to a bug report.",
                "Appearance - Reduce animations tones down motion across the app; 24-hour clock switches the top-bar clock format.",
                "Data - Clear saved data wipes your owned blueprints, Blueprint Network members and groups, detected RSI handle, shopping cart, work orders and pinned resources after a confirmation; the mining reference data is left untouched.",
            ]),

        new("◐", "Appearance",
            "Tune how Nexus moves and reads - motion and clock options live in Settings.",
            [new("Settings", "› Appearance")],
            [
                "Turn on Reduce animations in Settings › Appearance to minimize motion across the app.",
                "Switch the top-bar clock between 12-hour and 24-hour time in Settings › Appearance.",
                "Replay this guided tour anytime with the Replay Tutorial button below.",
            ]),

        new("dock:cargo", "Cargo Hauling",
            "Nexus reads the hauling contracts you accept from Game.log and builds a consolidated collect-and-deliver plan across every active haul.",
            [new("Auto-scan contracts", "switch"), new("⊕", "set contract region")],
            [
                "Open the Cargo Hauling module in the app dock - contracts you accept in game appear automatically, no manual entry.",
                "Active hauls show as cards with the contractor, route, and each collect / deliver leg.",
                "The Collect / deliver consolidation table groups every leg by location, so you can fly one efficient route across all your hauls.",
                "Finished hauls drop to the bottom with an outcome chip - Complete in green, other endings in amber.",
                "Want reward, contractor, and cargo details? Scan the in-game Contracts screen: on the overlay HAULING tab, turn on Auto-scan contracts and click ⊕ Set contract detection region over the contract panel.",
                "Contract scanning uses its own yellow detection box, separate from the magenta RS box - the two never interfere.",
                "The overlay HAULING tab also shows your totals and the consolidated STOPS plan without leaving the game.",
                "Hauls clear automatically when you change or leave a shard - see your current and recent shards on the overlay HUB.",
                "Needs your Game.log - set the path in Settings › Game.log Paths if it isn't auto-detected.",
            ]),

        new("dock:operations", "Operations",
            "The landing dashboard - your whole operation at a glance, with jump-off links into every module.",
            [new("LIVE / OFFLINE", "game link")],
            [
                "Operations opens by default when Nexus starts - it's the first module in the app dock.",
                "KPI cards along the top give an at-a-glance readout: last RS scan, refinery queue, cargo in transit, session blueprints, and network coverage.",
                "The REFINERY QUEUE and ACTIVE HAULS panels link straight into their modules with Open tracker and Open hauling.",
                "NETWORK RISK flags blueprints only one person in your Blueprint Network owns.",
                "SERVER / SHARD shows your current shard and recent ones - the same data as the overlay HUB.",
                "The LIVE badge on the Operations dock tile means Star Citizen is running; it flips to OFFLINE when the game closes. The GAME SESSION pill in the header mirrors the same signal.",
            ]),
    ];

    private static Brush R(string key) => (Brush)Application.Current.FindResource(key);

    // The header shopping-cart vector - the same Path data as the main window's cart button.
    private const string CartPathData =
        "M7,18c-1.1,0-1.99,0.9-1.99,2S5.9,22,7,22s2-0.9,2-2S8.1,18,7,18z M1,2v2h2l3.6,7.59l-1.35,2.45C5.16,14.37,5,14.79,5,15.25c0,1.1,0.9,2,2,2h12v-2H7.42c-0.14,0-0.25-0.11-0.25-0.25l0.03-0.12L8.1,13h7.45c0.75,0,1.41-0.41,1.75-1.03l3.58-6.49C20.95,5.34,21,5.17,21,5c0-0.55-0.45-1-1-1H5.21L4.27,2H1z M17,18c-1.1,0-1.99,0.9-1.99,2s0.89,2,1.99,2s2-0.9,2-2S18.1,18,17,18z";

    // Topic vector factory. "dock:<key>" renders the module's actual dock glyph (static,
    // via AnimatedDockIcon with no RadioButton host); "cart" renders the header cart vector.
    private static FrameworkElement TopicVector(string icon, double size, Brush color)
    {
        if (icon == "cart")
        {
            var p = new System.Windows.Shapes.Path { Data = Geometry.Parse(CartPathData), Fill = color };
            return new Viewbox { Width = size, Height = size, Child = p };
        }
        var g = new AnimatedDockIcon { IconKey = icon[5..], Width = size, Height = size };
        g.SetStaticColor(color);
        return g;
    }

    private static bool IsVectorIcon(string icon) => icon == "cart" || icon.StartsWith("dock:");

    // Recolor a topic-row icon - a text glyph, a dock glyph, or the cart vector.
    private static void RecolorIcon(UIElement icon, Brush brush)
    {
        switch (icon)
        {
            case TextBlock tb: tb.Foreground = brush; break;
            case Border { Child: AnimatedDockIcon adi }: adi.SetStaticColor(brush); break;
            case Border { Child: Viewbox { Child: System.Windows.Shapes.Path p } }: p.Fill = brush; break;
        }
    }

    private readonly TextBox _search = new();
    private readonly TextBlock _searchHint = new() { Text = "Search topics…", IsHitTestVisible = false };
    private readonly StackPanel _topicList = new();
    private readonly Border _contentHost = new();
    private readonly System.Collections.Generic.List<(Topic Topic, Border Row)> _rows = new();
    private Topic? _selected;

    public HelpDialog()
    {
        Title = "Help - Nexus";
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
        SelectTopic(Topics.OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase).First());
        DialogMotion.Attach(this);
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
        foreach (var topic in Topics.OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase))
        {
            var bar = new Border { Width = 3, CornerRadius = new CornerRadius(2), Background = Brushes.Transparent };
            UIElement icon;
            if (IsVectorIcon(topic.Icon))
            {
                var sv = TopicVector(topic.Icon, 15, R("FgDimBrush"));
                sv.HorizontalAlignment = HorizontalAlignment.Center;
                icon = new Border { MinWidth = 18, VerticalAlignment = VerticalAlignment.Center, Child = sv };
            }
            else
            {
                icon = new TextBlock
                {
                    Text = topic.Icon, FontSize = 12, MinWidth = 18,
                    Foreground = R("FgDimBrush"), VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Center,
                };
            }
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
            RecolorIcon(inner.Children[1], sel ? R("AccentBrush") : R("FgDimBrush"));                       // icon (text glyph or share vector)
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
        if (IsVectorIcon(topic.Icon))
        {
            var sv = TopicVector(topic.Icon, 20, R("AccentBrush"));
            sv.VerticalAlignment = VerticalAlignment.Center;
            sv.Margin = new Thickness(0, 0, 10, 0);
            header.Children.Add(sv);
        }
        else
        {
            header.Children.Add(new TextBlock
            {
                Text = topic.Icon, FontSize = 18, FontWeight = FontWeights.Bold,
                Foreground = R("AccentBrush"), VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
            });
        }
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
        closeBtn.Click += (_, __) => DialogMotion.Close(this, base.Close);
        Grid.SetColumn(closeBtn, 0);

        var tutorialBtn = new Button
        {
            Content = "▶  Replay Tutorial",
            Style = (Style)Application.Current.FindResource("AccentButton"),
            Padding = new Thickness(20, 8, 20, 8),
            HorizontalAlignment = HorizontalAlignment.Right,
            ToolTip = "Replay the guided welcome tour",
        };
        tutorialBtn.Click += (_, __) => { TutorialRequested = true; DialogMotion.Close(this, base.Close); };
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
        else DialogMotion.Close(this, base.Close);
    }
}
