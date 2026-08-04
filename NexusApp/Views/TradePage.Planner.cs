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
    private CommodityPickerBox _shipPicker = null!;
    // Committed display row -> catalog id. Built with the rows in BuildPlannerChrome so the two can
    // never disagree; a display string must never reach TradeShipId.
    private Dictionary<string, string> _shipDisplayToId = new(StringComparer.Ordinal);
    private TextBox _budgetBox = null!;
    private Border _demandAnyPill = null!;
    private Border _demandMinPill = null!;
    private Border _demandTwoXPill = null!;
    private Border _rankProfitPill = null!;
    private Border _rankProfitPerScuPill = null!;
    private Border _rankProfitPerGmPill = null!;
    // The TRADE ship list, not the grid catalog: every flyable hull with cargo space (~90), where
    // CargoShipCatalog carries only the 15 whose 3D grids are reviewed and signed off. The planner
    // needs totals and a max container size, never geometry. See TradeShipCatalog's class comment.
    private readonly TradeShipCatalog _shipCatalog = TradeShipCatalog.LoadEmbedded();

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

    // Overlay sync (overlay planner spec, 2026-08-02): forget the session pick and re-seed from
    // the persisted TradeStartManual on the next refresh, same idiom as ResyncCommodityFromSettings
    // below - RefreshStartCombo re-runs SeedStartKind once _startSeeded is false again. Internal
    // seam for TradePage.ResyncSharedTradeSettings.
    private void ResyncStartFromSettings()
    {
        _startSeeded = false;
        _startSelectedKind = null;
    }

    // Location-first display (owner's ask, 2026-07-31): the combo shows TradeOriginResolver.
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

    // Overlay sync (overlay planner spec, 2026-08-02): forget the session pick and re-seed from
    // the persisted TradeDestManual on the next refresh, same idiom as ResyncStartFromSettings
    // above. Internal seam for TradePage.ResyncSharedTradeSettings.
    private void ResyncDestFromSettings()
    {
        _destSeeded = false;
        _destSelectedName = null;
    }

    // Location-first display (owner's ask, 2026-07-31), same mechanism as _startDisplayToKind
    // above: display -> real terminal name ("ANY" maps to itself), rebuilt fresh every
    // RefreshDestCombo call so SelectionChanged can map the combo's displayed text back to the
    // real name that gets persisted into AppSettings.TradeDestManual.
    private Dictionary<string, string>? _destDisplayToName;

    // COMMODITY picker (issue #41, planner half; owner's revision: the same type-or-browse
    // CommodityPickerBox the SELL and PRICES fields use, not a plain dropdown): same
    // seed-once/revalidate-per-rebuild idiom as the DESTINATION picker above, minus the display
    // map - commodity names are not terminal names, so LocationFirst never applies and the items
    // are shown as-is. "ANY" rides as the picker's pinned first row and is the sentinel for "no
    // constraint"; AppSettings.TradeCommodityFilter persists null for that same state, mirroring
    // TradeDestManual's contract. Committed is the only selection path (typing never commits), so
    // no suppress flag is needed here - programmatic Text writes are suppressed inside the control.
    private const string AnyCommodity = "ANY";
    private CommodityPickerBox _plannerCommodityPicker = null!;
    private string? _commoditySelectedName;         // the active pick ("ANY" = unconstrained); null only before the first seed
    private bool _commoditySeeded;                  // seeds once from TradeCommodityFilter, same idiom as the DESTINATION picker above

    // Overlay sync (overlay planner spec, 2026-08-02): forget the session pick and re-seed from
    // the persisted TradeCommodityFilter on the next refresh. Internal seam for
    // TradePage.ResyncSharedTradeSettings.
    private void ResyncCommodityFromSettings()
    {
        _commoditySeeded = false;
        _commoditySelectedName = null;
    }

    // Overlay sync (overlay planner spec, 2026-08-02): the ship combo has no per-rebuild refresh
    // method of its own (unlike Start/Dest/Commodity, BuildPlannerChrome seeds it once and it is
    // otherwise only touched by a direct user pick), so an external write needs its own immediate
    // re-seed here rather than deferring to the next rebuild - the same reasoning
    // ResyncSharedTradeSettings already documents for calling RefreshScopePills unconditionally.
    // The picker's Text setter is a programmatic write it suppresses internally (it never reopens
    // the popup and never raises Committed), so unlike the old ComboBox this needs no reentrancy
    // flag of its own. Internal seam for TradePage.ResyncSharedTradeSettings.
    //
    // Deliberately skipped while the user is mid-interaction: writing the box during a typed query
    // would stomp what they are halfway through, which is the same defer-to-InteractionEnded rule
    // the commodity fields follow. The overlay's write still lands on the next quiet moment.
    private void ResyncShipFromSettings()
    {
        if (_shipPicker is null || _shipPicker.IsInteracting) return;
        var expect = ShipRowText(CurrentShip());
        if (!string.Equals(_shipPicker.Text, expect, StringComparison.Ordinal)) _shipPicker.Text = expect;
    }

    // One place builds a ship row's text, so the picker's items, its seeded text, the abandoned-
    // query revert and the overlay's own rows can never drift into different phrasings.
    private static string ShipRowText(TradeShip s) => $"{s.DisplayName} - {s.TotalScu} SCU";

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

    // Debounce for the live re-rank (owner's live-lag report, 2026-07-31): RoutePlanner.Rank
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
            try { RebuildPlanner(); SessionBudgetChanged?.Invoke(CurrentBudget()); }
            finally { _inBudgetLiveRerank = false; }
        };
        return timer;
    }

    private TradeShip CurrentShip() =>
        _shipCatalog.ById(App.Settings.Current.TradeShipId) ?? _shipCatalog.Ships.First();

    private double? CurrentBudget()
    {
        var digits = new string((_budgetBox?.Text ?? "").Where(char.IsDigit).ToArray());
        return digits.Length > 0 && double.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    // Overlay planner spec, 2026-08-02: the overlay's initial push (on attach, before either
    // budget commit point has fired this session) needs the current parsed budget, not just
    // future changes - so this reads straight through CurrentBudget rather than caching its own
    // copy that could go stale relative to the box's live text.
    internal double? CurrentSessionBudget => CurrentBudget();

    // Raised at both budget commit points below (the debounce tick and the LostFocus commit),
    // never per keystroke - a keystroke only restarts the debounce timer, it does not reach
    // either commit point until the timer fires or the box loses focus.
    internal event Action<double?>? SessionBudgetChanged;

    // Built once, on the first RebuildPlanner. Everything here survives every later rebuild: the
    // controls keep their identity, so a click that moved focus off the budget box lands on a live
    // control, and typed-but-unblurred text is never thrown away by a background tick.
    private void BuildPlannerChrome()
    {
        if (_plannerInputs is not null) return;

        _plannerInputs = new StackPanel();

        var topRow = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };

        // SHIP: type-or-browse, the same CommodityPickerBox the commodity fields and the overlay's
        // own ship picker use. It replaced a plain NexusComboBox when the catalog went from 15 hulls
        // to ~90 (owner, 2026-08-03: "allow the user to type in the search bar just like the
        // commodities") - at that length a scroll-only dropdown is unusable. Rows are the SAME
        // "{DisplayName} - {TotalScu} SCU" strings the overlay builds, so the two surfaces share one
        // vocabulary; _shipDisplayToId maps a committed row back to the catalog id TradeShipId
        // persists, because a display string must never be persisted.
        var shipGrp = new StackPanel { Margin = new Thickness(0, 0, 16, 0), Width = 220 };
        shipGrp.Children.Add(FieldLabel("Ship"));
        _shipPicker = new CommodityPickerBox();
        var shipRows = new List<string>(_shipCatalog.Ships.Count);
        _shipDisplayToId = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var s in _shipCatalog.Ships)
        {
            var rowText = ShipRowText(s);
            shipRows.Add(rowText);
            _shipDisplayToId[rowText] = s.Id;
        }
        _shipPicker.SetItems(shipRows);
        _shipPicker.Text = ShipRowText(CurrentShip());
        _shipPicker.Opened += () => Logger.Info("[UI] Trade planner: ship list opened");
        _shipPicker.Committed += display =>
        {
            // An unmapped row cannot happen (rows and map come from one loop above) but fails safe
            // as a no-op rather than persisting a display string.
            if (!_shipDisplayToId.TryGetValue(display, out var id)) return;
            if (App.Settings.Current.TradeShipId == id) return;   // same-row re-click: no log, no rebuild
            App.Settings.Current.TradeShipId = id;
            App.Settings.Save();
            Logger.Info($"[UI] Trade planner: ship {id}");
            SharedTradeSettingsChanged?.Invoke();
            RebuildPlanner();
        };
        // Abandoned-query cleanup, same contract as the commodity fields: walking away without
        // committing reverts the box to naming the ship the routes were actually ranked with.
        _shipPicker.InteractionEnded += () =>
        {
            var expect = ShipRowText(CurrentShip());
            if (!string.Equals(_shipPicker.Text, expect, StringComparison.Ordinal))
                _shipPicker.Text = expect;
        };
        shipGrp.Children.Add(_shipPicker);
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
            SessionBudgetChanged?.Invoke(CurrentBudget());
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
        // DEBOUNCED (owner's live-lag report, 2026-07-31 - see _budgetDebounceTimer's own comment
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
        // item, selecting it means no constraint. Right margin since issue #41 seated the
        // COMMODITY picker after it - the same 16px gap every other non-last group in a row gets.
        var destGrp = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
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

        // COMMODITY picker (issue #41; owner's revision: type-or-browse, matching the SELL tab's
        // commodity field). "ANY" is the pinned first row, selecting it means no constraint. No
        // display map: the rows ARE the persisted names. Width matches the other two
        // CommodityPickerBox instances so the control feels identical across the trade tabs.
        var commodityGrp = new StackPanel { Width = 220 };
        commodityGrp.Children.Add(FieldLabel("Commodity"));
        _plannerCommodityPicker = new CommodityPickerBox { PinnedFirst = AnyCommodity };
        _plannerCommodityPicker.Opened += () => Logger.Info("[UI] trade: commodity list opened");
        _plannerCommodityPicker.Committed += name => SetCommodityFilter(name);
        // Abandoned-query cleanup, same contract as the Prices flow: once the user walks away
        // without committing, the box reverts to naming the active filter.
        _plannerCommodityPicker.InteractionEnded += () =>
        {
            var expect = _commoditySelectedName ?? "";
            if (!string.Equals(_plannerCommodityPicker.Text, expect, StringComparison.Ordinal))
                _plannerCommodityPicker.Text = expect;
        };
        commodityGrp.Children.Add(_plannerCommodityPicker);
        routeRow.Children.Add(commodityGrp);

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

        var snap = App.Market.Snapshot;
        RefreshStartCombo(snap);
        RefreshDestCombo(snap);
        RefreshPlannerCommodityPicker(snap);
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
        var routes = RoutePlanner.Rank(BuildSourcePairs(snap.TradePrices.Rows), terminals, ship.TotalScu, ship.MaxContainerScu,
            CurrentBudget(), originIds, App.Settings.Current.TradeScope, take: 25,
            TradePlanArgs.ParseDemandFilter(App.Settings.Current.TradeStockFilter), destIds,
            TradePlanArgs.ParseRankMode(App.Settings.Current.TradeRankMode),
            // The same measurement this page already renders per row as a dim decoration - now it
            // can also do the ranking, instead of the planner sorting purely on money while showing
            // a distance it ignored.
            App.Map.DistanceMeters, CommodityFilterName());

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
            // A COMMODITY filter that produced nothing is the next most specific cause (issue
            // #41): the list was deliberately narrowed to one commodity, so name it rather than
            // reporting a generic absence of routes the other controls cannot explain.
            else if (CommodityFilterName() is { } commodity)
            {
                message = $"No routes haul {commodity} with the current settings. Set Commodity to ANY, or widen the scope.";
                Logger.Info($"[UI] Trade planner run: 0 routes, commodity {commodity}");
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

        Logger.Info($"[UI] Trade planner run: {routes.Count} routes, ship {ship.Id}, scope {App.Settings.Current.TradeScope}, start {App.Settings.Current.TradeStartManual ?? "LIVE"}, dest {_destSelectedName ?? AnyDestination}, commodity {_commoditySelectedName ?? AnyCommodity}");

        // One line per rebuild, not per route: enough for the App Log Monitor and the diagnostic
        // snapshot to answer "is the container snap biting in the field", without turning a
        // 25-route rebuild into 25 log lines. Silent when nothing snapped, which is the common case.
        var snappedCount = routes.Count(r => r.TripQty < r.PlannedQty);
        if (snappedCount > 0)
            Logger.Info($"[UI] Trade planner run: {snappedCount} of {routes.Count} routes snapped to buyable containers, {routes.Sum(r => r.PlannedQty - r.TripQty)} SCU trimmed");
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
        SharedTradeSettingsChanged?.Invoke();
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
        SharedTradeSettingsChanged?.Invoke();
        RebuildPlanner();
    }

    // The COMMODITY picker's counterpart to SetDest (issue #41): same no-op-on-unchanged guard, so
    // re-picking what is already selected never logs or reruns. Null and "" both mean ANY,
    // matching AppSettings.TradeCommodityFilter's own contract.
    private void SetCommodityFilter(string? name)
    {
        var chosen = string.IsNullOrEmpty(name) ? AnyCommodity : name;
        if (_commoditySelectedName == chosen) return;

        _commoditySelectedName = chosen;
        App.Settings.Current.TradeCommodityFilter = chosen == AnyCommodity ? null : chosen;
        App.Settings.Save();
        Logger.Info($"[UI] trade: commodity {chosen}");
        SharedTradeSettingsChanged?.Invoke();
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
    //
    // Final review 2026-08-02: both stale-name fallbacks below (the seed's fall to LIVE and the
    // re-validate's fall to ANY) persist the downgrade and raise SharedTradeSettingsChanged, the
    // commodity picker's stale-clear idiom (RefreshPlannerCommodityPicker). The ranking
    // (RebuildPlanner) and the overlay planner both read the PERSISTED kind, so a session-only
    // downgrade left the combo claiming one thing while both surfaces ranked another. No log
    // either time: a programmatic correction, not a user pick. Staleness is judged only against a
    // REAL list (the commodity picker's empty-list rule: no priced terminals yet means "not
    // loaded", never "stale"), so a pre-fetch rebuild can never wipe a valid saved value. The
    // raises cannot ping-pong: SharedTradeSettingsChanged's one subscriber relays to
    // OverlayWindow.OnSharedTradeSettingsChanged, which only re-ranks its own panel and never
    // writes settings; the overlay-to-desktop direction (TradeSettingsChangedByOverlay ->
    // ResyncSharedTradeSettings) only re-arms these seeds, and a re-run of either fallback is
    // then a no-op - the persisted value is already the downgraded one, so the guard skips the
    // Save and the raise.
    private void RefreshStartCombo(MarketSnapshot? snap)
    {
        string? liveLoc = App.Locations.LastKnownLocation;
        var terminalNames = TerminalNames(snap);

        // Location-first display (owner's ask, 2026-07-31): kindToDisplay/displayToKind are
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
                // SeedStartKind is pure, so its stale-name fall to LIVE lands here: the seed can
                // only differ from a non-empty persisted value when that value was a terminal
                // name a REAL list no longer offers (ANY/LIVE/found names seed as themselves; an
                // empty list defers with null and never reaches this branch). Not seed-display-
                // only, so it must persist: RebuildPlanner ranks from the persisted kind, and
                // without this write the combo showed LIVE while both this page and the overlay
                // kept restricting to the dead name. null/"" stays untouched - it is not a stale
                // name but the unconstrained default StartTerminalIds already treats as ANY.
                if (App.Settings.Current.TradeStartManual is { Length: > 0 } persisted && persisted != seed)
                {
                    App.Settings.Current.TradeStartManual = seed;
                    App.Settings.Save();
                    SharedTradeSettingsChanged?.Invoke();
                }
            }
        }
        else if (_startSelectedKind is null)
        {
            _startSelectedKind = AnyStart;   // defensive only: null cannot follow a consumed seed
        }
        else if (_startSelectedKind != AnyStart && _startSelectedKind != "LIVE"
                 && terminalNames.Count > 0 && !terminalNames.Contains(_startSelectedKind))
        {
            _startSelectedKind = AnyStart;
            if (App.Settings.Current.TradeStartManual != AnyStart)
            {
                App.Settings.Current.TradeStartManual = AnyStart;
                App.Settings.Save();
                SharedTradeSettingsChanged?.Invoke();
            }
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
        SharedTradeSettingsChanged?.Invoke();
        RebuildPlanner();
    }

    private void RefreshDemandFilterPills()
    {
        var active = TradePlanArgs.ParseDemandFilter(App.Settings.Current.TradeStockFilter);
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

    // Same no-op-on-unchanged guard as SetDemandFilter: a click on the pill that is already active
    // never logs or rebuilds.
    private void SetRankMode(RankMode mode)
    {
        var label = RankModeLabel(mode);
        if (App.Settings.Current.TradeRankMode == label) return;
        App.Settings.Current.TradeRankMode = label;
        App.Settings.Save();
        Logger.Info($"[UI] trade: rank mode {label}");
        SharedTradeSettingsChanged?.Invoke();
        RebuildPlanner();
    }

    private void RefreshRankModePills()
    {
        var active = TradePlanArgs.ParseRankMode(App.Settings.Current.TradeRankMode);
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

    // Re-validate on every rebuild, same rule as the Prices flow's RefreshPricesCommodityBox: an
    // hourly snapshot refresh can drop the previously selected destination terminal (or the very
    // first rebuild ever runs before any snapshot exists), and a one-time seed would leave the
    // field stuck on a name no longer offered. First call ever seeds from the persisted
    // TradeDestManual setting (null/"" falls back to ANY); every later call revalidates the
    // CURRENT selection against the CURRENT terminal list instead of re-reading the setting, so a
    // live user pick this session is never clobbered by what was last saved.
    //
    // Final review 2026-08-02: a stale name's drop to ANY (both the seed's and the re-validate's)
    // persists the downgrade (TradeDestManual = null, SetDest's own ANY contract) and raises
    // SharedTradeSettingsChanged - the commodity picker's stale-clear idiom, judged only against
    // a REAL list. See RefreshStartCombo's comment above for the full rationale and the
    // no-ping-pong argument; without the persist the overlay kept restricting sell legs to the
    // dead persisted name while this combo showed ANY.
    private void RefreshDestCombo(MarketSnapshot? snap)
    {
        var terminalNames = TerminalNames(snap);

        // Location-first display (owner's ask, 2026-07-31), same mechanism as RefreshStartCombo
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
            // Stale-clear persist (see the method comment): only when a REAL list rejected a
            // saved name. An empty list is the pre-fetch rebuild, where "not offered" means
            // "not loaded yet" - the saved value may be perfectly valid once data lands, so
            // wiping it there would destroy it on every planner open that beats the fetch.
            if (terminalNames.Count > 0 && !string.IsNullOrEmpty(persisted) && _destSelectedName == AnyDestination)
            {
                App.Settings.Current.TradeDestManual = null;
                App.Settings.Save();
                SharedTradeSettingsChanged?.Invoke();
            }
        }
        else if (_destSelectedName is null)
        {
            _destSelectedName = AnyDestination;   // defensive only: null cannot follow a consumed seed
        }
        else if (_destSelectedName != AnyDestination && terminalNames.Count > 0 && !terminalNames.Contains(_destSelectedName))
        {
            _destSelectedName = AnyDestination;
            if (!string.IsNullOrEmpty(App.Settings.Current.TradeDestManual))
            {
                App.Settings.Current.TradeDestManual = null;
                App.Settings.Save();
                SharedTradeSettingsChanged?.Invoke();
            }
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
    private IReadOnlySet<int>? DestTerminalIds(IReadOnlyList<MarketTerminal> terminals) =>
        TradePlanArgs.DestTerminalIds(
            _destSelectedName == AnyDestination ? null : _destSelectedName, terminals);

    // COMMODITY picker refresh (issue #41): same re-validate-on-every-rebuild rule as
    // RefreshDestCombo above, minus the display map (commodity names are shown as-is). The item
    // list is the shared CommodityNames derivation (TradePage.cs, also the Prices flow's list);
    // "ANY" rides as the picker's own pinned first row, never as a list entry. Names match
    // case-insensitively, adopting the list's canonical casing on a hit (Rank filters
    // OrdinalIgnoreCase and the Prices flow revalidates the same way, so a snapshot that merely
    // re-cases a name must not drop the filter). A persisted or selected name the snapshot truly
    // no longer offers falls back to ANY silently - a programmatic correction, not a user pick,
    // so it never logs or rebuilds - and the stale persisted value is cleared with it so it
    // cannot resurrect next launch. Clearing the persisted value is still a SHARED-setting write,
    // so both clears raise SharedTradeSettingsChanged (the event's contract): a presented overlay
    // must not keep ranking with the dead filter name. No loop: the overlay's handler only
    // re-ranks its own panel (OnSharedTradeSettingsChanged), it never writes settings back.
    // Staleness is never judged against an EMPTY list (the planner
    // opening before the first market fetch - the A5 rule from RefreshStartCombo): a
    // commodity-name seed defers to the next refresh instead of guessing, and the box honestly
    // shows its empty-state placeholder meanwhile.
    private void RefreshPlannerCommodityPicker(MarketSnapshot? snap)
    {
        var commodities = CommodityNames(snap);

        if (!_commoditySeeded)
        {
            var persisted = App.Settings.Current.TradeCommodityFilter;
            if (string.IsNullOrEmpty(persisted))
            {
                _commoditySeeded = true;   // null/"" means ANY and needs no list to seed against
                _commoditySelectedName = AnyCommodity;
            }
            else if (commodities.Count > 0)
            {
                _commoditySeeded = true;
                var match = commodities.Find(c => string.Equals(c, persisted, StringComparison.OrdinalIgnoreCase));
                _commoditySelectedName = match ?? AnyCommodity;
                if (match is null)
                {
                    App.Settings.Current.TradeCommodityFilter = null;
                    App.Settings.Save();
                    SharedTradeSettingsChanged?.Invoke();
                }
            }
        }
        else if (commodities.Count > 0 && _commoditySelectedName is not null && _commoditySelectedName != AnyCommodity)
        {
            var match = commodities.Find(c => string.Equals(c, _commoditySelectedName, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                _commoditySelectedName = AnyCommodity;
                App.Settings.Current.TradeCommodityFilter = null;
                App.Settings.Save();
                SharedTradeSettingsChanged?.Invoke();
            }
            else if (!string.Equals(match, _commoditySelectedName, StringComparison.Ordinal))
            {
                // Re-cased, not removed: adopt the canonical casing so the suppressed
                // SelectedItem write below still lands on a real ItemsSource entry.
                _commoditySelectedName = match;
            }
        }

        // The backing list is pushed even mid-interaction (same contract as the other two picker
        // instances): SetItems never touches an open popup's rows or the box text. The write-back
        // below defers while the user is typing or browsing; InteractionEnded (wired in
        // BuildPlannerChrome) catches the abandoned-query case.
        _plannerCommodityPicker.SetItems(commodities);
        if (_plannerCommodityPicker.IsInteracting) return;
        var expect = _commoditySelectedName ?? "";
        if (!string.Equals(_plannerCommodityPicker.Text, expect, StringComparison.Ordinal))
            _plannerCommodityPicker.Text = expect;
    }

    // Resolves the COMMODITY combo's current pick to RoutePlanner's filter argument. "ANY" (the
    // default, and the literal first combo item) means no constraint - null, the same sentinel
    // the buy and sell terminal sets already use for "unrestricted".
    private string? CommodityFilterName() =>
        _commoditySelectedName is null || _commoditySelectedName == AnyCommodity ? null : _commoditySelectedName;

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
    private static Border PinChip(bool active, bool sellOnly = false)
    {
        var text = new TextBlock
        {
            // "PIN TO OVERLAY", not "PIN" (owner, 2026-08-01): a bare PIN said nothing about where
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
        ApplyPinChipVisual(chip, active, sellOnly);
        return chip;
    }

    // Repaints an existing chip rather than rebuilding it (owner's live pass, 2026-08-01: "when i
    // click pin to overlay the main app flashes and lags a bit"). The click handler used to call
    // RebuildPlanner, which re-ranks ~2,600 price rows and rebuilds up to 25 route rows WITH their
    // staggered entrance cascade - to change the colour of one chip. That replayed entrance is the
    // flash, the rank plus 25 rebuilds is the lag, and it also collapsed whatever row the user had
    // expanded. Nothing about the ranking changes when a route is pinned.
    //
    // sellOnly words the tooltip honestly per surface: a sell pin draws NO Starmap leg (a leg
    // needs two ends), so its tooltip must not promise one - the untrue-claim rule.
    private static void ApplyPinChipVisual(Border chip, bool active, bool sellOnly = false)
    {
        ((TextBlock)chip.Child).Foreground = active ? Hud.Br("GoldBrush") : Hud.Br("FgDimBrush");
        chip.BorderBrush = active ? Hud.Br("GoldBrush") : Hud.Br("BorderBrush");
        chip.ToolTip = (active, sellOnly) switch
        {
            (true, false) => "Stop showing this route in the overlay and on the Starmap.",
            (false, false) => $"Show this route in the overlay's TRADE tab and on the Starmap. Up to {RoutePlanner.MaxPins} at once.",
            (true, true) => "Stop showing this sell stop in the overlay.",
            (false, true) => $"Show this sell stop in the overlay's TRADE tab. Up to {RoutePlanner.MaxPins} pins at once.",
        };
    }

    // Every pin chip currently on screen, so a pin can repaint all of them in place. ALL of them,
    // not just the one clicked: pinning at the cap evicts the OLDEST pin, and that route may be
    // visible in this same list - or over on the Sell tab's list - so its chip has to go dim in
    // the same gesture. The sell list lives here beside the planner's because RefreshPinChips is
    // the one repaint path both flows share.
    private readonly List<(TradeRoute Route, Border Chip)> _pinChips = new();
    private readonly List<(int TerminalId, int CommodityId, Border Chip)> _sellPinChips = new();

    /// <summary>Repaints every pin chip on screen (planner rows AND sell rows) from the current
    /// pin list. Internal because the overlay's own per-card close reaches it through MainWindow:
    /// that path used to call Refresh(), which rebuilds all THREE flows to dim one chip.</summary>
    internal void RefreshPinChips()
    {
        foreach (var (route, chip) in _pinChips) ApplyPinChipVisual(chip, IsPinned(route));
        foreach (var (tid, cid, chip) in _sellPinChips) ApplyPinChipVisual(chip, IsSellPinned(tid, cid), sellOnly: true);
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

    // Every route card renders its full detail inline (owner, 2026-08-03: "I do not like the click
    // to expand for additional details on each of the planner route, can you just show it all
    // within each routes hero card"). So there is no chevron, no click handler and no per-row open
    // state here any more; the card is a readout, not a control. The Sell tab keeps its expander -
    // its rows are a long browse list where collapsing is what makes it scannable, and it was not
    // part of this ask. The shared ChevronGlyph/SetChevronOpen/DetailBand helpers therefore stay.
    private FrameworkElement BuildRouteRow(TradeRoute r, int index, TradeShip ship, Dictionary<int, MarketTerminal> terminals)
    {
        var frame = Hud.CardFrame(BuildRouteRowContent(r, index, ship, terminals),
            out var cardFrame, out _, chamfer: 8, padding: new Thickness(16, 13, 18, 13));
        return new Border { Child = frame, Margin = new Thickness(0, 0, 0, 10) };
    }

    private static Grid PositionChevron(Path chevron)
    {
        var grid = new Grid { IsHitTestVisible = false };
        grid.Children.Add(new Border { Child = chevron, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        return grid;
    }

    private UIElement BuildRouteRowContent(TradeRoute r, int index, TradeShip ship, Dictionary<int, MarketTerminal> terminals)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // source picker
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // purchase block
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // detail band

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
        if (TradePlanArgs.ParseRankMode(App.Settings.Current.TradeRankMode) == RankMode.ProfitPerScu)
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

        // Star / arrow / Star grid, NOT a horizontal StackPanel (owner, 2026-08-04: at narrow
        // window widths the sell leg's price and demand lines ran under the PROFIT / TRIP
        // readout). A horizontal StackPanel measures its children with infinite width, so the
        // legs rendered at full natural width and spilled out of this star column under the
        // profit block (WPF does not clip) - and that same infinite measure defeated the terminal
        // name's CharacterEllipsis, which BuildLeg's own top row is explicitly built around.
        // Star columns hand each leg a finite share, so the card degrades by trimming long names
        // (full name stays in the tooltip) instead of overlapping its own readouts.
        var legs = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        legs.ColumnDefinitions.Add(new ColumnDefinition());
        legs.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        legs.ColumnDefinitions.Add(new ColumnDefinition());
        var buyLeg = BuildLeg("Buy at", r.BuyRow.TerminalName, buySystem, r.BuyRow.Buy, "STOCK", r.BuyRow.BuyStockScu, r.TripQty, r.BuyRow.ModifiedUtc, r.BuyRow.ContainerSizes, ship.MaxContainerScu, isBuy: true, SctDeltasFor(r.BuyRow, "buy"), ToggleFor(r.BuyRow, true), out var applyBuy, buyTerm, RaiseShowOnMap);
        Grid.SetColumn(buyLeg, 0); legs.Children.Add(buyLeg);
        var legArrow = new Path
        {
            Data = Geometry.Parse("M3,12 L18,12 M12,6 L18,12 L12,18"), Width = 20, Height = 20, Stroke = Hud.Br("FgDimBrush"),
            StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            Fill = Brushes.Transparent, Stretch = Stretch.Uniform, Margin = new Thickness(14, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(legArrow, 1); legs.Children.Add(legArrow);
        var sellLeg = BuildLeg("Sell at", r.SellRow.TerminalName, sellSystem, r.SellRow.Sell, "DEMAND", r.SellRow.SellDemandScu, r.TripQty, r.SellRow.ModifiedUtc, r.SellRow.ContainerSizes, ship.MaxContainerScu, isBuy: false, SctDeltasFor(r.SellRow, "sell"), ToggleFor(r.SellRow, false), out var applySell, sellTerm, RaiseShowOnMap);
        Grid.SetColumn(sellLeg, 2); legs.Children.Add(sellLeg);
        Grid.SetRow(legs, 1); Grid.SetColumn(legs, 0);
        grid.Children.Add(legs);

        // ONE picker for the whole route (owner: "one toggle per route so if you switch to sct both
        // stock and demand show SCT and vice versa"), moving QUANTITIES ONLY - prices stay UEX's on
        // both legs. It drives both legs together through the repaint hooks they handed back, so
        // the card never shows one leg's stock from one feed beside the other's from the other.
        //
        // Shown as soon as EITHER leg has a second opinion. A leg with only UEX simply does not
        // move, which is honest: the alternative would be hiding the control on a route where half
        // of it is genuinely switchable.
        if (applyBuy is not null || applySell is not null)
        {
            var srcRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            srcRow.Children.Add(new TextBlock
            {
                Text = "STOCK AND DEMAND FROM", FontFamily = Hud.Font("UiFont"), FontSize = 9, FontWeight = FontWeights.Bold,
                Foreground = Hud.Br("FgDimBrush"), VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            });
            void ShowSource(bool sct)
            {
                applyBuy?.Invoke(sct);
                applySell?.Invoke(sct);
                srcRow.Children.RemoveRange(1, srcRow.Children.Count - 1);
                srcRow.Children.Add(RouteSourcePill("UEX", active: !sct, ranked: true, onClick: () => ShowSource(false)));
                srcRow.Children.Add(RouteSourcePill("SCT", active: sct, ranked: false, onClick: () => ShowSource(true)));
            }
            ShowSource(false);   // UEX is what the route was ranked and priced on, so it opens there
            Grid.SetRow(srcRow, 2); Grid.SetColumnSpan(srcRow, 2);
            grid.Children.Add(srcRow);
        }

        // The purchase block lives on its OWN full-width row, not inside the buy leg. It used to
        // sit in the leg, where a StackPanel sizes to its widest child, so a four-size plan made
        // the buy column as wide as its chip row and shoved the sell leg and the profit block
        // across the card (owner, 2026-08-03, with a screenshot of two cards whose legs did not
        // line up). Wrapping the chips was not enough - the leg still measured them. Out here it
        // spans both columns and cannot influence any other element's width or placement, however
        // many containers it lists.
        // PLANNED, not TripQty. TripQty is already snapped to what this menu can supply, so planning
        // against it would report a shortfall of zero on every card and delete the one line that
        // explains why a 46 SCU hull is only hauling 40 (issue #31).
        if (ContainerPlanner.Plan(r.BuyRow.ContainerSizes, ship.MaxContainerScu, r.PlannedQty) is { } plan)
        {
            // Stretches deliberately: on its own row it drives nothing, and the chip WrapPanel
            // inside needs a real width to wrap against. Left-aligning here would give it infinite
            // available width and it would never wrap, just overrun the card.
            var purchase = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            // Both bounds read off the plan itself rather than the raw menu, so this line and the
            // containers under it always describe the same set of sizes. The range is the
            // terminal's menu filtered to what this hull can load: advertising a 32 SCU container
            // to a ship that tops out at 16 would name a size you cannot buy.
            purchase.Children.Add(LabelledValueLine("MIN/MAX SCU CONTAINER SIZE FOR PURCHASE",
                plan.SingleSize ? $"{plan.MaxContainerScu:n0} SCU"
                                : $"{plan.MinContainerScu:n0} / {plan.MaxContainerScu:n0} SCU",
                // Said in words when the terminal, not the hull, is the binding limit: the run is
                // forced into more and smaller containers than the ship could otherwise carry.
                warnNote: plan.MaxContainerScu < ship.MaxContainerScu ? "TERMINAL LIMIT" : null,
                warnTip: $"The largest container this terminal sells is {plan.MaxContainerScu:n0} SCU, below the "
                       + $"{ship.MaxContainerScu:n0} SCU this ship can load, so the run needs more containers than it otherwise would."));
            purchase.Children.Add(CratePlanLine(plan, r.PlannedQty));
            Grid.SetRow(purchase, 3); Grid.SetColumnSpan(purchase, 2);
            grid.Children.Add(purchase);
        }

        // Always visible: same divider-above-and-padding geometry DetailBand gives the Sell rows,
        // minus the collapse and minus the click-swallowing handler that only existed to stop a
        // click inside the band from toggling the card shut.
        var detailHost = new Border
        {
            Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(0, 12, 0, 0),
            BorderBrush = Hud.Br("NavBorderBrush"), BorderThickness = new Thickness(0, 1, 0, 0),
        };
        var detail = new StackPanel();
        // The old first line here read "Box size OK for {ship} ({N} SCU crates)" with a tick.
        // Removed, not reworded (owner, 2026-08-03: it is "vague and the wrong terminology"): it
        // was also always true, because RoutePlanner already drops any pair whose crates this hull
        // cannot take, so it confirmed a condition that can never be false on a row you can see.
        // What the owner actually wanted from it - which containers to buy - is the buy leg's
        // RECOMMENDED CONTAINERS line, stated where the purchase happens.
        //
        // The "Trip size N SCU = smallest of: ship N, stock N" line lived here too and is gone at
        // the owner's request: the same figure is already the STOCK bar's whole point on the buy
        // leg, and the RECOMMENDED CONTAINERS line now states what that quantity actually buys.
        //
        // The profit block is gone as well. Gross, Fees and Net were rendered unconditionally
        // while RoutePlanner sets Net == Gross with no fee provider, so the card carried the same
        // figure twice under two labels; collapsing it to one number then simply repeated the
        // card head's own PROFIT / TRIP readout (owner: "now we show profit/trip twice"). Only a
        // real fee split adds anything the head does not already say, so that is the only thing
        // rendered here, and it returns by itself the day a fee provider lands (the datamined
        // auto-load ladder is the obvious first one). Net stays out of it: the head is Net.
        double fees = r.Gross - r.Net;
        if (fees != 0)
        {
            var feeLine = new StackPanel { Orientation = Orientation.Horizontal };
            feeLine.Children.Add(FeePart("Gross", r.Gross, Hud.Br("FgBrush")));
            feeLine.Children.Add(FeePart("Fees", fees, Hud.Br("FgBrush")));
            detail.Children.Add(feeLine);
        }

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

        // Only mounted when it has something to say. With no fee split and no corroboration the
        // band is empty, and mounting it anyway would draw its divider rule under a card with
        // nothing beneath the line.
        if (detail.Children.Count > 0)
        {
            detailHost.Child = detail;
            Grid.SetRow(detailHost, 4); Grid.SetColumnSpan(detailHost, 2);
            grid.Children.Add(detailHost);
        }
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

    /// <summary>What SCT said about one side, kept beside the UEX row so a card can offer both.</summary>
    private readonly record struct SidePair(double SctPrice, int SctQuantity, DateTime SctUtc);

    // Keyed by (terminal, commodity, player-buy) from the last rank. Absent means only UEX reported
    // this side, in which case the row already carries everything there is to show.
    private Dictionary<(int TerminalId, int CommodityId, bool PlayerBuy), SidePair> _sidePairs = new();

    /// <summary>
    /// Records what SCT says about every side and returns the rows UNCHANGED.
    ///
    /// UEX alone prices and ranks the routes (owner: "for prices lets just stick with UEX"), so no
    /// figure is substituted here. This exists only so a card can offer SCT's stock and demand
    /// beside it. Choosing whichever feed observed a side more recently was built and then backed
    /// out: with one toggle per route, a route fresher on SCT for its buy leg and on UEX for its
    /// sell leg has no honest two-state default, and the profit would have come from a mixture the
    /// card could not display.
    ///
    /// Container sizes, terminal identity and commodity identity are UEX's regardless - SCT
    /// publishes none of them.
    /// </summary>
    private IReadOnlyList<TradePriceRow> BuildSourcePairs(IReadOnlyList<TradePriceRow> rows)
    {
        _sidePairs = new Dictionary<(int, int, bool), SidePair>();
        // One walk of the SCT snapshot instead of a scan per row: this runs on every rebuild, and
        // the budget box re-ranks while the user types.
        var sct = App.Sct.SideIndex();
        if (sct.Count == 0) return rows;

        foreach (var row in rows)
        {
            RecordPair(row, sct, playerBuy: true);
            RecordPair(row, sct, playerBuy: false);
        }
        return rows;
    }

    // Stores SCT's own figures for a side the two feeds share. UEX's stay on the row itself,
    // untouched, because UEX is what the card shows and what the route was ranked on.
    private void RecordPair(TradePriceRow row,
        IReadOnlyDictionary<(int TerminalId, int CommodityId, bool PlayerBuy), SctListing> sct,
        bool playerBuy)
    {
        if (!sct.TryGetValue((row.TerminalId, row.CommodityId, playerBuy), out var listing)) return;
        _sidePairs[(row.TerminalId, row.CommodityId, playerBuy)] =
            new SidePair(listing.Price, listing.Quantity, listing.TimestampUtc);
    }

    /// <summary>
    /// Both feeds' readings for one side, so a card can show either on demand.
    ///
    /// A VIEWER, not a planning input (owner: "i dont want it to re rank, i want it to just show
    /// what SCT or UEX is displaying for that commodity with that route"). Switching swaps the
    /// price, the quantity, the coverage bar and the age; the route keeps the position and the
    /// profit UEX gave it.
    /// </summary>
    private readonly record struct SourceToggle(
        double UexPrice, int UexQuantity, DateTime UexUtc,
        double SctPrice, int SctQuantity, DateTime SctUtc);

    private SourceToggle? ToggleFor(TradePriceRow row, bool playerBuy)
    {
        if (!_sidePairs.TryGetValue((row.TerminalId, row.CommodityId, playerBuy), out var pair)) return null;
        return new SourceToggle(
            playerBuy ? row.Buy : row.Sell,
            playerBuy ? row.BuyStockScu : row.SellDemandScu,
            row.ModifiedUtc,
            pair.SctPrice, pair.SctQuantity, pair.SctUtc);
    }

    /// <summary>
    /// The signed SCT-vs-UEX margin notes for one leg: price, then stock or demand. Null when
    /// there is nothing honest to compare.
    /// <para>
    /// Gated on the reconciler's own state rather than merely on an SCT row existing, so the two
    /// surfaces can never disagree about whether a second source counts. Corroborated and Disagree
    /// are precisely the states where both sides are present AND fresh; UexOnly covers a missing
    /// SCT row, a stale one, and the ship-ammunition carve-out, none of which should print a delta.
    /// </para>
    /// </summary>
    private (double? Price, double? Quantity) SctDeltasFor(TradePriceRow row, string side)
    {
        bool playerBuy = side == "buy";
        if (!_sidePairs.TryGetValue((row.TerminalId, row.CommodityId, playerBuy), out var pair)) return (null, null);

        // Still gated on the reconciler, so a stale second source prints no delta at all: it is the
        // same bar the CORROBORATED line uses, and the two must not disagree about whether a second
        // opinion counts.
        var rec = Reconcile(row, side);
        if (rec is null || (rec.State != PriceSourceState.Corroborated && rec.State != PriceSourceState.Disagree))
            return (null, null);

        return (SctDelta.Pct(playerBuy ? row.Buy : row.Sell, pair.SctPrice),
                SctDelta.Pct(playerBuy ? row.BuyStockScu : row.SellDemandScu, pair.SctQuantity));
    }

    /// <summary>A "SCT +3.2%" margin note. UEX remains the figure it sits beside; this only ever
    /// says how far the second source is from it. Amber once the price gap passes the same 3%
    /// agreement bar the corroboration state uses, dim otherwise - including for every quantity
    /// delta, because stock disagreement between the two feeds is the norm rather than a warning
    /// (they matched on 8 of 631 overlapping sell rows when measured).</summary>
    private static TextBlock? SctDeltaNote(double? pct, bool amberOverThreshold, string what, string otherSource)
    {
        if (pct is not { } p) return null;
        bool amber = amberOverThreshold && Math.Abs(p) > PriceReconciler.AgreeThresholdPct;
        return new TextBlock
        {
            Text = $"{otherSource} {SctDelta.Format(p)}",
            FontFamily = Hud.Font("MonoFont"), FontSize = 9, FontWeight = FontWeights.Bold,
            Foreground = amber ? Hud.Br("AccentBrush") : Hud.Br("FgDimBrush"),
            Margin = new Thickness(7, 0, 0, 1), VerticalAlignment = VerticalAlignment.Bottom,
            ToolTip = $"{(otherSource == "SCT" ? "SC Trade Tools" : "UEX")} reports {what} "
                    + $"{SctDelta.Format(p)} against the figure shown, which is the fresher of the "
                    + "two and the one this route is ranked on.",
        };
    }

    /// <summary>One half of a route's source picker. Carries no age of its own: the two legs can
    /// have been observed at different times by the same feed, so a single age here would be wrong
    /// for at least one of them. Each leg keeps its own freshness pill, which follows whichever
    /// feed is showing.</summary>
    private static Border RouteSourcePill(string name, bool active, bool ranked, Action onClick)
    {
        var pill = new Border
        {
            Background = active ? Hud.Br("AccentFaintBrush") : Hud.Br("Bg2NavBrush"),
            BorderBrush = active ? Hud.Br("AccentStrongBrush") : Hud.Br("NavBorderBrush"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3),
            Padding = new Thickness(9, 2, 9, 2), Margin = new Thickness(0, 0, 6, 0),
            Cursor = active ? null : Cursors.Hand,
            Child = new TextBlock
            {
                Text = name, FontFamily = Hud.Font("UiFont"), FontSize = 9.5, FontWeight = FontWeights.Bold,
                Foreground = active ? Hud.Br("AccentBrush") : Hud.Br("FgDimBrush"),
            },
            // Only UEX prices and ranks the routes, so a reader looking at SCT has to be told the
            // profit above did not come from it.
            // Only stock and demand move. Prices, the trip size, the profit and the ordering are
            // all UEX's whichever half is selected, so this must not imply otherwise.
            ToolTip = ranked
                ? $"Stock and demand as {name} reports them. Prices, trip size and profit are always UEX's."
                : $"Show stock and demand as {name} reports them, for comparison. Prices, trip size "
                + "and profit stay UEX's.",
        };
        if (!active) pill.MouseLeftButtonUp += (_, _) => onClick();
        return pill;
    }

    /// <summary>One "LABEL   value" line in the buy leg's purchase block, so the two lines there
    /// share exactly one layout and cannot drift apart in spacing or type.</summary>
    private static StackPanel LabelledValueLine(string label, string value, string? warnNote = null, string? warnTip = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0),
                                   HorizontalAlignment = HorizontalAlignment.Left };
        row.Children.Add(new TextBlock
        {
            Text = label, FontFamily = Hud.Font("UiFont"), FontSize = 9, FontWeight = FontWeights.Bold,
            Foreground = Hud.Br("FgDimBrush"), VerticalAlignment = VerticalAlignment.Center,
        });
        // The value is always neutral. It used to turn amber to flag "the terminal caps you below
        // your hull", which meant an identical, purely factual range read white on one card and
        // amber on the next with nothing on screen saying why (owner, 2026-08-03). A measurement is
        // not a warning; the warning gets its own words below.
        row.Children.Add(new TextBlock
        {
            Text = value, FontFamily = Hud.Font("MonoFont"), FontSize = 11,
            Foreground = Hud.Br("FgBrush"),
            Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            ToolTip = warnTip,
        });
        if (warnNote is not null)
            row.Children.Add(new TextBlock
            {
                Text = warnNote, FontFamily = Hud.Font("UiFont"), FontSize = 9, FontWeight = FontWeights.Bold,
                Foreground = Hud.Br("AccentBrush"), Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center, ToolTip = warnTip,
            });
        return row;
    }

    /// <summary>The buy leg's container plan: which sizes to actually buy here to reach the
    /// quantity the planner wanted, and an amber shortfall when this kiosk's sizes cannot reach
    /// it. Takes the PRE-SNAP quantity deliberately: TradeRoute.TripQty is already snapped to
    /// what this menu can supply, so planning against it would report a shortfall of zero on
    /// every card. Shares the LabelledValueLine geometry so it sits flush under the largest-size
    /// line above it.</summary>
    private static Panel CratePlanLine(ContainerPlan plan, int plannedQty)
    {
        // WrapPanel, left-aligned: a four-size plan (real: a Cutlass Black needing 16+16+8+4+2)
        // would otherwise run the leg as wide as its longest chip row and drag every sibling with
        // it - which is what stretched the freshness pill (owner, 2026-08-03: "have it isolated so
        // it doesnt affect the freshness pill length"). Wrapping keeps the block inside the card
        // and off the other rows' geometry.
        var row = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0),
                                  HorizontalAlignment = HorizontalAlignment.Left };
        row.Children.Add(new TextBlock
        {
            // Says what it is, in the game's own noun. The line it replaced ("Box size OK for...")
            // was called out as vague and wrong terminology; "RECOMMENDED CONTAINERS" then became
            // "OPTIMAL CONTAINER PURCHASE COUNT" (owner, 2026-08-03) because the number that
            // matters is how many of each to buy, not merely which sizes are recommended.
            Text = "OPTIMAL CONTAINER PURCHASE COUNT", FontFamily = Hud.Font("UiFont"), FontSize = 9, FontWeight = FontWeights.Bold,
            Foreground = Hud.Br("FgDimBrush"), VerticalAlignment = VerticalAlignment.Center,
        });

        if (plan.Empty)
        {
            // A real answer, not a gap: every container this kiosk sells is bigger than the whole trip.
            row.Children.Add(new TextBlock
            {
                Text = $"none sold here small enough for {plannedQty:n0} SCU",
                FontFamily = Hud.Font("UiFont"), FontSize = 11, Foreground = Hud.Br("AccentBrush"),
                Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            });
            return row;
        }

        // One chip per size instead of "21 x 32 SCU + 1 x 24 SCU" (owner, 2026-08-03: "it looks
        // like a math equation rather than easy to digest information"). The plus signs were the
        // worst of it - they read as a sum to be evaluated rather than a shopping list, and a
        // four-size plan (real: 2x16 + 1x8 + 1x4 + 1x2 for a Cutlass Black) read as arithmetic.
        // Discrete chips are the page's own idiom for a set of small facts.
        var tip = $"{plan.BoxCount} container{(plan.BoxCount == 1 ? "" : "s")} to load. "
                + "Fewer, larger containers load faster and cost less to auto-load.";
        foreach (var p in plan.Picks)
        {
            var chip = new Border
            {
                Background = Hud.Br("Bg2NavBrush"), BorderBrush = Hud.Br("NavBorderBrush"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3),
                Padding = new Thickness(7, 2, 7, 2), Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center, ToolTip = tip,
            };
            var inner = new StackPanel { Orientation = Orientation.Horizontal };
            // Count leads and carries the emphasis, because it is the thing being counted out at
            // the kiosk; the size follows dim as the qualifier.
            inner.Children.Add(new TextBlock
            {
                Text = $"{p.Count:n0}", FontFamily = Hud.Font("MonoFont"), FontSize = 11.5,
                Foreground = Hud.Br("FgBrush"), VerticalAlignment = VerticalAlignment.Center,
            });
            inner.Children.Add(new TextBlock
            {
                Text = p.Count == 1 ? $"container, {p.Scu:n0} SCU" : $"containers, {p.Scu:n0} SCU each",
                FontFamily = Hud.Font("UiFont"), FontSize = 10.5, Foreground = Hud.Br("FgDimBrush"),
                Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            });
            chip.Child = inner;
            row.Children.Add(chip);
        }

        // Only shown when it is true: a silent shortfall is exactly the surprise this feature
        // exists to remove.
        if (!plan.HitsTarget)
            row.Children.Add(new TextBlock
            {
                // Says the consequence in words rather than leaving "40 of 46" to be subtracted.
                Text = $"{plan.ShortfallScu:n0} SCU short of the {plannedQty:n0} planned",
                FontFamily = Hud.Font("UiFont"), FontSize = 10.5, Foreground = Hud.Br("AccentBrush"),
                Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                ToolTip = $"This terminal sells no combination of container sizes that reaches "
                        + $"{plannedQty:n0} SCU, so the run loads {plan.TotalScu:n0} SCU instead.",
            });
        return row;
    }

    private static StackPanel BuildLeg(string eyebrow, string terminalName, string? system, double price, string qtyLabel, int qty, int tripQty, DateTime asOfUtc, string containerSizes, int shipMaxContainerScu, bool isBuy, (double? Price, double? Quantity) sctDelta, SourceToggle? sourceToggle, out Action<bool>? applySource, MarketTerminal? terminal = null, Action<int>? onShowOnMap = null)
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
        // UEX, always (owner, 2026-08-03: "for prices lets just stick with UEX for those"). The
        // source toggle below moves stock and demand only, so this never changes after it is drawn
        // and needs no host to swap it through.
        priceRow.Children.Add(new TextBlock { Text = price.ToString("n0", CultureInfo.InvariantCulture), FontFamily = Hud.Font("MonoFont"), FontSize = 13, Foreground = Hud.Br("GoldBrush") });
        priceRow.Children.Add(new TextBlock { Text = "/SCU", FontFamily = Hud.Font("UiFont"), FontSize = 10, Foreground = Hud.Br("FgDimBrush"), Margin = new Thickness(3, 0, 0, 0) });
        if (SctDeltaNote(sctDelta.Price, amberOverThreshold: true, "this price as", "SCT") is { } priceDelta)
            priceRow.Children.Add(priceDelta);
        Grid.SetColumn(priceRow, 2); top.Children.Add(priceRow);
        leg.Children.Add(top);

        var barRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 0) };
        var bar = new Grid { Width = 90 };
        var barTip = $"This bar shows how much of your trip the {(qtyLabel == "STOCK" ? "stock" : "demand")} covers. Green: covers your full trip. Amber: covers at least half. Red: less than half.";
        void PaintBar(int amount)
        {
            bar.Children.Clear();
            bar.Children.Add(TripBar(TradeBarMath.FillFraction(amount, tripQty),
                                     TradeBarMath.Color(TradeBarMath.Tier(amount, tripQty)), barTip));
        }
        PaintBar(qty);
        barRow.Children.Add(bar);
        var qtyValue = new TextBlock { Text = $"{qtyLabel} {qty:n0} SCU", FontFamily = Hud.Font("MonoFont"), FontSize = 10, Foreground = Hud.Br("FgDimBrush"), Margin = new Thickness(8, 0, 0, 0) };
        barRow.Children.Add(qtyValue);
        var qtyStamp = new TextBlock
        {
            FontFamily = Hud.Font("MonoFont"), FontSize = 9, FontWeight = FontWeights.Bold,
            Foreground = Hud.Br("CyanBrush"), Margin = new Thickness(7, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed,
            ToolTip = "How long ago SC Trade Tools observed this quantity. The price beside it is "
                    + "UEX's and is dated by the pill below.",
        };
        barRow.Children.Add(qtyStamp);
        var qtyDeltaHost = new ContentControl { VerticalAlignment = VerticalAlignment.Bottom };
        qtyDeltaHost.Content = SctDeltaNote(sctDelta.Quantity, amberOverThreshold: false, qtyLabel == "STOCK" ? "stock as" : "demand as", "SCT");
        barRow.Children.Add(qtyDeltaHost);
        // Max container size (task 2): AccentBrush warning when this leg's biggest box is smaller
        // than the ship's best - the trip needs smaller crates than the ship could otherwise carry.
        var legMaxScu = TradeMath.MaxContainerScu(containerSizes);
        // On the SELL leg the chip stays beside DEMAND where it always was. On the BUY leg it moves
        // down to sit directly above RECOMMENDED CONTAINERS (owner, 2026-08-03), because the two are
        // one thought: the biggest container this kiosk sells, then what to actually buy with it.
        if (!isBuy && MaxContainerChip(legMaxScu, warning: legMaxScu is { } m && m < shipMaxContainerScu) is { } maxChip)
            barRow.Children.Add(maxChip);
        leg.Children.Add(barRow);

        var age = DateTime.UtcNow - asOfUtc;
        // Left-aligned here rather than inside FreshChip (which the Sell and Prices flows share):
        // a Border in a vertical StackPanel stretches to the panel's width by default, so the pill
        // was inheriting the width of whatever the widest row below it happened to be.
        // Dates the PRICE, which is UEX's and never moves. The quantity carries its own tag when
        // it is showing SCT, because one pill cannot honestly date two figures from two feeds.
        var freshChip = FreshChip(FreshChipAge(age), age.TotalHours >= 24);
        freshChip.HorizontalAlignment = HorizontalAlignment.Left;
        leg.Children.Add(freshChip);

        // Repaints THIS LEG's stock or demand only. Handed back so ONE toggle on the card can drive
        // both legs together (owner: "have toggle only affect stock and demand quantities"). Null
        // when only UEX reports this side, which is what lets a card with a single switchable leg
        // still offer the toggle without pretending the other moved.
        applySource = null;
        if (sourceToggle is { } tog)
            applySource = sct =>
            {
                var shownQty = sct ? tog.SctQuantity : tog.UexQuantity;
                qtyValue.Text = $"{qtyLabel} {shownQty:n0} SCU";
                PaintBar(shownQty);
                // The margin note always quotes the feed NOT on screen, so it flips with it.
                qtyDeltaHost.Content = SctDeltaNote(
                    SctDelta.Pct(shownQty, sct ? tog.UexQuantity : tog.SctQuantity),
                    amberOverThreshold: false, qtyLabel == "STOCK" ? "stock as" : "demand as", sct ? "UEX" : "SCT");
                // When the figure beside it is no longer UEX's, say when SCT saw it - the leg's
                // freshness pill is dating the price and would otherwise be read as covering both.
                var sctAge = DateTime.UtcNow - tog.SctUtc;
                qtyStamp.Text = sct ? $"SCT {FreshChipAge(sctAge)}" : "";
                qtyStamp.Visibility = sct ? Visibility.Visible : Visibility.Collapsed;
            };

        // The purchase block, seated UNDER the freshness pill at the owner's request: it is the
        // leg's conclusion, so it reads last rather than interrupting the price/stock/freshness run.
        // Buy leg only - the sell leg unloads cargo the ship already holds, so both "largest size
        // for purchase" and a container plan would be advice about a purchase already made.
        return leg;
    }
}
