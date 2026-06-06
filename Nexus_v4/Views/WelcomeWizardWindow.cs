using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Nexus_v4.Views;

/// <summary>Which on-screen control the current tour step is pointing at.</summary>
public enum TutorialTarget
{
    None,
    RsDecoder, ScanHistory,                       // Scan page
    OpenOverlay, ShowBox, DrawRegion, ScanToggle,  // Auto-scan (overlay)
    OverlayTabs, ShoppingTab,                       // Overlay tabs
    Blueprints, Reference, WorkOrders,             // Blueprint Library / Mining Codex / Refinery Tracker
    DataVersion,                                    // Mining-data auto-update
}

/// <summary>
/// Full-app guided tour: the RS Signal Decoder, the auto-scan workflow
/// (open overlay → show box → draw region → start/stop scanning), the overlay
/// tabs, shopping list, Blueprint Library, Mining Codex, and Refinery Tracker —
/// ending with an offer to draw the scan region.
/// As the user moves between steps it raises <see cref="TargetChanged"/> so the
/// owner can navigate to the right page/tab and point a highlight ring at the
/// matching control.
/// Shown automatically on a fresh install (see SettingsService — existing users
/// are migrated to FirstRunComplete = true) and on demand from Help.
/// </summary>
public class WelcomeWizardWindow : Window
{
    /// <summary>True if the user chose to draw the scan region now (vs. finishing/skipping).</summary>
    public bool SetupRegionRequested { get; private set; }

    /// <summary>Raised whenever the visible step changes, with the control it explains.</summary>
    public event Action<TutorialTarget>? TargetChanged;

    /// <summary>The target for the currently visible step.</summary>
    public TutorialTarget CurrentTarget => _stepTargets[_step];

    private record Step(string Icon, string Title, string[] Lines, string? Image = null);

    // Aligned 1:1 with _steps below.
    private static readonly TutorialTarget[] _stepTargets =
    [
        TutorialTarget.None,        // Welcome
        TutorialTarget.RsDecoder,   // RS Signal Decoder (RS input)
        TutorialTarget.ScanHistory, // Results & recent scans
        TutorialTarget.OpenOverlay, // Open the overlay (⧉)
        TutorialTarget.ShowBox,     // Show the scan box (⊠/⊡)
        TutorialTarget.DrawRegion,  // Draw the scan region (⊕)
        TutorialTarget.ScanToggle,  // Start/stop scanning (▶/■)
        TutorialTarget.OverlayTabs, // Overlay tabs (SCAN/ORDERS/SHOPPING)
        TutorialTarget.ShoppingTab, // Shopping list & cart
        TutorialTarget.Blueprints,  // Blueprint Library (search)
        TutorialTarget.Reference,   // Mining Codex (group/filter)
        TutorialTarget.WorkOrders,  // Refinery Tracker (+ New)
        TutorialTarget.DataVersion, // Mining-data auto-update (version badge)
        TutorialTarget.None,        // You're all set
    ];

