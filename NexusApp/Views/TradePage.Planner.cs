using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using NexusApp.Services;
using NexusApp.Services.Cargo;
using NexusApp.Models.Cargo;

namespace NexusApp.Views;

// Trip-coverage bar tier/fill math (mock tripTier/tripFillPct, index.html:572,579). Pure, so it is
// unit tested directly rather than only through the row it renders inside.
internal static class TradeBarMath
{
    public static string Tier(int n, int tripQty) =>
        n >= tripQty ? "ok" : n >= tripQty / 2.0 ? "amber" : "danger";

    public static double FillFraction(int n, int tripQty) =>
        tripQty > 0 ? Math.Min(1.0, n / (double)tripQty) : 1.0;

    public static Brush Color(string tier) => tier switch
    {
        "ok"     => Hud.Br("OkBrush"),
        "amber"  => Hud.Br("AccentBrush"),
        _        => Hud.Br("DangerBrush"),
    };
}

public sealed partial class TradePage
{
    private ComboBox _shipCombo = null!;
    private TextBox _budgetBox = null!;
    private Border _fromHerePill = null!;
    private Border _anywherePill = null!;
    private Border _stockAnyPill = null!;
    private Border _stockCoversTripPill = null!;
    private Border _stockCoversTwoTripsPill = null!;
    private readonly CargoShipCatalog _shipCatalog = CargoShipCatalog.LoadEmbedded();
    private int _plannerExpanded = -1;

    // The input area (ship combo, budget box, anchor pills, caption) is built ONCE and only its
    // properties are updated afterwards; _plannerResults is the ONLY thing RebuildPlanner clears.
    // Before this, every rebuild started with PlannerHost.Children.Clear(), so the budget box's own
    // LostFocus - which WPF raises synchronously inside the mouse handling of whatever the user just
    // clicked - destroyed the ship combo and the anchor pills mid-click and ate that first click.
    // The same clear also wiped typed-but-unblurred text and collapsed expanded bands on every
    // hourly market tick.
    private StackPanel _plannerInputs = null!;
    private StackPanel _plannerResults = null!;

    // Session-typed budget (ASSUMED not in the fixed AppSettings contract - see task-12 brief's
    // "NOTE on a second small assumption": AppSettings has no TradeBudget field, so this holds the
    // value in memory only; it resets each session, which does not affect anything else in this file.
    private string _budgetText = "";

    // Reentrancy guard for the budget box's live TextChanged re-rank below, same pattern as the
    // Sell tab's quantity box (TradePage.Sell.cs, _inQtyLiveRerank) - RebuildPlanner never writes
    // back to _budgetBox.Text itself, so nothing today re-enters this handler, but the guard is
    // cheap insurance against a future write-back (or IME composition) looping back in.
    private bool _inBudgetLiveRerank;

    private ShipCargoDef CurrentShip() =>
        _shipCatalog.ById(App.Settings.Current.TradeShipId) ?? _shipCatalog.Ships.First();

