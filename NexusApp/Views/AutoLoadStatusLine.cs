using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using NexusApp.Models;
using NexusApp.Services;

namespace NexusApp.Views;

/// <summary>
/// Live auto-load / auto-unload readout (task 8). Game.log never reports completion, so this
/// counts UP from the flagged kiosk transaction and ends only by the mouse LOADED button (records
/// a calibration sample), DISCARD (no sample), or the tracker's own 2h abandon expiry.
///
/// Two hosts, same control, the <see cref="ExecHangarStatusLine"/> API shape:
/// compact (the overlay strip, one row per active entry when clicked open) and standard (the
/// trade tab panel, entry rows shown directly). Collapsed entirely while
/// <see cref="App.AutoLoad"/> carries no entries.
///
/// Lifecycle is caller-owned: <see cref="Start"/> subscribes to
/// <see cref="AutoLoadTracker.EntriesChanged"/> and paints immediately; <see cref="Stop"/>
/// unsubscribes and halts the ticker. Start is idempotent - it unsubscribes before it
/// subscribes, so calling it more than once can never double-subscribe OnEntries, and a single
/// Stop always fully detaches. The 1-second ticker itself runs ONLY while entries exist
/// (started/stopped from <see cref="OnEntries"/>, never stacked - same guard as
/// ExecHangarStatusLine) and calls <see cref="AutoLoadTracker.ExpireStale"/> once per tick with no
/// logging; the tracker itself logs the abandon.
/// </summary>
public sealed class AutoLoadStatusLine : StackPanel
{
    private readonly bool _compact;
    private readonly string _surfaceName;
    private bool _expanded;   // compact only: strip click toggles the entry rows open/closed
    private bool _stopped;    // guards a BeginInvoke continuation queued before Stop() lands

    private DispatcherTimer? _ticker;

    // Elapsed TextBlocks repainted every tick without a full Rebuild (row rebuilding would drop
    // hover state and flicker). IsRow marks the entry-row 20px readout, which also carries the
    // over-estimate WarnBrush + glow; the strip's own 11px readout never carries that state.
    private readonly List<(AutoLoadEntry Entry, TextBlock Text, bool IsRow)> _elapsedTexts = new();

    // Standard-row progress bars, keyed by their two ColumnDefinitions so a tick can resize the
    // star split in place (same no-Rebuild discipline as _elapsedTexts) instead of replacing the
    // Grid. Compact rows never add to this list - the bar is a standard-variant-only surface.
    private readonly List<(AutoLoadEntry Entry, ColumnDefinition FillCol, ColumnDefinition RestCol)> _progressBars = new();

    public AutoLoadStatusLine(bool compact, string surfaceName)
    {
        _compact = compact;
        _surfaceName = surfaceName;
        Rebuild();   // paint the initial state; Start() drives it live from here
    }

    /// <summary>Subscribe to tracker changes, repaint immediately, log once. Safe to call
    /// repeatedly - it unsubscribes before it subscribes, so a second Start can never
    /// double-subscribe OnEntries (mirrors ExecHangarStatusLine.Start).</summary>
    public void Start()
    {
        _stopped = false;
        App.AutoLoad.EntriesChanged -= OnEntries;   // defensive resubscribe: never double-fire
        App.AutoLoad.EntriesChanged += OnEntries;
        Rebuild();
        SyncTicker();
        Logger.Info($"[UI] auto-load status line started: {_surfaceName}");
    }

    /// <summary>Unsubscribe and halt the ticker. Safe to call repeatedly / when never started.</summary>
    public void Stop()
    {
        _stopped = true;
        App.AutoLoad.EntriesChanged -= OnEntries;
        StopTicker();
    }

