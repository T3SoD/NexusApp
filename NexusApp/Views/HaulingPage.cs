using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using NexusApp.Models;
using NexusApp.Services;
using NexusApp.Services.Map;

namespace NexusApp.Views;

/// <summary>
/// The Cargo Hauling page (code-built, like NetworkPage), rebuilt onto the shared MOBIGLAS
/// Hud primitives. Reads App.Hauls and renders four sections: accepted trade routes (Task C,
/// spec 2026-08-09), a two-up grid of active-haul cards (per-leg load/drop rows), a load/drop
/// consolidation TABLE, and a finished-hauls panel with outcome chips. Rebuilds itself whenever
/// the tracker raises Changed.
/// </summary>
public sealed class HaulingPage : UserControl
{
    private readonly StackPanel _body = new();
    private Button? _clearBtn;   // built once in the header; visibility toggled by Refresh()
    private AutoLoadStatusLine? _autoLoadPanel;   // moved off Trade 2026-08-09; app-lifetime, started once
    private MoneyPanel? _moneyPanel;   // moved off Trade 2026-08-09 (task B4); repainted via Refresh()

    // Row-insert highlight identity: haul id + leg/objective key, tracked across rebuilds so the
    // one-shot flash only ever plays once per row, on the Refresh() where it first appears.
    private readonly HashSet<string> _seenRowKeys = new();
    private bool _hasSeededOnce;   // set true after the very first Refresh(); that call never highlights

    // Accepted-route Delete route buttons arm a DispatcherTimer per card (Task C, spec 2026-08-09
    // section 3: "There is no cursor-poll tick on this page" - unlike the overlay's shared
    // PollArmedConfirms). Tracked here so RenderAcceptedRoutes can stop every one of them before
    // the cards they belong to are discarded on the next rebuild, rather than leaving a live timer
    // ticking against a button nobody can see any more.
    private readonly List<DispatcherTimer> _deleteConfirmTimers = new();

    // Chip palette shared with the rest of the HUD (matches Hud.StateBar / Hud.StatusChip tints).
    private static readonly Color _amber = Color.FromRgb(0xFF, 0xB2, 0x3E);
    private static readonly Color _green = Color.FromRgb(0x66, 0xE6, 0xA6);
    private static readonly Color _armedRed = Color.FromRgb(0xE5, 0x48, 0x4D);   // mock value, matches OverlayWindow.ArmedConfirmBrush
    private Color Cyan => Hud.Col("CyanBrush");

    private readonly Dictionary<string, Brush> _brushCache = new();
    private Brush Br(string key) => _brushCache.TryGetValue(key, out var b) ? b : (_brushCache[key] = (Brush)Application.Current.FindResource(key));
    private FontFamily? _head, _mono, _disp;
    private FontFamily Head => _head ??= (FontFamily)Application.Current.FindResource("HeadFont");
    private FontFamily Mono => _mono ??= (FontFamily)Application.Current.FindResource("MonoFont");
    private FontFamily Disp => _disp ??= (FontFamily)Application.Current.FindResource("DisplayFont");

    public HaulingPage()
    {
        Build();
        Refresh();
        InteractionLog.Nav("Cargo Hauling");
        App.Hauls.Changed += () => Dispatcher.Invoke(Refresh);
    }

    /// <summary>Rebuild every section from the current App.Hauls state.</summary>
    public void Refresh()
    {
        // Money surfaces (task B4): MoneyPanel gates its own live subscriptions on IsVisible, so a
        // settlement or a capture that lands while the user is elsewhere is missed on purpose - this
        // unconditional call is what catches it on re-entry, the same contract TradePage.Refresh()
        // gave these surfaces before the move. MainWindow.InitHaulingPage calls this page's own
        // Refresh() on every visit, so no extra wiring is needed there.
        _moneyPanel?.Refresh();

        _body.Children.Clear();

        // The very first Refresh() (page construction) only seeds row identity - no light show on
        // page open. Later Refresh() calls highlight newly inserted rows, but only while this page
        // is actually the one visible - background rebuilds while parked on another module never flash.
        bool allowHighlight = _hasSeededOnce && IsVisible && !Motion.Reduced;

        // Clear-all lives in the header (built once); only show it when there's something to clear.
        if (_clearBtn is not null)
            _clearBtn.Visibility = App.Hauls.AllHauls.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // Accepted trade routes (Task C, spec 2026-08-09 section 3): rendered ABOVE Active hauls,
        // and unconditionally - unlike the hauls sections below, an empty accepted-route list is
        // not folded into the page's single empty state, since accepting a route and accepting a
        // contract are independent actions and a page with only one of the two is not truly empty.
        RenderAcceptedRoutes();

        if (App.Hauls.AllHauls.Count == 0)
        {
            _body.Children.Add(Placeholder("No active hauls. Accept a hauling contract in-game."));
            _hasSeededOnce = true;
            return;
        }

        RenderActive(allowHighlight);
        RenderBottom();
        _hasSeededOnce = true;
    }

