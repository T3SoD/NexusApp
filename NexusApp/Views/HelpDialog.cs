using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NexusApp.Views;

public class HelpDialog : Window
{
    /// <summary>Set when the user clicks "Replay Tutorial" — the owner launches the tour after this dialog closes.</summary>
    public bool TutorialRequested { get; private set; }

    private static readonly (string Icon, string Title, string[] Items)[] _sections =
    [
        ("⧉", "OVERLAY",
        [
            "Click ⧉ in the top-right of the main window to open the floating overlay.",
            "Drag the NEXUS header bar to reposition the overlay anywhere on screen.",
            "The overlay stays on top of all windows including your game.",
            "Close the overlay with the ✕ button — position and size are saved for next time.",
            "The overlay has three tabs: SCAN, ORDERS, and SHOPPING.",
            "SCAN — auto-scan controls, RS input, results, and scan history.",
            "ORDERS — button to open the Refinery Tracker flyout panel beside the overlay.",
            "SHOPPING — inline view of your current shopping list.",
        ]),

        ("◎", "AUTO-SCAN",
        [
            "Switch to the SCAN tab in the overlay to access all scan controls.",
            "Click ⊕ to draw a scan region — your cursor becomes a crosshair.",
            "Click and drag a rectangle over the RS value shown in your game.",
            "img:/Assets/RS_Signature.png",
            "Click ■ to start scanning — the overlay reads the RS value automatically every ~0.5 seconds.",
            "Click ▶ to stop scanning. Click ⊕ again to redraw the region.",
            "Click ⊠ to show the magenta scan box indicator on screen; click ⊡ to hide it.",
            "The scan box is hidden by default on launch.",
            "Use the opacity slider in the SCAN tab to adjust overlay transparency (20–100%).",
            "◉ Reading… appears in the status bar when a candidate value is being confirmed.",
        ]),

        ("RS", "RS SIGNAL DECODER",
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

        ("◑", "REFINERY TRACKER",
        [
            "Navigate to REFINERY TRACKER from the left sidebar.",
            "Click + New to create a work order. Fill in the label, resource, location, refinery, and status.",
            "Set a refinery timer using the Hours and Minutes fields — the countdown starts immediately.",
            "The live progress bar fills smoothly as time elapses.",
            "When the timer expires the status automatically changes to Ready to Collect.",
            "Click a work order row on the left to open it for editing.",
            "Use Save to commit changes and Delete to remove the order.",
            "Work orders and their timers survive app restarts.",
            "In the overlay, switch to the ORDERS tab and click 📋 Open Refinery Tracker to open the flyout panel.",
            "The flyout has a hide-completed toggle (☐/☑) and a side-swap button (⇄) in its header.",
        ]),

        ("▣", "BLUEPRINT LIBRARY",
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
        ]),

        ("◆", "MINING CODEX",
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

        ("🛒", "SHOPPING LIST",
        [
            "Add items from RS Signal Decoder results, Blueprint Library ingredients, or the Mining Codex.",
            "Click 🛒 in the main toolbar to open the shopping list dialog.",
            "Each row shows the resource name, quantity, and unit.",
            "Click − next to an item to remove it.",
            "In the overlay, switch to the SHOPPING tab to view the same list inline.",
            "Shopping list data is saved and persists across sessions.",
            "Resources already in your shopping list are highlighted with a teal background in both scan results and recent scan history.",
            "An IN CART badge appears on matching scan result cards; a CART badge appears on matching history entries.",
            "Highlights update automatically when you add or remove items from the list.",
        ]),
    ];

    public HelpDialog()
    {
        Title = "Help — Nexus";
        Width = 600; Height = 660;
        PreviewKeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
        Background = (Brush)Application.Current.FindResource("BgBrush");
        Foreground = (Brush)Application.Current.FindResource("FgBrush");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;

        var outer = new Grid();
        outer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(28, 16, 28, 8),
        };

        var stack = new StackPanel();

        // Header
        stack.Children.Add(new TextBlock
        {
            Text = "NEXUS — User Guide",
            FontSize = 18, FontWeight = FontWeights.Bold,
            Foreground = (Brush)Application.Current.FindResource("AccentBrush"),
            Margin = new Thickness(0, 0, 0, 4),
        });
        stack.Children.Add(new TextBlock
        {
            Text = "A quick reference for every feature in the app.",
            FontSize = 12,
            Foreground = (Brush)Application.Current.FindResource("FgDimBrush"),
            Margin = new Thickness(0, 0, 0, 20),
        });

        foreach (var (icon, title, items) in _sections)
        {
            // Section header
            var header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 8, 0, 6),
            };
            header.Children.Add(new TextBlock
            {
                Text = icon,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                Foreground = (Brush)Application.Current.FindResource("AccentBrush"),
            });
            header.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 13, FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.FindResource("FgBrush"),
            });
            stack.Children.Add(header);

            // Bullet items
            foreach (var item in items)
            {
                // An "img:<path>" entry renders an inline screenshot instead of a bullet.
                if (item.StartsWith("img:"))
                {
                    stack.Children.Add(new Border
                    {
                        BorderBrush = (Brush)Application.Current.FindResource("NavBorderBrush"),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(4),
                        Margin = new Thickness(20, 5, 0, 7),
                        HorizontalAlignment = HorizontalAlignment.Left,
                        Child = new Image
                        {
                            Source = new BitmapImage(new Uri($"pack://application:,,,{item[4..]}", UriKind.Absolute)),
                            Stretch = Stretch.Uniform,
                            Width = 90,
                        },
                    });
                    continue;
                }

                var row = new Grid { Margin = new Thickness(8, 1, 0, 1) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var dot = new TextBlock
                {
                    Text = "·  ", FontSize = 12,
                    Foreground = (Brush)Application.Current.FindResource("FgDimBrush"),
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 1, 0, 0),
                };
                var text = new TextBlock
                {
                    Text = item, FontSize = 12, TextWrapping = TextWrapping.Wrap,
                    Foreground = (Brush)Application.Current.FindResource("FgBrush"),
                    Margin = new Thickness(0, 1, 0, 0),
                };
                Grid.SetColumn(dot, 0);
                Grid.SetColumn(text, 1);
                row.Children.Add(dot);
                row.Children.Add(text);
                stack.Children.Add(row);
            }

            // Divider
            stack.Children.Add(new Border
            {
                Height = 1,
                Margin = new Thickness(0, 14, 0, 6),
                Background = (Brush)Application.Current.FindResource("NavBorderBrush"),
            });
        }

        scroll.Content = stack;
        outer.Children.Add(scroll);

        // Footer
        var footer = new Border
        {
            BorderBrush = (Brush)Application.Current.FindResource("NavBorderBrush"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(20, 12, 20, 12),
        };
        Grid.SetRow(footer, 1);

        var footerRow = new Grid();
        footerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var closeBtn = new Button
        {
            Content = "Close",
            Style = (Style)Application.Current.FindResource("NexusButton"),
            Padding = new Thickness(20, 8, 20, 8),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        closeBtn.Click += (s, e) => Close();
        Grid.SetColumn(closeBtn, 0);

        var tutorialBtn = new Button
        {
            Content = "▶  Replay Tutorial",
            Style = (Style)Application.Current.FindResource("AccentButton"),
            Padding = new Thickness(20, 8, 20, 8),
            HorizontalAlignment = HorizontalAlignment.Right,
            ToolTip = "Walk through the auto-scan setup tour again",
        };
        tutorialBtn.Click += (s, e) => { TutorialRequested = true; Close(); };
        Grid.SetColumn(tutorialBtn, 2);

        footerRow.Children.Add(closeBtn);
        footerRow.Children.Add(tutorialBtn);
        footer.Child = footerRow;
        outer.Children.Add(footer);

        Content = outer;
    }
}