    private double? CurrentBudget()
    {
        var digits = new string((_budgetBox?.Text ?? "").Where(char.IsDigit).ToArray());
        return digits.Length > 0 && double.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    // Built once, on the first RebuildPlanner. Everything here survives every later rebuild: the
    // controls keep their identity, so a click that moved focus off the budget box lands on a live
    // control, and typed-but-unblurred text is never thrown away by a background tick.
    private void BuildPlannerChrome()
    {
        if (_plannerInputs is not null) return;

        _plannerInputs = new StackPanel();

        var inputRow = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };

        var shipGrp = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        shipGrp.Children.Add(FieldLabel("Ship"));
        _shipCombo = new ComboBox
        {
            Style = (Style)Application.Current.FindResource("NexusComboBox"), MinWidth = 200,
            ItemsSource = _shipCatalog.Ships.Select(s => $"{s.DisplayName} - {s.TotalScu} SCU").ToList(),
        };
        int shipIdx = _shipCatalog.Ships.ToList().FindIndex(s => s.Id == App.Settings.Current.TradeShipId);
        _shipCombo.SelectedIndex = shipIdx >= 0 ? shipIdx : 0;
        _shipCombo.SelectionChanged += (_, _) =>
        {
            var ship = _shipCatalog.Ships.ElementAt(_shipCombo.SelectedIndex);
            App.Settings.Current.TradeShipId = ship.Id;
            App.Settings.Save();
            Logger.Info($"[UI] Trade planner: ship {ship.Id}");
            RebuildPlanner();
        };
        shipGrp.Children.Add(_shipCombo);
        inputRow.Children.Add(shipGrp);

        var budgetGrp = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        budgetGrp.Children.Add(FieldLabel("Budget (optional)"));
        _budgetBox = new TextBox
        {
            Style = (Style)Application.Current.FindResource("NexusTextBox"), FontFamily = Hud.Font("MonoFont"),
            Width = 150, Text = _budgetText,
        };
        _budgetBox.LostFocus += (_, _) =>
        {
            if (_budgetText == _budgetBox.Text) return;   // guard, same pattern as SetAnchor/SetScope: no-op blur never logs or rebuilds
            _budgetText = _budgetBox.Text;
            Logger.Info("[UI] Trade planner: budget updated");
            RebuildPlanner();   // results only: the control the user just clicked is still alive
        };
        // Live re-rank per keystroke, same fix as the Sell tab's quantity box (TradePage.Sell.cs,
        // item C): budget used to apply only on blur, so nothing re-ranked until the user typed a
        // budget AND clicked elsewhere. RebuildPlanner only ever clears/repopulates
        // _plannerResults, never _plannerInputs (this box's own parent, built once - see this
        // method's opening comment), so this can never recreate the box the user is typing into or
        // steal its focus/caret. Deliberately leaves _budgetText and the log line alone:
        // CurrentBudget already reads _budgetBox.Text directly (not _budgetText), so a rebuild here
        // re-ranks against the box's live text with no extra bookkeeping - the one "budget updated"
        // log line stays exclusively on LostFocus-with-change above, so typing does not spam the log.
        _budgetBox.TextChanged += (_, _) =>
        {
            if (_inBudgetLiveRerank) return;
            _inBudgetLiveRerank = true;
            try { RebuildPlanner(); }
            finally { _inBudgetLiveRerank = false; }
        };
        budgetGrp.Children.Add(_budgetBox);
        inputRow.Children.Add(budgetGrp);

        var anchorGrp = new StackPanel();
        anchorGrp.Children.Add(FieldLabel("Routes"));
        var anchorRow = new StackPanel { Orientation = Orientation.Horizontal };
        _fromHerePill = ScopePill($"FROM HERE ({OriginLabel})");
        _anywherePill = ScopePill("ANYWHERE");
        _fromHerePill.MouseLeftButtonUp += (_, _) => SetAnchor(true);
        _anywherePill.MouseLeftButtonUp += (_, _) => SetAnchor(false);
        anchorRow.Children.Add(_fromHerePill);
        anchorRow.Children.Add(_anywherePill);
        anchorGrp.Children.Add(anchorRow);
        inputRow.Children.Add(anchorGrp);

        // Stock/demand coverage filter (task 5): same pill chrome as the anchor pills above, three
        // mutually-exclusive states rather than two, using SetPillOn per pill (already shared with
        // RefreshAnchorPills) so the highlighted-state visuals stay identical across both groups.
        var stockFilterGrp = new StackPanel();
        stockFilterGrp.Children.Add(FieldLabel("Coverage"));
        var stockFilterRow = new StackPanel { Orientation = Orientation.Horizontal };
        _stockAnyPill = ScopePill("ANY");
        _stockCoversTripPill = ScopePill("COVERS TRIP");
        _stockCoversTwoTripsPill = ScopePill("COVERS 2X");
        _stockAnyPill.MouseLeftButtonUp += (_, _) => SetStockFilter(StockFilter.Any);
        _stockCoversTripPill.MouseLeftButtonUp += (_, _) => SetStockFilter(StockFilter.CoversTrip);
        _stockCoversTwoTripsPill.MouseLeftButtonUp += (_, _) => SetStockFilter(StockFilter.CoversTwoTrips);
        stockFilterRow.Children.Add(_stockAnyPill);
        stockFilterRow.Children.Add(_stockCoversTripPill);
        stockFilterRow.Children.Add(_stockCoversTwoTripsPill);
        stockFilterGrp.Children.Add(stockFilterRow);
        inputRow.Children.Add(stockFilterGrp);

        _plannerInputs.Children.Add(inputRow);

        _plannerInputs.Children.Add(new TextBlock
        {
            Text = "Ranked by what a trip really pays with your ship and budget, not raw margin. Bars show trip coverage.",   // mock:857, verbatim
            FontFamily = Hud.Font("UiFont"), FontSize = 10.5, Foreground = Hud.Br("FgDimBrush"), Margin = new Thickness(0, 0, 0, 14),
        });

        _plannerResults = new StackPanel();
        PlannerHost.Children.Add(_plannerInputs);
        PlannerHost.Children.Add(_plannerResults);
    }