    // -- layout --------------------------------------------------------------------

    private void Build()
    {
        var root = new Grid { Margin = new Thickness(20, 16, 20, 16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // header
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // money panel
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // auto-load
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });  // content

        // Header action slot: Clear all. The Auto-scan / Show-contract-box toggles now live only in
        // the overlay's HAULING tab (single control surface), so this page no longer mirrors them.
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        _clearBtn = ActionButton("Clear all");
        _clearBtn.VerticalAlignment = VerticalAlignment.Center;
        _clearBtn.Margin = new Thickness(0, 0, 0, 0);
        _clearBtn.Click += (_, _) => App.Hauls.ClearAll();   // Changed -> Refresh() rebuilds the list

        // Ambient route-convoy accent, matching the glyph on the other page headers, left of the action.
        var headGlyph = Hud.AmbientGlyph(Hud.Ambient.RouteConvoy, 40);
        headGlyph.VerticalAlignment = VerticalAlignment.Center;
        headGlyph.Margin = new Thickness(0, 0, 12, 0);
        actions.Children.Add(headGlyph);
        actions.Children.Add(_clearBtn);

        var header = Hud.Header("LOGISTICS", "Cargo Hauling",
            "Work you have taken on: contracts, loading and payout.", actions);
        Grid.SetRow(header, 0); root.Children.Add(header);

        // Money surfaces, moved here from Trade (task B4, spec 2026-08-09 section 2.3/3, the verb
        // split - "Trade is a catalogue; the money belongs with the work"). Sits above the
        // auto-load panel per the spec's section 3 ordering (money bar, then AUTO-LOADING). Built
        // once, like the auto-load panel below it; MoneyPanel wires its own live subscriptions and
        // Refresh() below repaints it on every page entry.
        _moneyPanel = new MoneyPanel();
        _moneyPanel.Margin = new Thickness(0, 0, 0, 12);
        Grid.SetRow(_moneyPanel, 1); root.Children.Add(_moneyPanel);

        // Auto-load countdown, moved here from Trade (spec 2026-08-09, the verb split). Lifetime is
        // caller-owned and this page is an app-lifetime singleton like TradePage, so Start() once
        // here and never Stop(): App.AutoLoad is a shared, lock-guarded singleton and each status
        // line subscribes its own handler idempotently, so a second live instance is a fan-out
        // rather than a double-subscribe.
        _autoLoadPanel = new AutoLoadStatusLine(compact: false, surfaceName: "hauling");
        _autoLoadPanel.Start();
        _autoLoadPanel.Margin = new Thickness(0, 0, 0, 12);
        Grid.SetRow(_autoLoadPanel, 2); root.Children.Add(_autoLoadPanel);

        var scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _body,
        };
        Grid.SetRow(scroller, 3); root.Children.Add(scroller);