    private static readonly Step[] _steps =
    [
        new("◆", "Welcome to Nexus",
        [
            "Nexus is your Star Citizen mining companion — it reads RS (Radioactive Signal) values straight off your screen and decodes the resource and node count.",
            "This tour walks through everything: the RS decoder, auto-scan, blueprints, the Mining Codex, your shopping list, and the Refinery Tracker.",
            "In a hurry? You can skip the tour at any time — and replay it later from the Help (?) window.",
        ]),
        new("◈", "RS Signal Decoder",
        [
            "The Scan page is the heart of Nexus. Type any RS value into the box and press Enter.",
            "Nexus instantly decodes which resource you’re looking at and how many nodes the deposit holds.",
            "No screen capture needed here — this works even if you’d rather read the number yourself.",
        ]),
        new("≡", "Results & recent scans",
        [
            "Every resource that matches your RS value is listed and ranked by likelihood, with its tier and where to sell it.",
            "Recent scans collect below so you can glance back at what you’ve already read.",
            "Click any past scan to pull its results back up instantly.",
        ]),
        new("⧉", "Open the overlay",
        [
            "Click ⧉ in the top-right of the main window to open the floating overlay.",
            "It stays on top of all windows, including your game.",
            "Drag the NEXUS header bar to move it anywhere on screen.",
            "All scan controls live on the overlay’s SCAN tab.",
        ]),
        new("⊠", "Show the scan box",
        [
            "On the SCAN tab, click ⊠ to show the magenta scan box on screen.",
            "The box marks exactly where Nexus is looking — handy while you line it up.",
            "It’s hidden by default. Click ⊡ to hide it again any time.",
        ]),
        new("⊕", "Draw the scan region",
        [
            "Click ⊕ — the screen dims and your cursor becomes a crosshair.",
            "Click and drag a tight rectangle around just the RS number — like the magenta box below.",
            "Smaller is better — include only the digits, no labels or icons.",
            "Press Escape to cancel without changing the region.",
        ], Image: "/Assets/scan_region_example.png"),
        new("▶", "Start and stop scanning",
        [
            "Click ▶ to start — Nexus reads the region every ~0.5s and decodes each RS value automatically.",
            "While scanning, the button shows ■ — click it to stop.",
            "Results and recent-scan history fill in on their own as you mine.",
            "◉ Reading… in the status bar means a value is being confirmed.",
        ]),
        new("▤", "Overlay tabs",
        [
            "The overlay packs three tabs: SCAN, ORDERS, and SHOPPING.",
            "SCAN holds the auto-scan controls and live results you just saw.",
            "ORDERS opens the Refinery Tracker and SHOPPING shows your cart — all without leaving the game.",
        ]),
        new("🛒", "Shopping list & cart",
        [
            "Add any resource to your shopping list with the 🛒 button next to a scan result.",
            "Listed items glow teal everywhere they appear — scan results, history, and here on the SHOPPING tab.",
            "It’s an at-a-glance reminder of exactly what you’re hunting for this run.",
        ]),
        new("❖", "Blueprint Library",
        [
            "Search any craftable item to see exactly which resources and quantities it needs.",
            "Each blueprint also lists HOW TO UNLOCK it — the factions, missions, and ranks that grant it.",
            "Start typing for live suggestions, then press Enter to open the full blueprint.",
        ]),
        new("▦", "Mining Codex",
        [
            "The Codex is the full reference table of every resource Nexus knows.",
            "Group by resource or location, and filter by star system or mining method to focus the list.",
            "Great for planning a route before you ever undock.",
        ]),
        new("📋", "Refinery Tracker",
        [
            "Log your refinery jobs so you never lose track of what’s cooking or when it’s done.",
            "Hit + New to start an order, fill in the materials and yields, then Save.",
            "Open it any time from here or the ORDERS tab on the overlay.",
        ]),
        new("⟳", "Always up to date",
        [
            "Nexus keeps its mining data current on its own — new prices and resources download quietly in the background.",
            "When an update lands, a banner offers to apply it; the version badge up here shows what you’re running.",
            "No reinstalls — you’ll always be mining with the latest numbers.",
        ]),
        new("✓", "You’re all set",
        [
            "That’s the full tour: RS decoding, auto-scan, blueprints, the Codex, your shopping list, and refinery tracking.",
            "Ready to draw your scan region now? You’ll want Star Citizen open with an RS value visible on screen.",
            "You can replay this tour any time from the Help (?) window.",
        ]),
    ];

    private int _step;

    private readonly StackPanel _dotsPanel = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock  _stepLabel = new() { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Border     _bodyHost  = new();
    private readonly Button     _backBtn;
    private readonly Button     _nextBtn;
    private readonly Button     _skipBtn;

    public WelcomeWizardWindow()
    {
        Title = "Welcome to Nexus";
        Width = 560; Height = 690;
        Background = (Brush)Application.Current.FindResource("BgBrush");
        Foreground = (Brush)Application.Current.FindResource("FgBrush");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        PreviewKeyDown += OnKeyDown;

        _stepLabel.Foreground = (Brush)Application.Current.FindResource("FgDimBrush");

        var outer = new Grid();
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // step dots
        outer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });  // body
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // footer

        // ── Step indicator ──────────────────────────────────────────────────────
        var indicator = new StackPanel { Margin = new Thickness(0, 18, 0, 4) };
        indicator.Children.Add(_dotsPanel);
        indicator.Children.Add(_stepLabel);
        Grid.SetRow(indicator, 0);
        outer.Children.Add(indicator);

