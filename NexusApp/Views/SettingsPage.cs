using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using NexusApp.Services;

namespace NexusApp.Views;

// Settings as an in-app page (hosted in the main content area, reached from the Settings module
// at the bottom of the app dock),
// not a pop-out dialog. Single theme (MOBIGLAS), so there is no appearance/theme picker: just
// the Game.log paths, Blueprint Network identity, diagnostics, and the destructive clear-data action.
public sealed class SettingsPage : UserControl
{
    private readonly Action _openLogMonitor;
    private readonly Action _openAppLogMonitor;

    public SettingsPage(Action openLogMonitor, Action openAppLogMonitor)
    {
        _openLogMonitor = openLogMonitor;
        _openAppLogMonitor = openAppLogMonitor;

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var panel = new StackPanel { Margin = new Thickness(28, 22, 28, 40) };

        panel.Children.Add(Hud.Header("Configuration", "Settings",
            "Paths and data. Everything stays on this machine."));

        // ── Game.log paths ─────────────────────────────────────────────────────
        var openLogBtn = GhostButton("Open Game.log Monitor");
        openLogBtn.Click += (s, e) => _openLogMonitor?.Invoke();
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
            SettingRow("Game.log Monitor",
                "Track your session from Star Citizen's Game.log: auto-collect blueprints you receive " +
                "(they're marked Owned in your library), or import the ones you already own from past logs.",
                openLogBtn, last: true)));

        // ── Blueprint Network ───────────────────────────────────────────────────
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

        // ── Diagnostics ─────────────────────────────────────────────────────────
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

        // ── Overlay ─────────────────────────────────────────────────────────────
        var overlayPassToggle = new Hud.ToggleSwitch(App.Settings.Current.OverlayPassThroughWhenCursorHidden)
        {
            OnToggled = on =>
            {
                App.Settings.Current.OverlayPassThroughWhenCursorHidden = on;
                App.Settings.Save();
                Logger.Info($"[UI] Overlay click-through when cursor hidden: {(on ? "on" : "off")}");
            },
        };
        panel.Children.Add(SectionPanel("Overlay", false,
            SettingRow("Click-through in FPS and flight",
                "While the game hides the cursor (on foot in FPS, or piloting), the overlay stays visible " +
                "but lets the mouse pass straight through, so a stray click can't land on it or pull focus " +
                "from the game. It becomes clickable again the moment the game shows the cursor.",
                overlayPassToggle, last: true)));

        // ── Appearance ──────────────────────────────────────────────────────────
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
                clockToggle, last: true)));

        // ── Data ──────────────────────────────────────────────────────────────
        var clearBtn = DangerButton("Clear saved data…");
        clearBtn.MouseLeftButtonUp += (s, e) => ClearSavedData();
        panel.Children.Add(SectionPanel("Data", true,
            SettingRow("Clear saved data",
                "Clear everything you've saved in Nexus: owned blueprints, Blueprint Network members and " +
                "groups, your detected RSI handle, shopping cart, work orders and pinned resources. The " +
                "mining reference data is not affected.",
                clearBtn, last: true)));

        scroll.Content = panel;
        Content = scroll;
    }

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
            Background = danger ? Hud.Br("DangerBrush") : Hud.Br("AccentBrush"),
        });
        headBar.Children.Add(new TextBlock
        {
            Text = header.ToUpperInvariant(), FontFamily = Hud.Font("DisplayFont"),
            FontSize = 13, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center,
            Foreground = danger ? Hud.Br("DangerBrush") : Hud.Br("AccentBrush"),
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

    // Persist the Game.log path and re-point every Game.log-driven watcher so it takes effect immediately.
    // Blank means "auto-detect". The actual path is not logged (it can contain a Windows username).
    private static void ApplyGameLogPath(string path)
    {
        if (App.Settings.Current.GameLogPath == path) return;
        App.Settings.Current.GameLogPath = path;
        App.Settings.Save();

        App.GameLog.PreferredPath = path;
        App.Hauls.PreferredPath = path;
        App.Shards.PreferredPath = path;

        var effective = string.IsNullOrWhiteSpace(path) ? GameLogWatcher.FindGameLog() : path;
        App.Hauls.Start(effective, fromBeginning: true);
        App.Shards.Start(effective, fromBeginning: true);
        if (App.GameLog.IsRunning) App.GameLog.Start(effective, fromBeginning: true);   // Start preserves AutoMark

        Logger.Info("[UI] Game.log path updated in Settings");
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

    // ── Saved data ────────────────────────────────────────────────────────────
    private void ClearSavedData()
    {
        var confirm = MessageBox.Show(
            "This permanently deletes all of your saved data:\n\n" +
            "    -  Owned blueprints\n" +
            "    -  Blueprint Network members and groups\n" +
            "    -  Your detected RSI handle\n" +
            "    -  Shopping cart\n" +
            "    -  Work orders\n" +
            "    -  Pinned resources\n\n" +
            "The mining reference data is kept.\n\n" +
            "This cannot be undone. Are you sure?",
            "Clear all saved data?",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;

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
