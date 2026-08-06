using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using NexusApp.Services;

namespace NexusApp.Views;

// Owner-only Admin page (visible only for the owner's RSI handle, via OwnerGate.IsOwnerReal in
// MainWindow). Three HUD sub-tabs: ROSTER (who has access), DIAGNOSTICS (live app state), and
// TOOLS (gate preview, demo profile, folders). Follows the SettingsPage tab-strip pattern; all
// motion collapses under Motion.Reduced. A visibility gate, not a security boundary.
public sealed class AdminPage : UserControl
{
    private readonly Action _openLogMonitor;
    private readonly Action _openAppLogMonitor;

    private readonly Border[] _tabButtons = new Border[3];
    private readonly TextBlock[] _tabLabels = new TextBlock[3];
    private readonly ScrollViewer[] _panes = new ScrollViewer[3];
    private readonly TranslateTransform _underlineT = new();
    private Grid _stripHost = null!;
    private Border _underline = null!;
    private StackPanel _diagHost = null!;
    private Border _previewBanner = null!;
    private TextBlock _previewBannerText = null!;
    private TextBlock _previewState = null!;
    private TextBlock _demoState = null!;
    private TextBlock _sctState = null!;
    private int _activeIndex = -1;

    public AdminPage(Action openLogMonitor, Action openAppLogMonitor)
    {
        _openLogMonitor = openLogMonitor;
        _openAppLogMonitor = openAppLogMonitor;
        InteractionLog.Nav("Admin");

        var root = new Grid { Margin = new Thickness(28, 22, 28, 0) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = Hud.Header("Owner Console", "Admin",
            "Access roster, live diagnostics, and owner tools. Only your handle ever sees this.");
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        BuildStrip();
        Grid.SetRow(_stripHost, 1);
        root.Children.Add(_stripHost);

        var paneHost = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        _panes[0] = BuildRosterPane();
        _panes[1] = BuildDiagnosticsPane();
        _panes[2] = BuildToolsPane();
        foreach (var pane in _panes) { pane.Visibility = Visibility.Collapsed; paneHost.Children.Add(pane); }
        Grid.SetRow(paneHost, 2);
        root.Children.Add(paneHost);

        Content = root;

        int restore = Array.IndexOf(AdminTabs.Ids,
            AdminTabs.NormalizeForRestore(App.Settings.Current.AdminActiveTab));
        SwitchTab(restore, persist: false);

        _stripHost.Loaded += (_, _) => MoveUnderline(_activeIndex, animate: false);
        _stripHost.SizeChanged += (_, _) => MoveUnderline(_activeIndex, animate: false);
    }

    // Called by MainWindow on every page open, so diagnostics and tool state are always current.
    public void Refresh()
    {
        RebuildDiagnostics();
        RefreshToolsState();
    }

    // ── Tab strip (SettingsPage pattern, no danger tab, no attention pip) ──
    private void BuildStrip()
    {
        _stripHost = new Grid { Height = 42 };
        _stripHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _stripHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var hairline = new Border
        {
            Height = 1, Background = Hud.Br("NavBorderBrush"), VerticalAlignment = VerticalAlignment.Bottom,
        };
        Grid.SetColumnSpan(hairline, 2);
        _stripHost.Children.Add(hairline);

        var cluster = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
        cluster.Children.Add(MakeTab(0, "Roster"));
        cluster.Children.Add(MakeTab(1, "Diagnostics"));
        cluster.Children.Add(MakeTab(2, "Tools"));
        Grid.SetColumn(cluster, 0);
        _stripHost.Children.Add(cluster);

        _underline = new Border
        {
            Height = 2, Width = 0, CornerRadius = new CornerRadius(1),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
            Background = Hud.Br("AccentBrush"), RenderTransform = _underlineT,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Hud.Col("AccentColor"), BlurRadius = 8, ShadowDepth = 0, Opacity = 0.5,
            },
        };
        Grid.SetColumnSpan(_underline, 2);
        _stripHost.Children.Add(_underline);
    }

