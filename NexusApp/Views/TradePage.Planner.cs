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
using System.Windows.Threading;
using NexusApp.Services;
using NexusApp.Services.Map;
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
    private Border _demandAnyPill = null!;
    private Border _demandMinPill = null!;
    private Border _demandTwoXPill = null!;
    private Border _rankProfitPill = null!;
    private Border _rankProfitPerScuPill = null!;
    private Border _rankProfitPerGmPill = null!;
    private readonly CargoShipCatalog _shipCatalog = CargoShipCatalog.LoadEmbedded();
    private int _plannerExpanded = -1;

    // STARTING LOCATION picker (task 10): replaces the old FROM HERE/ANYWHERE anchor pills
    // entirely (SetAnchor/_fromHerePill/_anywherePill are gone). "ANY" is the literal first
    // ComboBox item (= null originTerminalIds, old ANYWHERE); "LIVE - {location}" is present only
    // when App.Locations.LastKnownLocation is non-null (old FROM HERE's live half); every other
    // item is a priced terminal name (old FROM HERE's manual-pick half, now scoped to this one
    // picker instead of the shared ORIGIN chip - see TradePage.cs's now display-only chip). The
    // persisted value (AppSettings.TradeStartManual) is the raw KIND - "ANY", "LIVE", or the
    // terminal name itself - never the combo's own display text, since the "LIVE - {location}"
    // display string changes with the location while the kind does not. Same seed-once/revalidate-
    // per-rebuild idiom as the DESTINATION picker below.
    private const string AnyStart = "ANY";
    private const string LiveStartPrefix = "LIVE - ";
    private ComboBox _startCombo = null!;
    private Border _startLiveBtn = null!;       // small pill button: selects LIVE when a session is live, else just logs
    private List<string>? _startNames;          // the list currently bound to the ComboBox (DISPLAY strings - see _startDisplayToKind)
    private string? _startSelectedKind;          // "ANY" | "LIVE" | a terminal name; null only before the first seed
    private bool _startSeeded;
    private bool _suppressStartSelection;        // in-place ItemsSource/SelectedItem writes are not user picks

    // Location-first display (the owner's ask, 2026-07-31): the combo shows TradeOriginResolver.
    // LocationFirst's flipped label ("ARC-L1 - Admin") but _startSelectedKind/AppSettings.
    // TradeStartManual must keep the REAL UEX name the whole time (TerminalIdForName/
    // StartTerminalIds only ever resolve real names). Rebuilt fresh every RefreshStartCombo call,
    // display -> kind ("ANY"/"LIVE"/terminal name), so the SelectionChanged handler below can map
    // the combo's own displayed text straight back to the kind that gets persisted.
    private Dictionary<string, string>? _startDisplayToKind;

    // DESTINATION picker (task 6): "ANY" is the literal first ComboBox item and the sentinel this
    // page uses for "no constraint" - distinct from AppSettings.TradeDestManual, which persists
    // null/"" for that same state (see RefreshDestCombo/DestTerminalIds). Built once in
    // BuildPlannerChrome; the ComboBox's items and selection are updated in place on every rebuild
    // via RefreshDestCombo, same idiom as the Prices flow's commodity picker
    // (TradePage.Prices.cs, RefreshPricesCommodityBox). Moved into the ROUTE section (task 10),
    // alongside Starting Location, out of its own standalone input group.
    private const string AnyDestination = "ANY";
    private ComboBox _destCombo = null!;
    private List<string>? _destNames;          // the list currently bound to the ComboBox (leading "ANY", DISPLAY strings - see _destDisplayToName)
    private string? _destSelectedName;         // mirrors the ComboBox's SelectedItem; null only before the first seed; ALWAYS the real UEX name
    private bool _destSeeded;                   // seeds once from TradeDestManual, same idiom as the STARTING LOCATION picker above
    private bool _suppressDestSelection;        // in-place ItemsSource/SelectedItem writes are not user picks

    // Location-first display (the owner's ask, 2026-07-31), same mechanism as _startDisplayToKind
    // above: display -> real terminal name ("ANY" maps to itself), rebuilt fresh every
    // RefreshDestCombo call so SelectionChanged can map the combo's displayed text back to the
    // real name that gets persisted into AppSettings.TradeDestManual.
    private Dictionary<string, string>? _destDisplayToName;

    // The input area (ship combo, budget box, route pickers, caption) is built ONCE and only its
    // properties are updated afterwards; _plannerResults is the ONLY thing RebuildPlanner clears.
    // Before this, every rebuild started with PlannerHost.Children.Clear(), so the budget box's own
    // LostFocus - which WPF raises synchronously inside the mouse handling of whatever the user just
    // clicked - destroyed the ship combo and the route pickers mid-click and ate that first click.
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
    // cheap insurance against a future write-back (or IME composition) looping back in. Now
    // guards the DEBOUNCED call (see _budgetDebounceTimer below), not an immediate one.
    private bool _inBudgetLiveRerank;

    // Debounce for the live re-rank (the owner's live-lag report, 2026-07-31): RoutePlanner.Rank
    // bucketizes the WHOLE TradePrices row set (~2,600 rows in a live snapshot) and pairs every
    // buy against every sell PER COMMODITY, then RebuildPlanner rebuilds up to 25 WPF route rows
    // - firing that on every single keystroke stutters at typing speed. Same DispatcherTimer
    // idiom as ExecHangarStatusLine.cs (a single reused timer, never recreated per event): each
    // TextChanged restarts the timer instead of rebuilding immediately, so only the keystroke
    // that ends a quiet BudgetDebounceMs window actually triggers RebuildPlanner. Built lazily on
    // first use (BuildPlannerChrome only ever runs once, so "lazily" and "once" are the same
    // thing here) rather than in the ctor, matching this file's existing lazy-field style.
    // Internal (not private) so NexusApp.Tests can assert the interval constant directly - the
    // timer wiring itself is WPF dispatcher plumbing and not unit-testable (same documented
    // precedent as ExecHangarStatusLine's own 1-second ticker), so the interval value is the one
    // piece of this that a test can pin.
    internal const int BudgetDebounceMs = 250;
    private DispatcherTimer? _budgetDebounceTimer;

    // Built once (called only from the `??=` in the TextChanged handler above), then reused for
    // every later keystroke - a fresh DispatcherTimer per keystroke would be its own small waste
    // on top of the exact problem this exists to avoid. The Tick handler stops the timer before
    // doing anything else, same as ExecHangarStatusLine's own Tick pattern of never leaving a
    // timer running past the work it exists to gate, then defers to RebuildPlanner through the
    // same reentrancy guard the immediate call used to use directly.
    private DispatcherTimer MakeBudgetDebounceTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(BudgetDebounceMs) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_inBudgetLiveRerank) return;
            _inBudgetLiveRerank = true;
            try { RebuildPlanner(); }
            finally { _inBudgetLiveRerank = false; }
        };
        return timer;
    }

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

        var topRow = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };

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
        topRow.Children.Add(shipGrp);

        var budgetGrp = new StackPanel();
        budgetGrp.Children.Add(FieldLabel("Budget (optional)"));
        _budgetBox = new TextBox
        {
            Style = (Style)Application.Current.FindResource("NexusTextBox"), FontFamily = Hud.Font("MonoFont"),
            Width = 150, Text = _budgetText,
        };
        _budgetBox.LostFocus += (_, _) =>
        {
            // Cancel any pending debounced re-rank BEFORE the no-op guard below: a blur always
            // applies immediately from here on down, so a tick that lands after this handler
            // returns would just re-run RebuildPlanner a second time for nothing. Unconditional
            // and cheap (Stop() on an idle timer is a no-op), so it runs even on a no-op blur.
            _budgetDebounceTimer?.Stop();
            if (_budgetText == _budgetBox.Text) return;   // guard, same pattern as SetDemandFilter/SetScope: no-op blur never logs or rebuilds
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
        //
        // DEBOUNCED (the owner's live-lag report, 2026-07-31 - see _budgetDebounceTimer's own comment
        // above for the cost this avoids): every keystroke restarts the one shared timer instead
        // of rebuilding right away; only the Tick, after BudgetDebounceMs of quiet typing, calls
        // RebuildPlanner. The reentrancy guard (_inBudgetLiveRerank) moves down into the Tick
        // handler with it, unchanged in purpose - it still exists purely as insurance against a
        // future write-back or IME composition re-entering the rebuild, not because anything here
        // re-enters it today.
        _budgetBox.TextChanged += (_, _) =>
        {
            _budgetDebounceTimer ??= MakeBudgetDebounceTimer();
            _budgetDebounceTimer.Stop();
            _budgetDebounceTimer.Start();
        };
        budgetGrp.Children.Add(_budgetBox);
        topRow.Children.Add(budgetGrp);

        _plannerInputs.Children.Add(topRow);

        // ROUTE section (task 10): eyebrow header ("Route", the same FieldLabel idiom every other
        // group on this page already uses for its own field label) over Starting Location (combo +
        // LIVE button) side by side with the DESTINATION combo, moved in from its old standalone
        // group. This StackPanel's own bottom margin is the "visual line break" separating the
        // section from Ship/Budget above and Demand/Rank below - spacing, not a rule line, matching
        // how every other gap on this page is already built.
        var routeSection = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        routeSection.Children.Add(FieldLabel("Route"));
        var routeRow = new WrapPanel { Orientation = Orientation.Horizontal };

        var startGrp = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        startGrp.Children.Add(FieldLabel("Starting Location"));
        var startRow = new StackPanel { Orientation = Orientation.Horizontal };
        _startCombo = new ComboBox
        {
            Style = (Style)Application.Current.FindResource("NexusComboBox"), MinWidth = 170,
        };
        _startCombo.SelectionChanged += (_, _) =>
        {
            if (_suppressStartSelection) return;
            if (_startCombo.SelectedItem is not string display) return;
            // _startDisplayToKind (rebuilt every RefreshStartCombo) maps the flipped label the
            // user actually clicked back to the real kind SetStart persists; StartKindForDisplay
            // is only a defensive fallback (the map is always populated before the combo is
            // interactive) so a user pick can never persist a flipped display string.
            var kind = _startDisplayToKind is not null && _startDisplayToKind.TryGetValue(display, out var k)
                ? k
                : StartKindForDisplay(display);
            SetStart(kind);
        };
        startRow.Children.Add(_startCombo);
        _startLiveBtn = ScopePill("LIVE");
        _startLiveBtn.Margin = new Thickness(8, 0, 0, 0);
        _startLiveBtn.MouseLeftButtonUp += (_, _) => SetStartLive();
        startRow.Children.Add(_startLiveBtn);
        startGrp.Children.Add(startRow);
        routeRow.Children.Add(startGrp);

        // DESTINATION picker (task 6): a plain ComboBox, same idiom as the ORIGIN combo the
        // context row used to have before task 10 made it display-only - "ANY" is always the first
        // item, selecting it means no constraint.
        var destGrp = new StackPanel();
        destGrp.Children.Add(FieldLabel("Destination"));
        _destCombo = new ComboBox
        {
            Style = (Style)Application.Current.FindResource("NexusComboBox"), MinWidth = 160,
        };
        _destCombo.SelectionChanged += (_, _) =>
        {
            if (_suppressDestSelection) return;
            if (_destCombo.SelectedItem is not string display) return;
            // _destDisplayToName (rebuilt every RefreshDestCombo) maps the flipped label the user
            // actually clicked back to the real terminal name that gets persisted - the fallback
            // to `display` itself only matters before the map is ever populated, and "ANY" always
            // maps to itself.
            var name = _destDisplayToName is not null && _destDisplayToName.TryGetValue(display, out var real) ? real : display;
            SetDest(name);
        };
        destGrp.Children.Add(_destCombo);
        routeRow.Children.Add(destGrp);

        routeSection.Children.Add(routeRow);
        _plannerInputs.Children.Add(routeSection);

        var bottomRow = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };

        // Demand-at-destination filter (task 5, resemantic task 10): same pill chrome as every
        // other group on this page, three mutually-exclusive states, using SetPillOn per pill so
        // the highlighted-state visuals stay identical across every pill group.
        var demandFilterGrp = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        demandFilterGrp.Children.Add(FieldLabel("Demand at Destination"));
        var demandFilterRow = new StackPanel { Orientation = Orientation.Horizontal };
        _demandAnyPill = ScopePill("ANY");
        _demandMinPill = ScopePill("MIN FOR TRIP");
        _demandTwoXPill = ScopePill("2X FOR TRIP");
        _demandAnyPill.MouseLeftButtonUp += (_, _) => SetDemandFilter(StockFilter.Any);
        _demandMinPill.MouseLeftButtonUp += (_, _) => SetDemandFilter(StockFilter.CoversTrip);
        _demandTwoXPill.MouseLeftButtonUp += (_, _) => SetDemandFilter(StockFilter.CoversTwoTrips);
        demandFilterRow.Children.Add(_demandAnyPill);
        demandFilterRow.Children.Add(_demandMinPill);
        demandFilterRow.Children.Add(_demandTwoXPill);
        demandFilterGrp.Children.Add(demandFilterRow);
        bottomRow.Children.Add(demandFilterGrp);

        // Rank mode (task 7): same pill idiom as Demand at Destination above - PROFIT (default)
        // orders by raw net/trip; PROFIT PER SCU re-ranks by net/tripQty, surfacing high-margin
        // small-qty routes over high-net bulk ones (RoutePlanner.RankMode).
        var rankModeGrp = new StackPanel();
        rankModeGrp.Children.Add(FieldLabel("Rank by"));
        var rankModeRow = new StackPanel { Orientation = Orientation.Horizontal };
        _rankProfitPill = ScopePill("PROFIT");
        _rankProfitPerScuPill = ScopePill("PROFIT PER SCU");
        _rankProfitPerGmPill = ScopePill("PROFIT PER Gm");
        _rankProfitPill.MouseLeftButtonUp += (_, _) => SetRankMode(RankMode.Profit);
        _rankProfitPerScuPill.MouseLeftButtonUp += (_, _) => SetRankMode(RankMode.ProfitPerScu);
        _rankProfitPerGmPill.MouseLeftButtonUp += (_, _) => SetRankMode(RankMode.ProfitPerGm);
        rankModeRow.Children.Add(_rankProfitPill);
        rankModeRow.Children.Add(_rankProfitPerScuPill);
        rankModeRow.Children.Add(_rankProfitPerGmPill);
        rankModeGrp.Children.Add(rankModeRow);
        bottomRow.Children.Add(rankModeGrp);

        _plannerInputs.Children.Add(bottomRow);

        _plannerInputs.Children.Add(new TextBlock
        {
            Text = "Ranked by what a trip really pays with your ship and budget, not raw margin. Bars show trip coverage.",   // mock:857, verbatim
            FontFamily = Hud.Font("UiFont"), FontSize = 10.5, Foreground = Hud.Br("FgDimBrush"), Margin = new Thickness(0, 0, 0, 14),
        });

        _plannerResults = new StackPanel();

        // Anchored inputs (task 10): PlannerHost is a Grid (TradePage.cs) - Auto row for
        // _plannerInputs (never scrolls) + Star row for a ScrollViewer around _plannerResults only,
        // so ship/budget/route/demand/rank stay on screen while just the results list scrolls. The
        // only pane built this way; Sell/Prices keep the single whole-pane ScrollViewer
        // (TradePage.cs's WrapPane), since only the planner flow was asked to anchor its inputs.
        PlannerHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        PlannerHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(_plannerInputs, 0);
        PlannerHost.Children.Add(_plannerInputs);
        var resultsScroll = new ScrollViewer
        {
            Content = _plannerResults,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Grid.SetRow(resultsScroll, 1);
        PlannerHost.Children.Add(resultsScroll);
    }

    // "ANY" and "LIVE - {location}" are the only two combo items that do not literally name a
    // terminal, so mapping the combo's own display text back to the persisted kind is a plain
    // prefix/equality check rather than a second lookup table.
    private static string StartKindForDisplay(string display) =>
        display == AnyStart ? AnyStart :
        display.StartsWith(LiveStartPrefix, StringComparison.Ordinal) ? "LIVE" :
        display;

    private void RebuildPlanner()
    {
        BuildPlannerChrome();
        if (!EnsureMarketConsent(_plannerResults, _plannerInputs)) return;
        _plannerResults.Children.Clear();
        _pinChips.Clear();   // the chips belonged to the rows just dropped
        _plannerExpanded = -1;

        var snap = App.Market.Snapshot;
        RefreshStartCombo(snap);
        RefreshDestCombo(snap);
        RefreshDemandFilterPills();
        RefreshRankModePills();

        // Small dim note above the results list, only when the DESTINATION picker is actually
        // constraining sell legs - covers both the empty-state and populated branches below, since
        // both need the same explanation for why routes are narrowed (task 6).
        bool destActive = _destSelectedName is not null && _destSelectedName != AnyDestination;
        if (destActive) _plannerResults.Children.Add(DestinationActiveNote(_destSelectedName!));

        if (snap is null || snap.TradePrices.Rows.Count == 0)
        {
            _plannerResults.Children.Add(EmptyOrStaleNote(snap?.TradePrices.FetchedUtc));
            return;
        }

        // Terminal lookup, built once per rebuild: TerminalId -> MarketTerminal. Reused below both
        // for origin resolution and for each route's System tags (Buy/Sell legs).
        var terminals = snap.Terminals.Rows.ToDictionary(t => t.Id);
        var ship = CurrentShip();
        // Starting Location (task 10): TradeOriginResolver.StartTerminalIds is the one pure seam
        // that turns the combo's persisted kind (ANY/LIVE/a terminal name) into the terminal id set
        // RoutePlanner needs - null only for ANY; every other kind is non-null, EMPTY when it could
        // not resolve (no live session for LIVE, an unrecognized name), which restricts the buy leg
        // to zero rather than silently falling back to ANY (same contract DestTerminalIds already
        // uses for the sell leg). That empty case gets its own message below instead of the generic
        // "no routes buy from here" one, since here the problem is an unresolved start, not a real
        // absence of routes.
        var originIds = TradeOriginResolver.StartTerminalIds(App.Settings.Current.TradeStartManual,
            App.Locations.LastKnownLocation, snap.Terminals.Rows, App.Locations.LastKnownUexLocation);
        bool originUnknown = originIds is { Count: 0 };
        var destIds = DestTerminalIds(snap.Terminals.Rows);
        var routes = RoutePlanner.Rank(snap.TradePrices.Rows, terminals, ship.TotalScu, ship.MaxContainerScu,
            CurrentBudget(), originIds, App.Settings.Current.TradeScope, take: 25,
            ParseDemandFilter(App.Settings.Current.TradeStockFilter), destIds,
            ParseRankMode(App.Settings.Current.TradeRankMode),
            // The same measurement this page already renders per row as a dim decoration - now it
            // can also do the ranking, instead of the planner sorting purely on money while showing
            // a distance it ignored.
            App.Map.DistanceMeters);

        // Pin refresh (Task 8, resemantic 2026-08-01 when pins began surviving a restart). A pin is
        // identified by its (buy terminal, sell terminal, commodity) triple, not by object identity:
        // this `routes` list is a brand new set of TradeRoute instances every rebuild. Any pin the
        // fresh ranking contains has its trip quantity and margin brought up to date here.
        //
        // It no longer DROPS the pins the ranking is missing. That rule was right while a pin lasted
        // only as long as the session that made it; a ranking is the best 25 routes for the ship,
        // budget and scope selected right now, so once pins persist, "not in the top 25" would have
        // silently erased them the first time the user switched ships.
        RefreshPins(routes);

        if (routes.Count == 0)
        {
            // Empty-state ladder, most specific cause first. The two scope-conflict rungs (A3) sit
            // above the generic messages because a contradiction between the scope pill and a
            // picker is not an absence of routes, and reporting it as one sends the user off
            // adjusting ship, budget and demand filter to fix something none of those can reach.
            // DESTINATION is tested before STARTING LOCATION only to pick one when both conflict:
            // the message stays short, and fixing the first surfaces the second on the next run.
            var scope = App.Settings.Current.TradeScope;
            string message;
            if (originUnknown)
            {
                message = "Starting location unknown - pick one above, or set it to ANY.";
                Logger.Info("[UI] Trade planner run: 0 routes, origin unknown");
            }
            else if (RoutePlanner.ChosenSystemOutsideScope(destIds, terminals, scope) is { } destSystem)
            {
                // Named with the same location-first label the user picked from the dropdown, not
                // the raw UEX name, so the message points at something they recognize.
                message = $"{TradeOriginResolver.LocationFirst(_destSelectedName)} is in {destSystem}, "
                        + $"but your scope is {scope}. Widen the scope to ALL, or pick a destination in {scope}.";
                Logger.Info($"[UI] Trade planner run: 0 routes, destination outside scope {scope} (in {destSystem})");
            }
            else if (RoutePlanner.ChosenSystemOutsideScope(originIds, terminals, scope) is { } startSystem)
            {
                message = $"Your starting location is in {startSystem}, but your scope is {scope}. "
                        + $"Widen the scope to ALL, or start from somewhere in {scope}.";
                Logger.Info($"[UI] Trade planner run: 0 routes, starting location outside scope {scope} (in {startSystem})");
            }
            else
            {
                message = originIds is not null
                    ? "No routes buy from your starting location right now. Try ANY, or a wider scope."
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

        Logger.Info($"[UI] Trade planner run: {routes.Count} routes, ship {ship.Id}, scope {App.Settings.Current.TradeScope}, start {App.Settings.Current.TradeStartManual ?? "LIVE"}, dest {_destSelectedName ?? AnyDestination}");
    }

    // Starting Location (task 10): mirrors SetDemandFilter's no-op-on-unchanged guard - a pick that
    // matches the current kind never logs or rebuilds.
    private void SetStart(string kind)
    {
        if (_startSelectedKind == kind) return;
        _startSelectedKind = kind;
        App.Settings.Current.TradeStartManual = kind;
        App.Settings.Save();
        Logger.Info($"[UI] trade: start {kind}");
        RebuildPlanner();
    }

    // DESTINATION's counterpart to SetStart, extracted from the combo's own SelectionChanged so the
    // map's SEND TO PLANNER has a single path to set a destination (app review: it previously had
    // none, which is part of why it could only ever send a start). Same no-op-on-unchanged guard, so
    // re-picking what is already selected never logs or reruns. Null and "" both mean ANY, matching
    // AppSettings.TradeDestManual's own contract.
    private void SetDest(string? name)
    {
        var chosen = string.IsNullOrEmpty(name) ? AnyDestination : name;
        if (_destSelectedName == chosen) return;

        _destSelectedName = chosen;
        App.Settings.Current.TradeDestManual = chosen == AnyDestination ? null : chosen;
        App.Settings.Save();
        Logger.Info($"[UI] trade: destination {chosen}");
        RebuildPlanner();
    }

    // LIVE button: selects the LIVE item when a session is actually live. With no live session it
    // does nothing to the selection - only logs - rather than picking a kind that would just
    // resolve to the same empty/origin-unknown state the combo already shows.
    private void SetStartLive()
    {
        if (App.Locations.LastKnownLocation is null)
        {
            Logger.Info("[UI] trade: start live unavailable");
            return;
        }
        SetStart("LIVE");
    }

    // Re-validate on every rebuild, same idiom as RefreshDestCombo: an hourly snapshot refresh can
    // drop a previously picked terminal name, and the very first rebuild ever runs before any
    // snapshot exists. Unlike the destination combo, "LIVE" is a KIND that survives even while its
    // display item is temporarily absent (no live session right now) - the same way the old FROM
    // HERE anchor stayed selected through a gap in live location data; only a terminal-name pick
    // that drops out of the currently offered list falls back to ANY.
    private void RefreshStartCombo(MarketSnapshot? snap)
    {
        string? liveLoc = App.Locations.LastKnownLocation;
        var terminalNames = TerminalNames(snap);

        // Location-first display (the owner's ask, 2026-07-31): kindToDisplay/displayToKind are
        // rebuilt fresh on every refresh from the current terminal list - they never persist
        // anything themselves, they just translate between the real kind (_startSelectedKind,
        // AppSettings.TradeStartManual) and the flipped label shown on screen. "ANY" and the
        // "LIVE - {location}" item are NOT run through LocationFirst: they are not terminal
        // names, and flipping "LIVE - {location}" would mangle it into "{location} - LIVE".
        var kindToDisplay = new Dictionary<string, string>(StringComparer.Ordinal) { [AnyStart] = AnyStart };
        var displayToKind = new Dictionary<string, string>(StringComparer.Ordinal) { [AnyStart] = AnyStart };
        if (liveLoc is not null)
        {
            var liveDisplay = $"{LiveStartPrefix}{liveLoc}";
            kindToDisplay["LIVE"] = liveDisplay;
            displayToKind[liveDisplay] = "LIVE";
        }
        foreach (var n in terminalNames)
        {
            var disp = TradeOriginResolver.LocationFirst(n);
            kindToDisplay[n] = disp;
            displayToKind[disp] = n;
        }
        _startDisplayToKind = displayToKind;

        // The combo's own item order is sorted by DISPLAY text, not TerminalNames' raw-name
        // order - grouping by location (the whole point of the flip) only shows up if the list
        // itself is ordered by the flipped label.
        var names = new List<string> { AnyStart };
        if (liveLoc is not null) names.Add(kindToDisplay["LIVE"]);
        names.AddRange(terminalNames.Select(n => kindToDisplay[n]).OrderBy(d => d, StringComparer.OrdinalIgnoreCase));

        if (!_startSeeded)
        {
            // A5: a null seed means "not yet" - the persisted terminal name met an EMPTY list,
            // which is the planner opening before the first market fetch lands, not a stale name.
            // Staying unseeded lets the next refresh (first snapshot in) seed against a real list;
            // the combo honestly shows nothing selected meanwhile. Consuming the seed here used to
            // turn the saved start into the LIVE fallback for the rest of the session, and
            // SetStart's no-op guard then made the mismatch sticky: the ranking kept restricting
            // to the saved terminal while the combo claimed LIVE, and picking LIVE looked like a
            // no-op so it never persisted.
            if (SeedStartKind(App.Settings.Current.TradeStartManual, terminalNames) is { } seed)
            {
                _startSeeded = true;
                _startSelectedKind = seed;
            }
        }
        else if (_startSelectedKind is null ||
                 (_startSelectedKind != AnyStart && _startSelectedKind != "LIVE" && !terminalNames.Contains(_startSelectedKind)))
        {
            _startSelectedKind = AnyStart;
        }

        // The combo shows nothing selected while the kind is LIVE but no live item exists this
        // render (no session) - an honest "nothing to show" beats highlighting ANY, which would
        // read as "unrestricted" when the real state is "unresolved, waiting on a live location."
        // The terminal-name branch below maps through kindToDisplay so the combo shows the
        // flipped label for the currently selected kind, never the raw persisted name.
        string? display = _startSelectedKind switch
        {
            "LIVE" => liveLoc is not null ? kindToDisplay["LIVE"] : null,
            AnyStart => AnyStart,
            null => null,
            _ => kindToDisplay.TryGetValue(_startSelectedKind, out var d) ? d : _startSelectedKind,
        };

        _suppressStartSelection = true;
        try
        {
            if (_startNames is null || !_startNames.SequenceEqual(names, StringComparer.Ordinal))
            {
                _startNames = names;
                _startCombo.ItemsSource = names;
            }
            if (!string.Equals(_startCombo.SelectedItem as string, display, StringComparison.Ordinal))
                _startCombo.SelectedItem = display;
        }
        finally { _suppressStartSelection = false; }

        bool liveSelected = _startSelectedKind == "LIVE" && liveLoc is not null;
        SetPillOn(_startLiveBtn, liveSelected);
        _startLiveBtn.Opacity = liveLoc is null ? 0.45 : 1.0;
    }

    // The first-render seed decision, pure so a test can hold it still (A5, final review F4).
    // ANY, LIVE and the null/empty first-run default depend on nothing, so they seed no matter
    // what the list holds. A terminal NAME is the one kind that must be checked against the list,
    // and against an EMPTY list "not found" is indistinguishable from "market data not loaded
    // yet" - so empty defers (null return) instead of guessing, while a real list that lacks the
    // name means the name went stale and fails open to LIVE (the old FROM HERE default).
    internal static string? SeedStartKind(string? persisted, IReadOnlyCollection<string> terminalNames) =>
        persisted switch
        {
            AnyStart => AnyStart,
            "LIVE" => "LIVE",
            null or "" => "LIVE",
            _ when terminalNames.Contains(persisted) => persisted,
            _ when terminalNames.Count == 0 => null,
            _ => "LIVE",
        };

    // Demand-at-destination filter (task 5, resemantic task 10): DEMAND-ONLY. Buy stock already
    // caps tripQty via TradeMath.TripQty (a route can never trip more than the terminal has to
    // sell), so the buy side is self-limiting and no longer independently checked here - only
    // RoutePlanner.PassesStockFilter's sellDemandScu comparison remains. Same no-op-on-unchanged
    // guard as SetRankMode: a click on the pill that is already active never logs or rebuilds.
    private void SetDemandFilter(StockFilter filter)
    {
        var persisted = DemandFilterPersistValue(filter);
        if (App.Settings.Current.TradeStockFilter == persisted) return;
        App.Settings.Current.TradeStockFilter = persisted;
        App.Settings.Save();
        Logger.Info($"[UI] trade: demand filter {DemandFilterPillText(filter)}");
        RebuildPlanner();
    }

    private void RefreshDemandFilterPills()
    {
        var active = ParseDemandFilter(App.Settings.Current.TradeStockFilter);
        SetPillOn(_demandAnyPill, active == StockFilter.Any);
        SetPillOn(_demandMinPill, active == StockFilter.CoversTrip);
        SetPillOn(_demandTwoXPill, active == StockFilter.CoversTwoTrips);
    }

    // Persisted setting values (task 10): short, and deliberately distinct from the pill's own
    // display text below - "ANY"/"MIN"/"2X", not the longer "MIN FOR TRIP"/"2X FOR TRIP" a reader
    // sees on screen.
    private static string DemandFilterPersistValue(StockFilter filter) => filter switch
    {
        StockFilter.CoversTrip => "MIN",
        StockFilter.CoversTwoTrips => "2X",
        _ => "ANY",
    };

    private static string DemandFilterPillText(StockFilter filter) => filter switch
    {
        StockFilter.CoversTrip => "MIN FOR TRIP",
        StockFilter.CoversTwoTrips => "2X FOR TRIP",
        _ => "ANY",
    };

    // Fail-open: an unrecognized persisted value (corrupt settings.json, a future rollback) falls
    // back to Any. Also accepts the pre-task-10 "COVERS TRIP"/"COVERS 2X" strings, so a
    // settings.json written before this rename still resolves to the same tier instead of silently
    // resetting to Any.
    private static StockFilter ParseDemandFilter(string? value) => value switch
    {
        "MIN" or "COVERS TRIP" => StockFilter.CoversTrip,
        "2X" or "COVERS 2X" => StockFilter.CoversTwoTrips,
        _ => StockFilter.Any,
    };

    // Same no-op-on-unchanged guard as SetDemandFilter: a click on the pill that is already active
    // never logs or rebuilds.
    private void SetRankMode(RankMode mode)
    {
        var label = RankModeLabel(mode);
        if (App.Settings.Current.TradeRankMode == label) return;
        App.Settings.Current.TradeRankMode = label;
        App.Settings.Save();
        Logger.Info($"[UI] trade: rank mode {label}");
        RebuildPlanner();
    }

    private void RefreshRankModePills()
    {
        var active = ParseRankMode(App.Settings.Current.TradeRankMode);
        SetPillOn(_rankProfitPill, active == RankMode.Profit);
        SetPillOn(_rankProfitPerScuPill, active == RankMode.ProfitPerScu);
        SetPillOn(_rankProfitPerGmPill, active == RankMode.ProfitPerGm);
    }

    private static string RankModeLabel(RankMode mode) => mode switch
    {
        RankMode.ProfitPerScu => "PROFIT PER SCU",
        RankMode.ProfitPerGm => "PROFIT PER GM",
        _ => "PROFIT",
    };

    // Any stored value that isn't a recognized label (corrupt settings.json, a future rollback)
    // falls back to Profit - the planner's original ordering, same fail-open idiom as ParseDemandFilter.
    private static RankMode ParseRankMode(string? value) => value switch
    {
        "PROFIT PER SCU" => RankMode.ProfitPerScu,
        "PROFIT PER GM" => RankMode.ProfitPerGm,
        _ => RankMode.Profit,
    };

    // Re-validate on every rebuild, same rule as the Prices flow's RefreshPricesCommodityBox: an
    // hourly snapshot refresh can drop the previously selected destination terminal (or the very
    // first rebuild ever runs before any snapshot exists), and a one-time seed would leave the
    // field stuck on a name no longer offered. First call ever seeds from the persisted
    // TradeDestManual setting (null/"" falls back to ANY); every later call revalidates the
    // CURRENT selection against the CURRENT terminal list instead of re-reading the setting, so a
    // live user pick this session is never clobbered by what was last saved.
    private void RefreshDestCombo(MarketSnapshot? snap)
    {
        var terminalNames = TerminalNames(snap);

        // Location-first display (the owner's ask, 2026-07-31), same mechanism as RefreshStartCombo
        // above: nameToDisplay/displayToName are rebuilt fresh on every refresh and never touch
        // _destSelectedName or AppSettings.TradeDestManual, which stay the real UEX name the
        // whole time (SelectionChanged persists `name`, never `display`). "ANY" is not a
        // terminal name and is never run through LocationFirst.
        var nameToDisplay = new Dictionary<string, string>(StringComparer.Ordinal) { [AnyDestination] = AnyDestination };
        var displayToName = new Dictionary<string, string>(StringComparer.Ordinal) { [AnyDestination] = AnyDestination };
        foreach (var n in terminalNames)
        {
            var disp = TradeOriginResolver.LocationFirst(n);
            nameToDisplay[n] = disp;
            displayToName[disp] = n;
        }
        _destDisplayToName = displayToName;

        // Sorted by DISPLAY text, not TerminalNames' raw-name order - same reasoning as the
        // STARTING LOCATION combo above.
        var names = new List<string> { AnyDestination };
        names.AddRange(terminalNames.Select(n => nameToDisplay[n]).OrderBy(d => d, StringComparer.OrdinalIgnoreCase));

        if (!_destSeeded)
        {
            _destSeeded = true;
            var persisted = App.Settings.Current.TradeDestManual;
            _destSelectedName = !string.IsNullOrEmpty(persisted) && terminalNames.Contains(persisted) ? persisted : AnyDestination;
        }
        else if (_destSelectedName is null || (_destSelectedName != AnyDestination && !terminalNames.Contains(_destSelectedName)))
        {
            _destSelectedName = AnyDestination;
        }

        string display = _destSelectedName == AnyDestination
            ? AnyDestination
            : nameToDisplay.TryGetValue(_destSelectedName!, out var d) ? d : _destSelectedName!;

        _suppressDestSelection = true;
        try
        {
            if (_destNames is null || !_destNames.SequenceEqual(names, StringComparer.Ordinal))
            {
                _destNames = names;
                _destCombo.ItemsSource = names;
            }
            if (!string.Equals(_destCombo.SelectedItem as string, display, StringComparison.Ordinal))
                _destCombo.SelectedItem = display;
        }
        finally { _suppressDestSelection = false; }
    }

    // Resolves the DESTINATION combo's current pick to the terminal id set RoutePlanner needs.
    // "ANY" (the default, and the literal first combo item) means no constraint - null, the same
    // sentinel RoutePlanner already uses for "unrestricted" on the buy leg. A named terminal that
    // fails to resolve (TerminalIdForName returns null - a stale persisted name no live terminal
    // matches) yields an EMPTY set rather than falling back to ANY, mirroring OriginTerminalIds'
    // contract: an unresolved constraint restricts to nothing, it never silently widens back out.
    private IReadOnlySet<int>? DestTerminalIds(IReadOnlyList<MarketTerminal> terminals)
    {
        if (_destSelectedName is null || _destSelectedName == AnyDestination) return null;
        return TradeOriginResolver.TerminalIdForName(_destSelectedName, terminals) is { } id
            ? new HashSet<int> { id }
            : new HashSet<int>();
    }

    // Small dim note (task 6, brief's "results header" fallback: no persistent header line exists
    // in the planner results area to append onto, so this is a standalone TextBlock shown above the
    // results list only while the DESTINATION picker is actively constraining sell legs).
    private static TextBlock DestinationActiveNote(string name) => new()
    {
        Text = $"Routes ranked TO {name}.",
        FontFamily = Hud.Font("UiFont"), FontSize = 11.5, Foreground = Hud.Br("FgDimBrush"), Margin = new Thickness(0, 0, 0, 10),
    };

    private static void SetPillOn(Border pill, bool on)
    {
        var text = (TextBlock)pill.Child;
        text.Foreground = on ? Hud.Br("AccentBrush") : Hud.Br("FgDimBrush");
        pill.BorderBrush = on ? Hud.Br("AccentStrongBrush") : Hud.Br("NavBorderBrush");
        pill.Background = on ? Hud.Br("AccentFaintBrush") : Hud.Br("Bg2NavBrush");
    }

    // PIN chip (Task 8): house chip geometry (mono-free, matches TierChip/ScopePill: 1px border,
    // radius 3, padding 7,2,7,2 - TierChip's exact numbers), active state Gold like the tab strip's
    // own active color (TabColor, TradePage.cs:412) rather than the amber AccentBrush every other
    // toggle chip on this page uses - a pin is a session marker, not a filter, and reusing the tab
    // strip's own "this is the one you're on" color keeps that distinction visible at a glance.
    private static Border PinChip(bool active)
    {
        var text = new TextBlock
        {
            // "PIN TO OVERLAY", not "PIN" (the owner, 2026-08-01): a bare PIN said nothing about where
            // the route goes, and the overlay is now the surface it goes to - the Starmap leg is a
            // second effect, and the tooltip below is where that belongs.
            Text = "PIN TO OVERLAY", FontFamily = Hud.Font("UiFont"), FontSize = 9, FontWeight = FontWeights.Bold,
        };
        var chip = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3), Padding = new Thickness(7, 2, 7, 2), Cursor = Cursors.Hand,
            Margin = new Thickness(8, 0, 0, 0), Child = text, VerticalAlignment = VerticalAlignment.Center,
            // Reworded 2026-08-01 (app review): the old text promised the route "stays here through
            // a refresh", which RebuildPlanner never implements - it ranks fresh every time and the
            // pin's only effect on THIS list is to be cleared when the route falls out of it. The
            // pin's actual payoff is on other surfaces entirely, which no Trade surface mentioned.
            // Reworded again the same day, once several routes could be pinned at once: the chip
            // now names its main destination and the tooltip carries the rest, including the cap.
        };
        ApplyPinChipVisual(chip, active);
        return chip;
    }

    // Repaints an existing chip rather than rebuilding it (the owner's live pass, 2026-08-01: "when i
    // click pin to overlay the main app flashes and lags a bit"). The click handler used to call
    // RebuildPlanner, which re-ranks ~2,600 price rows and rebuilds up to 25 route rows WITH their
    // staggered entrance cascade - to change the colour of one chip. That replayed entrance is the
    // flash, the rank plus 25 rebuilds is the lag, and it also collapsed whatever row the user had
    // expanded. Nothing about the ranking changes when a route is pinned.
    private static void ApplyPinChipVisual(Border chip, bool active)
    {
        ((TextBlock)chip.Child).Foreground = active ? Hud.Br("GoldBrush") : Hud.Br("FgDimBrush");
        chip.BorderBrush = active ? Hud.Br("GoldBrush") : Hud.Br("BorderBrush");
        chip.ToolTip = active
            ? "Stop showing this route in the overlay and on the Starmap."
            : $"Show this route in the overlay's TRADE tab and on the Starmap. Up to {RoutePlanner.MaxPins} at once.";
    }

    // Every pin chip currently on screen, so a pin can repaint all of them in place. ALL of them,
    // not just the one clicked: pinning at the cap evicts the OLDEST pin, and that route may be
    // visible in this same list, so its chip has to go dim in the same gesture.
    private readonly List<(TradeRoute Route, Border Chip)> _pinChips = new();

    /// <summary>Repaints every pin chip on screen from the current pin list. Internal because the
    /// overlay's own per-card close reaches it through MainWindow: that path used to call Refresh(),
    /// which rebuilds all THREE flows (planner, sell, prices) to dim one chip.</summary>
    internal void RefreshPinChips()
    {
        foreach (var (route, chip) in _pinChips) ApplyPinChipVisual(chip, IsPinned(route));
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
            && DistanceTag(MapCatalog.FormatGm(routeDistanceM)) is { } routeDistTag)
        {
            head.Children.Add(routeDistTag);
        }
        // Rank mode (task 7): only while RANK BY is set to PROFIT PER SCU, name the per-SCU figure
        // routes are actually being sorted by - otherwise the ranking looks arbitrary next to the
        // PROFIT / TRIP headline, which always shows raw Net regardless of rank mode.
        if (ParseRankMode(App.Settings.Current.TradeRankMode) == RankMode.ProfitPerScu)
        {
            head.Children.Add(RankPerScuTag(r.Net, r.TripQty));
        }
        // PIN toggle (Task 8, MAP tab route pinning): active state comes from IsPinned, which is
        // the same triple rule the stale-pin drop in RebuildPlanner applies, so "is this row
        // pinned" can never disagree with what the overlay and the map are showing - one rule, not
        // three hand-written comparisons.
        // Last in the head row: the informational tags read together, the action sits at the end.
        var pinChip = PinChip(IsPinned(r));
        _pinChips.Add((r, pinChip));
        pinChip.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;   // never bubble to the row host and toggle the expand band
            PinRoute(r);
            RefreshPinChips();   // repaint in place; see ApplyPinChipVisual for why not a rebuild
        };
        head.Children.Add(pinChip);
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
        legs.Children.Add(BuildLeg("Buy at", r.BuyRow.TerminalName, buySystem, r.BuyRow.Buy, "STOCK", r.BuyRow.BuyStockScu, r.TripQty, r.BuyRow.ModifiedUtc, r.BuyRow.ContainerSizes, ship.MaxContainerScu, buyTerm, RaiseShowOnMap));
        legs.Children.Add(new Path
        {
            Data = Geometry.Parse("M3,12 L18,12 M12,6 L18,12 L12,18"), Width = 20, Height = 20, Stroke = Hud.Br("FgDimBrush"),
            StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            Fill = Brushes.Transparent, Stretch = Stretch.Uniform, Margin = new Thickness(14, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center,
        });
        legs.Children.Add(BuildLeg("Sell at", r.SellRow.TerminalName, sellSystem, r.SellRow.Sell, "DEMAND", r.SellRow.SellDemandScu, r.TripQty, r.SellRow.ModifiedUtc, r.SellRow.ContainerSizes, ship.MaxContainerScu, sellTerm, RaiseShowOnMap));
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

    // Per-route aUEC/SCU tag (task 7): only rendered while RANK BY is PROFIT PER SCU, naming the
    // exact figure that mode is sorting by. Same dim/small/no-chrome geometry as MaxContainerChip/
    // DistanceTag (TradePage.cs) - MonoFont like MaxContainerChip since this is a numeric rate
    // sitting beside other numerals, not a proper-noun label like DistanceTag's Gm readout.
    private static FrameworkElement RankPerScuTag(double net, int tripQty)
    {
        double n = tripQty > 0 ? Math.Round(net / tripQty) : 0;
        return new TextBlock
        {
            Text = $"{n.ToString("n0", CultureInfo.InvariantCulture)} aUEC/SCU", FontFamily = Hud.Font("MonoFont"), FontSize = 9,
            FontWeight = FontWeights.Bold, Foreground = Hud.Br("FgDimBrush"),
            Margin = new Thickness(6, 0, 0, 1), VerticalAlignment = VerticalAlignment.Bottom,
        };
    }

    private static StackPanel FeePart(string label, double value, Brush valueColor)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 20, 0) };
        p.Children.Add(new TextBlock { Text = $"{label}: ", FontFamily = Hud.Font("UiFont"), FontSize = 11.5, Foreground = Hud.Br("FgDimBrush") });
        p.Children.Add(new TextBlock { Text = $"{value:n0} aUEC", FontFamily = Hud.Font("MonoFont"), FontSize = 11.5, Foreground = valueColor });
        return p;
    }

    private static StackPanel BuildLeg(string eyebrow, string terminalName, string? system, double price, string qtyLabel, int qty, int tripQty, DateTime modifiedUtc, string containerSizes, int shipMaxContainerScu, MarketTerminal? terminal = null, Action<int>? onShowOnMap = null)
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
        // The leg name opens its stop on the Starmap (app review G7b, the other half of the
        // one-way leaf the Codex LOCATIONS rows closed). RESOLVE-THEN-DECORATE, per leg, exactly as
        // that list does: a terminal the geometry catalog cannot place keeps the appearance it has
        // always had - no cursor, no tooltip change, no handler - rather than offering a jump that
        // would do nothing.
        if (onShowOnMap is not null && App.Map.ResolveTerminal(terminal) is { } stop)
        {
            name.Cursor = Cursors.Hand;
            name.ToolTip = $"{terminalName}{Environment.NewLine}Show {stop.Name} on the Starmap.";
            name.MouseEnter += (_, _) => name.Foreground = Hud.Br("AccentBrush");
            name.MouseLeave += (_, _) => name.Foreground = Hud.Br("FgBrush");
            name.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;   // never bubble to the row host and toggle the expand band
                onShowOnMap(stop.Id);
            };
        }
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
