using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using NexusApp.Services;

namespace NexusApp.Views;

/// <summary>
/// Shared Executive Hangar status line (issue #26 amendment, live-run scope expansion
/// 2026-07-27): the two-row PYAM contested-zone hangar cycle readout used on both the Guides
/// page (standard mode, in the Contested Zones section head's star column) and the overlay
/// GUIDES tab (compact mode, in a chamfered block above the guide catalog). One control and one
/// <see cref="ExecHangarCycle"/> math call per refresh drive both rows, so the two surfaces
/// cannot drift.
///
/// Row 1: five phase dots, phase word, verb ("closes in" / "opens in"), mono countdown, an
/// optional "still active" tail note, and the Re-anchor / Reset ghost links (Reset only while an
/// anchor override is active). Row 2: the next-opens readout, right-aligned under row 1 in
/// standard mode, left-aligned in compact mode.
///
/// Compact-mode exception: at the overlay's 320px width, row 1 cannot fit the tail note alongside
/// a visible Reset link in every phase (measured against the actual embedded fonts - see the task
/// 4 report). The tail badge is left out of compact row 1 entirely; the same fact is folded into
/// the tooltip instead, only while it is true.
///
/// Lifecycle is caller-owned, never automatic: <see cref="Start"/> recomputes and repaints
/// immediately, writes the once-per-entry log line, and starts a 1-second DispatcherTimer if one
/// is not already running (never stacks across repeated calls - see the guard inside);
/// <see cref="Stop"/> halts and releases it. Callers must Start on entry and Stop on every exit
/// from their host surface: GuidesPage wires this via IsVisibleChanged (a lazy-singleton page,
/// visibility-toggled, never Loaded/Unloaded); the overlay wires it via SwitchTab entering/leaving
/// "guides" plus OnClosed for the whole-window teardown.
/// </summary>
public sealed class ExecHangarStatusLine : StackPanel
{
    private const double DotSize = 7;
    private const double DotGap = 4;
    private const double ItemGap = 6;

    private readonly bool _compact;
    private readonly string _surfaceName;

    private readonly Ellipse[] _dots = new Ellipse[5];
    private readonly TextBlock _phaseText;
    private readonly TextBlock _verbText;
    private readonly TextBlock _countdownText;
    private readonly TextBlock _tailText;
    private readonly TextBlock _resetLink;
    private readonly StackPanel _row1;
    private readonly TextBlock _nextOpensText;

    private DispatcherTimer? _timer;