    private void RebuildPlanner()
    {
        BuildPlannerChrome();
        if (!EnsureMarketConsent(_plannerResults, _plannerInputs)) return;
        _plannerResults.Children.Clear();
        _plannerExpanded = -1;
        RefreshAnchorPills();
        RefreshStockFilterPills();

        var snap = App.Market.Snapshot;
        if (snap is null || snap.TradePrices.Rows.Count == 0)
        {
            _plannerResults.Children.Add(EmptyOrStaleNote(snap?.TradePrices.FetchedUtc));
            return;
        }

        // Terminal lookup, built once per rebuild: TerminalId -> MarketTerminal. Reused below both
        // for origin resolution and for each route's System tags (Buy/Sell legs).
        var terminals = snap.Terminals.Rows.ToDictionary(t => t.Id);
        var ship = CurrentShip();
        var originIds = App.Settings.Current.TradeAnchorFromHere ? OriginTerminalIds(snap.Terminals.Rows) : null;
        // FROM HERE with an origin that resolved to zero terminals (no live location, no manual
        // pick, or nothing matched either): originIds is non-null but empty, which RoutePlanner
        // now restricts to zero buy candidates rather than silently falling back to ANYWHERE (see
        // RoutePlanner.Rank's doc comment / spec Decision 6). That case gets its own empty-state
        // message below instead of the generic "no routes buy from here" one, since here the
        // problem is an unknown origin, not a real absence of routes.
        bool originUnknown = App.Settings.Current.TradeAnchorFromHere && originIds is { Count: 0 };
        var routes = RoutePlanner.Rank(snap.TradePrices.Rows, terminals, ship.TotalScu, ship.MaxContainerScu,
            CurrentBudget(), originIds, App.Settings.Current.TradeScope, take: 25,
            ParseStockFilter(App.Settings.Current.TradeStockFilter));

        if (routes.Count == 0)
        {
            string message;
            if (originUnknown)
            {
                message = "Origin unknown - pick a manual origin above, or switch to ANYWHERE.";
                Logger.Info("[UI] Trade planner run: 0 routes, origin unknown");
            }
            else
            {
                message = App.Settings.Current.TradeAnchorFromHere
                    ? "No routes buy from here right now. Try ANYWHERE, or a wider scope."
                    : "No routes match the current scope and budget.";
            }
            _plannerResults.Children.Add(new TextBlock
            {
                Text = message,
                FontFamily = Hud.Font("UiFont"), FontSize = 12.5, Foreground = Hud.Br("FgDimBrush"),
            });
            return;
        }

        for (int i = 0; i < routes.Count; i++)
        {
            var row = BuildRouteRow(routes[i], i, ship, terminals);
            CascadeIn(row, i);
            _plannerResults.Children.Add(row);
        }

        Logger.Info($"[UI] Trade planner run: {routes.Count} routes, ship {ship.Id}, scope {App.Settings.Current.TradeScope}, anchor {(App.Settings.Current.TradeAnchorFromHere ? "FROM HERE" : "ANYWHERE")}");
    }

    private void SetAnchor(bool fromHere)
    {
        if (App.Settings.Current.TradeAnchorFromHere == fromHere) return;
        App.Settings.Current.TradeAnchorFromHere = fromHere;
        App.Settings.Save();
        Logger.Info($"[UI] Trade planner anchor: {(fromHere ? "FROM HERE" : "ANYWHERE")}");
        RebuildPlanner();
    }

    private void RefreshAnchorPills()
    {
        bool fromHere = App.Settings.Current.TradeAnchorFromHere;
        // The pill is built once, so its label has to keep tracking OriginLabel here rather than at
        // construction: the origin changes under it (a live location arriving, a manual pick, the
        // Manual/Live links) and the pill must always name the origin the results were ranked from.
        ((TextBlock)_fromHerePill.Child).Text = $"FROM HERE ({OriginLabel})";
        SetPillOn(_fromHerePill, fromHere);
        SetPillOn(_anywherePill, !fromHere);
    }