        // ── Body host ──────────────────────────────────────────────────────────
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(32, 12, 32, 8),
            Content = _bodyHost,
        };
        Grid.SetRow(scroll, 1);
        outer.Children.Add(scroll);

        // ── Footer ──────────────────────────────────────────────────────────────
        var footer = new Border
        {
            BorderBrush = (Brush)Application.Current.FindResource("NavBorderBrush"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(20, 12, 20, 12),
        };
        Grid.SetRow(footer, 2);

        var footerRow = new Grid();
        footerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });               // skip / finish
        footerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });               // back
        footerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });               // next / set up

        _skipBtn = new Button
        {
            Style = (Style)Application.Current.FindResource("NexusButton"),
            Padding = new Thickness(16, 8, 16, 8),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        _skipBtn.Click += (s, e) => { SetupRegionRequested = false; Close(); };
        Grid.SetColumn(_skipBtn, 0);

        _backBtn = new Button
        {
            Content = "←  Back",
            Style = (Style)Application.Current.FindResource("NexusButton"),
            Padding = new Thickness(16, 8, 16, 8),
            Margin = new Thickness(0, 0, 8, 0),
        };
        _backBtn.Click += (s, e) => { if (_step > 0) { _step--; Render(); } };
        Grid.SetColumn(_backBtn, 2);

        _nextBtn = new Button
        {
            Style = (Style)Application.Current.FindResource("AccentButton"),
            Padding = new Thickness(20, 8, 20, 8),
        };
        _nextBtn.Click += OnNext;
        Grid.SetColumn(_nextBtn, 3);

        footerRow.Children.Add(_skipBtn);
        footerRow.Children.Add(_backBtn);
        footerRow.Children.Add(_nextBtn);
        footer.Child = footerRow;
        outer.Children.Add(footer);

        Content = outer;
        Render();
    }

    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        switch (e.Key)
        {
            case System.Windows.Input.Key.Escape: SetupRegionRequested = false; Close(); break;
            case System.Windows.Input.Key.Left  when _step > 0: _step--; Render(); break;
            case System.Windows.Input.Key.Right when _step < _steps.Length - 1: _step++; Render(); break;
        }
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (_step == _steps.Length - 1) { SetupRegionRequested = true; Close(); }
        else { _step++; Render(); }
    }

    private void Render()
    {
        bool last = _step == _steps.Length - 1;

        // Step dots
        _dotsPanel.Children.Clear();
        for (int i = 0; i < _steps.Length; i++)
            _dotsPanel.Children.Add(new Border
            {
                Width = 8, Height = 8,
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(3, 0, 3, 0),
                Background = (Brush)Application.Current.FindResource(i == _step ? "AccentBrush" : "BorderBrush"),
            });
        _stepLabel.Text = $"Step {_step + 1} of {_steps.Length}";

        // Body
        _bodyHost.Child = BuildBody(_steps[_step]);

        // Footer state
        _backBtn.Visibility = _step > 0 ? Visibility.Visible : Visibility.Collapsed;
        _nextBtn.Content = last ? "Set up auto-scan now" : "Next  →";
        _skipBtn.Content = last ? "Finish" : "Skip tour";

        TargetChanged?.Invoke(_stepTargets[_step]);
    }

    private static UIElement BuildBody(Step step)
    {
        var stack = new StackPanel();

        stack.Children.Add(new TextBlock
        {
            Text = step.Icon,
            FontSize = 40,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = (Brush)Application.Current.FindResource("AccentBrush"),
            Margin = new Thickness(0, 8, 0, 8),
        });
        stack.Children.Add(new TextBlock
        {
            Text = step.Title,
            FontSize = 20, FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = (Brush)Application.Current.FindResource("FgBrush"),
            Margin = new Thickness(0, 0, 0, 18),
        });

        foreach (var line in step.Lines)
        {
            var row = new Grid { Margin = new Thickness(4, 4, 4, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var dot = new TextBlock
            {
                Text = "·  ", FontSize = 14,
                Foreground = (Brush)Application.Current.FindResource("AccentBrush"),
                VerticalAlignment = VerticalAlignment.Top,
            };
            var body = new TextBlock
            {
                Text = line, FontSize = 13, TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.FindResource("FgBrush"),
                LineHeight = 19,
            };
            Grid.SetColumn(dot, 0);
            Grid.SetColumn(body, 1);
            row.Children.Add(dot);
            row.Children.Add(body);
            stack.Children.Add(row);
        }

        if (step.Image is { } imagePath)
        {
            var frame = new Border
            {
                BorderBrush = (Brush)Application.Current.FindResource("BorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(4, 14, 4, 4),
                HorizontalAlignment = HorizontalAlignment.Center,
                ClipToBounds = true,
                Child = new Image
                {
                    Source = new System.Windows.Media.Imaging.BitmapImage(
                        new Uri($"pack://application:,,,{imagePath}", UriKind.Absolute)),
                    Stretch = Stretch.Uniform,
                    MaxWidth = 460,
                },
            };
            stack.Children.Add(frame);
        }

        return stack;
    }
}
