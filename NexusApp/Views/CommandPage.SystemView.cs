using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using NexusApp.Models;
using NexusApp.Services;
using NexusApp.Services.Map;

namespace NexusApp.Views;

/// <summary>
/// Operations system-view layout (redesign 2026-08-10).
///
/// <para>The left column runs live to durable to spatial: three job cards, blueprint coverage, then
/// the system view at the foot. The right column leads with the shard, because which shard you are
/// on is the fact that invalidates everything under it, then the wallet with the money that moved,
/// then session profit with its history.</para>
///
/// <para>Every list on this page comes from <see cref="OperationsPanels"/>, which is pure and
/// tested. This file only turns those rows into controls, so a layout defect can never be a maths
/// defect.</para>
/// </summary>
public sealed partial class CommandPage
{
    private Grid? _jobStrip;

    // The session bars, captured fresh on each Refresh so PlayEntrance grows the current visit's
    // own elements and never a previous build's.
    private readonly List<Border> _profitBars = new();

    // ── the lit panel surface (ATMOSPHERE) ──────────────────────────────────────────────────────
    // A panel is the FLAT card surface, a few points above the near-black page, with an edge one
    // step brighter than the shipped NavBorder:
    //   fill   #0C1219 (Bg2Nav)     border rgba(127,233,224,.16) -> 0x29
    // The mock's gradient fill was tried twice and dropped both times: at panel size on the shipped
    // page it reads as a blue wash, not as light. The depth cue is the edge plus the toned values
    // below, nothing on the surface itself.
    private static readonly Brush LitEdge = Frozen(Color.FromArgb(0x29, 0x7F, 0xE9, 0xE0));

    private static SolidColorBrush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    /// <summary>A panel on the lit surface. Every panel on this page goes through here so the
    /// treatment cannot drift between them.</summary>
    private Grid LitCard(UIElement content, double chamfer = 12, Thickness? padding = null,
                         Brush? border = null)
        => Hud.Panel(content, chamfer: chamfer, brackets: false, bg: Br("Bg2NavBrush"),
                     border: border ?? LitEdge, padding: padding ?? new Thickness(16));

    // ── the page body ───────────────────────────────────────────────────────────────────────────
    // Built ONCE and then refilled in place, unlike every other part of this page. The system view
    // is a WebView2, which is a native window: detaching and reattaching it on every live data tick
    // would reparent that window several times a minute while the player is flying. So the frame is
    // permanent, the map slot inside it is populated exactly once, and Refresh only swaps the
    // panels around it. Everything above the body still rebuilds wholesale, as it always did.
    private Grid? _bodyGrid;
    private readonly ContentControl _jobSlot = new();
    private readonly ContentControl _coverageSlot = new() { Margin = new Thickness(0, 12, 0, 0) };
    private readonly StackPanel _rightCol = new() { Margin = new Thickness(8, 0, 0, 0) };