    // Same no-op-on-unchanged guard as SetAnchor: a click on the pill that is already active never
    // logs or rebuilds.
    private void SetStockFilter(StockFilter filter)
    {
        var label = StockFilterLabel(filter);
        if (App.Settings.Current.TradeStockFilter == label) return;
        App.Settings.Current.TradeStockFilter = label;
        App.Settings.Save();
        Logger.Info($"[UI] trade: stock filter {label}");
        RebuildPlanner();
    }

    private void RefreshStockFilterPills()
    {
        var active = ParseStockFilter(App.Settings.Current.TradeStockFilter);
        SetPillOn(_stockAnyPill, active == StockFilter.Any);
        SetPillOn(_stockCoversTripPill, active == StockFilter.CoversTrip);
        SetPillOn(_stockCoversTwoTripsPill, active == StockFilter.CoversTwoTrips);
    }

    private static string StockFilterLabel(StockFilter filter) => filter switch
    {
        StockFilter.CoversTrip => "COVERS TRIP",
        StockFilter.CoversTwoTrips => "COVERS 2X",
        _ => "ANY",
    };

    // Any stored value that isn't a recognized label (corrupt settings.json, a future rollback)
    // falls back to Any - the same fail-open behavior the planner had before this filter existed.
    private static StockFilter ParseStockFilter(string? value) => value switch
    {
        "COVERS TRIP" => StockFilter.CoversTrip,
        "COVERS 2X" => StockFilter.CoversTwoTrips,
        _ => StockFilter.Any,
    };

    private static void SetPillOn(Border pill, bool on)
    {
        var text = (TextBlock)pill.Child;
        text.Foreground = on ? Hud.Br("AccentBrush") : Hud.Br("FgDimBrush");
        pill.BorderBrush = on ? Hud.Br("AccentStrongBrush") : Hud.Br("NavBorderBrush");
        pill.Background = on ? Hud.Br("AccentFaintBrush") : Hud.Br("Bg2NavBrush");
    }

    private static TextBlock FieldLabel(string text) => new()
    {
        Text = text.ToUpperInvariant(), FontFamily = Hud.Font("UiFont"), FontSize = 9, FontWeight = FontWeights.Bold,
        Foreground = Hud.Br("FgDimBrush"), Margin = new Thickness(0, 0, 0, 6),
    };

    // Serve-stale-with-age (house rule): a snapshot that has never fetched shows the neutral empty
    // state; one that has fetched but currently has zero rows (e.g. mid-refresh) still shows its age.
    private static TextBlock EmptyOrStaleNote(DateTime? fetchedUtc) => new()
    {
        Text = fetchedUtc is null || fetchedUtc.Value == default
            ? "No trade price data yet. It refreshes about once an hour while Nexus is open."
            : $"No trade routes to show right now (data from {MarketNotice.FormatAge(DateTime.UtcNow - fetchedUtc!.Value)}).",
        FontFamily = Hud.Font("UiFont"), FontSize = 12.5, Foreground = Hud.Br("FgDimBrush"),
    };