    // GameLogFeed already raises its events on the UI dispatcher (GameLogFeed.cs:126-127), so this
    // BeginInvoke is not thread marshaling. It exists so a LOADED/DISCARD click (which calls
    // App.AutoLoad.Complete/Discard synchronously, inside our own event handler) can mutate the
    // tracker without this handler clearing the visual tree out from under the click that is
    // still bubbling - the rebuild is deferred to the next dispatcher cycle instead of reentering
    // mid-route. _stopped guards a continuation already queued when Stop() lands (e.g. window
    // close mid-flight) from repainting or resurrecting the ticker on a dead control.
    private void OnEntries()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_stopped) return;
            Rebuild();
            SyncTicker();
        }));
    }

    private void SyncTicker()
    {
        if (App.AutoLoad.Entries.Count > 0)
        {
            if (_ticker != null) return;   // already running - never stack a second timer
            _ticker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _ticker.Tick += (_, _) => OnTick();
            _ticker.Start();
        }
        else
        {
            StopTicker();
        }
    }

    private void StopTicker()
    {
        _ticker?.Stop();
        _ticker = null;
    }

    // Expire stale entries, then repaint the ticking values only. No logging here - the tracker
    // logs the abandon itself; a per-tick log line would spam nexus.log at 1 Hz.
    private void OnTick()
    {
        App.AutoLoad.ExpireStale();
        RepaintElapsed();
    }

    private void RepaintElapsed()
    {
        var nowUtc = DateTime.UtcNow;
        var table = AutoLoadTimeTable.Instance;
        foreach (var (entry, text, isRow) in _elapsedTexts)
        {
            text.Text = AutoLoadStatusText.Clock(entry, table, nowUtc);
            if (isRow) ApplyElapsedStyle(entry, text, nowUtc);
        }
        foreach (var (entry, fillCol, restCol) in _progressBars)
        {
            if (AutoLoadStatusText.Progress(entry, table, nowUtc) is { } frac)
            {
                fillCol.Width = new GridLength(frac, GridUnitType.Star);
                restCol.Width = new GridLength(1 - frac, GridUnitType.Star);
            }
        }
    }

    private static void ApplyElapsedStyle(AutoLoadEntry entry, TextBlock text, DateTime nowUtc)
    {
        var over = AutoLoadStatusText.IsOver(entry, AutoLoadTimeTable.Instance, nowUtc);
        text.Foreground = over ? Hud.Br("WarnBrush") : Hud.Br("FgBrush");
        text.Effect = over
            ? new DropShadowEffect { Color = Hud.Col("WarnBrush"), BlurRadius = 12, ShadowDepth = 0, Opacity = 0.25 }
            : null;
    }

    private void Rebuild()
    {
        Children.Clear();
        _elapsedTexts.Clear();
        _progressBars.Clear();

        var entries = App.AutoLoad.Entries;
        if (entries.Count == 0)
        {
            _expanded = false;   // the next auto-load always opens collapsed, never pre-expanded
            Visibility = Visibility.Collapsed;
            return;
        }
        Visibility = Visibility.Visible;

        if (_compact)
        {
            Children.Add(BuildStrip(entries));
            if (_expanded)
                foreach (var e in entries)
                    Children.Add(BuildEntryRow(e));
        }
        else
        {
            Children.Add(BuildEyebrow());
            foreach (var e in entries)
                Children.Add(BuildEntryRow(e));
        }
    }

    // ── Compact strip: amber-faint band, bottom hairline, click toggles the entry rows ──
    private Border BuildStrip(IReadOnlyList<AutoLoadEntry> entries)
    {
        var newest = entries[^1];   // Entries is insertion order; the last add is the newest
        var kindBrush = newest.Kind == TransactionKind.Sell ? Hud.Br("CyanBrush") : Hud.Br("AccentBrush");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var dot = new Ellipse
        {
            Width = 6, Height = 6, Fill = kindBrush, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        };
        Hud.PulseDot(dot, true);
        left.Children.Add(dot);
        left.Children.Add(new TextBlock
        {
            // Mock cites 0.1em letter-spacing; WPF TextBlock has no letter-spacing property
            // (same gap accepted by every other frozen-label spot in this file, e.g. FILTERS).
            Text = AutoLoadStatusText.StripWord(newest), FontFamily = Hud.Font("UiFont"), FontSize = 8.5,
            FontWeight = FontWeights.Bold, Foreground = kindBrush, VerticalAlignment = VerticalAlignment.Center,
        });
        if (AutoLoadStatusText.OverflowCount(entries.Count) is { } overflow)
            left.Children.Add(new TextBlock
            {
                Text = overflow, FontFamily = Hud.Font("MonoFont"), FontSize = 8.5,
                Foreground = Hud.Br("FgDimBrush"), VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
            });
        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        var value = new TextBlock
        {
            Text = AutoLoadStatusText.Clock(newest, AutoLoadTimeTable.Instance, DateTime.UtcNow), FontFamily = Hud.Font("MonoFont"),
            FontSize = 11, Foreground = Hud.Br("FgBrush"), VerticalAlignment = VerticalAlignment.Center,
        };
        _elapsedTexts.Add((newest, value, false));
        Grid.SetColumn(value, 1);
        grid.Children.Add(value);

        var strip = new Border
        {
            Background = Hud.Br("AccentFaintBrush"), BorderBrush = Hud.Br("NavBorderBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(12, 4, 12, 4),
            Cursor = Cursors.Hand, Child = grid,
        };
        strip.MouseLeftButtonUp += (_, _) =>
        {
            _expanded = !_expanded;
            Logger.Info($"[UI] auto-load strip {(_expanded ? "expanded" : "collapsed")} ({_surfaceName})");
            Rebuild();
        };
        return strip;
    }

    // ── Standard mode eyebrow: the Hud.Header dash idiom (Hud.cs:264-270), without the rest of
    // Header's title/subtitle block - the host page supplies its own page card chrome. ──
    private static StackPanel BuildEyebrow()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 11) };
        row.Children.Add(new Border
        {
            Width = 16, Height = 2, Background = Hud.Br("AccentBrush"), VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            Effect = new DropShadowEffect { Color = Hud.Col("AccentBrush"), BlurRadius = 7, ShadowDepth = 0, Opacity = 0.8 },
        });
        row.Children.Add(new TextBlock
        {
            Text = "AUTO-LOAD", FontFamily = Hud.Font("UiFont"), FontSize = 10.5, FontWeight = FontWeights.Bold,
            Foreground = Hud.Br("AccentBrush"), VerticalAlignment = VerticalAlignment.Center,
        });
        return row;
    }

    // ── One entry row: Hud.RowCard chrome, kind/title/countdown head, cargo+location/est sub,
    // standard-only progress bar, LOADED/DISCARD. The ship is never shown - Game.log cannot
    // assert it. Shared by the compact strip's expanded rows too, gated per-piece by _compact. ──
    private Border BuildEntryRow(AutoLoadEntry entry)
    {
        var table = AutoLoadTimeTable.Instance;
        var nowUtc = DateTime.UtcNow;
        // The tracker removes an entry the same tick it crosses AbandonAfter (ExpireStale runs
        // before this ever repaints), so this only ever catches the up-to-1s window before that
        // tick lands - kept dim and action-less rather than let a closing row take one more click.
        var pendingAbandon = nowUtc - entry.StartUtc >= AutoLoadTracker.AbandonAfter;

        var content = new StackPanel();

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var kind = new TextBlock
        {
            Text = entry.Kind == TransactionKind.Sell ? "UNLOAD" : "LOAD",
            FontFamily = Hud.Font("UiFont"), FontSize = 10, FontWeight = FontWeights.Bold,
            Foreground = pendingAbandon ? Hud.Br("FgDimBrush")
                : entry.Kind == TransactionKind.Sell ? Hud.Br("CyanBrush") : Hud.Br("AccentBrush"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
        };
        Grid.SetColumn(kind, 0);
        head.Children.Add(kind);

        var title = new TextBlock
        {
            Text = AutoLoadStatusText.Title(entry), FontSize = 12.5, Foreground = Hud.Br("FgBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        Grid.SetColumn(title, 1);
        head.Children.Add(title);

        var elapsedText = new TextBlock
        {
            Text = AutoLoadStatusText.Clock(entry, table, nowUtc), FontFamily = Hud.Font("MonoFont"), FontSize = 20,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ApplyElapsedStyle(entry, elapsedText, nowUtc);
        _elapsedTexts.Add((entry, elapsedText, true));
        Grid.SetColumn(elapsedText, 2);
        head.Children.Add(elapsedText);

        content.Children.Add(head);

        var sub = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
        sub.Children.Add(new TextBlock
        {
            Text = AutoLoadStatusText.CargoLine(entry), FontFamily = Hud.Font("MonoFont"), FontSize = 10,
            Foreground = Hud.Br("FgDimBrush"),
        });
        sub.Children.Add(new TextBlock
        {
            Text = AutoLoadStatusText.Location(entry), FontFamily = Hud.Font("UiFont"), FontSize = 10,
            Foreground = Hud.Br("FgDimBrush"), TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(10, 0, 0, 0),
        });
        if (AutoLoadStatusText.EstLine(entry, table) is { } est)
            sub.Children.Add(new TextBlock
            {
                Text = est, FontFamily = Hud.Font("MonoFont"), FontSize = 10, Foreground = Hud.Br("FgDimBrush"),
                Margin = new Thickness(10, 0, 0, 0),
            });
        content.Children.Add(sub);

        // Standard variant only - compact rows (the overlay strip's expanded entries, which share
        // this same builder) never gain a bar. Star/star split carries the fraction (the roiMeter
        // idiom, TradePage.Planner.cs:1381-1388), so a tick just resizes the two columns in place
        // via _progressBars rather than rebuilding the Grid.
        if (!_compact && AutoLoadStatusText.Progress(entry, table, nowUtc) is { } frac)
        {
            var fillCol = new ColumnDefinition { Width = new GridLength(frac, GridUnitType.Star) };
            var restCol = new ColumnDefinition { Width = new GridLength(1 - frac, GridUnitType.Star) };
            var meter = new Grid { Height = 5, Margin = new Thickness(0, 6, 0, 0) };
            meter.ColumnDefinitions.Add(fillCol);
            meter.ColumnDefinitions.Add(restCol);
            var track = new Border { Background = Hud.Br("Bg3Brush"), CornerRadius = new CornerRadius(3) };
            Grid.SetColumnSpan(track, 2);
            meter.Children.Add(track);
            var fillBrush = entry.Kind == TransactionKind.Sell ? Hud.Br("CyanBrush") : Hud.Br("AccentBrush");
            meter.Children.Add(new Border { Background = fillBrush, CornerRadius = new CornerRadius(3) });   // column 0
            content.Children.Add(meter);
            _progressBars.Add((entry, fillCol, restCol));
        }

        if (!pendingAbandon)
        {
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 9, 0, 0) };

            var loaded = new Button
            {
                Content = "LOADED", Style = (Style)Application.Current.FindResource("AccentButton"),
                FontSize = 11.5, Padding = new Thickness(16, 7, 16, 7), Cursor = Cursors.Hand,
            };
            loaded.Click += (_, _) =>
            {
                var elapsed = (int)(DateTime.UtcNow - entry.StartUtc).TotalSeconds;
                Logger.Info($"[UI] auto-load LOADED clicked: elapsed {elapsed}s predicted {entry.PredictedSeconds?.ToString() ?? "n/a"}s ({_surfaceName})");
                App.AutoLoad.Complete(entry);
            };
            actions.Children.Add(loaded);

            var discard = new TextBlock
            {
                Text = "DISCARD", FontSize = 9.5, Foreground = Hud.Br("FgDimBrush"), Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
            };
            discard.MouseEnter += (_, _) => discard.TextDecorations = TextDecorations.Underline;
            discard.MouseLeave += (_, _) => discard.TextDecorations = null;
            discard.MouseLeftButtonUp += (_, _) =>
            {
                Logger.Info($"[UI] auto-load discarded from {_surfaceName}");
                App.AutoLoad.Discard(entry);
            };
            actions.Children.Add(discard);

            content.Children.Add(actions);
        }

        return Hud.RowCard(content);
    }
}