    private Border MakeTab(int index, string label)
    {
        var text = new TextBlock
        {
            Text = label.ToUpperInvariant(), FontFamily = Hud.Font("UiFont"),
            FontSize = 12, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center,
            Foreground = Hud.Br("FgDimBrush"),
        };
        _tabLabels[index] = text;

        var btn = new Border
        {
            Background = Brushes.Transparent, CornerRadius = new CornerRadius(3, 3, 0, 0),
            Padding = new Thickness(15, 9, 15, 9), Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Bottom, Child = text,
        };
        _tabButtons[index] = btn;

        btn.MouseEnter += (_, _) => { text.Foreground = Hud.Br("FgBrush"); btn.Background = Hud.Br("AccentFaintBrush"); };
        btn.MouseLeave += (_, _) =>
        {
            text.Foreground = _activeIndex == index ? Hud.Br("GoldBrush") : Hud.Br("FgDimBrush");
            btn.Background = Brushes.Transparent;
        };
        btn.MouseLeftButtonUp += (_, _) => SwitchTab(index);
        return btn;
    }

    private void SwitchTab(int index, bool persist = true)
    {
        if (index == _activeIndex) return;
        _activeIndex = index;
        for (int i = 0; i < _panes.Length; i++)
        {
            _panes[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
            _tabLabels[i].Foreground = i == index ? Hud.Br("GoldBrush") : Hud.Br("FgDimBrush");
        }
        MoveUnderline(index, animate: true);
        if (persist)
        {
            App.Settings.Current.AdminActiveTab = AdminTabs.Ids[index];
            App.Settings.Save();
            Logger.Info($"[UI] Admin tab: {AdminTabs.Ids[index].ToUpperInvariant()}");
        }
    }

    private void MoveUnderline(int index, bool animate)
    {
        if (index < 0 || _tabButtons[index].ActualWidth == 0) return;
        var tab = _tabButtons[index];
        var x = tab.TransformToAncestor(_stripHost).Transform(new Point(0, 0)).X;
        _underline.Width = tab.ActualWidth;
        if (Motion.Reduced || !animate)
        {
            _underlineT.BeginAnimation(TranslateTransform.XProperty, null);
            _underlineT.X = x;
            return;
        }
        _underlineT.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(x, TimeSpan.FromMilliseconds(220)) { EasingFunction = Motion.Reveal });
    }

