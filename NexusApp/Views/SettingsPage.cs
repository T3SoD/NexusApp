using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using NexusApp.Services;

namespace NexusApp.Views;

// Settings as an in-app page (hosted in the main content area, reached from the Settings module
// at the bottom of the app dock),
// not a pop-out dialog. Single theme (MOBIGLAS), so there is no appearance/theme picker: just
// the Game.log paths, Blueprint Network identity, diagnostics, and the destructive clear-data action.
//
// The page is a HUD tab strip: GAME / DIAGNOSTICS / UPDATES / INTERFACE cluster to the left, the
// destructive DATA tab docked to the right, and one category pane visible at a time. A traveling
// underline slides between tabs (crossfading amber to danger red over DATA), the incoming pane fades
// and slides in the direction of travel, and the GAME tab's needs-attention pip breathes while no
// Game.log is set. All motion collapses to instant state changes under Reduce animations.
public sealed class SettingsPage : UserControl
{
    private readonly Action _openLogMonitor;
    private readonly Action _openAppLogMonitor;

    // The destructive DATA tab's red, matching the literal SettingsPage already uses elsewhere.
    private static readonly Color DangerColor = Color.FromRgb(0xE5, 0x53, 0x53);

    // Tab strip state. These back the five-tab HUD strip so the switch logic (and the later motion
    // pass) has one place to read the tabs, labels, panes, and the traveling underline from.
    private readonly Border[] _tabButtons = new Border[5];
    private readonly TextBlock[] _tabLabels = new TextBlock[5];
    private readonly ScrollViewer[] _panes = new ScrollViewer[5];
    private readonly TranslateTransform _underlineT = new();
    private readonly SolidColorBrush _underlineBrush;               // local + unfrozen so it can be color-crossfaded on switch
    private readonly SolidColorBrush _dangerFull = new(DangerColor);
    private readonly SolidColorBrush _dangerDim = new(Color.FromArgb(0xB8, 0xE5, 0x53, 0x53)); // danger at ~72% for the idle DATA tab
    private readonly ScaleTransform _gameDotScale = new(1, 1);      // the needs-attention pip's breathe scale
    private Grid _stripHost = null!;
    private Border _underline = null!;
    private DropShadowEffect _underlineGlow = null!;               // the underline's glow, retinted amber/danger on switch
    private Ellipse _gameDot = null!;
    private int _activeIndex = -1;

    // Clear-saved-data confirmation modal. A themed in-page scrim over a chamfered danger panel
    // replaces the first Clear-saved-data MessageBox; the second (post-clear restart) prompt stays a
    // MessageBox. Built once and shown/hidden, so the entrance can animate and reset on reopen.
    private Border _modalScrim = null!;
    private Grid _modalPanel = null!;
    private readonly TranslateTransform _modalPanelT = new(0, 12);   // panel rise on entrance
    private readonly ScaleTransform _modalPanelScale = new(0.98, 0.98);

    // Updates section (UPDATES tab). Held so the rows can be refreshed as the update service
    // moves between states; null in the demo profile, where the section is inert.
    private TextBlock? _updateStatusText;
    private StackPanel? _updateActionHost;
    private Hud.ToggleSwitch? _updateCheckToggle;

    // Market data section (UPDATES tab). Held so the rows can be refreshed as the fetch cycle
    // moves between states; null in the demo profile, where the section is inert.
    private Hud.ToggleSwitch? _marketToggle;
    private Button? _marketRefreshBtn;
    private TextBlock? _marketStatusText;
    private Hud.ToggleSwitch? _sctToggle;

    // Overlay ghost mode toggle (INTERFACE). Held so it can be re-synced when ghost mode is
    // changed elsewhere (the ghost rail's own flyout switch) instead of only seeding once here.
    private Hud.ToggleSwitch? _ghostToggle;

    // Overlay click-through toggle (INTERFACE). Held so it can be re-synced when the overlay's
    // quick-settings flyout flips it elsewhere, the same precedent as _ghostToggle above.
    private Hud.ToggleSwitch? _passThroughToggle;

    public SettingsPage(Action openLogMonitor, Action openAppLogMonitor)
    {
        _openLogMonitor = openLogMonitor;
        _openAppLogMonitor = openAppLogMonitor;
        _underlineBrush = new SolidColorBrush(Hud.Col("AccentColor"));

        var root = new Grid { Margin = new Thickness(28, 22, 28, 0) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                    // page header
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                    // tab strip
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });// pane host

        // Reworded 2026-08-01 (app review). "Everything stays on this machine" predates the update
        // checker and the two market-data sources, and this same page now hosts all three network
        // toggles. It was defensible about the USER'S data (update checks send nothing about you,
        // market fetches are anonymous reads) but read as "this app never goes online", which is no
        // longer true. Say the honest version instead of the reassuring one.
        var header = Hud.Header("Configuration", "Settings",
            "Paths and data. Your files stay on this machine; anything that uses the internet is off "
            + "until you turn it on.");
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        BuildStrip();
        Grid.SetRow(_stripHost, 1);
        root.Children.Add(_stripHost);

        // ── Pane host: the four category panes stacked in one cell; only the active one is visible ──
        var paneHost = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        _panes[0] = BuildGamePane();
        _panes[1] = BuildDiagnosticsPane();
        _panes[2] = BuildUpdatesPane();
        _panes[3] = BuildInterfacePane();
        _panes[4] = BuildDataPane();
        foreach (var pane in _panes) { pane.Visibility = Visibility.Collapsed; paneHost.Children.Add(pane); }
        Grid.SetRow(paneHost, 2);
        root.Children.Add(paneHost);

        // Clear-saved-data confirmation modal: a full-page scrim over the whole page (all rows), drawn
        // above the header, strip, and panes. Hidden until the Data tab's Clear button opens it.
        var modal = BuildClearDataModal();
        Grid.SetRowSpan(modal, 3);
        Panel.SetZIndex(modal, 100);
        root.Children.Add(modal);

        Content = root;

        RefreshGameDot();

        // Restore the last tab, but never auto-open the destructive DATA tab. persist:false so simply
        // opening the page never writes settings.json, mirroring the overlay's restore idiom.
        int restore = Array.IndexOf(SettingsTabs.Ids,
            SettingsTabs.NormalizeForRestore(App.Settings.Current.SettingsActiveTab));
        SwitchTab(restore, persist: false);

