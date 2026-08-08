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
/// unsubscribes and halts the ticker. The 1-second ticker itself runs ONLY while entries exist
/// (started/stopped from <see cref="OnEntries"/>, never stacked - same guard as
/// ExecHangarStatusLine) and calls <see cref="AutoLoadTracker.ExpireStale"/> once per tick with no
/// logging; the tracker itself logs the abandon.
/// </summary>
public sealed class AutoLoadStatusLine : StackPanel
{
    private readonly bool _compact;
    private readonly string _surfaceName;
    private bool _expanded;   // compact only: strip click toggles the entry rows open/closed

    private DispatcherTimer? _ticker;

    // Elapsed TextBlocks repainted every tick without a full Rebuild (row rebuilding would drop
    // hover state and flicker). IsRow marks the entry-row 20px readout, which also carries the
    // over-estimate WarnBrush + glow; the strip's own 11px readout never carries that state.
    private readonly List<(AutoLoadEntry Entry, TextBlock Text, bool IsRow)> _elapsedTexts = new();

    public AutoLoadStatusLine(bool compact, string surfaceName)
    {
        _compact = compact;
        _surfaceName = surfaceName;
        Rebuild();   // paint the initial state; Start() drives it live from here
    }

    /// <summary>Subscribe to tracker changes, repaint immediately, log once. Safe to call once
    /// per surface entry (mirrors ExecHangarStatusLine.Start).</summary>
    public void Start()
    {
        App.AutoLoad.EntriesChanged += OnEntries;
        Rebuild();
        SyncTicker();
        Logger.Info($"[UI] auto-load status line started: {_surfaceName}");
    }

    /// <summary>Unsubscribe and halt the ticker. Safe to call repeatedly / when never started.</summary>
    public void Stop()
    {
        App.AutoLoad.EntriesChanged -= OnEntries;
        StopTicker();
    }

    // AutoLoadTracker fires EntriesChanged from wherever the transaction line was applied (the
    // Game.log tail poll thread, or synchronously from our own LOADED/DISCARD click) - marshal
    // before touching the visual tree, the app-wide idiom for every App.*.Changed subscription.
    private void OnEntries()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
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
        foreach (var (entry, text, isRow) in _elapsedTexts)
        {
            text.Text = AutoLoadStatusText.Elapsed(entry, nowUtc);
            if (isRow) ApplyElapsedStyle(entry, text, nowUtc);
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

        var entries = App.AutoLoad.Entries;
        if (entries.Count == 0)
        {
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
            Text = AutoLoadStatusText.Elapsed(newest, DateTime.UtcNow), FontFamily = Hud.Font("MonoFont"),
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

    // ── One entry row: Hud.RowCard chrome, kind/shop/elapsed head, cargo/est sub, LOADED/DISCARD. ──
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

        var shop = new TextBlock
        {
            Text = entry.ShopName, FontSize = 12.5, Foreground = Hud.Br("FgBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        Grid.SetColumn(shop, 1);
        head.Children.Add(shop);

        var elapsedText = new TextBlock
        {
            Text = AutoLoadStatusText.Elapsed(entry, nowUtc), FontFamily = Hud.Font("MonoFont"), FontSize = 20,
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
        if (AutoLoadStatusText.EstLine(entry, table) is { } est)
            sub.Children.Add(new TextBlock
            {
                Text = est, FontFamily = Hud.Font("MonoFont"), FontSize = 10, Foreground = Hud.Br("FgDimBrush"),
                Margin = new Thickness(10, 0, 0, 0),
            });
        content.Children.Add(sub);

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