    // ── Shared pane scaffolding ──
    private static ScrollViewer Pane(UIElement content) => new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Content = content,
    };

    private static StackPanel Section(string label, UIElement content)
    {
        var s = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        s.Children.Add(new TextBlock
        {
            Text = label, Style = (Style)Application.Current.FindResource("SectionLabel"),
            Margin = new Thickness(2, 0, 0, 8),
        });
        s.Children.Add(Hud.Panel(content, chamfer: 10, padding: new Thickness(16)));
        return s;
    }

    private static Button Btn(string label, Action onClick)
    {
        var b = new Button
        {
            Content = label,
            Style = (Style)Application.Current.FindResource("NexusButton"),
            Margin = new Thickness(0, 0, 8, 0),
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    // ── Pane bodies (filled in by the roster / diagnostics / tools tasks) ──

    // ── ROSTER: who has access. View-only: the list is compiled in, an in-app edit could only
    // ever affect this machine, so pretending to edit it here would misrepresent what testers
    // actually receive. ──
    private ScrollViewer BuildRosterPane()
    {
        var stack = new StackPanel();

        var owner = new StackPanel();
        owner.Children.Add(new TextBlock
        {
            Text = OwnerGate.OwnerHandle, FontFamily = Hud.Font("MonoFont"),
            FontSize = 16, FontWeight = FontWeights.Bold, Foreground = Hud.Br("AccentBrush"),
        });
        owner.Children.Add(new TextBlock
        {
            Text = "App owner. Sees every gated surface, including this tab.",
            FontFamily = Hud.Font("UiFont"), FontSize = 11.5, Foreground = Hud.Br("FgDimBrush"),
            Margin = new Thickness(0, 4, 0, 0),
        });
        stack.Children.Add(Section("OWNER", owner));

        var testers = new StackPanel();
        var roster = AccessGate.Testers;
        for (int i = 0; i < roster.Count; i++)
        {
            var row = new Grid { Margin = new Thickness(0, i == 0 ? 0 : 6, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var idx = new TextBlock
            {
                Text = (i + 1).ToString("00"), FontFamily = Hud.Font("MonoFont"),
                FontSize = 11, Foreground = Hud.Br("FgDimBrush"), VerticalAlignment = VerticalAlignment.Center,
            };
            var handle = new TextBlock
            {
                Text = roster[i], FontFamily = Hud.Font("MonoFont"),
                FontSize = 12.5, Foreground = Hud.Br("FgBrush"), VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(handle, 1);
            row.Children.Add(idx);
            row.Children.Add(handle);
            testers.Children.Add(row);
        }
        stack.Children.Add(Section($"BETA TESTERS ({roster.Count})", testers));

        var matrix = new Grid();
        matrix.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        matrix.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        matrix.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        string[][] rows =
        [
            ["FEATURE", "OWNER", "TESTER"],
            ["Cargo Planner tab", "yes", "yes"],
            ["Grid Studio tab", "yes", "yes"],
            ["Import submission", "yes", "yes"],
            ["Export to catalog patch", "yes", "no"],
            ["Admin tab", "yes", "no"],
        ];
        for (int r = 0; r < rows.Length; r++)
        {
            matrix.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int c = 0; c < 3; c++)
            {
                var cell = new TextBlock
                {
                    Text = rows[r][c],
                    FontFamily = Hud.Font(r == 0 ? "UiFont" : "MonoFont"),
                    FontSize = r == 0 ? 10.5 : 12,
                    FontWeight = r == 0 ? FontWeights.Bold : FontWeights.Normal,
                    Foreground = r == 0 ? Hud.Br("FgDimBrush")
                        : rows[r][c] == "no" ? Hud.Br("FgDimBrush") : Hud.Br("OkBrush"),
                    Margin = new Thickness(0, r == 0 ? 0 : 7, 0, 0),
                };
                if (c == 0 && r > 0) cell.Foreground = Hud.Br("FgBrush");
                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                matrix.Children.Add(cell);
            }
        }
        stack.Children.Add(Section("ACCESS MATRIX", matrix));

        stack.Children.Add(new TextBlock
        {
            Text = "The roster is compiled into the app. Onboarding or removing a tester means editing AccessGate and cutting a release.",
            FontFamily = Hud.Font("UiFont"), FontSize = 11, Foreground = Hud.Br("FgDimBrush"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 0, 24),
        });

        return Pane(stack);
    }

    // ── DIAGNOSTICS: read-only live state. Every row read is individually guarded and renders
    // "unavailable" on failure: this dashboard must never be able to crash the app. Every path
    // shown goes through the user-profile redaction so a screenshot can never leak the OS
    // username. Rebuilt on every page open plus the manual refresh button. ──
    private ScrollViewer BuildDiagnosticsPane()
    {
        var stack = new StackPanel();
        var refresh = Btn("Refresh", RebuildDiagnostics);
        refresh.HorizontalAlignment = HorizontalAlignment.Left;
        refresh.Margin = new Thickness(0, 0, 0, 14);
        stack.Children.Add(refresh);
        _diagHost = new StackPanel();
        stack.Children.Add(_diagHost);
        return Pane(stack);
    }

    private static string Redact(string? path) => DiagnosticSnapshot.RedactUserProfile(
        path, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    private static UIElement Row(string label, Func<string?> value)
    {
        string v;
        try
        {
            v = value() ?? "unavailable";
            if (string.IsNullOrWhiteSpace(v)) v = "unavailable";
        }
        catch (Exception ex)
        {
            // A failing row must degrade on screen AND leave a trace in the log this dashboard
            // exists to triage with.
            v = "unavailable";
            Logger.Error($"[UI] admin: diagnostics row '{label}' failed", ex);
        }
        var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var l = new TextBlock
        {
            Text = label, FontFamily = Hud.Font("UiFont"), FontSize = 12,
            Foreground = Hud.Br("FgDimBrush"), VerticalAlignment = VerticalAlignment.Top,
        };
        var r = new TextBlock
        {
            Text = v, FontFamily = Hud.Font("MonoFont"), FontSize = 12,
            Foreground = Hud.Br("FgBrush"), TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetColumn(r, 1);
        g.Children.Add(l);
        g.Children.Add(r);
        return g;
    }

    private static UIElement Rows(params UIElement[] rows)
    {
        var s = new StackPanel();
        foreach (var r in rows) s.Children.Add(r);
        return s;
    }

    private void RebuildDiagnostics()
    {
        if (_diagHost == null) return;
        Logger.Info("[UI] admin: diagnostics refreshed");
        _diagHost.Children.Clear();

        _diagHost.Children.Add(Section("BUILD", Rows(
            Row("App version", () => $"{AppInfo.Version} ({AppInfo.Distribution})"),
            Row("Build fingerprint", () => AppInfo.BuildFingerprint),
            Row("Star Citizen data", () => GameData.Version),
            Row("Mining data", () => App.Data.MiningDataVersion),
            Row("OS", () => Environment.OSVersion.VersionString),
            Row("GPU", () =>
            {
                var lines = GpuInfo.AdapterLines();
                return lines.Count == 0 ? "none found"
                    : string.Join("\n", lines).Replace("[WIN] display adapter: ", "");
            }))));

        _diagHost.Children.Add(Section("IDENTITY AND GATES", Rows(
            Row("Detected RSI handle", () =>
                string.IsNullOrEmpty(App.Settings.Current.DetectedRsiHandle)
                    ? "none yet" : App.Settings.Current.DetectedRsiHandle),
            Row("Owner gate", () => OwnerGate.IsOwnerActive ? "open" : "closed"),
            Row("Approved gate", () => AccessGate.IsApprovedActive ? "open" : "closed"),
            Row("Preview", () => GatePreview.IsActive ? GatePreview.Active.ToString() : "off"))));

        _diagHost.Children.Add(Section("SESSION", Rows(
            Row("Game.log", () => string.IsNullOrEmpty(App.GameLog.Path) ? "not found" : Redact(App.GameLog.Path)),
            Row("Watcher", () => App.GameLog.IsRunning ? "running" : "stopped"),
            Row("Session", () => App.GameLog.IsSessionLive ? "live" : "not live"))));

        _diagHost.Children.Add(Section("CRASH STATE", Rows(
            Row("Last automatic restart", () => RelaunchNotice.FormatTimestamp(App.Settings.Current.LastAutoRelaunchUtc)),
            Row("Relaunched this session", () => App.RelaunchedThisSession ? "yes" : "no"),
            Row("Relaunch loop guard", () =>
                CrashGuard.IsMarkerFresh(CrashGuard.DefaultMarkerPath, DateTime.UtcNow, CrashGuard.RelaunchLoopWindow)
                    ? "armed (marker fresh)" : "clear"))));

        _diagHost.Children.Add(Section("UPDATES", Rows(
            Row("Update checks", () => App.Settings.Current.UpdateCheckEnabled switch
            {
                null => "not asked",
                true => "enabled",
                false => "disabled",
            }),
            Row("Last check", () => App.Settings.Current.LastUpdateCheckUtc is { } t
                ? t.ToString("yyyy-MM-dd HH:mm 'UTC'")
                : "never"),
            Row("State", () => App.Update.State.ToString()),
            Row("Last failure", () => string.IsNullOrEmpty(App.Update.LastFailure) ? "none" : App.Update.LastFailure),
            Row("Manifest version seen", () => App.Update.Available?.Version.ToString(3) ?? "none"))));

        var logRows = Rows(
            Row("nexus.log", () =>
            {
                var fi = new FileInfo(Logger.LogPath);
                return fi.Exists
                    ? $"{fi.Length / 1024} KB, started {fi.CreationTimeUtc:yyyy-MM-dd HH:mm} UTC"
                    : "not created yet";
            }),
            Row("Location", () => Redact(Logger.LogPath)));
        var logButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        logButtons.Children.Add(Btn("App Log Monitor", _openAppLogMonitor));
        logButtons.Children.Add(Btn("Game.log Monitor", _openLogMonitor));
        var logStack = new StackPanel();
        logStack.Children.Add(logRows);
        logStack.Children.Add(logButtons);
        _diagHost.Children.Add(Section("LOGS", logStack));

        _diagHost.Children.Add(Section("ASSETS", Rows(
            Row("Hull outlines (shipped)", () =>
                Directory.Exists(CargoWebView.ShippedHullsDirPath)
                    ? Directory.GetFiles(CargoWebView.ShippedHullsDirPath, "*.bin").Length.ToString()
                    : "0"),
            Row("Hull outlines (local overrides)", () =>
                Directory.Exists(CargoWebView.HullsDirPath)
                    ? Directory.GetFiles(CargoWebView.HullsDirPath, "*.bin").Length.ToString()
                    : "0"),
            Row("WebView2 runtime", () =>
            {
                try { return Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString(); }
                catch { return "not installed"; }
            }))));
    }

    // ── TOOLS: gate preview, demo profile, folders. This pane refreshes its OWN state: MainWindow
    // deliberately never rebuilds the cached Admin page when the preview role changes, and the
    // Admin tile stays visible under preview (IsOwnerReal), so every button handler calls
    // RefreshToolsState() itself to keep the banner and the state lines honest. ──
    private ScrollViewer BuildToolsPane()
    {
        var stack = new StackPanel();

        // Persistent banner while a preview is active. Amber alert-strip idiom (CommandPage).
        _previewBannerText = new TextBlock
        {
            FontFamily = Hud.Font("UiFont"), FontSize = 12, FontWeight = FontWeights.Bold,
            Foreground = Hud.Br("AccentBrush"), VerticalAlignment = VerticalAlignment.Center,
        };
        var bannerGrid = new Grid();
        bannerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bannerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bannerGrid.Children.Add(_previewBannerText);
        var exitBtn = Btn("Exit preview", () => { GatePreview.Set(GatePreview.Role.None); RefreshToolsState(); });
        exitBtn.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(exitBtn, 1);
        bannerGrid.Children.Add(exitBtn);
        _previewBanner = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xB2, 0x3E)),
            BorderBrush = Hud.Br("AccentStrongBrush"), BorderThickness = new Thickness(2, 1, 1, 1),
            Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 18),
            Visibility = Visibility.Collapsed, Child = bannerGrid,
        };
        stack.Children.Add(_previewBanner);

        // Gate preview card.
        var preview = new StackPanel();
        preview.Children.Add(new TextBlock
        {
            Text = "See the app exactly as a visitor or a beta tester does (dock tabs and gated buttons "
                 + "respond for real). Session-only: nothing is saved, and a restart always returns to "
                 + "reality. This tab itself stays visible so you can always come back here to exit.",
            FontFamily = Hud.Font("UiFont"), FontSize = 11.5, Foreground = Hud.Br("FgDimBrush"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
        });
        _previewState = new TextBlock
        {
            FontFamily = Hud.Font("MonoFont"), FontSize = 12, Foreground = Hud.Br("FgBrush"),
            Margin = new Thickness(0, 0, 0, 10),
        };
        preview.Children.Add(_previewState);
        var previewButtons = new StackPanel { Orientation = Orientation.Horizontal };
        previewButtons.Children.Add(Btn("View as visitor", () => { GatePreview.Set(GatePreview.Role.Visitor); RefreshToolsState(); }));
        previewButtons.Children.Add(Btn("View as beta tester", () => { GatePreview.Set(GatePreview.Role.BetaTester); RefreshToolsState(); }));
        previewButtons.Children.Add(Btn("Exit preview", () => { GatePreview.Set(GatePreview.Role.None); RefreshToolsState(); }));
        preview.Children.Add(previewButtons);
        stack.Children.Add(Section("GATE PREVIEW", preview));

        // Demo profile card.
        var demo = new StackPanel();
        demo.Children.Add(new TextBlock
        {
            Text = "Relaunch Nexus into the isolated StarlightHauler demo profile for public "
                 + "screenshots. Your live data is never read or written. To return, close the demo "
                 + "app and start Nexus normally.",
            FontFamily = Hud.Font("UiFont"), FontSize = 11.5, Foreground = Hud.Br("FgDimBrush"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
        });
        _demoState = new TextBlock
        {
            FontFamily = Hud.Font("MonoFont"), FontSize = 12, Foreground = Hud.Br("FgBrush"),
            Margin = new Thickness(0, 0, 0, 10),
        };
        demo.Children.Add(_demoState);
        var demoButtons = new StackPanel { Orientation = Orientation.Horizontal };
        demoButtons.Children.Add(Btn("Launch demo mode", OnLaunchDemo));
        demoButtons.Children.Add(Btn("Reset demo profile", OnResetDemo));
        demo.Children.Add(demoButtons);
        stack.Children.Add(Section("DEMO PROFILE", demo));

        // Data tools card. The SCT toggle that lived here is gone: live market data is now a
        // single consent covering UEX and SCT together (2026-08-03: the separate toggles were
        // removed in favor of all or nothing), so there is nothing owner-specific left to switch.
        // What survives is the one-shot fetch, which was the genuinely useful half of this card -
        // SCT rides the hourly market tick but only actually refetches every 6h, so seeing a
        // change now still needs a way to force it.
        var data = new StackPanel();
        data.Children.Add(new TextBlock
        {
            Text = "SCT second source: crowdsourced price listings from SC Trade Tools, used "
                 + "alongside UEX on the trading tab. It follows the Settings market-data consent "
                 + "and is fully inert while that is off - no network call, no data load. Auto "
                 + "refresh is every 6 hours; this button forces one now.",
            FontFamily = Hud.Font("UiFont"), FontSize = 11.5, Foreground = Hud.Br("FgDimBrush"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
        });
        _sctState = new TextBlock
        {
            FontFamily = Hud.Font("MonoFont"), FontSize = 12, Foreground = Hud.Br("FgBrush"),
            Margin = new Thickness(0, 0, 0, 10),
        };
        data.Children.Add(_sctState);
        data.Children.Add(Btn("Fetch SCT now", () =>
        {
            // No-ops safely while consent is off - every SctMarketService entry point self-gates -
            // but saying so beats a button that silently does nothing.
            if (App.Settings.Current.MarketDataEnabled != true)
            {
                _sctState.Text = "SCT: off (turn on live market data in Settings first)";
                return;
            }
            App.KickSctFetch("admin");
            RefreshToolsState();
        }));
        // Keeps the state line honest when the consent is flipped in Settings while this pane is open.
        App.Sct.Changed += () => Dispatcher.BeginInvoke(RefreshToolsState);
        stack.Children.Add(Section("DATA TOOLS", data));

        // Folders card.
        var folders = new StackPanel { Orientation = Orientation.Horizontal };
        folders.Children.Add(Btn("Open data folder", () => OpenFolder(AppPaths.Root)));
        folders.Children.Add(Btn("Open logs folder", () => OpenFolder(Path.GetDirectoryName(Logger.LogPath) ?? AppPaths.Root)));
        stack.Children.Add(Section("FOLDERS", folders));

        return Pane(stack);
    }

    private void RefreshToolsState()
    {
        if (_previewState == null) return;
        _previewState.Text = GatePreview.IsActive
            ? $"Previewing as: {(GatePreview.Active == GatePreview.Role.Visitor ? "visitor" : "beta tester")}"
            : "Preview off (you see the app as the owner).";
        _previewBanner.Visibility = GatePreview.IsActive ? Visibility.Visible : Visibility.Collapsed;
        _previewBannerText.Text = GatePreview.Active == GatePreview.Role.Visitor
            ? "PREVIEWING AS VISITOR" : "PREVIEWING AS BETA TESTER";
        _demoState.Text = DemoProfile.IsSeeded(AppPaths.DemoRoot)
            ? "Demo profile: seeded (relaunches resume its session state)."
            : "Demo profile: not seeded yet (created on first launch).";
        _sctState.Text = App.Settings.Current.MarketDataEnabled == true
            ? $"SCT: on with live market data (last fetch {(App.Sct.SnapshotFetchedUtc is { } f ? f.ToLocalTime().ToString("g") : "none yet")})"
            : "SCT: off (fully inert - no network call, no data load)";
    }

    private void OnLaunchDemo()
    {
        var confirm = MessageBox.Show(
            "Launch Nexus in demo profile mode?\n\nThis app will close and a demo instance "
            + "(StarlightHauler) will start. Your live data is not touched. To return, close the "
            + "demo app and start Nexus normally.",
            "Demo mode", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;
        // Shut down ONLY once the child is confirmed started; otherwise the owner is left with
        // no app at all.
        if (DemoProfile.StartDemoInstance())
        {
            Application.Current.Shutdown();
            return;
        }
        MessageBox.Show(
            "The demo instance could not be started. See nexus.log for details.",
            "Demo mode", MessageBoxButton.OK, MessageBoxImage.Warning);
        // A failed launch may still have seeded the root; keep the card honest.
        RefreshToolsState();
    }

    private void OnResetDemo()
    {
        try { DemoProfile.Reset(AppPaths.DemoRoot); }
        catch (Exception ex)
        {
            // Most likely cause: a demo instance is still running and holds nexus.db open.
            Logger.Error("[UI] admin: demo profile reset failed", ex);
            MessageBox.Show(
                "The demo profile could not be reset. If a demo instance is still running, close it and try again.",
                "Demo mode", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        RefreshToolsState();
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Logger.Info($"[UI] admin: open folder {Redact(path)}");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Logger.Error("[UI] admin: open folder failed", ex); }
    }
}