        // ActualWidth is 0 until layout runs, so place the underline once the strip is laid out, and
        // keep it aligned as the window resizes. Both snap instantly; only a user tab switch animates.
        _stripHost.Loaded += (_, _) => MoveUnderline(_activeIndex, animate: false);
        _stripHost.SizeChanged += (_, _) => MoveUnderline(_activeIndex, animate: false);
    }

    // ── Tab strip ───────────────────────────────────────────────────────────────
    // The strip: left cluster (GAME / DIAGNOSTICS / UPDATES / INTERFACE), a star spacer, and the DATA
    // tab docked right, over a full-width hairline with the traveling underline drawn on top of it.
    private void BuildStrip()
    {
        // No top margin here: Hud.Header already carries an 18px bottom margin, and stacking a
        // second margin on the strip would double the header-to-strip gap.
        _stripHost = new Grid { Height = 42 };
        _stripHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                     // left cluster
        _stripHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });// spacer
        _stripHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                     // DATA (right)

        var hairline = new Border
        {
            Height = 1, Background = Hud.Br("NavBorderBrush"), VerticalAlignment = VerticalAlignment.Bottom,
        };
        Grid.SetColumnSpan(hairline, 3);
        _stripHost.Children.Add(hairline);

        var cluster = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
        cluster.Children.Add(MakeTab(0, "Game", danger: false));
        cluster.Children.Add(MakeTab(1, "Diagnostics", danger: false));
        cluster.Children.Add(MakeTab(2, "Updates", danger: false));
        cluster.Children.Add(MakeTab(3, "Interface", danger: false));
        Grid.SetColumn(cluster, 0);
        _stripHost.Children.Add(cluster);

        var dataTab = MakeTab(4, "Data", danger: true);
        Grid.SetColumn(dataTab, 2);
        _stripHost.Children.Add(dataTab);

        // Traveling underline over the hairline. A LOCAL unfrozen brush so its amber<->danger color can
        // be crossfaded on switch; the transform is what slides it between tabs, and the glow is retinted
        // to match the active tab.
        _underlineGlow = new DropShadowEffect { Color = Hud.Col("AccentColor"), BlurRadius = 8, ShadowDepth = 0, Opacity = 0.5 };
        _underline = new Border
        {
            Height = 2, Width = 0, CornerRadius = new CornerRadius(1),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
            Background = _underlineBrush, RenderTransform = _underlineT, Effect = _underlineGlow,
        };
        Grid.SetColumnSpan(_underline, 3);
        _stripHost.Children.Add(_underline);
    }

    // One tab: a padded chamfer-topped click box whose whole area selects the tab. Index 0 also carries
    // the needs-attention pip. Hover swaps to the bright text + faint fill; leave restores idle or active.
    private Border MakeTab(int index, string label, bool danger)
    {
        var text = new TextBlock
        {
            Text = label.ToUpperInvariant(), FontFamily = Hud.Font("UiFont"),
            FontSize = 12, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center,
            Foreground = danger ? _dangerDim : Hud.Br("FgDimBrush"),
        };
        _tabLabels[index] = text;

        var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(text);

        if (index == 0)
        {
            // Needs-attention pip: an amber dot 8px after the GAME label, shown only when no effective
            // Game.log is set (see RefreshGameDot). It breathes (opacity + scale about its center) while
            // visible, unless Reduce animations is on.
            _gameDot = new Ellipse
            {
                Width = 7, Height = 7, Fill = Hud.Br("AccentBrush"), Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed,
                RenderTransform = _gameDotScale, RenderTransformOrigin = new Point(0.5, 0.5),
                Effect = new DropShadowEffect { Color = Hud.Col("AccentColor"), BlurRadius = 7, ShadowDepth = 0, Opacity = 0.4 },
            };
            content.Children.Add(_gameDot);
        }

        var btn = new Border
        {
            Background = Brushes.Transparent, CornerRadius = new CornerRadius(3, 3, 0, 0),
            Padding = new Thickness(15, 9, 15, 9), Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Bottom, Child = content,
        };
        _tabButtons[index] = btn;

        btn.MouseEnter += (s, e) =>
        {
            if (danger) { text.Foreground = _dangerFull; btn.Background = new SolidColorBrush(Color.FromArgb(0x1F, 0xE5, 0x53, 0x53)); }
            else { text.Foreground = Hud.Br("FgBrush"); btn.Background = Hud.Br("AccentFaintBrush"); }
        };
        btn.MouseLeave += (s, e) => { text.Foreground = TabColor(index); btn.Background = Brushes.Transparent; };
        btn.MouseLeftButtonUp += (s, e) => SwitchTab(index);
        return btn;
    }

    // The tab label's non-hover color: active (GoldBrush, or full red for DATA) or idle (FgDimBrush, or
    // dim red for DATA).
    private Brush TabColor(int index)
    {
        bool danger = index == 4;
        bool active = index == _activeIndex;
        if (danger) return active ? _dangerFull : _dangerDim;
        return active ? Hud.Br("GoldBrush") : Hud.Br("FgDimBrush");
    }

    // Show one category pane and mark its tab active. A no-op when the tab is already the active,
    // shown one. Only user-driven switches persist and log; the restore-on-open call passes false.
    private void SwitchTab(int index, bool persist = true)
    {
        if (index == _activeIndex && _panes[index].Visibility == Visibility.Visible) return;

        // Direction of travel drives the pane slide, so capture the outgoing index before it is replaced.
        int previous = _activeIndex;
        _activeIndex = index;

        for (int i = 0; i < _panes.Length; i++)
            _panes[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;

        for (int i = 0; i < _tabLabels.Length; i++)
            _tabLabels[i].Foreground = TabColor(i);

        MoveUnderline(index, animate: true);
        RevealPane(index, previous);

        if (persist)
        {
            App.Settings.Current.SettingsActiveTab = SettingsTabs.Ids[index];
            App.Settings.Save();
            Logger.Info($"[UI] Settings tab: {SettingsTabs.Ids[index].ToUpperInvariant()}");
        }
    }

    // The active tab's left offset and full padded width, measured within the strip. Only valid after
    // layout has run (ActualWidth is 0 before then).
    private (double X, double Width) MeasureTab(int index)
    {
        var t = _tabButtons[index];
        double x = t.TransformToAncestor(_stripHost).Transform(default).X;
        return (x, t.ActualWidth);
    }

    // Places the traveling underline under the given tab: its X offset, its width, and its amber/danger
    // color and glow. A user switch (animate true) slides the position and crossfades the color over
    // 280ms; layout re-placement, and Reduce animations, snap straight to the final state.
    private void MoveUnderline(int index, bool animate)
    {
        if (_stripHost.ActualWidth < 1) return;   // layout has not run yet; the Loaded handler will place it
        var (x, w) = MeasureTab(index);
        bool danger = index == 4;
        var color = danger ? DangerColor : Hud.Col("AccentColor");

        // The glow is retinted to the target color at the start; the brush crossfade carries the visual.
        _underlineGlow.Color = color;
        _underlineGlow.Opacity = danger ? 0.55 : 0.5;

        if (!animate || Motion.Reduced)
        {
            _underlineT.BeginAnimation(TranslateTransform.XProperty, null);
            _underlineT.X = x;
            _underline.BeginAnimation(FrameworkElement.WidthProperty, null);
            _underline.Width = w;
            _underlineBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            _underlineBrush.Color = color;
            return;
        }

        var dur = TimeSpan.FromMilliseconds(Motion.SlideMs);
        _underlineT.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(x, dur) { EasingFunction = Motion.Reveal });
        _underline.BeginAnimation(FrameworkElement.WidthProperty,
            new DoubleAnimation(w, dur) { EasingFunction = Motion.Reveal });
        _underlineBrush.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(color, dur) { EasingFunction = Motion.Reveal });
    }

    // Slides the incoming pane in from the direction of travel (higher index enters from the right,
    // lower from the left) while fading it up over 240ms. The outgoing pane just collapses, matching the
    // mock's lack of an exit animation. The first placement (no previous tab) and Reduce animations both
    // show the pane immediately with no slide.
    private void RevealPane(int index, int previous)
    {
        var pane = _panes[index];
        if (Motion.Reduced || previous < 0)
        {
            pane.BeginAnimation(UIElement.OpacityProperty, null);
            pane.Opacity = 1;
            pane.RenderTransform = null;
            return;
        }

        int dir = index > previous ? 1 : -1;
        var slide = new TranslateTransform(12 * dir, 0);
        pane.RenderTransform = slide;
        pane.Opacity = 0;

        var dur = TimeSpan.FromMilliseconds(Motion.DrillMs);
        pane.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = Motion.Reveal });
        var glide = new DoubleAnimation(12 * dir, 0, dur) { EasingFunction = Motion.Reveal };
        // Drop the transform once settled, but only if a newer switch has not already replaced it.
        glide.Completed += (_, _) => { if (ReferenceEquals(pane.RenderTransform, slide)) pane.RenderTransform = null; };
        slide.BeginAnimation(TranslateTransform.XProperty, glide);
    }

    // Show the GAME pip while the tab needs attention. Two causes: no effective Game.log exists (no
    // manual path set and nothing auto-detected on disk - FindGameLog always returns a candidate via
    // its LIVE default fallback, so the file itself is probed rather than trusting a null), or the
    // custom-folder notice is still unacknowledged (issue #28, spec section 4: the pip lights until
    // the user has seen why a non-standard install records no blueprints).
    //
    // internal, not private: the notice can also be acknowledged from Operations, and this page is a
    // lazily-built singleton that is never rebuilt on navigation, so MainWindow re-runs this through
    // its RefreshSettingsGameDot pass-through. internal keeps it off the public surface - the
    // pass-through is the intended entry point for other views.
    internal void RefreshGameDot()
    {
        bool missing = string.IsNullOrWhiteSpace(App.Settings.Current.GameLogPath)
            && !System.IO.File.Exists(GameLogWatcher.FindGameLog());
        bool needsAttention = missing
            || CustomChannelNotice.ShouldShow(App.GameLogFeed.ActiveChannel, App.GameLogFeed.Path,
                   App.Settings.Current.CustomChannelNoticePath,
                   App.Settings.Current.CustomChannelRecordsBlueprints);
        _gameDot.Visibility = needsAttention ? Visibility.Visible : Visibility.Collapsed;

        // The dot changes the GAME tab's width, so re-place the traveling underline when GAME is
        // the active tab. Deferred to DispatcherPriority.Loaded so the tab's ActualWidth reflects
        // the new dot state before MeasureTab reads it. Skipped at construction (_activeIndex is
        // -1 then, and the initial placement is owned by the _stripHost.Loaded handler).
        if (_activeIndex == 0)
            Dispatcher.BeginInvoke(new Action(() => MoveUnderline(0, animate: false)),
                System.Windows.Threading.DispatcherPriority.Loaded);

        // Breathe the pip (opacity 0.45<->1.0, scale 0.82<->1.06) to draw the eye to the GAME tab while
        // it needs attention; hold it solid when hidden or when Reduce animations is on. Reduced is read
        // here, so a live toggle is honored the next time the effective path is re-checked, matching how
        // the app applies Reduced lazily.
        if (needsAttention && !Motion.Reduced)
        {
            var dur = TimeSpan.FromMilliseconds(Motion.BreatheMs);
            _gameDot.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0.45, 1.0, dur) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = Motion.Breathe });
            _gameDotScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(0.82, 1.06, dur) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = Motion.Breathe });
            _gameDotScale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(0.82, 1.06, dur) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = Motion.Breathe });
        }
        else
        {
            _gameDot.BeginAnimation(UIElement.OpacityProperty, null);
            _gameDot.Opacity = 1.0;
            _gameDotScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _gameDotScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _gameDotScale.ScaleX = 1;
            _gameDotScale.ScaleY = 1;
        }
    }

    /// <summary>Jumps straight to the GAME tab. Public so MainWindow's SESSION chip can drive the
    /// page to the Game.log path controls when it clicks through from its own "no log" state (app
    /// review, Task 10) - a real user-driven switch, so it persists and logs exactly like a manual
    /// tab click.</summary>
    public void SwitchToGameTab() => SwitchTab(Array.IndexOf(SettingsTabs.Ids, "game"));

    // ── Category panes ──────────────────────────────────────────────────────────
    // GAME: Game.log paths + Blueprint Network identity.
    private ScrollViewer BuildGamePane()
    {
        var panel = new StackPanel { Margin = new Thickness(2, 2, 14, 40) };

        var openLogBtn = GhostButton("Open Game.log Monitor");
        openLogBtn.Click += (s, e) => _openLogMonitor?.Invoke();

        // Issue #28 auto-follow: which channel the shared tail is watching right now, and which
        // sibling channel Game.logs exist on disk. Refreshed whenever the feed switches channels
        // (auto-follow or a manual path change) or is (re)pointed at all, so a channel flip during a
        // live session is reflected here without reopening Settings.
        var envStatus = new TextBlock
        {
            FontFamily = Hud.Font("MonoFont"), FontSize = 12,
            Foreground = Hud.Br("FgBrush"),
            TextWrapping = TextWrapping.Wrap, MaxWidth = 360,
            HorizontalAlignment = HorizontalAlignment.Right, TextAlignment = TextAlignment.Right,
        };
        void RefreshEnvStatus()
        {
            var ch = App.GameLogFeed.ActiveChannel;
            var candidates = GameChannelProbe.Candidates(App.GameLogFeed.Path);
            // Nothing found has two very different causes: a genuinely custom folder (auto-follow is
            // off by design), or a standard layout whose Game.log simply is not there yet because
            // Star Citizen has not run. Only the first deserves the custom-folder note.
            var found = candidates.Count > 0
                ? string.Join(" · ", candidates.Select(p => GameChannels.FolderName(GameChannels.FromLogPath(p))))
                : GameChannels.FromLogPath(App.GameLogFeed.Path) == GameChannel.Custom
                    ? "custom folder (single file, no auto-follow)"
                    : "none yet (Game.log appears after Star Citizen runs)";
            envStatus.Text = $"Watching: {GameChannels.FolderName(ch)} (auto)\nChannels found: {found}";
        }
        RefreshEnvStatus();
        App.GameLogFeed.ChannelChanged += _ => Dispatcher.Invoke(RefreshEnvStatus);
        App.GameLogFeed.Started += _ => Dispatcher.Invoke(RefreshEnvStatus);

        // Custom-folder authorization: visible only while the active channel is Custom (an
        // unrecognized parent folder). Blueprint recording there defaults off; this is the user's
        // explicit opt-in that a non-standard install is really their LIVE game.
        var customAllow = new CheckBox
        {
            Content = "Record blueprints from this custom folder",
            IsChecked = App.Settings.Current.CustomChannelRecordsBlueprints,
            Foreground = Hud.Br("FgBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        customAllow.Checked += (_, _) => ApplyCustomAllow(true);
        customAllow.Unchecked += (_, _) => ApplyCustomAllow(false);
        var customRow = SettingRow("Custom folder detected",
            "This Game.log is not in a standard channel folder. Blueprint recording is OFF by default " +
            "for non-standard installs; check the box only if this is your real LIVE install.",
            customAllow);
        void RefreshCustomRow() => customRow.Visibility =
            App.GameLogFeed.ActiveChannel == GameChannel.Custom ? Visibility.Visible : Visibility.Collapsed;
        RefreshCustomRow();
        App.GameLogFeed.ChannelChanged += _ => Dispatcher.Invoke(RefreshCustomRow);
        // A channel flip can also start or clear the custom-folder notice, which the tab's
        // needs-attention pip mirrors.
        App.GameLogFeed.ChannelChanged += _ => Dispatcher.Invoke(RefreshGameDot);

        panel.Children.Add(SectionPanel("Game.log Paths", false,
            BuildPathRow(
                "Game.log path",
                App.Settings.Current.GameLogPath,
                "Game log (*.log)|*.log|All files (*.*)|*.*", "Game.log",
                "Required for: Session Tracking / Auto-Track Blueprints, Cargo Hauling, and Server / Shard " +
                "tracking. Auto-detected for default installs; set it here only if Nexus can't find it.",
                ApplyGameLogPath),
            BuildPathRow(
                "global.ini path (optional)",
                App.Settings.Current.GlobalIniPath,
                "Localization (*.ini)|*.ini|All files (*.*)|*.*", "global.ini",
                "Used to translate blueprint names renamed by a community localization mod (custom component " +
                "strings). Leave blank to auto-detect next to the Game.log.",
                ApplyGlobalIniPath),
            SettingRow("Environment status",
                "Nexus follows whichever channel is writing its Game.log (LIVE, HOTFIX, PTU, EPTU, " +
                "TECH-PREVIEW - siblings of the path above). Test channels never record blueprints; " +
                "HOTFIX counts as LIVE.",
                envStatus),
            customRow,
            SettingRow("Game.log Monitor",
                "Track your session from Star Citizen's Game.log: auto-collect blueprints you receive " +
                "(they're marked Owned in your library), or import the ones you already own from past logs.",
                openLogBtn, last: true)));

        var handleLabel = new TextBlock
        {
            Text = string.IsNullOrEmpty(App.Settings.Current.DetectedRsiHandle)
                ? "RSI Handle: not detected yet."
                : $"RSI Handle: {App.Settings.Current.DetectedRsiHandle}",
            FontSize = 11.5, Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right, TextAlignment = TextAlignment.Right,
            TextWrapping = TextWrapping.Wrap, MaxWidth = 260,
            Foreground = (Brush)Application.Current.FindResource("FgBrush"),
        };
        var detectHandleBtn = GhostButton("Detect my RSI handle");
        detectHandleBtn.Click += (s, e) =>
        {
            App.GameLog?.DetectHandleFromCurrentFile();
            var h = App.Settings.Current.DetectedRsiHandle;
            handleLabel.Text = string.IsNullOrEmpty(h)
                ? "RSI Handle: not found. Open Star Citizen (it writes Game.log at login), then try again."
                : $"RSI Handle: {h}";
        };
        var handleControl = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
        handleControl.Children.Add(detectHandleBtn);
        handleControl.Children.Add(handleLabel);
        panel.Children.Add(SectionPanel("Blueprint Network", false,
            SettingRow("Detect my RSI handle",
                "When you export a library to share, Nexus pre-fills your RSI handle, read from Star " +
                "Citizen's Game.log (read-only). Detect it here, or just use a nickname at export instead.",
                handleControl, last: true)));

        return Pane(panel);
    }

    // DIAGNOSTICS: app log monitor, CPU rendering toggle, last automatic restart.
    private ScrollViewer BuildDiagnosticsPane()
    {
        var panel = new StackPanel { Margin = new Thickness(2, 2, 14, 40) };

        var openAppLogBtn = GhostButton("Open App Log Monitor");
        openAppLogBtn.Click += (s, e) => _openAppLogMonitor?.Invoke();
        var cpuRenderToggle = new Hud.ToggleSwitch(App.Settings.Current.SoftwareRendering)
        {
            OnToggled = on =>
            {
                App.Settings.Current.SoftwareRendering = on;
                App.Settings.Save();
                Logger.Info($"[UI] CPU rendering (compatibility): {(on ? "on" : "off")} - takes effect next launch");
            },
        };
        panel.Children.Add(SectionPanel("Diagnostics", false,
            SettingRow("App Log Monitor",
                "See Nexus's own activity log live, and save a snapshot (app info + log) to send to the " +
                "developer if you hit a bug, on Discord or attached to a GitHub issue at " +
                "github.com/T3SoD/NexusApp/issues.",
                openAppLogBtn, last: false),
            SettingRow("CPU rendering",
                "If Nexus restarts itself or its window breaks when Star Citizen crashes or quits, " +
                "turn this on: Nexus draws with the CPU instead of the graphics card, which sidesteps " +
                "those display errors at a small CPU cost. Takes effect the next time Nexus starts.",
                cpuRenderToggle, last: false),
            SettingRow("Last automatic restart",
                "Shows the most recent time Nexus closed and reopened itself automatically after " +
                "Windows reported a display error, usually while the game was crashing or quitting.",
                RestartValue(App.Settings.Current.LastAutoRelaunchUtc), last: true)));

        return Pane(panel);
    }

    // UPDATES: app auto-update checks + live market data. Both moved out of DIAGNOSTICS (app
    // review, Task 10): they are opt-in feature/data-source toggles, not diagnostic tooling, and
    // grew that pane into a mixed bag as each shipped (Updates in v6.7, Market in v6.10). Section
    // names are unchanged (help breadcrumbs rule) - only the pane they live under moved.
    private ScrollViewer BuildUpdatesPane()
    {
        var panel = new StackPanel { Margin = new Thickness(2, 2, 14, 40) };

        // Updates: consent toggle, manual check, contextual action. Inert in the demo profile.
        if (AppPaths.IsDemoProfile)
        {
            panel.Children.Add(SectionPanel("Updates", false,
                SettingRow("Update checks",
                    "Update checks are unavailable in the demo profile.",
                    new TextBlock
                    {
                        Text = "Unavailable", FontFamily = Hud.Font("MonoFont"), FontSize = 13,
                        Foreground = Hud.Br("FgDimBrush"),
                    }, last: true)));
        }
        else
        {
            _updateCheckToggle = new Hud.ToggleSwitch(App.Settings.Current.UpdateCheckEnabled == true)
            {
                OnToggled = on =>
                {
                    App.Settings.Current.UpdateCheckEnabled = on;
                    App.Settings.Save();
                    Logger.Info($"[UPDATE] auto-check setting: {(on ? "on" : "off")}");
                },
            };
            var checkToggle = _updateCheckToggle;

            var checkNow = GhostButton("Check now");
            checkNow.Click += (_, _) => _ = App.Update.CheckAsync(manual: true);
            _updateStatusText = new TextBlock
            {
                FontFamily = Hud.Font("MonoFont"), FontSize = 11.5, Foreground = Hud.Br("FgDimBrush"),
                Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Right,
                TextWrapping = TextWrapping.Wrap, MaxWidth = 260, TextAlignment = TextAlignment.Right,
            };
            var checkStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
            checkStack.Children.Add(checkNow);
            checkStack.Children.Add(_updateStatusText);

            _updateActionHost = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };

            panel.Children.Add(SectionPanel("Updates", false,
                SettingRow("Check for updates automatically",
                    "Once a day when Nexus starts, Nexus asks github.com for the latest version " +
                    "number. Nothing about you or your data is sent. Downloads and installs always " +
                    "ask first.",
                    checkToggle, last: false),
                SettingRow("Check now",
                    "Ask github.com for the latest version right now, regardless of the automatic " +
                    "setting.",
                    checkStack, last: false),
                SettingRow("Update",
                    "Download and install the latest version. Every step asks before acting.",
                    _updateActionHost, last: true)));

            RefreshUpdateRows();
            if (App.Update != null)
                App.Update.Changed += () => Dispatcher.Invoke(RefreshUpdateRows);
        }

        // Market data: consent toggle, manual refresh, source note. Inert in the demo profile.
        // The UEX badge (Task 13) replaces the source row's plain text.
        if (AppPaths.IsDemoProfile)
        {
            panel.Children.Add(SectionPanel(MarketNotice.SettingsTitle, false,
                SettingRow("Market data",
                    "Market data is unavailable in the demo profile.",
                    new TextBlock
                    {
                        Text = "Unavailable", FontFamily = Hud.Font("MonoFont"), FontSize = 13,
                        Foreground = Hud.Br("FgDimBrush"),
                    }, last: true)));
        }
        else
        {
            _marketToggle = new Hud.ToggleSwitch(App.Settings.Current.MarketDataEnabled == true)
            {
                OnToggled = on =>
                {
                    App.Settings.Current.MarketDataEnabled = on;
                    App.Settings.Save();
                    Logger.Info("[UI] Toggle: market data " + (on ? "on" : "off"));
                    if (on) App.Market.MaybeAutoRefresh();
                    // No cycle-start event: reflect the toggle's effect on the refresh button
                    // (and status line) immediately rather than waiting on Changed.
                    RefreshMarketRows();
                },
            };

            // SC Trade Tools cross-check: graduated from the owner-only Admin flag to a real
            // Settings consent row (2026-07-30, the maintainer cleared the gate) - directly
            // adjacent to the market data toggle above, same section, same house chrome. Both
            // surfaces read/write the same AppSettings.SctDataEnabled field; the Admin card keeps
            // its own toggle as a one-shot fetch tool for the owner.
            _sctToggle = new Hud.ToggleSwitch(App.Settings.Current.SctDataEnabled)
            {
                OnToggled = on =>
                {
                    App.Settings.Current.SctDataEnabled = on;
                    App.Settings.Save();
                    Logger.Info("[UI] Toggle: SCT cross-check " + (on ? "on" : "off"));
                    if (on)
                    {
                        // Same start/fetch path the Admin card uses (AdminPage.cs's own SCT
                        // toggle), so the layer goes live immediately - no restart needed.
                        _ = Task.Run(async () =>
                        {
                            try { await App.Sct.RefreshAsync(manual: true); }
                            catch (Exception ex) { Logger.Error("[UI] settings: SCT fetch failed", ex); }
                        });
                    }
                    // Off needs no teardown call: every public entry point on SctMarketService
                    // (Start, RefreshAsync, SnapshotFetchedUtc, Find, SctOnlyBuyers) checks the
                    // flag first and self-gates, live, on every call - the setting write above is
                    // the whole story.
                },
            };

            _marketRefreshBtn = GhostButton(MarketNotice.RefreshNow);
            _marketRefreshBtn.Click += (_, _) =>
            {
                // Disabled at click time: Changed only fires at the END of a cycle, so there is
                // no cycle-start signal to react to instead.
                _marketRefreshBtn!.IsEnabled = false;
                _ = App.Market.RefreshAsync(manual: true);
            };
            _marketStatusText = new TextBlock
            {
                FontFamily = Hud.Font("MonoFont"), FontSize = 11.5, Foreground = Hud.Br("FgDimBrush"),
                Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Right,
                TextWrapping = TextWrapping.Wrap, MaxWidth = 260, TextAlignment = TextAlignment.Right,
            };
            var marketRefreshStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
            marketRefreshStack.Children.Add(_marketRefreshBtn);
            marketRefreshStack.Children.Add(_marketStatusText);
            // Second dim line under the status: how often the app goes to the network. Same mono
            // 11.5 right-aligned treatment as the status line above it, and static (the cadence
            // does not change with state), matching how the toggle description already states it
            // unconditionally. Wraps to two lines inside the shared 260 cap.
            marketRefreshStack.Children.Add(new TextBlock
            {
                Text = MarketNotice.CadenceNote,
                FontFamily = Hud.Font("MonoFont"), FontSize = 11.5, Foreground = Hud.Br("FgDimBrush"),
                Margin = new Thickness(0, 2, 0, 0), HorizontalAlignment = HorizontalAlignment.Right,
                TextWrapping = TextWrapping.Wrap, MaxWidth = 260, TextAlignment = TextAlignment.Right,
            });

            panel.Children.Add(SectionPanel(MarketNotice.SettingsTitle, false,
                SettingRow(MarketNotice.SettingsToggleTitle,
                    MarketNotice.SettingsToggleDesc,
                    _marketToggle, last: false),
                SettingRow("SC Trade Tools cross-check",
                    "Cross-checks UEX prices against SC Trade Tools, a second community-run price " +
                    "source, and marks matching rows CORROBORATED or SOURCES DISAGREE. Free public " +
                    "endpoints. Off by default.",
                    _sctToggle, last: false),
                SettingRow("Refresh now",
                    "Fetch the latest sell prices from UEX right now, regardless of the hourly " +
                    "schedule. Prices can go stale (and keep their visible age) when UEX stops " +
                    "listing a commodity, rather than disappearing.",
                    marketRefreshStack, last: false),
                SettingRow("Source",
                    "Where these prices come from.",
                    UexBadge(), last: true)));

            RefreshMarketRows();
            App.Market.Changed += () => Dispatcher.BeginInvoke(RefreshMarketRows);

            RefreshSctRow();
            App.Sct.Changed += () => Dispatcher.BeginInvoke(RefreshSctRow);
        }

        return Pane(panel);
    }

    // The pane is built once and cached for the app lifetime, but consent can change elsewhere
    // (the Admin DATA TOOLS card writes the same AppSettings.SctDataEnabled field): mirror the
    // setting so the toggle never lies - same shape as RefreshUpdateRows/RefreshMarketRows above.
    // Subscribed to App.Sct.Changed (worker thread on a real fetch, caller's own thread on
    // Start's disk load), so it always marshals through the dispatcher like those two.
    private void RefreshSctRow() => _sctToggle?.SetOnSilently(App.Settings.Current.SctDataEnabled);

    // Keeps the Market data rows honest as the fetch cycle moves through its states. Subscribed
    // to App.Market.Changed (worker thread): the subscription above uses Dispatcher.BeginInvoke,
    // not Invoke, because Market.Dispose only drains an in-flight cycle for up to 3s and a
    // blocking handler here would eat into that budget.
    private void RefreshMarketRows()
    {
        if (_marketToggle == null || _marketRefreshBtn == null || _marketStatusText == null) return;

        var on = App.Settings.Current.MarketDataEnabled == true;
        // Mirrors the setting rather than trusting its own prior state: consent can in principle
        // change elsewhere, same reasoning as the Updates toggle re-sync above.
        _marketToggle.SetOnSilently(on);
        _marketRefreshBtn.IsEnabled = on && !App.Market.FetchInProgress;
        _marketStatusText.Text = MarketNotice.StatusLine(
            App.Settings.Current.LastMarketFetchUtc?.ToLocalTime(), App.Market.LastError);
    }

    // Right-aligned dim note, the pattern the Downloading/Verifying mirrors already use.
    private static TextBlock RightNote(string text, bool wrap = false) => new()
    {
        Text = text, FontSize = 11.5, Foreground = Hud.Br("FgDimBrush"),
        HorizontalAlignment = HorizontalAlignment.Right,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        MaxWidth = 260, TextAlignment = TextAlignment.Right,
    };

    // Official "Powered by UEX" attribution badge (Task 13). Replaces the plain SourceNote
    // text row: the black rounded mark sits directly on the panel fill, no border, and is
    // never recolored or restyled (brand asset rule). SourceNote still carries the
    // accessible name and tooltip so screen readers get the same information sighted users
    // read off the badge.
    private static Image UexBadge()
    {
        // Capped decode, mirroring GuidesPage.Thumbnail's pattern: the source PNG is
        // 3840x1138 (~17.5 MB decoded RGBA) but this row only ever shows it at 26px tall.
        // DecodePixelHeight = 52 (2x display height) keeps it crisp at high DPI without
        // decoding the full-resolution bitmap.
        var bmp = new System.Windows.Media.Imaging.BitmapImage();
        bmp.BeginInit();
        bmp.UriSource = new Uri("pack://application:,,,/Assets/uex_badge.png", UriKind.Absolute);
        bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        bmp.DecodePixelHeight = 52;
        bmp.EndInit();
        bmp.Freeze();

        var badge = new Image
        {
            Source = bmp, Height = 26, Stretch = Stretch.Uniform,
            ToolTip = MarketNotice.SourceNote,
        };
        // Note: SettingRow (:939) unconditionally right-aligns any FrameworkElement control it
        // hosts, so no HorizontalAlignment is set here - it would be dead/contradictory.
        RenderOptions.SetBitmapScalingMode(badge, BitmapScalingMode.HighQuality);
        System.Windows.Automation.AutomationProperties.SetName(badge, MarketNotice.SourceNote);
        return badge;
    }

    // The power-user secondary action on every portable ReadyToInstall row (approved UX:
    // Settings is the surface for hand-managing the swap).
    private void AddOpenDownloadFolder()
    {
        var open = GhostButton("Open download folder");
        open.Margin = new Thickness(0, 6, 0, 0);
        open.Click += (_, _) => App.Update.OpenDownloadFolder();
        _updateActionHost!.Children.Add(open);
    }

    // Settings twin of CommandPage.RunPortableApply: disable the window (interaction guard
    // against version-skew lazy loads), release WebView2 handles, apply off-thread, shut
    // down on success. No overlay here; the row text mirrors the Installing state.
    private async Task RunPortableApplyFromSettings()
    {
        var owner = Window.GetWindow(this);
        if (owner != null) owner.IsEnabled = false;
        (owner as MainWindow)?.ShutdownWebViewsForUpdate();
        var ok = await App.Update.ApplyPortableAsync();
        if (ok) { Application.Current.Shutdown(); return; }
        if (owner != null) owner.IsEnabled = true;
    }

    // Keeps the Updates rows honest as the service moves through its states. Subscribed to
    // App.Update.Changed (worker thread), so it always marshals through the dispatcher.
    private void RefreshUpdateRows()
    {
        if (_updateStatusText == null || _updateActionHost == null) return;
        // The pane is built once and cached for the app lifetime, but consent can change
        // elsewhere (the Operations strip): mirror the setting so the toggle never lies.
        _updateCheckToggle?.SetOnSilently(App.Settings.Current.UpdateCheckEnabled == true);
        var svc = App.Update;
        _updateStatusText.Text = UpdateNotice.StatusLine(svc.State.ToString(), svc.Available?.Version,
            App.Settings.Current.LastUpdateCheckUtc, svc.LastFailureWasUserInitiated, svc.LastFailureKind.ToString());

        _updateActionHost.Children.Clear();
        switch (svc.State)
        {
            case UpdateState.UpdateAvailable:
                var download = GhostButton("Download update");
                download.Click += async (_, _) =>
                {
                    await App.Update.DownloadAsync();
                    if (App.Update.LastFailureWasVerification) ShowVerifyFailed();
                };
                _updateActionHost.Children.Add(download);
                break;
            case UpdateState.ReadyToInstall when AppInfo.Distribution == "Installer":
                var install = GhostButton("Install update");
                // Captured while the row is built: the service can move on (a re-check clears
                // Available) between the build and the click, and the prompt must name the
                // version this button was made for.
                var v = svc.Available!.Version;
                install.Click += (_, _) =>
                {
                    // Same decision, MessageBox form: this pane already confirms its restart
                    // prompt this way (post-clear restart precedent).
                    var res = MessageBox.Show(
                        UpdateNotice.InstallConfirmTitle(v) + "\n\n" + UpdateNotice.InstallConfirmBody,
                        "Install update", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
                    if (res != MessageBoxResult.Yes) return;
                    Logger.Info("[UI] install update: confirmed");
                    if (App.Update.LaunchInstaller())
                        Application.Current.Shutdown();
                    else if (App.Update.LastFailureWasVerification) ShowVerifyFailed();
                };
                _updateActionHost.Children.Add(install);
                break;
            // Stuck rollback (the CommandPage twin does the same): restoring is the next
            // start's job, so the row says so and offers no Try again that would fail.
            case UpdateState.ReadyToInstall when svc.LastPortableApplyFailure != null && svc.LastApplyLeftRestorePending:
                _updateActionHost.Children.Add(RightNote(UpdateNotice.RestorePendingBody, wrap: true));
                AddOpenDownloadFolder();
                break;
            case UpdateState.ReadyToInstall when svc.LastPortableApplyFailure != null:
                _updateActionHost.Children.Add(RightNote(UpdateNotice.PrepareFailedBody, wrap: true));
                var retryPortable = GhostButton("Try again");
                // Retry whichever path failed (the CommandPage twin does the same).
                retryPortable.Click += (_, _) =>
                {
                    Logger.Info("[UI] portable update: try again");
                    if (App.Update.PortableSwapAvailable) _ = RunPortableApplyFromSettings();
                    else _ = App.Update.UnpackForManualAsync();
                };
                _updateActionHost.Children.Add(retryPortable);
                AddOpenDownloadFolder();
                break;
            case UpdateState.ReadyToInstall when svc.PortableSwapAvailable:
                var installPortable = GhostButton("Install update");
                // Captured while the row is built (the LaunchInstaller row's precedent): the
                // prompt must name the version this button was made for.
                var vPortable = svc.Available!.Version;
                installPortable.Click += (_, _) =>
                {
                    var res = MessageBox.Show(
                        UpdateNotice.InstallConfirmTitle(vPortable) + "\n\n" + UpdateNotice.InstallConfirmBodyPortable,
                        "Install update", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
                    if (res != MessageBoxResult.Yes) return;
                    Logger.Info("[UI] install update: confirmed");
                    _ = RunPortableApplyFromSettings();
                };
                _updateActionHost.Children.Add(installPortable);
                AddOpenDownloadFolder();
                break;
            case UpdateState.ReadyToInstall:
                var manualPortable = GhostButton("Set up manual update");
                manualPortable.Click += (_, _) => { Logger.Info("[UI] portable update: manual handoff chosen"); _ = App.Update.UnpackForManualAsync(); };
                _updateActionHost.Children.Add(manualPortable);
                AddOpenDownloadFolder();
                break;
            // The spec's rule for this row is "mirrors the strip": in-flight states show the
            // same approved bodies the Operations strip shows, never "No update available."
            case UpdateState.Downloading when svc.Available is { } dl:
                _updateActionHost.Children.Add(new TextBlock
                {
                    Text = UpdateNotice.DownloadingBody(dl.Version, svc.DownloadedBytes, svc.TotalBytes),
                    FontSize = 11.5, Foreground = Hud.Br("FgDimBrush"),
                    HorizontalAlignment = HorizontalAlignment.Right,
                });
                break;
            case UpdateState.Verifying when svc.Available is { } vf:
                _updateActionHost.Children.Add(new TextBlock
                {
                    Text = UpdateNotice.VerifyingBody(vf.Version),
                    FontSize = 11.5, Foreground = Hud.Br("FgDimBrush"),
                    HorizontalAlignment = HorizontalAlignment.Right,
                });
                break;
            case UpdateState.Installing when svc.Available is { } ip && (svc.PortableApplyInProgress || svc.ManualUnpackInProgress):
                // wrap: PreparingBody exceeds the row's 260px note width and must not clip.
                _updateActionHost.Children.Add(RightNote(
                    svc.ManualUnpackInProgress ? UpdateNotice.UnpackingBody(ip.Version) : UpdateNotice.PreparingBody(ip.Version),
                    wrap: true));
                break;
            case UpdateState.ManualHandoff when svc.Available is { } mh:
                _updateActionHost.Children.Add(RightNote(UpdateNotice.ManualHandoffBody(mh.Version), wrap: true));
                break;
            default:
                _updateActionHost.Children.Add(new TextBlock
                {
                    Text = "No update available.", FontSize = 11.5, Foreground = Hud.Br("FgDimBrush"),
                    HorizontalAlignment = HorizontalAlignment.Right,
                });
                break;
        }
    }

    // The verify-failed warning, owned by the app window so it cannot end up behind Nexus
    // (the CommandPage twin does the same). Owner is null only if this pane is somehow
    // unparented, which falls back to the ownerless overload rather than throwing.
    private void ShowVerifyFailed()
    {
        var owner = Window.GetWindow(this);
        if (owner != null)
            MessageBox.Show(owner, UpdateNotice.VerifyFailedBody, UpdateNotice.VerifyFailedTitle,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        else
            MessageBox.Show(UpdateNotice.VerifyFailedBody, UpdateNotice.VerifyFailedTitle,
                MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    // INTERFACE: overlay click-through + appearance (reduce animations, 24-hour clock).
    private ScrollViewer BuildInterfacePane()
    {
        var panel = new StackPanel { Margin = new Thickness(2, 2, 14, 40) };

        _passThroughToggle = new Hud.ToggleSwitch(App.Settings.Current.OverlayPassThroughWhenCursorHidden)
        {
            OnToggled = on => App.SetOverlayPassThrough(on, "settings"),
        };
        // The overlay's own quick-settings flyout can flip click-through without touching this
        // page, the same precedent as the ghost-mode subscription below.
        App.OverlayPassThroughChanged +=
            on => Dispatcher.BeginInvoke(() => _passThroughToggle!.SetOnSilently(on));
        _ghostToggle = new Hud.ToggleSwitch(App.Settings.Current.OverlayGhostMode)
        {
            OnToggled = on => App.SetOverlayGhostMode(on, "settings"),
        };
        // The ghost rail's own flyout switch can flip ghost mode without touching this page, so a
        // one-time seed goes stale (review 2026-07-28). Re-sync silently whenever the shared state
        // changes, the same precedent as the Market toggle above; SettingsPage is an app-lifetime
        // singleton, so this subscription is never detached.
        App.OverlayGhostModeChanged += (on, _) =>
            Dispatcher.BeginInvoke(() => _ghostToggle!.SetOnSilently(on));
        panel.Children.Add(SectionPanel("Overlay", false,
            SettingRow("Click-through in FPS and flight",
                "While the game hides the cursor (on foot in FPS, or piloting), the overlay stays visible " +
                "but lets the mouse pass straight through, so a stray click can't land on it or pull focus " +
                "from the game. It becomes clickable again the moment the game shows the cursor.",
                _passThroughToggle, last: false),
            SettingRow("Ghost mode",
                "Collapses the overlay to a slim icon rail with a minimal in-game footprint. " +
                "Click a rail glyph to slide that tab out beside it; click it again to collapse. " +
                "The rail's gear opens quick settings.",
                _ghostToggle, last: false),
            ScaleRow("Overlay scale",
                "Make the in-game overlay and its work order flyout larger. The overlay grows " +
                "from its top-left corner; drag it back into place if it no longer sits where " +
                "you want it.",
                App.Settings.Current.OverlayUiScale,
                onTick: v => UiScaleService.SetOverlayScale(v),
                onCommit: v => Logger.Info($"[UI] Overlay scale: {Math.Round(UiScaleService.ClampScale(v) * 100)}%"),
                last: false),
            ScaleRow("Ghost rail scale",
                "Size ghost mode's icon rail and its quick-settings flyout independently of the " +
                "overlay panels. Below 100% the rail shrinks past its default for a minimal " +
                "in-game footprint.",
                App.Settings.Current.OverlayGhostRailScale,
                onTick: v => UiScaleService.SetGhostRailScale(v),
                onCommit: v => Logger.Info($"[UI] Ghost rail scale: {Math.Round(UiScaleService.ClampRailScale(v) * 100)}%"),
                last: true, min: UiScaleService.RailMin, max: UiScaleService.Max)));

        var reduceToggle = new Hud.ToggleSwitch(App.Settings.Current.ReduceAnimations)
        {
            OnToggled = on =>
            {
                App.Settings.Current.ReduceAnimations = on;
                App.Settings.Save();
                Motion.Reduced = on;
                Logger.Info($"[UI] Reduce animations: {(on ? "on" : "off")}");
            },
        };
        var clockToggle = new Hud.ToggleSwitch(App.Settings.Current.Clock24Hour)
        {
            OnToggled = on =>
            {
                App.Settings.Current.Clock24Hour = on;
                App.Settings.Save();
                Logger.Info($"[UI] Clock format: {(on ? "24-hour" : "12-hour")}");
            },
        };
        panel.Children.Add(SectionPanel("Appearance", false,
            SettingRow("Reduce animations",
                "Minimize motion across Nexus: skip page transitions, the dock and HUD pulses, " +
                "count-ups and the ambient panel glyphs. Takes full effect as you move between pages.",
                reduceToggle, last: false),
            SettingRow("24-hour clock",
                "Show the top-bar clock in 24-hour time. Off uses 12-hour with AM/PM.",
                clockToggle, last: false),
            ScaleRow("App scale",
                "Make everything in the main window larger. 100% is the standard size; higher " +
                "values enlarge all text and controls together. Dialogs and tool windows pick " +
                "up the new scale the next time they open.",
                App.Settings.Current.AppUiScale,
                onTick: null,
                onCommit: v =>
                {
                    UiScaleService.SetAppScale(v);
                    Logger.Info($"[UI] App scale: {Math.Round(UiScaleService.ClampScale(v) * 100)}%");
                },
                last: true)));

        return Pane(panel);
    }

    // DATA: the destructive clear-saved-data action (red-framed section).
    private ScrollViewer BuildDataPane()
    {
        var panel = new StackPanel { Margin = new Thickness(2, 2, 14, 40) };

        var clearBtn = DangerButton("Clear saved data…");
        clearBtn.MouseLeftButtonUp += (s, e) => ClearSavedData();
        panel.Children.Add(SectionPanel("Data", true,
            SettingRow("Clear saved data",
                "Clear everything you've saved in Nexus: owned blueprints, Blueprint Network members and " +
                "groups, your detected RSI handle, shopping cart, work orders and pinned resources. The " +
                "mining reference data is not affected.",
                clearBtn, last: true)));

        return Pane(panel);
    }

    // Wraps a category's section stack in a vertically scrolling pane (the app's themed thin scrollbar
    // applies automatically); the inner stack carries the bottom breathing room.
    private static ScrollViewer Pane(StackPanel content) => new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Content = content,
    };

    // ── HUD section scaffolding ─────────────────────────────────────────────────
    // A chamfered Hud.Panel for one settings section: a small uppercase header bar
    // (glow dash + display title) over an underline, then info-left / control-right rows.
    private static UIElement SectionPanel(string header, bool danger, params FrameworkElement[] rows)
    {
        var content = new StackPanel();

        var headBar = new StackPanel { Orientation = Orientation.Horizontal };
        headBar.Children.Add(new Border
        {
            Width = 14, Height = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 9, 0),
            Background = danger ? new SolidColorBrush(DangerColor) : Hud.Br("AccentBrush"),
        });
        headBar.Children.Add(new TextBlock
        {
            Text = header.ToUpperInvariant(), FontFamily = Hud.Font("DisplayFont"),
            FontSize = 13, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center,
            Foreground = danger ? new SolidColorBrush(DangerColor) : Hud.Br("AccentBrush"),
        });
        content.Children.Add(headBar);
        content.Children.Add(new Border
        {
            Height = 1, Opacity = 0.5, Margin = new Thickness(0, 9, 0, 2),
            Background = Hud.Br("NavBorderBrush"),
        });

        foreach (var r in rows) content.Children.Add(r);

        Brush? border = danger ? new SolidColorBrush(Color.FromArgb(0x66, 0xE5, 0x53, 0x53)) : null;
        var p = Hud.Panel(content, chamfer: 12, border: border, padding: new Thickness(18, 14, 18, 14));
        p.Margin = new Thickness(0, 0, 0, 16);
        return p;
    }

    // One settings row: title + dim description on the left, a control on the right,
    // with a hairline rule under every row except the last in its panel.
    private static FrameworkElement SettingRow(string title, string desc, UIElement control, bool last = false)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        info.Children.Add(new TextBlock
        {
            Text = title, FontFamily = Hud.Font("TechFont"), FontWeight = FontWeights.SemiBold,
            FontSize = 14, Foreground = Hud.Br("FgBrush"), TextWrapping = TextWrapping.Wrap,
        });
        if (!string.IsNullOrEmpty(desc))
            info.Children.Add(new TextBlock
            {
                Text = desc, FontSize = 11.5, TextWrapping = TextWrapping.Wrap,
                Foreground = Hud.Br("FgDimBrush"), Margin = new Thickness(0, 3, 0, 0), MaxWidth = 430,
                // Issue #25: a binding MaxWidth makes default Stretch render as CENTER, so each
                // description centered at its own text-length-dependent offset. Pin them left,
                // flush under the title, like every other row element.
                HorizontalAlignment = HorizontalAlignment.Left,
            });
        Grid.SetColumn(info, 0); grid.Children.Add(info);

        if (control is FrameworkElement fe) { fe.VerticalAlignment = VerticalAlignment.Center; fe.HorizontalAlignment = HorizontalAlignment.Right; }
        Grid.SetColumn(control, 1); grid.Children.Add(control);

        var wrap = new Border { Padding = new Thickness(0, 13, 0, 13), Child = grid };
        if (!last) { wrap.BorderBrush = Hud.Br("NavBorderBrush"); wrap.BorderThickness = new Thickness(0, 0, 0, 1); }
        return wrap;
    }

    // Right-side value for the "Last automatic restart" row: a calm mono local-time stamp with a
    // small amber marker naming the underlying display error, or the empty-state label when nothing
    // has been recorded. Mono + FgBrush (not cyan) per the frozen values. Reads the stored UTC.
    private static UIElement RestartValue(DateTime? lastUtc)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };

        if (lastUtc is null)
        {
            stack.Children.Add(new TextBlock
            {
                Text = RelaunchNotice.NoneRecorded, FontFamily = Hud.Font("MonoFont"), FontSize = 13,
                Foreground = Hud.Br("FgDimBrush"), HorizontalAlignment = HorizontalAlignment.Right,
            });
            return stack;
        }

        stack.Children.Add(new TextBlock
        {
            Text = RelaunchNotice.FormatTimestamp(lastUtc), FontFamily = Hud.Font("MonoFont"), FontSize = 13,
            Foreground = Hud.Br("FgBrush"), HorizontalAlignment = HorizontalAlignment.Right,
        });

        var marker = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 4, 0, 0),
        };
        marker.Children.Add(new Ellipse
        {
            Width = 6, Height = 6, Fill = Hud.Br("AccentBrush"), Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Effect = new DropShadowEffect { Color = Hud.Col("AccentBrush"), BlurRadius = 6, ShadowDepth = 0, Opacity = 0.6 },
        });
        marker.Children.Add(new TextBlock
        {
            Text = RelaunchNotice.Marker, FontFamily = Hud.Font("MonoFont"), FontSize = 10.5,
            Foreground = Hud.Br("FgDimBrush"), VerticalAlignment = VerticalAlignment.Center,
        });
        stack.Children.Add(marker);
        return stack;
    }

    // Outlined ghost action button (matches the app's NexusButton chrome).
    private static Button GhostButton(string text) => new()
    {
        Content = text,
        Style = (Style)Application.Current.FindResource("NexusButton"),
        Padding = new Thickness(16, 8, 16, 8),
        HorizontalAlignment = HorizontalAlignment.Right,
    };

    // Destructive (red) action button: outlined danger border with a tinted hover.
    private static Border DangerButton(string text)
    {
        var danger = new SolidColorBrush(Color.FromRgb(0xE5, 0x53, 0x53));
        var btn = new Border
        {
            Background = Brushes.Transparent,
            BorderBrush = danger, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4), Padding = new Thickness(16, 8, 16, 8),
            HorizontalAlignment = HorizontalAlignment.Right, Cursor = Cursors.Hand,
            Child = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = danger },
        };
        btn.MouseEnter += (s, e) => btn.Background = new SolidColorBrush(Color.FromArgb(0x18, 0xE5, 0x53, 0x53));
        btn.MouseLeave += (s, e) => btn.Background = Brushes.Transparent;
        return btn;
    }

    // A labelled file-path row: title + requirement note on the left, [text box][Browse…] on the right.
    // The path is committed (applied + saved) when the box loses focus or a file is picked.
    private static FrameworkElement BuildPathRow(
        string label, string initial, string filter, string defaultName, string requirement, Action<string> apply, bool last = false)
    {
        var box = new TextBox
        {
            Text = initial ?? "",
            MinWidth = 240,
            Padding = new Thickness(8, 5, 8, 5), VerticalContentAlignment = VerticalAlignment.Center,
            FontFamily = Hud.Font("MonoFont"), FontSize = 12,
            Background = (Brush)Application.Current.FindResource("Bg2NavBrush"),
            Foreground = (Brush)Application.Current.FindResource("FgBrush"),
            BorderBrush = (Brush)Application.Current.FindResource("NavBorderBrush"),
            BorderThickness = new Thickness(1),
        };
        box.LostFocus += (_, _) => apply(box.Text.Trim());

        var browse = new Button
        {
            Content = "Browse…", Style = (Style)Application.Current.FindResource("NexusButton"),
            Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(8, 0, 0, 0),
        };
        browse.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = filter, FileName = defaultName };
            if (dlg.ShowDialog() == true) { box.Text = dlg.FileName; apply(box.Text.Trim()); }
        };

        var ctl = new Grid { Width = 360 };
        ctl.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ctl.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(box, 0); ctl.Children.Add(box);
        Grid.SetColumn(browse, 1); ctl.Children.Add(browse);

        return SettingRow(label, requirement, ctl, last);
    }

    // A labelled scale slider row: HudSlider snapping in 5% steps plus a mono percentage
    // readout. Restore-then-wire discipline (v6.4.1 lesson): the initial value is set BEFORE
    // ValueChanged is attached, so construction-time coercion can never clobber the saved
    // setting. The percentage label updates on every snapped tick regardless. onTick, when
    // supplied, applies the scale live on each tick; onCommit runs once when the mouse
    // releases, so nexus.log records the final value instead of every intermediate step.
    //
    // Overlay scale passes an onTick and previews live, because it rescales a SEPARATE window
    // that cannot disturb this slider. App scale deliberately passes NO onTick and does its
    // apply inside onCommit instead: the App scale transform rescales the main window's
    // RootLayout, which hosts this very Settings page and the Thumb that is holding mouse
    // capture during the drag. Applying it mid-tick would rescale the control under the cursor
    // and break the gesture (the value jumps, and the window can jolt as its MinWidth grows).
    // Committing on mouse release keeps the drag stable and still applies the final value.
    private static FrameworkElement ScaleRow(
        string title, string desc, double initial, Action<double>? onTick, Action<double> onCommit,
        bool last = false, double min = UiScaleService.Min, double max = UiScaleService.Max)
    {
        var label = new TextBlock
        {
            // Clamp the readout the same way the slider thumb (below) and the applied window
            // scale do, so an out-of-range persisted value shows a label that agrees with the
            // actual scale from construction instead of only after the first drag.
            Text = $"{Math.Round(Math.Clamp(double.IsNaN(initial) || initial <= 0 ? 1.0 : initial, min, max) * 100)}%",
            FontFamily = Hud.Font("MonoFont"), FontSize = 13,
            Foreground = Hud.Br("FgBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0), MinWidth = 42, TextAlignment = TextAlignment.Right,
        };
        var slider = new Slider
        {
            Minimum = min, Maximum = max,
            IsSnapToTickEnabled = true, TickFrequency = UiScaleService.Step,
            SmallChange = UiScaleService.Step, LargeChange = UiScaleService.Step,
            Width = 160, VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)Application.Current.FindResource("HudSlider"),
        };
        slider.Value = Math.Clamp(double.IsNaN(initial) || initial <= 0 ? 1.0 : initial, min, max);
        slider.ValueChanged += (_, e) =>
        {
            label.Text = $"{Math.Round(e.NewValue * 100)}%";
            onTick?.Invoke(e.NewValue);
        };
        slider.LostMouseCapture += (_, _) => onCommit(slider.Value);

        var control = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        control.Children.Add(slider);
        control.Children.Add(label);
        return SettingRow(title, desc, control, last);
    }

    // Persist the Game.log path and re-point the shared Game.log tail so it takes effect immediately -
    // one retarget now serves the blueprint session, the haul tracker and the shard tracker. Blank means
    // "auto-detect". The actual path is not logged (it can contain a Windows username). Instance method
    // so it can refresh the GAME tab's needs-attention pip after the effective path changes.
    private void ApplyGameLogPath(string path)
    {
        if (App.Settings.Current.GameLogPath == path) return;
        App.Settings.Current.GameLogPath = path;
        App.Settings.Save();

        App.GameLogFeed.PreferredPath = path;
        var effective = string.IsNullOrWhiteSpace(path) ? GameLogWatcher.FindGameLog() : path;
        // A running blueprint session restarts THROUGH the feed so it also replays the new log
        // (Start preserves AutoMark); a stopped one stays detached while the tail re-points.
        if (App.GameLog.IsRunning) App.GameLog.Start(effective, fromBeginning: true);
        else App.GameLogFeed.Start(effective);

        Logger.Info("[UI] Game.log path updated in Settings");
        RefreshGameDot();
    }

    // Persist the optional global.ini path. The localization map is rebuilt fresh on the next import, so no
    // restart is needed. The actual path is not logged (it can contain a Windows username).
    private static void ApplyGlobalIniPath(string path)
    {
        if (App.Settings.Current.GlobalIniPath == path) return;
        App.Settings.Current.GlobalIniPath = path;
        App.Settings.Save();
        App.GameLog.InvalidateLocalizationMap();   // the live tail must pick up the new path now
        Logger.Info("[UI] global.ini path updated in Settings");
    }

    // Issue #28: the user's explicit opt-in that a non-standard (Custom-channel) install is really
    // their LIVE game, so its Game.log should record blueprint receipts. Off by default. Instance
    // method so it can refresh the GAME tab's needs-attention pip: authorizing settles the
    // custom-folder notice, revoking raises it again.
    private void ApplyCustomAllow(bool on)
    {
        if (App.Settings.Current.CustomChannelRecordsBlueprints == on) return;
        App.Settings.Current.CustomChannelRecordsBlueprints = on;
        App.Settings.Save();
        Logger.Info($"[UI] custom-folder blueprint recording {(on ? "authorized" : "revoked")}");
        RefreshGameDot();
    }

    // ── Saved data ────────────────────────────────────────────────────────────
    // The themed replacement for the first Clear-saved-data MessageBox: a full-page scrim over a
    // centered chamfered danger panel that lists exactly what will be deleted. The scrim and Cancel
    // dismiss without deleting anything; Clear runs the same destructive sequence the old flow gated.
    private Border BuildClearDataModal()
    {
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        head.Children.Add(new Border
        {
            Width = 14, Height = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 9, 0),
            Background = _dangerFull,
            Effect = new DropShadowEffect { Color = DangerColor, BlurRadius = 8, ShadowDepth = 0, Opacity = 0.5 },
        });
        head.Children.Add(new TextBlock
        {
            Text = "Clear all saved data?", FontFamily = Hud.Font("DisplayFont"),
            FontSize = 16, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center,
            Foreground = _dangerFull,
        });

        var body = new StackPanel();
        body.Children.Add(ModalParagraph("This permanently deletes all of your saved data:", Hud.Br("FgBrush")));

        // The deletion list, verbatim from the old MessageBox copy, rendered dim like the mock's list.
        var list = new StackPanel { Margin = new Thickness(4, 0, 0, 8) };
        foreach (var item in new[]
        {
            "Owned blueprints",
            "Blueprint Network members and groups",
            "Your detected RSI handle",
            "Shopping cart",
            "Work orders",
            "Pinned resources",
        })
            list.Children.Add(new TextBlock
            {
                Text = "•   " + item, FontSize = 12.5, TextWrapping = TextWrapping.Wrap,
                Foreground = Hud.Br("FgDimBrush"), Margin = new Thickness(0, 2, 0, 2),
            });
        body.Children.Add(list);

        body.Children.Add(ModalParagraph("The mining reference data is kept.", Hud.Br("FgBrush")));
        var warn = ModalParagraph("This cannot be undone. Are you sure?", Hud.Br("GoldBrush"));
        warn.FontWeight = FontWeights.SemiBold;                 // mock: modal-warn is amber-bright, 600
        warn.Margin = new Thickness(0, 0, 0, 0);                // last line; the actions row owns the gap below
        body.Children.Add(warn);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0),
        };
        var cancelBtn = GhostButton("Cancel");
        cancelBtn.Click += (s, e) => CancelClearData();
        var confirmBtn = DangerButton("Clear saved data");
        confirmBtn.Margin = new Thickness(10, 0, 0, 0);
        confirmBtn.MouseLeftButtonUp += (s, e) => ConfirmClearData();
        actions.Children.Add(cancelBtn);
        actions.Children.Add(confirmBtn);

        var content = new StackPanel();
        content.Children.Add(head);
        content.Children.Add(body);
        content.Children.Add(actions);

        _modalPanel = Hud.Panel(content, chamfer: 14,
            bg: Hud.Br("Bg2NavBrush"),
            border: new SolidColorBrush(Color.FromArgb(0x66, 0xE5, 0x53, 0x53)),
            padding: new Thickness(22, 20, 22, 22));
        _modalPanel.MaxWidth = 470;
        _modalPanel.HorizontalAlignment = HorizontalAlignment.Center;
        _modalPanel.VerticalAlignment = VerticalAlignment.Center;
        _modalPanel.RenderTransform = new TransformGroup { Children = { _modalPanelT, _modalPanelScale } };
        _modalPanel.RenderTransformOrigin = new Point(0.5, 0.5);
        // A click on the panel itself must not reach the scrim's dismiss handler behind it.
        _modalPanel.MouseLeftButtonUp += (s, e) => e.Handled = true;

        _modalScrim = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x9E, 0x03, 0x05, 0x08)),   // rgba(3,5,8,0.62)
            Padding = new Thickness(24),
            // Bleed over the page's own outer padding so the scrim darkens edge to edge, matching the mock.
            Margin = new Thickness(-28, -22, -28, 0),
            Visibility = Visibility.Collapsed,
            Child = _modalPanel,
        };
        _modalScrim.MouseLeftButtonUp += (s, e) => CancelClearData();
        return _modalScrim;
    }

    // One modal body paragraph: 12.5px text in the given color, with the mock's inter-paragraph gap.
    private static TextBlock ModalParagraph(string text, Brush color) => new()
    {
        Text = text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap,
        Foreground = color, Margin = new Thickness(0, 0, 0, 8),
    };

    // Opens the confirmation over the page. Nothing is deleted until the user clicks Clear. Entrance
    // matches the mock (scrim fades, panel fades up while rising 12px and settling from 0.98); Reduce
    // animations shows it at once.
    private void ClearSavedData()
    {
        _modalScrim.Visibility = Visibility.Visible;
        Logger.Info("[UI] Clear saved data: confirm shown");

        if (Motion.Reduced)
        {
            _modalScrim.BeginAnimation(UIElement.OpacityProperty, null); _modalScrim.Opacity = 1;
            _modalPanel.BeginAnimation(UIElement.OpacityProperty, null); _modalPanel.Opacity = 1;
            _modalPanelT.BeginAnimation(TranslateTransform.YProperty, null); _modalPanelT.Y = 0;
            _modalPanelScale.BeginAnimation(ScaleTransform.ScaleXProperty, null); _modalPanelScale.ScaleX = 1;
            _modalPanelScale.BeginAnimation(ScaleTransform.ScaleYProperty, null); _modalPanelScale.ScaleY = 1;
            return;
        }

        // Explicit From values so a reopen animates cleanly even if a prior entrance is still holding.
        _modalScrim.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));   // scrim fade, mock 0.16s
        var dur = TimeSpan.FromMilliseconds(Motion.DrillMs);
        _modalPanel.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = Motion.Reveal });
        _modalPanelT.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0, dur) { EasingFunction = Motion.Reveal });
        _modalPanelScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.98, 1, dur) { EasingFunction = Motion.Reveal });
        _modalPanelScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.98, 1, dur) { EasingFunction = Motion.Reveal });
    }

    // Dismisses the modal with no exit animation (the mock closes instantly).
    private void HideClearDataModal() => _modalScrim.Visibility = Visibility.Collapsed;

    // Cancel path: the scrim click or the Cancel button. Nothing is deleted.
    private void CancelClearData()
    {
        HideClearDataModal();
        Logger.Info("[UI] Clear saved data: cancelled");
    }

    // Confirm path: run the same destructive clear the old first MessageBox gated, then the existing
    // restart prompt, verbatim.
    private void ConfirmClearData()
    {
        HideClearDataModal();
        Logger.Info("[UI] Clear saved data: confirmed");

        App.Data.ClearShoppingList();
        App.Data.ClearWorkOrders();
        App.Data.ClearAllPins();
        App.Settings.ClearOwnedBlueprints();
        App.Settings.ClearPinnedResources();
        App.Network.ClearAll();
        App.Settings.ClearLocalNetworkIdentity();

        var restart = MessageBox.Show(
            "All saved data has been cleared.\n\nNexus needs to restart to refresh. Restart now?",
            "Data cleared",
            MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.Yes);
        if (restart == MessageBoxResult.Yes)
            ThemeService.RestartApp();
    }
}