    public ExecHangarStatusLine(bool compact, string surfaceName)
    {
        _compact = compact;
        _surfaceName = surfaceName;

        var dotsPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        for (var i = 0; i < _dots.Length; i++)
        {
            var dot = new Ellipse
            {
                Width = DotSize, Height = DotSize,
                Margin = new Thickness(i == 0 ? 0 : DotGap, 0, 0, 0),
            };
            _dots[i] = dot;
            dotsPanel.Children.Add(dot);
        }

        _phaseText = new TextBlock
        {
            FontFamily = Hud.Font("TechFont"), FontSize = compact ? 10 : 11, FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(ItemGap, 0, 0, 0),
        };

        // Owner ruling (live run 2026-07-27): say what the number counts down TO ("closes in" /
        // "opens in"); a bare countdown reads ambiguously against the 185m full-cycle mental model.
        _verbText = new TextBlock
        {
            FontSize = compact ? 9.5 : 10.5, Foreground = Hud.Br("FgDimBrush"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(ItemGap, 0, 0, 0),
        };

        _countdownText = new TextBlock
        {
            FontFamily = Hud.Font("MonoFont"), FontSize = compact ? 10 : 11, Foreground = Hud.Br("FgBrush"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(ItemGap, 0, 0, 0),
        };

        _tailText = new TextBlock
        {
            Text = "still active", FontSize = compact ? 9 : 10, Foreground = Hud.Br("FgDimBrush"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(ItemGap, 0, 0, 0),
            Visibility = Visibility.Collapsed,
        };

        var reanchor = new TextBlock
        {
            Text = "Re-anchor", FontFamily = Hud.Font("UiFont"), FontSize = compact ? 9.5 : 10.5,
            Foreground = Hud.Br("AccentBrush"), Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(ItemGap, 0, 0, 0),
        };
        reanchor.MouseEnter += (_, _) => reanchor.TextDecorations = TextDecorations.Underline;
        reanchor.MouseLeave += (_, _) => reanchor.TextDecorations = null;
        reanchor.MouseLeftButtonDown += (_, _) => OnReanchorClicked();

        _resetLink = new TextBlock
        {
            Text = "Reset", FontFamily = Hud.Font("UiFont"), FontSize = compact ? 9.5 : 10.5,
            Foreground = Hud.Br("FgDimBrush"), Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(ItemGap, 0, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        _resetLink.MouseEnter += (_, _) => _resetLink.TextDecorations = TextDecorations.Underline;
        _resetLink.MouseLeave += (_, _) => _resetLink.TextDecorations = null;
        _resetLink.MouseLeftButtonDown += (_, _) => OnResetClicked();

        _row1 = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _row1.Children.Add(dotsPanel);
        _row1.Children.Add(_phaseText);
        _row1.Children.Add(_verbText);
        _row1.Children.Add(_countdownText);
        // Compact row 1 leaves the tail badge out of the visual tree entirely (see the class
        // remark on the 320px measurement); _tailText still exists and its Visibility still
        // tracks IsFinalActiveTail in ApplySnapshot below (kept uniform for both modes), it is
        // simply never parented in compact mode so it can never occupy layout space or be seen.
        if (!compact) _row1.Children.Add(_tailText);
        _row1.Children.Add(reanchor);
        _row1.Children.Add(_resetLink);

        _nextOpensText = new TextBlock
        {
            FontFamily = Hud.Font("MonoFont"), FontSize = compact ? 9.5 : 10.5, Foreground = Hud.Br("FgDimBrush"),
            HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            Margin = new Thickness(0, 4, 0, 0),
        };

        Children.Add(_row1);
        Children.Add(_nextOpensText);

        RefreshStatus();   // paint the initial state; Start() drives it live from here
    }

    /// <summary>Recompute + repaint immediately, log the once-per-entry line, start the 1s ticker
    /// if it is not already running (never stacks across repeated calls).</summary>
    public void Start()
    {
        RefreshStatus();

        var overrideUtc = App.Settings.Current.ExecHangarAnchorOverrideUtc;
        var s = ExecHangarCycle.At(DateTime.UtcNow, overrideUtc);
        Logger.Info($"[UI] Exec hangar timer ({_surfaceName}): {(s.IsOpen ? "OPEN" : "CLOSED")}, " +
            $"{(s.IsOpen ? "closes in" : "opens in")} {ExecHangarCycle.FormatCountdown(s.TimeToTransition)} " +
            $"(anchor: {(overrideUtc.HasValue ? "custom" : "built-in " + ExecHangarCycle.CalibrationLabel)})");

        if (_timer != null) return;   // already running - never stack a second timer
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => RefreshStatus();
        _timer.Start();
    }

    /// <summary>Stop and release the ticker. Safe to call repeatedly / when never started.</summary>
    public void Stop()
    {
        _timer?.Stop();
        _timer = null;
    }

    // Recomputes the cycle from the current clock (and any Settings override) and repaints every
    // part of both rows in one pass. Called from the ctor (initial paint), every timer tick, and
    // right after a Re-anchor confirm or Reset so the line never waits for the next tick.
    private void RefreshStatus()
    {
        var overrideUtc = App.Settings.Current.ExecHangarAnchorOverrideUtc;
        var snapshot = ExecHangarCycle.At(DateTime.UtcNow, overrideUtc);
        ApplySnapshot(snapshot, overrideUtc);
    }

    private void ApplySnapshot(ExecHangarSnapshot s, DateTime? overrideUtc)
    {
        var litBrush = s.IsOpen ? Hud.Br("AccentBrush") : Hud.Br("CyanBrush");
        var unlitBrush = Hud.Br("Bg3Brush");
        var unlitStroke = Hud.Br("NavBorderBrush");
        for (var i = 0; i < _dots.Length; i++)
        {
            var lit = i < s.GreensLit;
            _dots[i].Fill = lit ? litBrush : unlitBrush;
            _dots[i].Stroke = lit ? null : unlitStroke;
            _dots[i].StrokeThickness = 1;
        }

        _phaseText.Text = s.IsOpen ? "OPEN" : "CLOSED";
        _phaseText.Foreground = s.IsOpen ? Hud.Br("AccentBrush") : Hud.Br("FgDimBrush");

        _verbText.Text = s.IsOpen ? "closes in" : "opens in";
        _countdownText.Text = ExecHangarCycle.FormatCountdown(s.TimeToTransition);
        _tailText.Visibility = s.IsFinalActiveTail ? Visibility.Visible : Visibility.Collapsed;
        _resetLink.Visibility = overrideUtc.HasValue ? Visibility.Visible : Visibility.Collapsed;

        _nextOpensText.Text = (_compact ? "Next: " : "Next open: ")
            + string.Join(", ", s.UpcomingOpensUtc.Take(_compact ? 2 : 3).Select(FormatClockTime));

        var calibrationLine = overrideUtc.HasValue
            ? $"Custom anchor set {FormatAnchorLocal(overrideUtc.Value)}"
            : $"Cycle calibration: {ExecHangarCycle.CalibrationLabel}";
        var tooltip =
            calibrationLine + "\n" +
            "Uses your PC clock. Keep Windows time set to automatic.\n" +
            "If in-game lights look off with time remaining, trust the timer; the lights catch up at the next state change.";
        // Compact-mode tradeoff (see the class remark): the tail badge that standard mode shows
        // in row 1 becomes a tooltip-only line here, and only while it is true.
        if (_compact && s.IsFinalActiveTail)
            tooltip += "\nStill active: lights show 0 during the last 5 minutes before close.";
        _row1.ToolTip = tooltip;
    }

    // Time format follows Settings > Appearance > 24-hour clock - the same selection MainWindow's
    // top-bar clock reads (App.Settings.Current.Clock24Hour ? "HH:mm:ss" : "h:mm:ss tt") - trimmed
    // to minutes for this compact readout ("Next open: 19:42, 22:47, 01:52").
    private static string FormatClockTime(DateTime utc)
        => utc.ToLocalTime().ToString(App.Settings.Current.Clock24Hour ? "HH:mm" : "h:mm tt");

    // The stored override round-trips through JSON settings and can come back Local or
    // Unspecified (same caveat as ExecHangarCycle.At); normalize the same way before converting.
    private static string FormatAnchorLocal(DateTime storedUtc)
    {
        var utc = storedUtc.Kind == DateTimeKind.Local
            ? storedUtc.ToUniversalTime()
            : DateTime.SpecifyKind(storedUtc, DateTimeKind.Utc);
        return $"{utc.ToLocalTime():d MMM yyyy} {FormatClockTime(utc)}";
    }

    private void OnResetClicked()
    {
        App.Settings.Current.ExecHangarAnchorOverrideUtc = null;
        App.Settings.Save();
        RefreshStatus();
        Logger.Info("[UI] Exec hangar anchor reset to built-in");
    }

    private void OnReanchorClicked()
    {
        if (!ShowReanchorConfirm(Window.GetWindow(this))) return;
        var anchor = DateTime.UtcNow;   // stored UTC, never local time

        // One log line carrying everything a maintainer needs to recalibrate the built-in anchor
        // from a user's diagnostic snapshot: the observed open instant, which anchor was active
        // before, what that prior calibration predicted at the click (makes the drift visible),
        // the app version, and the shard id (its numeric chunk is the game server build the
        // community calibrations are keyed to). Captured BEFORE the override is replaced.
        var priorOverride = App.Settings.Current.ExecHangarAnchorOverrideUtc;
        var prior = ExecHangarCycle.At(anchor, priorOverride);
        var priorAnchorDesc = priorOverride.HasValue
            ? $"custom {priorOverride.Value:yyyy-MM-dd'T'HH:mm:ss.fff'Z'}"
            : "built-in " + ExecHangarCycle.CalibrationLabel;
        var shard = App.Shards.Current?.ShardId
            ?? App.Shards.All.FirstOrDefault()?.ShardId
            ?? "unknown";

        App.Settings.Current.ExecHangarAnchorOverrideUtc = anchor;
        App.Settings.Save();
        RefreshStatus();
        Logger.Info($"[UI] Exec hangar re-anchored by user: {anchor:yyyy-MM-dd'T'HH:mm:ss.fff'Z'} UTC "
            + $"(app {AppInfo.Version}; prior anchor: {priorAnchorDesc}; prior prediction at click: "
            + $"{(prior.IsOpen ? "OPEN" : "CLOSED")}, {ExecHangarCycle.FormatCountdown(prior.TimeToTransition)} "
            + $"to {(prior.IsOpen ? "close" : "open")}; shard: {shard})");
    }

    // Compact themed confirm dialog. Chrome (themed Background/Foreground, ResizeMode, sizing)
    // is modeled on NetworkPage's small prompt/choice dialogs; DialogMotion.Attach and
    // UiScaleService.ApplyToDialog are added below to match the app's near-universal dialog
    // idiom (every dedicated *Dialog.cs, e.g. WorkOrderEditorDialog's identical
    // SizeToContent.Height sizing) - mouse only, still no default/cancel key bindings. Owner is
    // Window.GetWindow(this) from the caller, so it sits over whichever host invoked it (the
    // main window for the Guides page, the overlay for the GUIDES tab).
    private static bool ShowReanchorConfirm(Window? owner)
    {
        var win = new Window
        {
            Title = "RE-ANCHOR HANGAR CYCLE",
            Width = 420, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Owner = owner,
            Background = Hud.Br("BgBrush"), Foreground = Hud.Br("FgBrush"),
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
        };

        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock
        {
            Text = "Set the cycle from this moment. Click confirm only at the instant the hangar opens.",
            FontFamily = Hud.Font("UiFont"), FontSize = 12.5, Foreground = Hud.Br("FgBrush"),
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Your re-anchor time is written to the app log. Please send a diagnostic snapshot "
                 + "(Settings > Diagnostics) to the project so the built-in calibration can be updated for everyone.",
            FontFamily = Hud.Font("UiFont"), FontSize = 11, Foreground = Hud.Br("FgDimBrush"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
        });

        var cancel = new Button
        {
            Content = "Cancel", Style = (Style)Application.Current.FindResource("NexusButton"),
            Padding = new Thickness(16, 7, 16, 7), Cursor = Cursors.Hand,
        };
        var confirm = new Button
        {
            Content = "Confirm", Style = (Style)Application.Current.FindResource("AccentButton"),
            Padding = new Thickness(16, 7, 16, 7), Margin = new Thickness(8, 0, 0, 0), Cursor = Cursors.Hand,
        };
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        row.Children.Add(cancel);
        row.Children.Add(confirm);
        panel.Children.Add(row);

        win.Content = panel;
        confirm.Click += (_, _) => win.DialogResult = true;
        cancel.Click += (_, _) => win.DialogResult = false;
        DialogMotion.Attach(win);
        UiScaleService.ApplyToDialog(win, panel);   // App scale (issue #20)
        return win.ShowDialog() == true;
    }
}