    // Trip-coverage bar: full-width track (6px, radius 3, tier color @ 12% alpha) with a
    // proportional-width fill overlay (solid tier color, glow blur6/.5) laid over it - mock:656-661
    // + CSS 250-253 (.legBarTrack/.legBarFill), matching the mock's track-plus-overlay construction
    // exactly rather than two abutting star-width columns.
    //
    // Owner's live-pass ask, 2026-07-30 (items B/D): "the green and red bars dont make much sense,
    // are they supposed to be filled or what?" Root cause was `fill.HorizontalAlignment = Left`
    // below: a Border with no Child and no explicit Width measures to a natural DesiredSize.Width
    // of 0, and Left/Right/Center alignment (unlike Stretch) arranges an element at its
    // DesiredSize, not the available space - so the fill rendered as a genuine zero-width,
    // invisible rectangle every time, leaving only the dim 12%-alpha track visible regardless of
    // the tier or fraction. The fix is to let it Stretch (the default - no HorizontalAlignment set)
    // so it fills the ENTIRE available width of its host column, which `host`'s two Star columns
    // already size to exactly `frac` of the full track width.
    // Review fix (2026-07-30, Medium regression on B/D): the fill is a SIBLING of the tooltip-
    // carrying track, not a descendant, and Stretch made it hit-test-visible for the first time -
    // so hovering the colored fill (the exact spot users check to confirm coverage, worst on a
    // full/green bar where fill covers the whole track) showed no tooltip; only the exposed track
    // remainder did. Fix: the ToolTip now lives on the OUTER grid this method returns, so it shows
    // for the bar's entire footprint no matter which child the mouse actually hits (WPF's tooltip
    // lookup walks up from the hit element to the nearest ancestor with a ToolTip set) - and fill/
    // host are marked IsHitTestVisible=false, since they are pure visuals that should never
    // intercept input in the first place. track still fully covers the bar's width on its own, so
    // every hit still lands on something inside grid's subtree and bubbles up to the one tooltip.
    private static UIElement TripBar(double frac, Brush color, string tooltip)
    {
        var track = new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(((SolidColorBrush)color).Color) { Opacity = 0.12 } };
        var fill = new Border
        {
            Height = 6, CornerRadius = new CornerRadius(3), Background = color, IsHitTestVisible = false,
            Effect = new DropShadowEffect { Color = ((SolidColorBrush)color).Color, BlurRadius = 6, ShadowDepth = 0, Opacity = 0.5 },
        };
        var host = new Grid { IsHitTestVisible = false };
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0001, frac), GridUnitType.Star) });
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0001, 1 - frac), GridUnitType.Star) });
        Grid.SetColumn(fill, 0);
        var grid = new Grid { ToolTip = tooltip };
        grid.Children.Add(track);
        host.Children.Add(fill);
        grid.Children.Add(host);
        return grid;
    }

    // 16x16 chevron, rotates 0/90 on expand. Mock uses a framer spring (EXPAND_SPRING) - FLAGGED
    // DEVIATION per this section's brief: the app has no physics-spring expander anywhere
    // (confirmed: every existing expander, SettingsPage.RevealPane included, is cubic-bezier Reveal
    // over a fixed ms duration), so this uses Motion.Reveal over Motion.DrillMs instead.
    private static Path ChevronGlyph() => new()
    {
        Width = 12, Height = 12, Data = Geometry.Parse("M5,3 L11,8 L5,13"),   // mock:538-539
        Stroke = Hud.Br("FgDimBrush"), StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round, Fill = Brushes.Transparent,
        Stretch = Stretch.Uniform, RenderTransformOrigin = new Point(0.5, 0.5),
    };

    // Cheap house-glyph equivalent of the mock's Ico.dots() "two sources agreeing" icon
    // (nexus-design-lab/trading-tab/index.html:526-530: two filled circles joined by a short
    // line): a small Ellipse-Border-Ellipse trio, ok-green, sized to sit inline with the corrline
    // text. Recorded deviation - not a literal SVG port, this app has no vector-icon primitive.
    private static UIElement CorroborationDotsGlyph()
    {
        var ok = Hud.Br("OkBrush");
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new Ellipse { Width = 5, Height = 5, Fill = ok, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new Border { Width = 5, Height = 1.6, Background = ok, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new Ellipse { Width = 5, Height = 5, Fill = ok, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    // The expanded detail band, for both the planner and the sell flow. Clicks inside it are marked
    // handled so they never reach the row host and re-collapse the row the user is reading (mock
    // index.html:978 does the same with onClick stopPropagation). A Transparent background is what
    // makes the whole band hit-testable: with the default null background, only the glyphs of the
    // text inside it are, and every click on the band's own whitespace fell through to the card
    // frame behind it and collapsed the row anyway.
    private static Border DetailBand(Thickness margin, Thickness padding)
    {
        var band = new Border
        {
            Visibility = Visibility.Collapsed, Margin = margin, Padding = padding,
            Background = Brushes.Transparent,
            BorderBrush = Hud.Br("NavBorderBrush"), BorderThickness = new Thickness(0, 1, 0, 0),
        };
        band.MouseLeftButtonUp += (_, e) => e.Handled = true;
        return band;
    }

    private static void SetChevronOpen(Path chevron, bool open)
    {
        var rt = chevron.RenderTransform as RotateTransform ?? new RotateTransform();
        chevron.RenderTransform = rt;
        double target = open ? 90 : 0;
        if (Motion.Reduced) { rt.Angle = target; return; }
        rt.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(target, TimeSpan.FromMilliseconds(Motion.DrillMs)) { EasingFunction = Motion.Reveal });
    }

    private FrameworkElement BuildRouteRow(TradeRoute r, int index, ShipCargoDef ship, Dictionary<int, MarketTerminal> terminals)
    {
        var frame = Hud.CardFrame(BuildRouteRowContent(r, index, ship, terminals, out var chevron, out var detailHost),
            out var cardFrame, out _, chamfer: 8, padding: new Thickness(16, 13, 18, 13));
        frame.Children.Add(PositionChevron(chevron));
        var host = new Border { Cursor = Cursors.Hand, Child = frame, Margin = new Thickness(0, 0, 0, 10) };
        host.MouseLeftButtonUp += (_, _) =>
        {
            bool nowOpen = _plannerExpanded != index;
            _plannerExpanded = nowOpen ? index : -1;
            detailHost.Visibility = nowOpen ? Visibility.Visible : Visibility.Collapsed;
            SetChevronOpen(chevron, nowOpen);
        };
        return host;
    }

    private static Grid PositionChevron(Path chevron)
    {
        var grid = new Grid { IsHitTestVisible = false };
        grid.Children.Add(new Border { Child = chevron, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        return grid;
    }

    private UIElement BuildRouteRowContent(TradeRoute r, int index, ShipCargoDef ship, Dictionary<int, MarketTerminal> terminals, out Path chevron, out Border detailHost)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Terminal lookups for both legs, resolved once and reused below both for the System tags
        // and for the optional distance tag next to the tier chip (never a second linear scan).
        terminals.TryGetValue(r.BuyRow.TerminalId, out var buyTerm);
        terminals.TryGetValue(r.SellRow.TerminalId, out var sellTerm);

        var head = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        head.Children.Add(new TextBlock { Text = r.BuyRow.CommodityName, FontFamily = Hud.Font("UiFont"), FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Hud.Br("FgBrush"), Margin = new Thickness(0, 0, 10, 0) });
        head.Children.Add(TierChip(r.Tier));
        // Owner's ask, 2026-07-30 (decorating beyond the approved mock): the real gigameter
        // distance between the buy and sell legs, only when both resolve on the starmap in the
        // same system - DistanceMeters already encodes both the resolution and the same-system
        // gate, so this is a single call, not a duplicated check.
        if (_starmap.DistanceMeters(buyTerm, sellTerm) is { } routeDistanceM
            && DistanceTag(StarmapCatalog.FormatGm(routeDistanceM)) is { } routeDistTag)
        {
            head.Children.Add(routeDistTag);
        }
        Grid.SetRow(head, 0); Grid.SetColumn(head, 0);
        grid.Children.Add(head);

        var profit = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        profit.Children.Add(new TextBlock { Text = "PROFIT / TRIP", FontFamily = Hud.Font("UiFont"), FontSize = 9, FontWeight = FontWeights.Bold, Foreground = Hud.Br("FgDimBrush"), HorizontalAlignment = HorizontalAlignment.Right });
        var profitRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        profitRow.Children.Add(new TextBlock
        {
            Text = r.Net.ToString("n0", CultureInfo.InvariantCulture), FontFamily = Hud.Font("MonoFont"), FontSize = 22,
            Foreground = Hud.Br("AccentBrush"),
            Effect = new DropShadowEffect { Color = Hud.Col("AccentBrush"), BlurRadius = 12, ShadowDepth = 0, Opacity = 0.35 },   // mock:236
        });
        profitRow.Children.Add(new TextBlock { Text = " aUEC", FontFamily = Hud.Font("UiFont"), FontSize = 10, Foreground = Hud.Br("FgDimBrush"), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(4, 0, 0, 3) });
        profit.Children.Add(profitRow);
        profit.Children.Add(new TextBlock
        {
            Text = $"{r.TripQty:n0} SCU trip - after fees", FontFamily = Hud.Font("UiFont"), FontSize = 10, Foreground = Hud.Br("FgDimBrush"), HorizontalAlignment = HorizontalAlignment.Right,
        });
        Grid.SetRow(profit, 0); Grid.SetRowSpan(profit, 2); Grid.SetColumn(profit, 1);
        grid.Children.Add(profit);

        // System tags for both legs, reusing the terminal lookups resolved above (dictionary read
        // per row, not a linear scan of Terminals.Rows).
        string? buySystem = buyTerm?.System;
        string? sellSystem = sellTerm?.System;

        var legs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        legs.Children.Add(BuildLeg("Buy at", r.BuyRow.TerminalName, buySystem, r.BuyRow.Buy, "STOCK", r.BuyRow.BuyStockScu, r.TripQty, r.BuyRow.ModifiedUtc, r.BuyRow.ContainerSizes, ship.MaxContainerScu));
        legs.Children.Add(new Path
        {
            Data = Geometry.Parse("M3,12 L18,12 M12,6 L18,12 L12,18"), Width = 20, Height = 20, Stroke = Hud.Br("FgDimBrush"),
            StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            Fill = Brushes.Transparent, Stretch = Stretch.Uniform, Margin = new Thickness(14, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center,
        });
        legs.Children.Add(BuildLeg("Sell at", r.SellRow.TerminalName, sellSystem, r.SellRow.Sell, "DEMAND", r.SellRow.SellDemandScu, r.TripQty, r.SellRow.ModifiedUtc, r.SellRow.ContainerSizes, ship.MaxContainerScu));
        Grid.SetRow(legs, 1); Grid.SetColumn(legs, 0);
        grid.Children.Add(legs);

        bool fits = TradeMath.BoxFits(r.BuyRow.ContainerSizes, ship.MaxContainerScu);   // RoutePlanner already
                                                                                          // filters incompatible pairs, so this is always true for a
                                                                                          // returned row - kept explicit rather than assumed silently.
        detailHost = DetailBand(new Thickness(0, 12, 0, 0), new Thickness(0, 12, 0, 0));
        var detail = new StackPanel();
        var fitLine = new StackPanel { Orientation = Orientation.Horizontal };
        fitLine.Children.Add(new Path { Data = Geometry.Parse("M5,13 L10,18 L19,6"), Width = 15, Height = 15, Stroke = Hud.Br("OkBrush"), StrokeThickness = 1.7, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round, Fill = Brushes.Transparent, Margin = new Thickness(0, 0, 8, 0) });
        fitLine.Children.Add(new TextBlock { Text = fits ? $"Box size OK for {ship.DisplayName} ({ship.MaxContainerScu} SCU crates)" : "Container size mismatch for this ship", FontFamily = Hud.Font("UiFont"), FontSize = 12, Foreground = Hud.Br("FgBrush") });
        detail.Children.Add(fitLine);
        detail.Children.Add(new TextBlock { Text = $"Trip size {r.TripQty:n0} SCU = smallest of: {string.Join(", ", r.TripParts)}", FontFamily = Hud.Font("UiFont"), FontSize = 11.5, Foreground = Hud.Br("FgDimBrush"), Margin = new Thickness(0, 9, 0, 0) });
        var feeLine = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 9, 0, 0) };
        feeLine.Children.Add(FeePart("Gross", r.Gross, Hud.Br("FgBrush")));
        if (r.Gross - r.Net != 0) feeLine.Children.Add(FeePart("Fees", r.Gross - r.Net, Hud.Br("FgBrush")));
        feeLine.Children.Add(FeePart("Net profit/trip", r.Net, Hud.Br("AccentBrush")));
        detail.Children.Add(feeLine);

        // Corroboration narration: only when BOTH legs reconcile to Corroborated (mock:802,
        // gate `sct && r.corrob` - the planner's only corroboration surface, no head badge here
        // per the mock, which shows nothing in the row head for RouteRow).
        var buyRec = Reconcile(r.BuyRow, "buy");
        var sellRec = Reconcile(r.SellRow, "sell");
        if (buyRec?.State == PriceSourceState.Corroborated && sellRec?.State == PriceSourceState.Corroborated)
        {
            var corrLine = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 9, 0, 0) };
            corrLine.Children.Add(CorroborationDotsGlyph());
            corrLine.Children.Add(new TextBlock
            {
                Text = "Corroborated by SC Trade Tools - both legs within 3% agreement, under 48h",
                FontFamily = Hud.Font("UiFont"), FontSize = 11.5, Foreground = Hud.Br("OkBrush"),
                Margin = new Thickness(6, 0, 0, 0),
            });
            FadeScaleIn(corrLine, 0.96);   // mock:803, scale 0.96 (not the badge's 0.8)
            detail.Children.Add(corrLine);
        }

        detailHost.Child = detail;
        Grid.SetRow(detailHost, 2); Grid.SetColumnSpan(detailHost, 2);
        grid.Children.Add(detailHost);

        chevron = ChevronGlyph();
        return grid;
    }

    private static StackPanel FeePart(string label, double value, Brush valueColor)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 20, 0) };
        p.Children.Add(new TextBlock { Text = $"{label}: ", FontFamily = Hud.Font("UiFont"), FontSize = 11.5, Foreground = Hud.Br("FgDimBrush") });
        p.Children.Add(new TextBlock { Text = $"{value:n0} aUEC", FontFamily = Hud.Font("MonoFont"), FontSize = 11.5, Foreground = valueColor });
        return p;
    }

    private static StackPanel BuildLeg(string eyebrow, string terminalName, string? system, double price, string qtyLabel, int qty, int tripQty, DateTime modifiedUtc, string containerSizes, int shipMaxContainerScu)
    {
        var leg = new StackPanel { MinWidth = 160, Margin = new Thickness(0, 0, 14, 0) };
        leg.Children.Add(new TextBlock { Text = eyebrow.ToUpperInvariant(), FontFamily = Hud.Font("UiFont"), FontSize = 9, FontWeight = FontWeights.Bold, Foreground = Hud.Br("FgDimBrush") });   // mock:239-241, letter-spacing not settable on TextBlock; size/weight/color match
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        // name keeps TextTrimming.CharacterEllipsis as its own direct Grid-cell child (deliberately
        // NOT wrapped into a StackPanel with the tag): a horizontal StackPanel measures its children
        // with infinite available width in the stack direction, which defeats CharacterEllipsis -
        // this leg is the one site on the page that relies on that trimming for long terminal
        // names, so the tag gets its own Auto column instead, immediately after the name's Star
        // column. That preserves the exact trimming guarantee this site was built with while still
        // placing the tag right after the name, inline, in the same row.
        var name = new TextBlock { Text = terminalName, FontFamily = Hud.Font("UiFont"), FontSize = 12, Foreground = Hud.Br("FgBrush"), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = terminalName };
        Grid.SetColumn(name, 0); top.Children.Add(name);
        // Owner's live-pass ask, 2026-07-30 (item A): "the system is extremely close to the price
        // per scu in the planner tab" - SystemTag's own left margin (6px) already separates it from
        // the name, but the two Auto columns (tag, priceRow) sit flush against each other with no
        // gap at all, so the tag crowded straight into the price. Right margin only, matching the
        // Sell flow's own tag-to-next-element gap idiom (TradePage.Sell.cs:393, 10px) at the top of
        // this fix's 10-12px ask so the two clusters (name+tag vs price) read as clearly distinct.
        if (SystemTag(system) is { } tag) { tag.Margin = new Thickness(6, 0, 12, 1); Grid.SetColumn(tag, 1); top.Children.Add(tag); }
        var priceRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        priceRow.Children.Add(new TextBlock { Text = price.ToString("n0", CultureInfo.InvariantCulture), FontFamily = Hud.Font("MonoFont"), FontSize = 13, Foreground = Hud.Br("GoldBrush") });
        priceRow.Children.Add(new TextBlock { Text = "/SCU", FontFamily = Hud.Font("UiFont"), FontSize = 10, Foreground = Hud.Br("FgDimBrush"), Margin = new Thickness(3, 0, 0, 0) });
        Grid.SetColumn(priceRow, 2); top.Children.Add(priceRow);
        leg.Children.Add(top);

        string tier = TradeBarMath.Tier(qty, tripQty);
        var barRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 0) };
        var bar = new Grid { Width = 90 };
        bar.Children.Add(TripBar(TradeBarMath.FillFraction(qty, tripQty), TradeBarMath.Color(tier),
            $"This bar shows how much of your trip the {(qtyLabel == "STOCK" ? "stock" : "demand")} covers. Green: covers your full trip. Amber: covers at least half. Red: less than half."));
        barRow.Children.Add(bar);
        barRow.Children.Add(new TextBlock { Text = $"{qtyLabel} {qty:n0} SCU", FontFamily = Hud.Font("MonoFont"), FontSize = 10, Foreground = Hud.Br("FgDimBrush"), Margin = new Thickness(8, 0, 0, 0) });
        // Max container size (task 2): AccentBrush warning when this leg's biggest box is smaller
        // than the ship's best - the trip needs smaller crates than the ship could otherwise carry.
        var legMaxScu = TradeMath.MaxContainerScu(containerSizes);
        if (MaxContainerChip(legMaxScu, warning: legMaxScu is { } m && m < shipMaxContainerScu) is { } maxChip) barRow.Children.Add(maxChip);
        leg.Children.Add(barRow);

        var age = DateTime.UtcNow - modifiedUtc;
        leg.Children.Add(FreshChip(FreshChipAge(age), age.TotalHours >= 24));   // shared idiom, see FreshChipAge
        return leg;
    }
}