    /// <summary>Builds the permanent body frame, once. The caller adds the result to the page once
    /// and never removes it.</summary>
    private Grid SystemViewFrame()
    {
        _bodyGrid = new Grid();
        _bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.45, GridUnitType.Star) });
        _bodyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // The map takes the star row, so it grows with the window instead of sitting at a fixed
        // height below the fold. That is the whole point of not scrolling: the system view is
        // always fully on screen, at whatever size the window can spare.
        var left = new Grid { Margin = new Thickness(0, 0, 8, 0) };
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 240 });
        Grid.SetRow(_jobSlot, 0);
        left.Children.Add(_jobSlot);
        Grid.SetRow(_coverageSlot, 1);
        left.Children.Add(_coverageSlot);
        var map = MapSlot();
        map.Margin = new Thickness(0, 12, 0, 0);
        Grid.SetRow(map, 2);
        left.Children.Add(map);
        Grid.SetColumn(left, 0);
        _bodyGrid.Children.Add(left);

        // Only the right column scrolls, and it holds no native window, so it scrolls cleanly.
        var rightScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _rightCol,
        };
        Grid.SetColumn(rightScroll, 1);
        _bodyGrid.Children.Add(rightScroll);
        return _bodyGrid;
    }

    /// <summary>Refills the permanent frame. Everything except the map slot is rebuilt.</summary>
    private void FillSystemViewBody()
    {
        _jobSlot.Content = JobStrip();
        _coverageSlot.Content = CoverageCard();

        _rightCol.Children.Clear();
        _rightCol.Children.Add(ShardPanel());
        var wallet = WalletPanel();
        wallet.Margin = new Thickness(0, 12, 0, 0);
        _rightCol.Children.Add(wallet);
        var profit = ProfitPanel();
        profit.Margin = new Thickness(0, 12, 0, 0);
        _rightCol.Children.Add(profit);

        // The scene is initialized once on Ready; after that only a location change repaints it,
        // and PostLocator's own token guard is what keeps a data tick from resending the init.
        PostLocator(force: false);
    }

    // ── three job cards: the live work, one line each ────────────────────────────────────────────
    // The refinery queue is not deleted by this redesign, it is demoted. The table lives in its own
    // tab and what survives here is a count, a state, and a way in.
    private UIElement JobStrip()
    {
        // Rebuilt on every Refresh (tab-open AND live data ticks), so the countdown cells are reset
        // here. Holding the previous build's TextBlocks would tick controls that are no longer on
        // screen, the same trap the refinery table's own ticker documented (app review F11).
        _liveCells.Clear();

        var grid = new Grid();
        for (int i = 0; i < 3; i++) grid.ColumnDefinitions.Add(new ColumnDefinition());

        var cards = new[] { RefineryCard(), AutoLoadCard(), HangarCard() };

        for (int i = 0; i < cards.Length; i++)
        {
            cards[i].Margin = new Thickness(i == 0 ? 0 : 5, 0, i == cards.Length - 1 ? 0 : 5, 0);
            Grid.SetColumn(cards[i], i);
            grid.Children.Add(cards[i]);
        }
        _jobStrip = grid;
        StartLiveTicker();
        return grid;
    }

    private FrameworkElement RefineryCard()
    {
        var orders = App.Data.GetWorkOrders();
        int ready = orders.Count(o => o.Status == WorkOrderStatus.ReadyToCollect);
        int refining = orders.Count(o => o.Status != WorkOrderStatus.Complete
                                      && o.Status != WorkOrderStatus.ReadyToCollect);

        // Each job card owns one tone for its whole life, the mock's rule: gold is the refinery,
        // amber is the load, green is the hangar. Toning by state was tried first and left the
        // idle page all white, which is the monotone this round exists to fix.
        return JobCard(IconRefinery(), "REFINERY QUEUE",
            ready > 0 ? $"{ready} READY" : refining > 0 ? $"{refining} REFINING" : "CLEAR",
            ready > 0 ? $"{refining} still refining"
                      : refining > 0 ? "nothing ready to collect yet"
                                     : "no active work orders",
            "GoldBrush", "workorders").Panel;
    }

    // Auto load and the hangar are the only cards here driven by the clock rather than by data, so
    // they are the only two that register live cells with the ticker below.
    private FrameworkElement AutoLoadCard()
    {
        var entries = App.AutoLoad.Entries;
        var card = JobCard(IconCargo(), "AUTO LOAD", AutoLoadValue(entries, DateTime.UtcNow),
            AutoLoadSub(entries, DateTime.UtcNow), "AccentBrush", "hauling");
        _liveCells.Add((card.Value, card.Sub,
            () => AutoLoadValue(App.AutoLoad.Entries, DateTime.UtcNow),
            () => AutoLoadSub(App.AutoLoad.Entries, DateTime.UtcNow)));
        return card.Panel;
    }

    /// <summary>The auto-load headline: time left on the load that finishes soonest. Held at
    /// "00:00" rather than counting past zero, matching the shipped countdown rule - the game logs
    /// no completion event, so a load that overruns its prediction is not proof it ended.</summary>
    internal static string AutoLoadValue(IReadOnlyList<AutoLoadEntry> entries, DateTime nowUtc)
    {
        if (entries.Count == 0) return "IDLE";
        if (SoonestCompletion(entries, nowUtc) is { } done)
        {
            var left = done - nowUtc;
            if (left < TimeSpan.Zero) left = TimeSpan.Zero;
            return $"{(int)left.TotalMinutes:00}:{left.Seconds:00}";
        }
        // No countdown left to show. That is two different facts, and they must not share a word:
        // a load with no prediction is still running and simply cannot be timed, while a load whose
        // prediction has passed is as finished as this app can know.
        return entries.Any(e => AutoLoadBadge.CompletesAt(e) is null) ? "RUNNING" : "DONE";
    }

    internal static string AutoLoadSub(IReadOnlyList<AutoLoadEntry> entries, DateTime nowUtc)
    {
        if (entries.Count == 0) return "no load running";

        int running = 0, untimed = 0, finished = 0;
        foreach (var e in entries)
        {
            if (AutoLoadBadge.CompletesAt(e) is not { } done) untimed++;
            else if (done > nowUtc) running++;
            else finished++;
        }

        if (running == 0 && untimed == 0)
            return finished == 1 ? "load finished, not cleared" : $"{finished} loads finished";

        var parts = new List<string>(3);
        if (running > 0) parts.Add($"{running} running");
        if (untimed > 0) parts.Add($"{untimed} with no estimate");
        if (finished > 0) parts.Add($"{finished} finished");
        return string.Join(", ", parts);
    }

    private static DateTime? SoonestCompletion(IReadOnlyList<AutoLoadEntry> entries, DateTime nowUtc)
    {
        DateTime? soonest = null;
        foreach (var e in entries)
        {
            if (AutoLoadBadge.CompletesAt(e) is not { } done || done <= nowUtc) continue;
            if (soonest is null || done < soonest) soonest = done;
        }
        return soonest;
    }

    private FrameworkElement HangarCard()
    {
        var card = JobCard(IconClock(), "EXEC HANGAR", HangarValue(DateTime.UtcNow),
            HangarSub(DateTime.UtcNow), "OkBrush", "guides");
        _liveCells.Add((card.Value, card.Sub,
            () => HangarValue(DateTime.UtcNow), () => HangarSub(DateTime.UtcNow)));
        return card.Panel;
    }

    private static string HangarValue(DateTime nowUtc)
    {
        var s = ExecHangarCycle.At(nowUtc, App.Settings.Current.ExecHangarAnchorOverrideUtc);
        return s.IsOpen ? "OPEN" : "CLOSED";
    }

    private static string HangarSub(DateTime nowUtc)
    {
        var s = ExecHangarCycle.At(nowUtc, App.Settings.Current.ExecHangarAnchorOverrideUtc);
        return (s.IsOpen ? "closes in " : "opens in ") + ExecHangarCycle.FormatCountdown(s.TimeToTransition);
    }

    // Returns the two volatile TextBlocks alongside the panel rather than letting the caller dig
    // them back out of the visual tree: Hud.Panel wraps its content in a ContentControl, so a tree
    // walk would find nothing and the countdown would silently never tick.
    private (FrameworkElement Panel, TextBlock Value, TextBlock Sub) JobCard(
        UIElement icon, string key, string value, string sub, string valueBrush, string nav)
    {
        var sp = new StackPanel();
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = new StackPanel { Orientation = Orientation.Horizontal };
        label.Children.Add(icon);
        label.Children.Add(new TextBlock
        {
            Text = key, FontFamily = Ui, FontSize = 10, FontWeight = FontWeights.Bold,
            Foreground = Br("FgDimBrush"), VerticalAlignment = VerticalAlignment.Center,
        });
        head.Children.Add(label);
        var chev = Chevron();
        Grid.SetColumn(chev, 1);
        head.Children.Add(chev);
        sp.Children.Add(head);

        var valueCell = new TextBlock
        {
            Text = value, FontFamily = Disp, FontSize = 20, FontWeight = FontWeights.Bold,
            Foreground = Br(valueBrush), Margin = new Thickness(0, 6, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        sp.Children.Add(valueCell);
        var subCell = new TextBlock
        {
            Text = sub, FontFamily = Ui, FontSize = 11, Foreground = Br("FgDimBrush"),
            Margin = new Thickness(0, 5, 0, 0), TextWrapping = TextWrapping.Wrap, MinHeight = 28,
        };
        sp.Children.Add(subCell);

        var panel = LitCard(sp, chamfer: 10, padding: new Thickness(14, 12, 14, 12));
        WireNav(panel, $"Operations: {key} card", nav);
        return (panel, valueCell, subCell);
    }

    // ── the two live countdown cells ─────────────────────────────────────────────────────────────
    // Deliberately NOT a page Refresh on a timer. Refresh rebuilds the whole dashboard's visual
    // tree, and at one hertz that would be indefensible for two strings. This rewrites only the
    // cells that are actually volatile, and only when the text really changed, so the seconds that
    // produce no change touch no dependency property and invalidate no layout.
    private readonly List<(TextBlock Value, TextBlock Sub, Func<string> ReadValue, Func<string> ReadSub)> _liveCells = new();

    private void StartLiveTicker()
    {
        if (_liveCells.Count == 0) { _liveTicker?.Stop(); return; }
        _liveTicker ??= MakeLiveTicker();
        if (IsVisible) _liveTicker.Start();
    }

    private void TickLiveCells()
    {
        foreach (var (value, sub, readValue, readSub) in _liveCells)
        {
            var v = readValue();
            if (!string.Equals(value.Text, v, StringComparison.Ordinal)) value.Text = v;
            var s = readSub();
            if (!string.Equals(sub.Text, s, StringComparison.Ordinal)) sub.Text = s;
        }
    }

    // ── blueprint coverage, laid out sideways because it has the full column ─────────────────────
    // The single-owner warning that used to be its own amber card is folded in here: it is a
    // sentence about the same number, and two cards saying one thing was the old layout's fault.
    private FrameworkElement CoverageCard()
    {
        var catalog = App.Data.GetAllBlueprints();
        int total = catalog.Count;
        var counts = App.Network.OwnerCounts();
        int covered = 0, single = 0;
        foreach (var b in catalog)
        {
            int owners = (counts.TryGetValue(b.Name, out var c) ? c : 0)
                       + (App.Settings.IsBlueprintOwned(b.Name) ? 1 : 0);
            if (owners > 0) covered++;
            if (owners == 1) single++;
        }
        int pct = UiHelpers.PctOf(covered, total);
        int members = App.Network.MemberCount;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 20, 0) };
        var lbl = new StackPanel { Orientation = Orientation.Horizontal };
        lbl.Children.Add(IconNetwork());
        lbl.Children.Add(new TextBlock
        {
            Text = "NETWORK COVERAGE", FontFamily = Ui, FontSize = 10, FontWeight = FontWeights.Bold,
            Foreground = Br("FgDimBrush"), VerticalAlignment = VerticalAlignment.Center,
        });
        left.Children.Add(lbl);
        var big = new TextBlock
        {
            FontFamily = Disp, FontSize = 30, FontWeight = FontWeights.Bold,
            Foreground = Br("CyanBrush"), Margin = new Thickness(0, 5, 0, 0),
        };
        var pctRun = new Run(pct.ToString("N0"));
        big.Inlines.Add(pctRun);
        big.Inlines.Add(new Run(" %")
        {
            FontFamily = Ui, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Br("FgDimBrush"),
        });
        // The one number on this page a count-up can honestly animate. The job strip's values are
        // states ("2 READY", "OPEN"), not quantities, so they roll in with the cascade alone.
        _kpiCountTargets.Add((pctRun, pct, ""));
        left.Children.Add(big);
        grid.Children.Add(left);

        var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var midHead = new Grid();
        midHead.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        midHead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        midHead.Children.Add(new TextBlock
        {
            Text = $"{covered:N0} of {total:N0} blueprints owned by you or the network",
            FontFamily = Ui, FontSize = 11, Foreground = Br("FgDimBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 10, 0),
        });
        var memberCount = new TextBlock
        {
            Text = $"{members} member{(members == 1 ? "" : "s")}",
            FontFamily = Ui, FontSize = 11, Foreground = Br("FgDimBrush"),
        };
        Grid.SetColumn(memberCount, 1);
        midHead.Children.Add(memberCount);
        mid.Children.Add(midHead);

        var bar = Hud.StateBar(pct / 100.0, Hud.BarState.Cyan, height: 9);
        if (bar is FrameworkElement barEl) barEl.Margin = new Thickness(0, 7, 0, 0);
        // The lit page brightens the fill's glow from the shared bar's 8 at .55 to the mock's 12
        // at full alpha. Pattern-matched into StateBar's shape so a change there degrades this
        // page to the shipped glow instead of crashing the landing tab.
        if (bar is Grid barGrid && barGrid.Children.Count > 1 && barGrid.Children[1] is Grid fillHost
            && fillHost.Children.Count > 0 && fillHost.Children[0] is Border coverFill
            && coverFill.Effect is DropShadowEffect coverGlow)
        {
            coverGlow.BlurRadius = 12;
            coverGlow.Opacity = 1;
        }
        mid.Children.Add(bar);

        // A network with no members has no single-owner risk to report: every blueprint you own is
        // owned by exactly you, and calling that a risk would nag the solo player forever.
        mid.Children.Add(new TextBlock
        {
            Text = members == 0
                ? "No network members yet. Add one to share blueprint coverage."
                : single == 0
                    ? "No blueprint depends on a single owner."
                    : $"{single} blueprint{(single == 1 ? "" : "s")} sit with one owner. Lose that member and they leave the network.",
            FontFamily = Ui, FontSize = 11,
            Foreground = members > 0 && single > 0 ? Br("AccentBrush") : Br("FgDimBrush"),
            Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap,
        });
        Grid.SetColumn(mid, 1);
        grid.Children.Add(mid);

        var chev = Chevron();
        chev.VerticalAlignment = VerticalAlignment.Center;
        chev.Margin = new Thickness(16, 0, 0, 0);
        Grid.SetColumn(chev, 2);
        grid.Children.Add(chev);

        var panel = LitCard(grid, chamfer: 12, padding: new Thickness(16, 14, 16, 15));
        WireNav(panel, "Operations: network coverage card", "network");
        return panel;
    }

    // ── shard, current plus history ──────────────────────────────────────────────────────────────
    private FrameworkElement ShardPanel()
    {
        var sp = new StackPanel();
        var head = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var hl = new StackPanel { Orientation = Orientation.Horizontal };
        hl.Children.Add(IconLayers());
        hl.Children.Add(new TextBlock
        {
            Text = "SHARD", FontFamily = Ui, FontSize = 10, FontWeight = FontWeights.Bold,
            Foreground = Br("FgDimBrush"), VerticalAlignment = VerticalAlignment.Center,
        });
        head.Children.Add(hl);
        bool onShard = App.Shards.OnShard;
        var chip = Hud.Chip(onShard ? Color.FromRgb(0x66, 0xE6, 0xA6) : Color.FromRgb(0x86, 0x93, 0xA0),
                            onShard ? "CONNECTED" : "OFF SHARD");
        Grid.SetColumn(chip, 1);
        head.Children.Add(chip);
        sp.Children.Add(head);

        var rows = OperationsPanels.ShardRows(App.Shards.All, onShard, DateTime.UtcNow);
        if (rows.Count == 0)
        {
            sp.Children.Add(Empty("No shard seen yet. Start Star Citizen and Nexus reads it from the log."));
            return LitCard(sp, chamfer: 14, padding: new Thickness(18));
        }

        // Named exactly as the overlay's STATS panel names it, down to the spacing: one shard, one
        // name, wherever the player reads it. Cyan in every state; the chip above already carries
        // live versus not, and a white line here was one of the whites that made the page monotone.
        var cur = rows[0];
        sp.Children.Add(new TextBlock
        {
            Text = $"{cur.Region}  .  Shard {cur.Instance}", FontFamily = Ui, FontSize = 17,
            FontWeight = FontWeights.SemiBold, Foreground = Br("CyanBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        sp.Children.Add(new TextBlock
        {
            Text = cur.ShardId, FontFamily = Mono, FontSize = 10, Foreground = Br("FgDimBrush"),
            Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis,
        });
        sp.Children.Add(new TextBlock
        {
            Text = cur.Duration is { } d
                ? cur.Live ? $"Joined {d} ago on {App.GameLogFeed.ActiveChannel}."
                           : $"Lasted {d}. You are not on a shard now."
                : "Nexus cannot tell how long this shard lasted.",
            FontFamily = Ui, FontSize = 11, Foreground = Br("FgDimBrush"),
            Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap,
        });

        if (rows.Count > 1)
        {
            sp.Children.Add(new TextBlock
            {
                Text = "RECENT SHARDS", FontFamily = Ui, FontSize = 9, FontWeight = FontWeights.Bold,
                Foreground = Br("FgDimBrush"), Margin = new Thickness(0, 12, 0, 4),
            });
            for (int i = 1; i < rows.Count; i++) sp.Children.Add(ShardRowLine(rows[i]));
        }
        return LitCard(sp, chamfer: 14, padding: new Thickness(18));
    }

    private UIElement ShardRowLine(ShardRow r)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(66) });

        g.Children.Add(new Ellipse
        {
            Width = 6, Height = 6, Fill = Br(r.Live ? "OkBrush" : "FgDimBrush"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
        });
        var id = new TextBlock
        {
            Text = $"{r.Region} . {r.Instance}", FontFamily = Ui, FontSize = 11.5,
            Foreground = Br(r.Live ? "FgBrush" : "FgDimBrush"),
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(id, 1);
        g.Children.Add(id);
        // A shard with no measurable length prints nothing rather than a zero: "0m" would claim the
        // player was there for no time, which is a different and false statement.
        var dur = new TextBlock
        {
            Text = r.Duration ?? "", FontFamily = Mono, FontSize = 10.5, Foreground = Br("FgDimBrush"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0),
        };
        Grid.SetColumn(dur, 2);
        g.Children.Add(dur);
        var when = new TextBlock
        {
            Text = r.When, FontFamily = Mono, FontSize = 10.5, Foreground = Br("FgDimBrush"),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(when, 3);
        g.Children.Add(when);

        return new Border
        {
            Child = g, BorderBrush = Br("NavBorderBrush"), BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 6, 0, 6),
        };
    }

    // ── wallet plus the money that moved ─────────────────────────────────────────────────────────
    private FrameworkElement WalletPanel()
    {
        var sp = new StackPanel();
        sp.Children.Add(PanelHead("WALLET", "Open trade", "trade", IconWallet()));

        var w = App.Wallet;
        if (w == null)
        {
            sp.Children.Add(Empty("Wallet tracking is not running."));
            return LitCard(sp, chamfer: 14, padding: new Thickness(18));
        }

        var state = WalletDisplay.State(w.HasAnchor, w.Estimate, w.AnchorUtc,
                                        DateTime.UtcNow, App.GameLogFeed.IsSessionLive);
        var value = new TextBlock
        {
            FontFamily = Mono, FontSize = 26, FontWeight = FontWeights.Bold,
            Foreground = state switch
            {
                WalletUiState.Current => Br("GoldBrush"),
                WalletUiState.Aging => Br("AccentBrush"),
                WalletUiState.Impossible => Br("DangerBrush"),
                _ => Br("FgDimBrush"),
            },
        };
        var balanceRun = new Run(w.Estimate is { } est ? ProfitDisplay.Format(est) : ProfitDisplay.NoneValue);
        value.Inlines.Add(balanceRun);
        // Only a real balance rolls up. The "- - -" placeholder is not a number and must never be
        // animated toward one, or an unknown wallet would read as a wallet counting to zero.
        if (w.Estimate is { } roll) _kpiCountTargets.Add((balanceRun, roll, ""));
        if (w.HasAnchor)
            value.Inlines.Add(new Run("  aUEC")
            {
                FontFamily = Ui, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Br("FgDimBrush"),
            });
        sp.Children.Add(value);
        sp.Children.Add(new TextBlock
        {
            Text = w.HasAnchor ? WalletDisplay.Provenance(w.AnchorSource, w.AnchorUtc, DateTime.UtcNow)
                               : WalletDisplay.CardHint,
            FontFamily = Ui, FontSize = 11, Foreground = Br("FgDimBrush"),
            Margin = new Thickness(0, 5, 0, 0), TextWrapping = TextWrapping.Wrap,
        });

        // The fold merges settled kiosk transactions with the wallet movements nothing explained,
        // so an unattributed row is visible rather than quietly missing.
        var rows = OperationsPanels.TransactionRows(
            App.Profit?.Ledger.Transactions ?? Array.Empty<CommodityTransaction>(),
            w.SessionUntracked,
            guid => CommodityNameCatalog.Instance.Resolve(guid) ?? "",
            DateTime.UtcNow,
            max: 6,
            resolveWhere: t => ProfitDisplay.WhereText(t.PlaceLabel, t.PlaceIsArea, t.ShopName));

        sp.Children.Add(new Border
        {
            BorderBrush = Br("NavBorderBrush"), BorderThickness = new Thickness(0, 1, 0, 0),
            Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(0, 10, 0, 0),
            Child = TransactionList(rows),
        });
        return LitCard(sp, chamfer: 14, padding: new Thickness(18));
    }

    private UIElement TransactionList(IReadOnlyList<TxRow> rows)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock
        {
            Text = "MONEY MOVEMENTS", FontFamily = Ui, FontSize = 9, FontWeight = FontWeights.Bold,
            Foreground = Br("FgDimBrush"), Margin = new Thickness(0, 0, 0, 2),
        });

        if (rows.Count == 0)
        {
            sp.Children.Add(Empty("Nothing bought or sold this session yet."));
            return sp;
        }

        foreach (var r in rows)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
            info.Children.Add(new TextBlock
            {
                Text = r.What, FontFamily = Ui, FontSize = 12,
                Foreground = Br(r.Kind == TxKind.Unattributed ? "FgDimBrush" : "FgBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            info.Children.Add(new TextBlock
            {
                Text = $"{r.Where}  ·  {r.Ago}", FontFamily = Ui, FontSize = 10.5,
                Foreground = Br("FgDimBrush"), Margin = new Thickness(0, 1, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            g.Children.Add(info);

            var amount = new TextBlock
            {
                Text = ProfitDisplay.Signed(r.Amount) + " aUEC",
                FontFamily = Mono, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Foreground = r.Kind switch
                {
                    TxKind.Sell => Br("OkBrush"),
                    TxKind.Buy => Br("AccentBrush"),
                    _ => Br("FgDimBrush"),
                },
            };
            Grid.SetColumn(amount, 1);
            g.Children.Add(amount);

            sp.Children.Add(new Border
            {
                Child = g, BorderBrush = Br("NavBorderBrush"), BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(0, 7, 0, 7),
            });
        }
        return sp;
    }

    // ── session profit plus its history ──────────────────────────────────────────────────────────
    private FrameworkElement ProfitPanel()
    {
        var sp = new StackPanel();
        sp.Children.Add(PanelHead("SESSION PROFIT", "Open trade", "trade", IconTrend()));

        if (App.Profit == null)
        {
            sp.Children.Add(Empty("Profit tracking is not running."));
            return LitCard(sp, chamfer: 14, padding: new Thickness(18));
        }

        var ledger = App.Profit.Ledger;
        var state = ProfitDisplay.State(ledger.UnvoidedCount, ledger.Net, App.GameLogFeed.IsSessionLive);
        // Toned by sign, not by liveness: a closed session's profit is still a profit, and the sub
        // line below already says the session is over. Zero has no sign, so empty and break-even
        // both read dim - the display spec's rule that a flat net must never read as money made.
        var value = new TextBlock
        {
            FontFamily = Mono, FontSize = 24, FontWeight = FontWeights.Bold,
            Foreground = ledger.UnvoidedCount == 0 || ledger.Net == 0 ? Br("FgDimBrush")
                       : ledger.Net < 0 ? Br("DangerBrush") : Br("OkBrush"),
        };
        var netRun = new Run(ProfitDisplay.ChipValue(ledger.UnvoidedCount, ledger.Net));
        value.Inlines.Add(netRun);
        if (ledger.UnvoidedCount > 0) _kpiCountTargets.Add((netRun, ledger.Net, ""));
        if (ledger.UnvoidedCount > 0)
            value.Inlines.Add(new Run("  aUEC")
            {
                FontFamily = Ui, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Br("FgDimBrush"),
            });
        sp.Children.Add(value);

        sp.Children.Add(new TextBlock
        {
            Text = state == ProfitState.Offline
                ? "Star Citizen is not running, so this session is closed."
                : $"{ledger.UnvoidedCount} settlement{(ledger.UnvoidedCount == 1 ? "" : "s")} this session.",
            FontFamily = Ui, FontSize = 11, Foreground = Br("FgDimBrush"), Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        });

        sp.Children.Add(new Border
        {
            BorderBrush = Br("NavBorderBrush"), BorderThickness = new Thickness(0, 1, 0, 0),
            Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(0, 10, 0, 0),
            Child = ProfitBarChart(ProfitBarsForChannel()),
        });
        return LitCard(sp, chamfer: 14, padding: new Thickness(18));
    }

    // The bars come from the ACTIVE channel only: a PTU session must never move the LIVE trend,
    // which is the same rule the retained history itself is partitioned by.
    private IReadOnlyList<ProfitBar> ProfitBarsForChannel()
    {
        if (App.Profit == null) return Array.Empty<ProfitBar>();
        var ch = App.Profit.History.Channels.Find(c => c.Channel == App.GameLogFeed.ActiveChannel);
        if (ch == null) return Array.Empty<ProfitBar>();
        // The live session is not written to history until it closes, so no retained entry can
        // carry its key. Passing null says exactly that rather than mislabelling a finished bar.
        return OperationsPanels.ProfitBars(ch.Entries, currentKey: null, count: 7);
    }

    private UIElement ProfitBarChart(IReadOnlyList<ProfitBar> bars)
    {
        var sp = new StackPanel();
        var head = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(new TextBlock
        {
            Text = "RECENT SESSIONS", FontFamily = Ui, FontSize = 9, FontWeight = FontWeights.Bold,
            Foreground = Br("FgDimBrush"),
        });
        sp.Children.Add(head);

        if (bars.Count == 0)
        {
            _profitBars.Clear();
            sp.Children.Add(Empty("No finished sessions yet. The first one lands here when you close one."));
            return sp;
        }

        _profitBars.Clear();
        long peak = Math.Max(1, bars.Max(b => Math.Abs(b.Net)));
        var best = new TextBlock
        {
            Text = $"best {ProfitDisplay.Compact(peak)} aUEC", FontFamily = Ui, FontSize = 10,
            Foreground = Br("FgDimBrush"),
        };
        Grid.SetColumn(best, 1);
        head.Children.Add(best);

        var chart = new Grid { Height = 56 };
        var labels = new Grid { Margin = new Thickness(0, 5, 0, 0) };
        for (int i = 0; i < bars.Count; i++)
        {
            chart.ColumnDefinitions.Add(new ColumnDefinition());
            labels.ColumnDefinitions.Add(new ColumnDefinition());
        }

        for (int i = 0; i < bars.Count; i++)
        {
            var b = bars[i];
            // A losing or break-even session still gets a visible stub, never a gap: a missing bar
            // would silently under-report how many sessions were actually played. Break-even is
            // dim, not green - a session that made nothing must not read as one that made money.
            double h = Math.Max(3, Math.Abs(b.Net) / (double)peak * 54);
            var barColor = Hud.Col(b.Net == 0 ? "FgDimBrush" : b.Net < 0 ? "DangerBrush" : "OkBrush");
            // The mock's exact glow: 10px blur, the bar's own color at full alpha. The .62 element
            // opacity on past bars dims bar and glow together, which is also the mock's behavior.
            var glow = new DropShadowEffect { Color = barColor, BlurRadius = 10, ShadowDepth = 0 };
            glow.Freeze();
            var bar = new Border
            {
                Height = h, VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(3, 0, 3, 0), CornerRadius = new CornerRadius(2, 2, 0, 0),
                Background = Frozen(barColor),
                Opacity = b.Current ? 1 : 0.62,
                ToolTip = $"{b.Label}: {ProfitDisplay.Signed(b.Net)} aUEC",
                Effect = glow,
            };
            Grid.SetColumn(bar, i);
            chart.Children.Add(bar);
            _profitBars.Add(bar);

            var lbl = new TextBlock
            {
                Text = b.Label, FontFamily = Mono, FontSize = 9,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = b.Current ? Br("AccentBrush") : Br("FgDimBrush"),
            };
            Grid.SetColumn(lbl, i);
            labels.Children.Add(lbl);
        }
        sp.Children.Add(chart);
        sp.Children.Add(labels);
        return sp;
    }

    // ── the system view ──────────────────────────────────────────────────────────────────────────
    // A WebView2 is expensive and is a native window, so it is created ONCE, on the first build of
    // this page's body, and reparented across rebuilds rather than recreated. Refresh() rebuilds
    // the whole visual tree on every data tick; recreating a browser on each of those would be
    // indefensible. The locator's own controls live page-side for the same reason a WPF overlay
    // cannot work here: the WebView owns its rectangle and nothing WPF draws can sit on top of it.
    private MapWebView? _mapView;
    private bool _mapReady;
    private string? _mapPostedFor;   // the location token the scene was last initialized for

    // Called exactly once, from the body's one-time build above.
    private FrameworkElement MapSlot()
    {
        // The hero, and the only panel allowed to glow. The halo is two hairline rings rather than a
        // blur: it reads the same at this scale and costs nothing, which matters because the app
        // runs software-rendered by default. The 1px padding is load-bearing - the WebView is a
        // native window and would paint straight over a ring drawn at the same bounds.
        var host = new Grid { Background = Br("BgBrush") };
        host.Children.Add(new Border
        {
            IsHitTestVisible = false,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x1F, 0x7F, 0xE9, 0xE0)),
            BorderThickness = new Thickness(1), Margin = new Thickness(-3),
        });
        _mapView = new MapWebView();
        _mapView.Ready += () =>
        {
            _mapReady = true;
            Logger.Info("[UI] Operations system view: scene ready");
            PostLocator(force: true);
        };
        // The locator's only outbound action: hand the player to the real map tab.
        _mapView.OpenMapRequested += () => Dispatcher.BeginInvoke(() => _navigate("map"));
        host.Children.Add(new Border
        {
            BorderBrush = Br("CyanStrongBrush"), BorderThickness = new Thickness(1),
            Padding = new Thickness(1), Child = _mapView,
        });
        return host;
    }

    /// <summary>Releases the system view's WebView2 before a portable self-swap, so it stops
    /// holding file handles under the app directory. Same contract as the planner, grid studio and
    /// map pages; MainWindow calls all four.</summary>
    internal void ShutdownWebViewForUpdate()
    {
        _mapView?.ShutdownForUpdate();
        _mapReady = false;
        _mapPostedFor = null;
    }

    /// <summary>Sends the locator init: the player's system, the player marker, and every planner
    /// affordance off. Skipped when nothing about the location changed, because Refresh runs on
    /// every data tick and reinitializing the scene rebuilds its whole object graph.</summary>
    private void PostLocator(bool force)
    {
        if (_mapView == null || !_mapReady) return;

        var resolved = App.Player?.Current;
        var name = LocatorName();
        var system = resolved?.System ?? App.Settings.Current.MapSystem ?? "Stanton";
        // The token covers everything the card renders, not just the marker: the label can change
        // (a new jurisdiction) while the resolved object stays null, and that still has to repaint.
        var token = (resolved?.Id.ToString() ?? "none") + "|" + system + "|" + (name ?? "");
        if (!force && token == _mapPostedFor) return;
        _mapPostedFor = token;

        _mapView.PostJson(MapSceneBuilder.BuildInit(
            App.Map, system, MapLayerPins.Empty,
            tradeOn: false, guidesOn: false, miningOn: false, hangarOn: false, asteroidsOn: false,
            selection: null, draft: Array.Empty<int>(), planner: Array.Empty<int>(),
            reduced: Motion.Reduced, player: resolved?.Id, haulsOn: false, ordersOn: false,
            lite: true, playerNote: LocatorNote(), playerName: name));
        Logger.Info($"[UI] Operations system view: locator init {system}, " +
                    $"place {name ?? "unknown"}, marker {(resolved != null ? "placed" : "none")}");
    }

    /// <summary>The headline under YOU ARE HERE: the name the log gave, even when the catalog
    /// cannot place it. A jurisdiction reading is qualified rather than hidden, because a coarse
    /// answer is still the freshest fact available.</summary>
    internal static string? LocatorName()
    {
        var p = App.Player;
        if (p?.Label is not { Length: > 0 } label) return null;
        return p.LabelIsJurisdiction ? $"{label} space" : label;
    }

    /// <summary>The line under the headline. Dated rather than implied live: the location timeline
    /// is boundary-driven and sparse, so a reading can be hours old and must say so.</summary>
    internal static string? LocatorNote()
    {
        var p = App.Player;
        if (p?.Label is not { Length: > 0 }) return null;
        var system = p.System;
        var seen = p.SeenUtc is { } utc ? "Seen " + Ago(utc) + "." : null;
        return string.IsNullOrEmpty(system) ? seen : seen is null ? system : $"{system}. {seen}";
    }

    // ── shared bits ──────────────────────────────────────────────────────────────────────────────
    private FrameworkElement Chevron() => new Viewbox
    {
        Width = 12, Height = 12, HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Top, Opacity = 0.55,
        Child = new Path
        {
            Data = Geometry.Parse("M6,3 L11,8 L6,13"), Stroke = Br("FgDimBrush"), StrokeThickness = 1.8,
            Fill = Brushes.Transparent, Width = 16, Height = 16, Stretch = Stretch.Uniform,
        },
    };

    // One link grammar for the whole page: the card tints on hover, the click logs and navigates.
    // Panels are not Buttons, so the trio is wired by hand - same idiom the KPI cards used.
    private void WireNav(Grid panel, string logLabel, string nav)
    {
        var frame = (Path)panel.Children[0];
        var restFill = frame.Fill;
        panel.Cursor = System.Windows.Input.Cursors.Hand;
        panel.MouseEnter += (_, _) => frame.Fill = Br("HighlightBrush");
        panel.MouseLeave += (_, _) => frame.Fill = restFill;
        panel.MouseLeftButtonUp += (_, _) =>
        {
            InteractionLog.Nav(logLabel, panel);
            _navigate(nav);
        };
    }

    private UIElement IconClock() => Icon("M8,1 A7,7 0 1,0 8,15 A7,7 0 1,0 8,1 M8,4 L8,8 L11,9.5");
    private UIElement IconLayers() => Icon("M8,2 L1,5.5 L8,9 L15,5.5 Z M1,10 L8,13.5 L15,10");
}