        Content = root;
    }

    // -- accepted trade routes (Task C, spec 2026-08-09 section 3) -----------------
    // Reads AppSettings.PinnedRoutes directly, the same live list TradePage.PinnedRoutes exposes -
    // this page has no guarantee TradePage was ever constructed this session (it is a lazy
    // singleton like this one), so it goes straight to the source rather than through a page that
    // may not exist. There is no Changed event for this list the way App.Hauls has one, so a
    // Delete route calls Refresh() itself once it has persisted.

    private void RenderAcceptedRoutes()
    {
        // Stop every armed-but-unconfirmed Delete route timer from the PREVIOUS build before its
        // cards are discarded below - see the field comment for why a live timer must never outlive
        // the button it reverts.
        foreach (var t in _deleteConfirmTimers) t.Stop();
        _deleteConfirmTimers.Clear();

        var routes = App.Settings.Current.PinnedRoutes;
        _body.Children.Add(SectionHeader($"Accepted trade routes · {routes.Count}"));
        if (routes.Count == 0)
        {
            _body.Children.Add(MutedLine("No accepted routes. Rank routes in Trade and press ACCEPT ROUTE."));
            return;
        }

        var grid = new UniformGrid { Columns = 2 };
        foreach (var r in routes)
            grid.Children.Add(AcceptedRouteCard(r));
        _body.Children.Add(grid);
    }

    private UIElement AcceptedRouteCard(AcceptedRoute r)
    {
        var inner = new StackPanel();

        // Title row: commodity name (left, same treatment as the contractor name on a haul card)
        // + Delete route (right).
        var titleRow = new Grid();
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = new TextBlock
        {
            Text = r.CommodityName, FontFamily = Head, FontSize = 14.5, FontWeight = FontWeights.SemiBold,
            Foreground = Br("FgBrush"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(name, 0); titleRow.Children.Add(name);

        var del = DeleteRouteButton(r);
        Grid.SetColumn(del, 1); titleRow.Children.Add(del);
        inner.Children.Add(titleRow);

        // Projected margin: the ranked per-SCU margin against what is actually being carried once
        // known, the plan otherwise - PerScuMargin x (ActualQty ?? TripQty).
        var qty = r.ActualQty ?? r.TripQty;
        inner.Children.Add(MoneyLine(r.PerScuMargin * qty, _green, new Thickness(0, 6, 0, 8)));

        // Route line: FROM -> TO, or SELL AT for a sell-only accepted route (no buy leg to name).
        var routeText = r.BuyTerminalId is null
            ? $"SELL AT {r.SellTerminalName}"
            : $"{r.BuyTerminalName} -> {r.SellTerminalName}";
        inner.Children.Add(new TextBlock
        {
            Text = routeText, FontFamily = Mono, FontSize = 12, Foreground = Br("CyanBrush"),
            Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap,
        });

        inner.Children.Add(QuantityRow(r));
        inner.Children.Add(StagePipsRow(r.Stage));

        var card = Hud.Panel(inner, brackets: true, padding: new Thickness(14, 12, 14, 12));
        card.Margin = new Thickness(0, 0, 10, 10);
        return card;
    }

    // Value in MonoFont, unit as a smaller dim run beside it (house rule: every displayed aUEC
    // value carries the aUEC suffix - the Unit() idiom, TradePage.Planner.cs BuildFinancialRail).
    private UIElement MoneyLine(double value, Color color, Thickness margin)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = margin };
        row.Children.Add(new TextBlock
        {
            Text = value.ToString("N0"), FontFamily = Mono, FontSize = 15, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(color),
        });
        row.Children.Add(new TextBlock
        {
            Text = " aUEC", FontFamily = Hud.Font("UiFont"), FontSize = 10,
            Foreground = Br("FgDimBrush"), VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(4, 0, 0, 1),
        });
        return row;
    }

    // Quantity: when a matched buy corrected the figure, the original plan strikes through and the
    // real amount reads in amber beside it (Task C, spec 2026-08-09 section 1.4, "the route
    // corrects itself"). Nothing has matched yet for most routes today (Phase D, matching, is a
    // separate task) so the plain-quantity branch is what renders in practice.
    private UIElement QuantityRow(AcceptedRoute r)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        if (r.ActualQty is int actual && actual != r.TripQty)
        {
            row.Children.Add(new TextBlock
            {
                Text = $"{r.TripQty:N0} SCU", FontFamily = Mono, FontSize = 12, Foreground = Br("FgDimBrush"),
                TextDecorations = TextDecorations.Strikethrough, VerticalAlignment = VerticalAlignment.Center,
            });
            row.Children.Add(new TextBlock
            {
                Text = $"  {actual:N0} SCU", FontFamily = Mono, FontSize = 12, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(_amber), VerticalAlignment = VerticalAlignment.Center,
            });
        }
        else
        {
            row.Children.Add(new TextBlock
            {
                Text = $"{r.TripQty:N0} SCU", FontFamily = Mono, FontSize = 12, Foreground = Br("FgBrush"),
            });
        }
        return row;
    }

    private enum PipState { Done, Current, Future }

    private static PipState StagePipState(AcceptedStage pip, AcceptedStage current) =>
        pip < current ? PipState.Done : pip == current ? PipState.Current : PipState.Future;

    // Stage pips: ACCEPTED / LOADED / SOLD, the current stage amber, completed stages green, future
    // stages dim - a small filled/hollow dot per stage (StatusDot's own idiom, extended to three
    // colors instead of two) with a hairline between, matching PanelHeaderBar's own hairline.
    private UIElement StagePipsRow(AcceptedStage stage)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        AddStagePip(row, "ACCEPTED", StagePipState(AcceptedStage.Accepted, stage));
        AddStageHairline(row);
        AddStagePip(row, "LOADED", StagePipState(AcceptedStage.Loaded, stage));
        AddStageHairline(row);
        AddStagePip(row, "SOLD", StagePipState(AcceptedStage.Sold, stage));
        return row;
    }

    private void AddStagePip(StackPanel row, string label, PipState state)
    {
        Brush tint = state switch
        {
            PipState.Done => new SolidColorBrush(_green),
            PipState.Current => new SolidColorBrush(_amber),
            _ => Br("NavBorderBrush"),
        };
        row.Children.Add(new Border
        {
            Width = 7, Height = 7, CornerRadius = new CornerRadius(4),
            Background = state == PipState.Future ? Brushes.Transparent : tint,
            BorderBrush = tint, BorderThickness = new Thickness(1.5),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0),
        });
        row.Children.Add(new TextBlock
        {
            Text = label, FontFamily = Mono, FontSize = 9, FontWeight = FontWeights.Bold,
            Foreground = state == PipState.Future ? Br("FgDimBrush") : tint,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
        });
    }

    private void AddStageHairline(StackPanel row) => row.Children.Add(new Border
    {
        Width = 14, Height = 1, Background = Br("NavBorderBrush"),
        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
    });

    // Delete route: two-tap confirmed (TwoTapConfirm, 3s window), first click arms to "Sure?" on a
    // solid red, second click within the window removes it. This page has no shared cursor-poll
    // tick (unlike the overlay's PollArmedConfirms), so a per-button DispatcherTimer reverts an
    // unconfirmed arm instead - started on arm, stopped on fire, and swept again by
    // RenderAcceptedRoutes the moment this card is rebuilt (the field comment explains why).
    private Button DeleteRouteButton(AcceptedRoute r)
    {
        var btn = ActionButton("Delete route");
        btn.VerticalAlignment = VerticalAlignment.Top;
        btn.Margin = new Thickness(8, 0, 0, 0);

        void Rest()
        {
            btn.Content = "Delete route";
            btn.ClearValue(Button.BackgroundProperty);
            btn.ClearValue(Button.ForegroundProperty);
        }

        var confirm = new TwoTapConfirm(TimeSpan.FromSeconds(3), () =>
        {
            var buyLabel = r.BuyTerminalId is null ? "SELL-ONLY" : r.BuyTerminalName;
            App.Settings.Current.PinnedRoutes = RoutePlanner.RemovePin(App.Settings.Current.PinnedRoutes, r).ToList();
            App.Settings.Save();
            Logger.Info($"[CARGO] route deleted {r.CommodityName} {buyLabel}->{r.SellTerminalName}");
            Refresh();   // rebuilds this whole section; this card, and this button, go with it
        });

        var revert = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        revert.Tick += (_, _) => { revert.Stop(); Rest(); };
        _deleteConfirmTimers.Add(revert);

        btn.Click += (_, _) =>
        {
            if (confirm.Tap(DateTime.UtcNow))
            {
                btn.Content = "Sure?";
                btn.Background = new SolidColorBrush(_armedRed);
                btn.Foreground = Brushes.White;
                revert.Stop();
                revert.Start();
            }
            else Rest();
        };

        return btn;
    }

    // -- active hauls --------------------------------------------------------------

    private void RenderActive(bool allowHighlight)
    {
        var active = App.Hauls.ActiveHauls;
        _body.Children.Add(SectionHeader($"Active hauls · {active.Count}"));
        if (active.Count == 0)
        {
            _body.Children.Add(MutedLine("No active hauls right now."));
            return;
        }

        // Two-up card grid (the mock's grid2). Per-card margins create the gutters.
        var grid = new UniformGrid { Columns = 2 };
        foreach (var h in active)
            grid.Children.Add(ActiveHaulCard(h, allowHighlight));
        _body.Children.Add(grid);
    }

    private UIElement ActiveHaulCard(Haul h, bool allowHighlight)
    {
        var inner = new StackPanel();

        // Title row: company name + topology chip (left), aUEC reward, delete (right).
        var titleRow = new Grid();
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var nameStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        nameStack.Children.Add(new TextBlock
        {
            Text = Contractor(h), FontFamily = Head, FontSize = 14.5, FontWeight = FontWeights.SemiBold,
            Foreground = Br("FgBrush"), VerticalAlignment = VerticalAlignment.Center,
        });
        if (!string.IsNullOrWhiteSpace(h.Topology))
        {
            var topo = Hud.Chip(_amber, h.Topology);
            topo.Margin = new Thickness(10, 0, 0, 0);
            topo.VerticalAlignment = VerticalAlignment.Center;
            nameStack.Children.Add(topo);
        }

        // Max container size the contract delivers in. Corrected 2026-08-01 (app review): both the
        // comment and both tooltips used to credit contract OCR, but OCR is the FALLBACK, not the
        // source. HaulTracker.ApplyMarker looks the cap up in ContractCapCatalog by the exact
        // contract token first, and Enrich only lets an OCR value through when the catalog had no
        // entry. The unknown-case wording was the harmful half: "not found in the scanned text yet"
        // reads as "be patient", but contract scanning is OFF by default and can only be turned on
        // from the overlay's HAULING tab, so a player who never enabled it was waiting for
        // something that would never happen.
        bool capDetected = h.ContainerCap.HasValue;
        var capChip = Hud.Chip(capDetected ? Cyan : Color.FromRgb(0x7C, 0x8A, 0x99),
                               capDetected ? $"Box ≤ {h.ContainerCap} SCU" : "Box size ?");
        capChip.Margin = new Thickness(6, 0, 0, 0);
        capChip.VerticalAlignment = VerticalAlignment.Center;
        capChip.ToolTip = capDetected
            ? "Max container size this contract delivers in."
            : "No container size on record for this contract. Turning on contract scanning "
              + "(overlay, HAULING tab) lets Nexus read it from the contract panel.";
        nameStack.Children.Add(capChip);

        Grid.SetColumn(nameStack, 0); titleRow.Children.Add(nameStack);

        if (h.Reward > 0)
        {
            var reward = new TextBlock
            {
                Text = $"{h.Reward:N0} aUEC", FontFamily = Mono, FontSize = 12, FontWeight = FontWeights.SemiBold,
                Foreground = Br("CyanBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0),
            };
            Grid.SetColumn(reward, 1); titleRow.Children.Add(reward);
        }
        var del = DeleteButton(h.MissionId);
        Grid.SetColumn(del, 2); titleRow.Children.Add(del);
        inner.Children.Add(titleRow);

        // Route line in mono cyan (the mock's commodity route).
        if (!string.IsNullOrWhiteSpace(h.RouteTitle))
            inner.Children.Add(new TextBlock
            {
                Text = h.RouteTitle, FontFamily = Mono, FontSize = 12, Foreground = Br("CyanBrush"),
                Margin = new Thickness(0, 6, 0, 8), TextWrapping = TextWrapping.Wrap,
            });
        else
            inner.Children.Add(new Border { Height = 6 });

        if (h.ContractObjectives.Count > 0)
        {
            for (int i = 0; i < h.ContractObjectives.Count; i++)
                inner.Children.Add(OcrObjectiveRow(h.MissionId, i, h.ContractObjectives[i], allowHighlight));
        }
        else
        {
            foreach (var leg in h.Legs)
                inner.Children.Add(LegRow(h.MissionId, leg, allowHighlight));
        }

        var card = Hud.Panel(inner, brackets: true, padding: new Thickness(14, 12, 14, 12));
        card.Margin = new Thickness(0, 0, 10, 10);
        return card;
    }

    private UIElement LegRow(string missionId, HaulLeg leg, bool allowHighlight)
    {
        var role = leg.Role == HaulRole.Pickup ? "Collect" : "Deliver";

        // Pickup legs often carry no commodity/SCU/destination of their own (those live on the
        // sibling dropoff). Show whatever fields are present, skipping the empties.
        var segs = new List<string>();
        if (leg.TargetScu > 0) segs.Add($"{leg.TargetScu} SCU");
        if (!string.IsNullOrWhiteSpace(leg.Commodity)) segs.Add(leg.Commodity);
        if (!string.IsNullOrWhiteSpace(leg.Destination)) segs.Add($"-> {leg.Destination}");
        var desc = string.Join(" ", segs);

        var text = $"{role}: {desc}";

        // Progress row: a filled teal dot = leg completed, a hollow dot = still pending.
        var grid = new Grid { Margin = new Thickness(2, 6, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var dot = StatusDot(leg.Completed);
        Grid.SetColumn(dot, 0); grid.Children.Add(dot);
        var tb = new TextBlock
        {
            Text = text, FontFamily = Mono, FontSize = 12,
            Foreground = leg.Completed ? Br("FgDimBrush") : Br("FgBrush"),
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(tb, 1); grid.Children.Add(tb);

        // Row identity = haul id + the leg's own objective id (already leg-indexed); highlight only
        // the first time this key is seen, and only when the caller says it is allowed to.
        var key = $"{missionId}|{leg.ObjectiveId}";
        if (_seenRowKeys.Add(key) && allowHighlight) HighlightRow(grid);
        return grid;
    }

    // Builds the display string for one OCR-sourced ContractObjective, omitting empty segments.
    private static string OcrObjectiveText(ContractObjective o)
    {
        var sb = new System.Text.StringBuilder();
        if (o.Scu > 0) sb.Append($"{o.Scu} SCU");
        if (!string.IsNullOrWhiteSpace(o.Commodity))
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(o.Commodity);
        }
        if (!string.IsNullOrWhiteSpace(o.Pickup)) sb.Append($": {o.Pickup}");
        if (!string.IsNullOrWhiteSpace(o.Dropoff)) sb.Append($" -> {o.Dropoff}");
        return sb.ToString();
    }

    private UIElement OcrObjectiveRow(string missionId, int index, ContractObjective o, bool allowHighlight)
    {
        // OCR objectives are contract targets (no completion state), so the marker is a static teal pip.
        var grid = new Grid { Margin = new Thickness(2, 6, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var pip = new Border
        {
            Width = 7, Height = 7, CornerRadius = new CornerRadius(4),
            Background = Br("AccentFaintBrush"), BorderBrush = Br("AccentBrush"), BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 9, 0),
        };
        Grid.SetColumn(pip, 0); grid.Children.Add(pip);
        var tb = new TextBlock
        {
            Text = OcrObjectiveText(o), FontFamily = Mono, FontSize = 12, Foreground = Br("FgBrush"),
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(tb, 1); grid.Children.Add(tb);

        // OCR objectives carry no id of their own, so identity falls back to the haul id + the
        // objective's position in the list (its "leg index" equivalent - the list is replaced
        // wholesale on each OCR pass, but existing indices keep the same slot).
        var key = $"{missionId}|obj{index}";
        if (_seenRowKeys.Add(key) && allowHighlight) HighlightRow(grid);
        return grid;
    }

    // One-shot row-insert highlight: a per-row SolidColorBrush clone (never a shared/frozen resource)
    // fades from amber #FFB23E at 20% alpha to transparent, 400ms, ease-out - mirrors MainWindow's
    // FlashBorder one-shot idiom. FillBehavior.Stop + a transparent resting base value means the
    // background reads as plain once the flash ends.
    private static void HighlightRow(Panel row)
    {
        var resting = Color.FromArgb(0x00, 0xFF, 0xB2, 0x3E);
        var brush = new SolidColorBrush(resting);
        row.Background = brush;
        var anim = new ColorAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(Motion.FlashMs),
            FillBehavior = FillBehavior.Stop,
        };
        anim.KeyFrames.Add(new EasingColorKeyFrame(Color.FromArgb(0x33, 0xFF, 0xB2, 0x3E), KeyTime.FromPercent(0.0)));
        anim.KeyFrames.Add(new EasingColorKeyFrame(resting, KeyTime.FromPercent(1.0)) { EasingFunction = Motion.SlideOut });
        brush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
    }

    // -- bottom row: consolidation table (left) + finished hauls (right) -----------

    private void RenderBottom()
    {
        var consolidation = BuildConsolidationPanel();
        var finished = App.Hauls.FinishedHauls;

        if (finished.Count == 0)
        {
            consolidation.Margin = new Thickness(0, 16, 0, 0);
            _body.Children.Add(consolidation);
            return;
        }

        var row = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        consolidation.Margin = new Thickness(0, 0, 8, 0);
        Grid.SetColumn(consolidation, 0); row.Children.Add(consolidation);

        var finishedPanel = BuildFinishedPanel();
        finishedPanel.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(finishedPanel, 1); row.Children.Add(finishedPanel);

        _body.Children.Add(row);
    }

    // -- consolidation -------------------------------------------------------------

    private Grid BuildConsolidationPanel()
    {
        var con = App.Hauls.BuildConsolidation();

        // App review 2026-08-01: these stops used to render in dictionary insertion order, which is
        // the order contracts happened to be accepted in - meaningless to a hauler planning a run.
        // The app has had real coordinates for these places and a live player position all along and
        // used neither. Now ordered nearest-first when a session places the player; unchanged when
        // it does not, because sorting by distance from nowhere would be theatre.
        var here = App.Player.Current;
        var pickups = ConsolidationOrder.ByDistanceFrom(con.Pickups, App.Map, here);
        var dropoffs = ConsolidationOrder.ByDistanceFrom(con.Dropoffs, App.Map, here);

        var bodyStack = new StackPanel();
        bodyStack.Children.Add(PanelHeaderBar("Collect / deliver consolidation",
            here is null ? "grouped by location" : "grouped by location, nearest first"));

        if (con.Pickups.Count == 0 && con.Dropoffs.Count == 0)
        {
            bodyStack.Children.Add(new Border { Padding = new Thickness(14, 10, 14, 14), Child = MutedLine("Nothing to consolidate yet.") });
            return Hud.Panel(bodyStack, padding: new Thickness(0));
        }

        var table = new Grid { Margin = new Thickness(14, 10, 14, 12) };
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) }); // Location
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                         // Action
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });    // Commodity
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                         // SCU

        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddCell(table, 0, 0, HeaderCell("LOCATION", false));
        AddCell(table, 0, 1, HeaderCell("ACTION", false));
        AddCell(table, 0, 2, HeaderCell("COMMODITY", false));
        AddCell(table, 0, 3, HeaderCell("SCU", true));

        // The distance is appended to the LOCATION cell only on the first row of each stop, so a
        // stop with four commodities does not repeat it four times. Null (unplaceable stop, or no
        // session) renders exactly as before.
        var rowIdx = 1;
        foreach (var s in pickups)
        {
            var dist = ConsolidationOrder.DistanceTo(s, App.Map, here);
            bool first = true;
            foreach (var item in s.Items)
            {
                rowIdx = AddConsolidationRow(table, rowIdx, first && dist != null ? $"{s.Location}  ({dist})" : s.Location,
                                             true, item.Commodity, item.Scu);
                first = false;
            }
        }
        foreach (var s in dropoffs)
        {
            var dist = ConsolidationOrder.DistanceTo(s, App.Map, here);
            bool first = true;
            foreach (var item in s.Items)
            {
                rowIdx = AddConsolidationRow(table, rowIdx, first && dist != null ? $"{s.Location}  ({dist})" : s.Location,
                                             false, item.Commodity, item.Scu);
                first = false;
            }
        }

        bodyStack.Children.Add(table);
        return Hud.Panel(bodyStack, padding: new Thickness(0));
    }

    private int AddConsolidationRow(Grid table, int row, string location, bool load, string commodity, int scu)
    {
        table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var loc = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(location) ? "Unknown" : location, FontFamily = Head, FontSize = 12.5,
            Foreground = Br("FgBrush"), Margin = new Thickness(0, 5, 8, 5), VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        AddCell(table, row, 0, loc);

        // Colored action pill: Load = cyan, Drop = amber (per the mock's load/drop chips).
        var chip = Hud.Chip(load ? Cyan : _amber, load ? "Collect" : "Deliver");
        chip.Margin = new Thickness(0, 5, 12, 5);
        chip.VerticalAlignment = VerticalAlignment.Center;
        AddCell(table, row, 1, chip);

        var com = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(commodity) ? "Cargo" : commodity, FontFamily = Mono, FontSize = 11.5,
            Foreground = Br("FgDimBrush"), Margin = new Thickness(0, 5, 8, 5), VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        AddCell(table, row, 2, com);

        var scuTb = new TextBlock
        {
            Text = scu.ToString("N0"), FontFamily = Mono, FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = Br("CyanBrush"), TextAlignment = TextAlignment.Right, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 5, 0, 5), VerticalAlignment = VerticalAlignment.Center,
        };
        AddCell(table, row, 3, scuTb);

        return row + 1;
    }

    private static void AddCell(Grid g, int row, int col, UIElement el)
    {
        Grid.SetRow(el, row); Grid.SetColumn(el, col); g.Children.Add(el);
    }

    private UIElement HeaderCell(string text, bool right) => new TextBlock
    {
        Text = text, FontFamily = Mono, FontSize = 9.5, FontWeight = FontWeights.Bold, Foreground = Br("FgDimBrush"),
        Margin = new Thickness(0, 0, right ? 0 : 8, 8),
        TextAlignment = right ? TextAlignment.Right : TextAlignment.Left,
        HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left,
    };

    // -- finished hauls ------------------------------------------------------------

    private Grid BuildFinishedPanel()
    {
        var finished = App.Hauls.FinishedHauls;

        var bodyStack = new StackPanel();
        bodyStack.Children.Add(PanelHeaderBar($"Finished hauls · {finished.Count}", null));

        var rows = new StackPanel { Margin = new Thickness(14, 4, 10, 12) };
        foreach (var h in finished)
            rows.Children.Add(FinishedRow(h));
        bodyStack.Children.Add(rows);

        return Hud.Panel(bodyStack, padding: new Thickness(0));
    }

    private UIElement FinishedRow(Haul h)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = new TextBlock
        {
            Text = Contractor(h), FontFamily = Head, FontSize = 12.5, FontWeight = FontWeights.SemiBold,
            Foreground = Br("FgBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 8, 6),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(name, 0); grid.Children.Add(name);

        var noteText = string.IsNullOrWhiteSpace(h.RouteTitle) ? h.Topology : h.RouteTitle;
        var note = new TextBlock
        {
            Text = noteText, FontFamily = Mono, FontSize = 11, Foreground = Br("FgDimBrush"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 12, 6), TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(note, 1); grid.Children.Add(note);

        // Complete = green chip; any other ending (Abandoned/Failed/Deactivated) reads as an amber warning.
        var complete = h.Outcome == HaulOutcome.Complete;
        var chip = Hud.Chip(complete ? _green : _amber, h.Outcome.ToString().ToUpperInvariant());
        chip.VerticalAlignment = VerticalAlignment.Center;
        chip.Margin = new Thickness(0, 0, 8, 0);
        Grid.SetColumn(chip, 2); grid.Children.Add(chip);

        var del = DeleteButton(h.MissionId);
        del.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(del, 3); grid.Children.Add(del);

        return grid;
    }

    // -- helpers -------------------------------------------------------------------

    private static string CompanyOf(Haul h) => string.IsNullOrWhiteSpace(h.Company) ? "Unknown company" : h.Company;
    // Prefer OCR-sourced ContractedBy over the generator-derived company name when present.
    private static string Contractor(Haul h) => string.IsNullOrWhiteSpace(h.ContractedBy) ? CompanyOf(h) : h.ContractedBy;

    // Small bordered action button, mirroring NetworkPage.ActionButton (both now styled via the
    // shared NexusButton template - were bare Buttons falling back to stock WPF chrome with no
    // hover feedback).
    private Button ActionButton(string text) => new()
    {
        Content = text, Style = (Style)Application.Current.FindResource("NexusButton"),
        Padding = new Thickness(12, 6, 12, 6), FontWeight = FontWeights.SemiBold, FontSize = 12,
    };

    // Flat "x" affordance that deletes a single haul. A plain character, not an icon/emoji. Styled
    // via NexusButton (transparent at rest, matching the prior look) so it gets the app's real hover
    // feedback instead of stock WPF chrome; ToolTip added since it's icon-only.
    private Button DeleteButton(string missionId)
    {
        var btn = new Button
        {
            Content = "x", FontFamily = Mono, FontSize = 14, FontWeight = FontWeights.Bold,
            Style = (Style)Application.Current.FindResource("NexusButton"),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 2, 8, 2), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Top,
            ToolTip = "Delete this haul",
        };
        btn.Click += (_, _) => App.Hauls.Remove(missionId);   // Changed -> Refresh() rebuilds the list
        return btn;
    }

    // Section kicker rendered as a command-center teal eyebrow.
    private UIElement SectionHeader(string text) => new TextBlock
    {
        Text = text.ToUpperInvariant(), FontFamily = Head, FontSize = 10.5, FontWeight = FontWeights.Bold,
        Foreground = Br("AccentBrush"), Margin = new Thickness(2, 4, 0, 10),
    };

    // In-panel header bar: title (+ optional right-aligned sub) over a hairline divider.
    private UIElement PanelHeaderBar(string title, string? sub)
    {
        var wrap = new StackPanel();
        var bar = new Grid { Margin = new Thickness(14, 11, 14, 9) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var t = new TextBlock
        {
            Text = title.ToUpperInvariant(), FontFamily = Head, FontSize = 12.5, FontWeight = FontWeights.Bold,
            Foreground = Br("FgBrush"), VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(t, 0); bar.Children.Add(t);

        if (!string.IsNullOrWhiteSpace(sub))
        {
            var s = new TextBlock
            {
                Text = sub, FontSize = 10.5, Foreground = Br("FgDimBrush"), VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(s, 1); bar.Children.Add(s);
        }
        wrap.Children.Add(bar);
        wrap.Children.Add(new Border { Height = 1, Background = Br("NavBorderBrush") });
        return wrap;
    }

    // Small progress pip: filled teal when the step is done, hollow outline while pending.
    private UIElement StatusDot(bool done) => new Border
    {
        Width = 7, Height = 7, CornerRadius = new CornerRadius(4),
        Background = done ? Br("AccentBrush") : Brushes.Transparent,
        BorderBrush = done ? Br("AccentBrush") : Br("NavBorderBrush"), BorderThickness = new Thickness(1.5),
        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 9, 0),
    };

    private UIElement MutedLine(string text) => new TextBlock
    {
        Text = text, FontSize = 12, Foreground = Br("FgDimBrush"),
        Margin = new Thickness(2, 2, 0, 6), TextWrapping = TextWrapping.Wrap,
    };

    // Empty state rendered as a centered chamfered HUD panel rather than bare text.
    private UIElement Placeholder(string text)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        var glyph = Hud.AmbientGlyph(Hud.Ambient.RouteConvoy, 104);
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        glyph.Margin = new Thickness(0, 0, 0, 18);
        stack.Children.Add(glyph);
        stack.Children.Add(new TextBlock
        {
            Text = text, Foreground = Br("FgDimBrush"), FontSize = 13, TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center,
        });
        var panel = Hud.Panel(stack, brackets: true, padding: new Thickness(28));
        panel.Margin = new Thickness(0, 8, 0, 0);
        return panel;
    }
}
