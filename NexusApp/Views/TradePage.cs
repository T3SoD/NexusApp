using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using NexusApp.Services;

namespace NexusApp.Views;

// TRADE page: route planner, sell lookup, price browser (spec docs/superpowers/specs/
// 2026-07-29-trading-tab-design.md; mock nexus-design-lab/trading-tab). Built entirely in code,
// no separate .xaml, matching CommandPage/SettingsPage. Partial across TradePage.cs (this file:
// skeleton, tab strip, context row), TradePage.Planner.cs, TradePage.Sell.cs, TradePage.Prices.cs
// (Tasks 12-14), and TradeBadges.cs (Task 15, SCT corroboration).
public sealed partial class TradePage : UserControl
{
    // ── Tab strip state (ported from SettingsPage.cs:34-46, 3 tabs, no danger tab, no pip) ──
    private readonly Border[] _tabButtons = new Border[3];
    private readonly TextBlock[] _tabLabels = new TextBlock[3];
    // Task 10: PlannerHost (index 0) owns its own internal Auto/Star scroll split for anchored
    // inputs, so this is FrameworkElement, not ScrollViewer - Sell/Prices (indices 1-2) are still
    // built by WrapPane and stay ScrollViewers underneath, just held through the wider type.
    private readonly FrameworkElement[] _panes = new FrameworkElement[3];
    private readonly TranslateTransform _underlineT = new();
    private readonly SolidColorBrush _underlineBrush;
    private Grid _stripHost = null!;
    private Border _underline = null!;
    private DropShadowEffect _underlineGlow = null!;
    private int _activeIndex = -1;

    private static readonly string[] TabLabels = { "Planner", "Sell", "Prices" };   // mock:1046-1050

    // ── Flow content hosts (empty here; Tasks 12-14 populate them via Rebuild*) ──
    // PlannerHost is a Grid, not a StackPanel (task 10): TradePage.Planner.cs's BuildPlannerChrome
    // gives it an Auto row (inputs) + Star row (a ScrollViewer around results only), so the
    // planner's inputs stay anchored on screen while its results scroll. Sell/Prices are unchanged.
    internal readonly Grid PlannerHost = new();
    internal readonly StackPanel SellHost = new();
    internal readonly StackPanel PricesHost = new();

    // ── Context row state (mock .ctxrow, index.html:1113-1131) ──
    // ORIGIN chip (task 10): display-only. Shows the live session location (with the LIVE
    // indicator) or "No session" - the manual dropdown/click-to-change path (the old _originCombo,
    // _originManualOverride, _manualOriginName/_manualOriginSeeded, ManualOrigin()) is gone
    // entirely; the route planner's own Starting Location picker (TradePage.Planner.cs) is now
    // where a manual origin gets chosen, scoped to that one flow instead of shared page-wide state.
    private Border _originChip = null!;
    private Ellipse _originDot = null!;
    private TextBlock _originValue = null!;
    private bool _originDotLive;          // the dot is already wearing its live dressing (glow effect +
                                            // breathe loop): refreshes that STAY in live mode must not
                                            // re-allocate the effect or restart the loop from full opacity
    private readonly Border[] _scopePills = new Border[4];
    private static readonly string[] Scopes = { "ALL", "STANTON", "PYRO", "NYX" };   // mock:1116
    private Border _uexPill = null!;
    private TextBlock _uexAgeValue = null!;
    private Ellipse _uexPillDot = null!;
    private Border _sctAgePill = null!;
    private TextBlock _sctAgeValue = null!;
    private Ellipse _sctPillDot = null!;

    // Datamined starmap positions (owner's ask, 2026-07-30), shared by the Planner and Sell flows'
    // distance tags - loaded once per page instance, same idiom as _shipCatalog below.
    private readonly StarmapCatalog _starmap = StarmapCatalog.LoadEmbedded();

    public TradePage()
    {
        _underlineBrush = new SolidColorBrush(Hud.Col("AccentColor"));

        var root = new Grid { Margin = new Thickness(28, 22, 28, 0) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // header
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // tab strip
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // context row
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // pane host

        var header = Hud.Header("Trade", "Trade",
            "Route planning, sell lookup, and price browsing across Stanton, Pyro, and Nyx.");   // mock:1106-1108
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        BuildStrip();
        var stripPills = BuildStripPills();   // must exist before BuildContextRow() below, whose trailing
                                                // RefreshContextRow() call reads _uexPill/_sctAgePill
        var stripRow = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(stripPills, Dock.Right);
        stripRow.Children.Add(stripPills);
        stripRow.Children.Add(_stripHost);   // fills the rest (left)
        Grid.SetRow(stripRow, 1);
        root.Children.Add(stripRow);

        var contextRow = BuildContextRow();
        Grid.SetRow(contextRow, 2);
        root.Children.Add(contextRow);

        var paneHost = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        // Planner (task 10, anchored inputs): PlannerHost owns its own internal Auto/Star split
        // (built in TradePage.Planner.cs's BuildPlannerChrome) so its inputs stay pinned while only
        // the results scroll - it is NOT wrapped in WrapPane like the other two flows, which still
        // scroll whole-pane exactly as before.
        _panes[0] = PlannerHost;
        _panes[1] = WrapPane(SellHost);
        _panes[2] = WrapPane(PricesHost);
        foreach (var pane in _panes) { pane.Visibility = Visibility.Collapsed; paneHost.Children.Add(pane); }
        Grid.SetRow(paneHost, 3);
        root.Children.Add(paneHost);

        Content = root;

        int restore = Array.IndexOf(TradeFlows.Ids, TradeFlows.NormalizeForRestore(App.Settings.Current.TradeActiveFlow));
        SwitchTab(restore < 0 ? 0 : restore, persist: false);

        _stripHost.Loaded += (_, _) => MoveUnderline(_activeIndex, animate: false);
        _stripHost.SizeChanged += (_, _) => MoveUnderline(_activeIndex, animate: false);

        // Live data refresh triggers, each gated on the page actually being on screen: MainWindow
        // collapses the whole page host (SetActivePage sets PageTrade.Visibility), which clears
        // IsVisible on this control, and re-entry always calls Refresh() via InitTradePage - so a
        // tick that lands while the user is elsewhere is never lost, just not paid for. Off-screen
        // repaints are not free either: they churn the inputs' surrounding state for nobody.
        App.Market.Changed += () => Dispatcher.BeginInvoke(() => { if (IsVisible) Refresh(); });
        App.Locations.Changed += () => Dispatcher.BeginInvoke(() => { if (IsVisible) RefreshContextRow(); });
        // SCT is a worker-thread raise (the service documents it), so this marshals like the other
        // two. Without this subscription nothing repainted when the first dark fetch landed: the
        // age pill and every corroboration badge waited for the next hourly market tick.
        App.Sct.Changed += () => Dispatcher.BeginInvoke(() => { if (IsVisible) RefreshSctSurfaces(); });

        RebuildPlanner();
        RebuildSell();
        RebuildPrices();
    }

    private static ScrollViewer WrapPane(UIElement content) => new()
    {
        Content = content,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
    };

    /// <summary>Called by MainWindow.InitTradePage() on every visit, so a snapshot refresh or an
    /// origin change that happened while the user was on another page is caught immediately.</summary>
    public void Refresh()
    {
        RefreshContextRow();
        RebuildPlanner();
        RebuildSell();
        RebuildPrices();
    }

    /// <summary>The surfaces a new SCT snapshot changes: the age pill, plus every flow's results
    /// (the planner's corroboration line, the sell flow's badges and SCT-only rows, the price
    /// browser's merged SCT-only rows). The UEX-sourced context row readouts are untouched.</summary>
    private void RefreshSctSurfaces()
    {
        RefreshSctAgePill();
        RebuildPlanner();
        RebuildSell();
        RebuildPrices();
    }

    // ── Shared consent guard (Tasks 12-14 call this first in every Rebuild*) ─────────────────
    // Mirrors the tri-state check used at every other price surface (MainWindow.Codex.cs:1033,
    // MainWindow.WorkOrders.cs:274, OverlayWindow.xaml.cs:1732): only "== true" (explicit opt-in)
    // shows data; null (unanswered) and false (declined) both show the same empty state, and the
    // page-level MarketConsentHost strip (wired in this task's MainWindow.xaml.cs changes) is what
    // asks the question - this guard just keeps the page itself honest meanwhile.
    private const string ConsentEmptyMessage =
        "Turn on live market data (above, or in Settings) to see trade routes, sell prices, and the price browser.";

    // Each flow's input area is built ONCE and only its results area is torn down and rebuilt, so
    // this hides the inputs while unconsented rather than destroying them: the flow renders exactly
    // this one message and no stale controls, and the inputs come straight back when consent flips
    // on (MainWindow's enable handler calls Refresh()).
    private bool EnsureMarketConsent(Panel resultsHost, UIElement inputs)
    {
        if (App.Settings.Current.MarketDataEnabled == true)
        {
            inputs.Visibility = Visibility.Visible;
            return true;
        }
        inputs.Visibility = Visibility.Collapsed;
        resultsHost.Children.Clear();
        resultsHost.Children.Add(new TextBlock
        {
            Text = ConsentEmptyMessage, FontFamily = Hud.Font("UiFont"), FontSize = 12.5,
            Foreground = Hud.Br("FgDimBrush"), TextWrapping = TextWrapping.Wrap, MaxWidth = 520,
            Margin = new Thickness(0, 8, 0, 0),
        });
        return false;
    }

    // ── CascadeIn: hand-duplicated per page by house convention (confirmed: NOT a shared Hud
    // helper; CommandPage.cs:170 and MainWindow.Codex.cs:1366 each keep their own copy on purpose).
    // TradePage keeps exactly one copy here since Tasks 12-14 share this one partial class. ──
    internal static void CascadeIn(FrameworkElement fe, int index)
    {
        if (Motion.Reduced) { fe.Opacity = 1; fe.RenderTransform = null; return; }
        const int riseInPx = 12;
        const int stepMs = 40;      // MainWindow.xaml.cs:1693, mock MS.cascadeStep
        const int durMs = 200;      // MainWindow.xaml.cs:1694/mock MS.cascade
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };   // mock QUADOUT
        var begin = TimeSpan.FromMilliseconds(index * stepMs);
        var tt = new TranslateTransform(0, riseInPx);
        fe.RenderTransform = tt;
        fe.Opacity = 0;
        fe.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(durMs)) { BeginTime = begin, EasingFunction = ease });
        tt.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(riseInPx, 0, TimeSpan.FromMilliseconds(durMs)) { BeginTime = begin, EasingFunction = ease });
    }

    // ── SCT corroboration (Task 15) ───────────────────────────────────────────────────────────
    // Real, id-based lookup surface (SctMarketService.Find/SctOnlyBuyers): TradePriceRow already
    // carries both TerminalId/CommodityId, so this needs no name-based resolution. The outer flag
    // check is kept even though Find/SctOnlyBuyers both self-gate on SctDataEnabled - "zero UI
    // trace while dark" is checked live at every call site, not assumed from the service alone.
    private static SctListing? FindSctListing(TradePriceRow row, string side) =>
        App.Settings.Current.SctDataEnabled ? App.Sct.Find(row.TerminalId, row.CommodityId, side) : null;

    internal static ReconciledPrice? Reconcile(TradePriceRow row, string side) =>
        PriceReconciler.Reconcile(row, side, FindSctListing(row, side), DateTime.UtcNow);

    // The synthesized reconciliation for a listing UEX has no row for at all - the same shape
    // PriceReconciler.Reconcile itself returns for the SCT-only case. Shared by the sell flow's
    // SCT-only buyer rows and the price browser's merged SCT-only rows so the synthesized shape
    // lives in one place instead of two identical constructor calls.
    internal static ReconciledPrice SctOnlyReconciled(SctListing listing) =>
        new(listing.Price, PriceSourceState.SctOnly, 0, default, listing.TimestampUtc);

    // Fade+scale-in 150ms SlideOut easing, reduced-motion instant (mock BADGE_MS=150, index.html:458).
    // Shared by CorroborationBadge (badges, scale 0.8, mock:942-957) and the planner's corrline
    // (scale 0.96, mock:802-804) so the one animation idiom lives once instead of twice.
    private static void FadeScaleIn(FrameworkElement el, double fromScale)
    {
        if (Motion.Reduced) { el.Opacity = 1; return; }
        el.Opacity = 0;
        var st = new ScaleTransform(fromScale, fromScale);
        el.RenderTransform = st;
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        var dur = TimeSpan.FromMilliseconds(150);
        el.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = Motion.SlideOut });
        st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(fromScale, 1, dur) { EasingFunction = Motion.SlideOut });
        st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(fromScale, 1, dur) { EasingFunction = Motion.SlideOut });
    }

    // Corroboration badge: absent entirely for UexOnly (the common/dark case) - TradeBadges.Text
    // returning null is the single source of truth for that, so this never needs its own state
    // check. Colors: Corroborated = ok fill/border/dot (mock:311), Disagree = amber chip (mock:312),
    // SctOnly = transparent fill + dim outline (mock:313 wants a dashed border; WPF has no cheap
    // dashed CornerRadius border primitive, so a solid dim outline is the closest faithful
    // approximation - recorded deviation, unchanged from the brief).
    internal static FrameworkElement? CorroborationBadge(ReconciledPrice? reconciled)
    {
        if (reconciled is not { } r) return null;
        var text = TradeBadges.Text(r.State, r.DisagreePct);
        if (text is null) return null;   // UexOnly

        var (bg, border, fg) = r.State switch
        {
            PriceSourceState.Corroborated => (new SolidColorBrush(Color.FromArgb(0x1F, 0x66, 0xE6, 0xA6)), new SolidColorBrush(Color.FromArgb(0x66, 0x66, 0xE6, 0xA6)), Hud.Br("OkBrush")),
            PriceSourceState.Disagree     => (Hud.Br("AccentFaintBrush"), Hud.Br("AccentStrongBrush"), Hud.Br("AccentBrush")),
            _                             => ((Brush)Brushes.Transparent, Hud.Br("BorderBrush"), Hud.Br("FgDimBrush")),   // SctOnly
        };
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        if (r.State == PriceSourceState.Corroborated)
            content.Children.Add(new Ellipse { Width = 5, Height = 5, Fill = fg, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = text, FontFamily = Hud.Font("MonoFont"), FontSize = 9, FontWeight = FontWeights.Bold, Foreground = fg });

        var badge = new Border
        {
            Background = bg, BorderBrush = border, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3), Padding = new Thickness(7, 2, 7, 2), Child = content,
            ToolTip = TradeBadges.Tooltip(r.State, r.DisagreePct),
        };
        FadeScaleIn(badge, 0.8);
        return badge;
    }

    // ── Small shared chip builders (Tasks 12-14 call these) ──────────────────────────────────

    // Proximity tier chip: mono 9 bold dim, 1px line-strong border, radius 3, padding 2,7,2,7 -
    // deliberately NOT color-coded (mock:228-230, manifest: "Proximity tier chip ... deliberately
    // NOT color-coded, modeled on PatchTagChip geometry"). BorderBrush here is the "line-strong"
    // token (mock --line-strong = BorderColor #337FE9E0, Palette.Luxury.xaml:14), not NavBorderBrush.
    internal static Border TierChip(ProximityTier tier)
    {
        var text = new TextBlock
        {
            Text = ProximityTiers.Label(tier), FontFamily = Hud.Font("MonoFont"), FontSize = 9,
            FontWeight = FontWeights.Bold, Foreground = Hud.Br("FgDimBrush"),
        };
        var chip = new Border
        {
            BorderBrush = Hud.Br("BorderBrush"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3), Padding = new Thickness(7, 2, 7, 2),
            Child = text, VerticalAlignment = VerticalAlignment.Center,
        };
        chip.ToolTip = "Distance between the buy and sell stops. Closest to farthest: Same Orbit, " +
                       "Same Planet, Same System, Cross-System.";   // mock:653, verbatim
        return chip;
    }

    // Freshness/staleness chip: mono 9 bold, dim by default, re-tints amber past 24h (mock:264-271,
    // manifest: "generalizes ... PatchTagChip ... from 'patch tag' to 'age of this price'").
    // Built locally (not reusing MainWindow's private PatchTagChip) since that helper is not public
    // and this page needs its own age-vs-patch semantics, not a patch string.
    internal static Border FreshChip(string ageText, bool stale)
    {
        var text = new TextBlock
        {
            Text = $"{ageText} ago", FontFamily = Hud.Font("MonoFont"), FontSize = 9, FontWeight = FontWeights.Bold,
            Foreground = stale ? Hud.Br("AccentBrush") : Hud.Br("FgDimBrush"),
        };
        return new Border
        {
            Background = stale ? Hud.Br("AccentFaintBrush") : new SolidColorBrush(Color.FromArgb(0x1F, 0x86, 0x93, 0xA0)),
            BorderBrush = stale ? Hud.Br("AccentStrongBrush") : new SolidColorBrush(Color.FromArgb(0x66, 0x86, 0x93, 0xA0)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3),
            Padding = new Thickness(5, 1, 5, 1), Child = text, VerticalAlignment = VerticalAlignment.Center,
        };
    }

    /// <summary>FreshChip's age fragment, from an age. FreshChip always appends " ago" itself, so
    /// this strips that suffix from MarketNotice.FormatAge's "Xm/Xh/Xd ago" shapes before handing it
    /// over. FormatAge's "just now" case (age under a minute) has no " ago" to strip - passing it
    /// through would render "just now ago" - so that one case gets its own short unit fragment
    /// instead, matching FreshChip's "number+unit" contract. Shared by the planner's legs and the
    /// sell flow's buyer rows, which need the identical fragment.</summary>
    internal static string FreshChipAge(TimeSpan age)
    {
        var raw = MarketNotice.FormatAge(age);
        return raw.EndsWith(" ago", StringComparison.Ordinal) ? raw[..^4] : "<1m";
    }

    // System tag (owner's live-pass ask, 2026-07-30): a dim, small, uppercase suffix naming which
    // star system (Stanton/Pyro/Nyx) a terminal is in, so ALL-scope rows stay unambiguous. NOT a
    // chip - no border/background, matches the eyebrow/dim-label idiom used elsewhere on this page
    // (e.g. the BUY AT / SELL AT eyebrows, HeaderCell). Absent (never a placeholder or dash) for
    // null/whitespace System - SCT-only rows have no UEX terminal id to resolve one from.
    internal static FrameworkElement? SystemTag(string? system)
    {
        if (string.IsNullOrWhiteSpace(system)) return null;
        return new TextBlock
        {
            Text = system.ToUpperInvariant(), FontFamily = Hud.Font("UiFont"), FontSize = 9,
            FontWeight = FontWeights.Bold, Foreground = Hud.Br("FgDimBrush"),
            Margin = new Thickness(6, 0, 0, 1), VerticalAlignment = VerticalAlignment.Bottom,
        };
    }

    // Distance tag (owner's ask, 2026-07-30, decorating beyond the approved mock): a dim gigameter
    // readout naming the real straight-line distance between a route's two legs (or the origin and
    // a buyer), shown only when StarmapCatalog resolved both ends in the same system. Same dim/
    // small/no-chrome geometry as SystemTag right above, but a SIBLING helper rather than a shared
    // one - SystemTag's ToUpperInvariant would turn "Gm" into "GM", which is not the unit's real
    // casing, so this keeps the formatted text exactly as StarmapCatalog.FormatGm produced it.
    // Absent (never a placeholder or dash) for null/whitespace - every non-resolving path (either
    // terminal missing, either side unresolved on the starmap, or a cross-system pair) already
    // stops before this is even called.
    internal static FrameworkElement? DistanceTag(string? formatted)
    {
        if (string.IsNullOrWhiteSpace(formatted)) return null;
        return new TextBlock
        {
            Text = formatted, FontFamily = Hud.Font("UiFont"), FontSize = 9,
            FontWeight = FontWeights.Bold, Foreground = Hud.Br("FgDimBrush"),
            Margin = new Thickness(6, 0, 0, 1), VerticalAlignment = VerticalAlignment.Bottom,
        };
    }

    // Max container size tag (task 2, container-size ground truth): a dim readout naming the
    // largest crate a terminal's container_sizes list offers (TradeMath.MaxContainerScu), same
    // dim/small/no-chrome geometry as DistanceTag right above - a SIBLING helper, not shared,
    // since this one uses MonoFont to match the numeric STOCK/DEMAND readouts it sits beside on
    // the sell and planner rows, where DistanceTag's Gm figure uses UiFont. Absent (never a
    // placeholder or dash) when maxScu is null - callers pass TradeMath.MaxContainerScu's result
    // straight through. warning tints AccentBrush instead of the default dim: the planner-leg case
    // where the terminal's biggest box is smaller than the ship's best; the sell flow has no ship
    // context and never passes true.
    internal static FrameworkElement? MaxContainerChip(int? maxScu, bool warning = false)
    {
        if (maxScu is not { } n) return null;
        return new TextBlock
        {
            Text = $"MAX {n} SCU", FontFamily = Hud.Font("MonoFont"), FontSize = 9,
            FontWeight = FontWeights.Bold, Foreground = warning ? Hud.Br("AccentBrush") : Hud.Br("FgDimBrush"),
            Margin = new Thickness(6, 0, 0, 1), VerticalAlignment = VerticalAlignment.Bottom,
        };
    }

    // ── Tab strip (ported from SettingsPage.cs:133-173, 3 tabs, no right-docked danger tab) ──
    private void BuildStrip()
    {
        _stripHost = new Grid { Height = 42 };
        _stripHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var hairline = new Border { Height = 1, Background = Hud.Br("NavBorderBrush"), VerticalAlignment = VerticalAlignment.Bottom };
        _stripHost.Children.Add(hairline);

        var cluster = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
        for (int i = 0; i < TabLabels.Length; i++) cluster.Children.Add(MakeTab(i, TabLabels[i]));
        _stripHost.Children.Add(cluster);

        _underlineGlow = new DropShadowEffect { Color = Hud.Col("AccentColor"), BlurRadius = 8, ShadowDepth = 0, Opacity = 0.5 };   // mock:135
        _underline = new Border
        {
            Height = 2, Width = 0, CornerRadius = new CornerRadius(1),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
            Background = _underlineBrush, RenderTransform = _underlineT, Effect = _underlineGlow,
        };
        _stripHost.Children.Add(_underline);
    }

    // ── Data source pills, docked right of the tab strip (owner's ask, 2026-07-31: moved out of the
    // context row so they read as strip chrome, not a row 2 filter) - built here so the fields exist
    // before BuildContextRow()'s trailing RefreshContextRow()/RefreshSctAgePill() calls run. Moved
    // verbatim out of BuildContextRow: same BuildPill() calls, same SCT margin, same Collapsed default.
    // VerticalAlignment=Center on the wrapper so the LEDs line up with the tab labels.
    private StackPanel BuildStripPills()
    {
        var pills = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        _uexPill = BuildPill("UEX", out _uexAgeValue, out _uexPillDot);
        _uexPill.ToolTip = "";   // set live in RefreshContextRow (age changes the tooltip text)
        pills.Children.Add(_uexPill);

        _sctAgePill = BuildPill("SCT", out _sctAgeValue, out _sctPillDot);
        _sctAgePill.Margin = new Thickness(8, 0, 0, 0);
        _sctAgePill.Visibility = Visibility.Collapsed;   // Task 11 also owns this pill's absence rule
        pills.Children.Add(_sctAgePill);

        return pills;
    }

    private Border MakeTab(int index, string label)
    {
        var text = new TextBlock
        {
            Text = label.ToUpperInvariant(), FontFamily = Hud.Font("UiFont"), FontSize = 12,
            FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center,
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
        btn.MouseEnter += (_, _) => { text.Foreground = Hud.Br("FgBrush"); btn.Background = Hud.Br("AccentFaintBrush"); };   // mock:132
        btn.MouseLeave += (_, _) => { text.Foreground = TabColor(index); btn.Background = Brushes.Transparent; };
        btn.MouseLeftButtonUp += (_, _) => SwitchTab(index);
        return btn;
    }

    private Brush TabColor(int index) => index == _activeIndex ? Hud.Br("GoldBrush") : Hud.Br("FgDimBrush");   // mock:133, active=GOLD not amber

    private void SwitchTab(int index, bool persist = true)
    {
        if (index == _activeIndex && _panes[index].Visibility == Visibility.Visible) return;
        int previous = _activeIndex;
        _activeIndex = index;
        for (int i = 0; i < _panes.Length; i++) _panes[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
        for (int i = 0; i < _tabLabels.Length; i++) _tabLabels[i].Foreground = TabColor(i);
        MoveUnderline(index, animate: true);
        RevealPane(index, previous);
        if (persist)
        {
            App.Settings.Current.TradeActiveFlow = TradeFlows.Ids[index];
            App.Settings.Save();
            Logger.Info($"[UI] Trade flow: {TradeFlows.Ids[index].ToUpperInvariant()}");
        }
    }

    private (double X, double Width) MeasureTab(int index)
    {
        var t = _tabButtons[index];
        double x = t.TransformToAncestor(_stripHost).Transform(default).X;
        return (x, t.ActualWidth);
    }

    private void MoveUnderline(int index, bool animate)
    {
        if (_stripHost.ActualWidth < 1) return;
        var (x, w) = MeasureTab(index);
        if (!animate || Motion.Reduced)
        {
            _underlineT.BeginAnimation(TranslateTransform.XProperty, null); _underlineT.X = x;
            _underline.BeginAnimation(FrameworkElement.WidthProperty, null); _underline.Width = w;
            return;
        }
        var dur = TimeSpan.FromMilliseconds(Motion.SlideMs);   // mock MS.slide=280
        _underlineT.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, dur) { EasingFunction = Motion.Reveal });
        _underline.BeginAnimation(FrameworkElement.WidthProperty, new DoubleAnimation(w, dur) { EasingFunction = Motion.Reveal });
    }

    private void RevealPane(int index, int previous)
    {
        var pane = _panes[index];
        if (Motion.Reduced || previous < 0) { pane.BeginAnimation(UIElement.OpacityProperty, null); pane.Opacity = 1; pane.RenderTransform = null; return; }
        int dir = index > previous ? 1 : -1;
        var slide = new TranslateTransform(12 * dir, 0);
        pane.RenderTransform = slide; pane.Opacity = 0;
        var dur = TimeSpan.FromMilliseconds(Motion.DrillMs);
        pane.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = Motion.Reveal });
        var glide = new DoubleAnimation(12 * dir, 0, dur) { EasingFunction = Motion.Reveal };
        glide.Completed += (_, _) => { if (ReferenceEquals(pane.RenderTransform, slide)) pane.RenderTransform = null; };
        slide.BeginAnimation(TranslateTransform.XProperty, glide);
    }

    // ── Context row (mock .ctxrow, index.html:1113-1131) ─────────────────────────────────────
    private FrameworkElement BuildContextRow()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 18) };   // mock:143 margin:16 0 18

        _originChip = BuildOriginChip();
        row.Children.Add(_originChip);
        row.Children.Add(Sep());

        for (int i = 0; i < Scopes.Length; i++)
        {
            int idx = i;
            var pill = ScopePill(Scopes[i]);
            pill.MouseLeftButtonUp += (_, _) => SetScope(Scopes[idx]);
            _scopePills[i] = pill;
            row.Children.Add(pill);
        }

        RefreshContextRow();
        RefreshScopePills();
        return row;
    }

    private static Border Sep() => new()
    {
        Width = 1, Margin = new Thickness(2, 0, 2, 0), VerticalAlignment = VerticalAlignment.Stretch,
        Background = Hud.Br("NavBorderBrush"),
    };

    // Status-strip pill chrome, reused verbatim (MainWindow.xaml:110-146 / mock:145-149): padding
    // 9,3,11,3, radius 4, Bg2Nav fill, 1px NavBorder line, dot 7x7, label mono 9 bold dim, value mono 10.
    // The dot (owner's live-pass ask, 2026-07-30, item 4: a freshness LED before the label) is the
    // same 7x7 Ellipse construction MainWindow's own top-strip pills use (MainWindow.xaml e.g.
    // SessionDot/BlueprintDot/ShardDot: Width=7 Height=7, Margin 0,0,7,0, VerticalAlignment=Center) -
    // built once here and dressed live by SetFreshnessDot below, never animated (static fill; the
    // ORIGIN live dot stays the only breathing LED on this page).
    private static Border BuildPill(string label, out TextBlock valueText, out Ellipse dot)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        dot = new Ellipse
        {
            Width = 7, Height = 7, Fill = Hud.Br("FgDimBrush"), Margin = new Thickness(0, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(dot);
        row.Children.Add(new TextBlock
        {
            Text = label, FontFamily = Hud.Font("UiFont"), FontSize = 9, FontWeight = FontWeights.Bold,
            Foreground = Hud.Br("FgDimBrush"), Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center,
        });
        valueText = new TextBlock { FontFamily = Hud.Font("MonoFont"), FontSize = 10, Foreground = Hud.Br("FgBrush") };
        row.Children.Add(valueText);
        return new Border
        {
            Background = Hud.Br("Bg2NavBrush"), BorderBrush = Hud.Br("NavBorderBrush"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4), Padding = new Thickness(9, 3, 11, 3), Margin = new Thickness(0, 0, 0, 0),
            Child = row, VerticalAlignment = VerticalAlignment.Center,
        };
    }

    // Freshness LED color rule (item 4), consistent with FreshChip's existing 24h staleness cutoff:
    // OkBrush under 24h, AccentBrush (amber) at/after 24h, DangerBrush when the pill is rendered but
    // there is no data at all yet (age is null - consent on, nothing fetched). Static fill only,
    // called fresh on every RefreshContextRow/RefreshSctAgePill pass.
    private static void SetFreshnessDot(Ellipse dot, TimeSpan? age)
    {
        dot.Fill = age is null ? Hud.Br("DangerBrush")
            : age.Value.TotalHours >= 24 ? Hud.Br("AccentBrush")
            : Hud.Br("OkBrush");
    }

    private static Border ScopePill(string label)
    {
        var text = new TextBlock
        {
            Text = label, FontFamily = Hud.Font("UiFont"), FontSize = 10.5, FontWeight = FontWeights.Bold,
            Foreground = Hud.Br("FgDimBrush"),
        };
        return new Border
        {
            Background = Hud.Br("Bg2NavBrush"), BorderBrush = Hud.Br("NavBorderBrush"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4), Padding = new Thickness(10, 4, 10, 4), Cursor = Cursors.Hand,
            Margin = new Thickness(0, 0, 8, 0), Child = text, VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private void SetScope(string scope)
    {
        if (App.Settings.Current.TradeScope == scope) return;
        App.Settings.Current.TradeScope = scope;
        App.Settings.Save();
        Logger.Info($"[UI] Trade scope: {scope}");
        RefreshScopePills();
        RebuildPlanner();
        RebuildSell();
        RebuildPrices();
    }

    private void RefreshScopePills()
    {
        var active = App.Settings.Current.TradeScope;
        for (int i = 0; i < Scopes.Length; i++)
        {
            bool on = Scopes[i] == active;
            var text = (TextBlock)_scopePills[i].Child;
            text.Foreground = on ? Hud.Br("AccentBrush") : Hud.Br("FgDimBrush");
            _scopePills[i].BorderBrush = on ? Hud.Br("AccentStrongBrush") : Hud.Br("NavBorderBrush");
            _scopePills[i].Background = on ? Hud.Br("AccentFaintBrush") : Hud.Br("Bg2NavBrush");
        }
    }

    // ORIGIN chip (mock index.html:691-716), display-only since task 10. Live state: cyan pulse
    // dot + "{loc} - LIVE" mono cyan. No-session state: dim dot, "No session" text - there is no
    // manual dropdown/click-to-change path anymore (that concept moved to the route planner's own
    // Starting Location picker, TradePage.Planner.cs, scoped to that one flow instead of shared
    // page-wide state). Which state shows is driven purely by whether a live location is currently
    // known (App.Locations.LastKnownLocation).
    private Border BuildOriginChip()
    {
        _originDot = new Ellipse
        {
            Width = 7, Height = 7, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        _originValue = new TextBlock { FontFamily = Hud.Font("MonoFont"), FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
        var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(_originDot);
        content.Children.Add(new TextBlock
        {
            Text = "ORIGIN", FontFamily = Hud.Font("UiFont"), FontSize = 9, FontWeight = FontWeights.Bold,
            Foreground = Hud.Br("FgDimBrush"), Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center,
        });
        content.Children.Add(_originValue);
        return new Border
        {
            Background = Hud.Br("Bg2NavBrush"), BorderBrush = Hud.Br("NavBorderBrush"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4), Padding = new Thickness(9, 3, 11, 3), Margin = new Thickness(0, 0, 8, 0),
            Child = content, VerticalAlignment = VerticalAlignment.Center,
        };
    }

    /// <summary>The terminal names the manual ORIGIN dropdown offers, in the order it offers
    /// them: only terminals that actually carry price data (live: 823 terminals -> 135 priced),
    /// never the full raw /terminals list. Filters by TerminalId, never by name - the price
    /// row's TerminalName and the Terminals row's Name are reported by different UEX endpoints
    /// and their vocabularies differ for the same terminal (e.g. "CBD Lorville" vs "CBD -
    /// Central Business District - Lorville"), so a name-based filter would silently drop or
    /// duplicate real terminals. Internal static (not instance-dependent) so it is unit-testable
    /// against a hand-built snapshot; null snapshot yields an empty list.</summary>
    internal static List<string> TerminalNames(MarketSnapshot? snap)
    {
        if (snap is null) return new List<string>();
        var priced = snap.TradePrices.Rows.Select(r => r.TerminalId).ToHashSet();
        return snap.Terminals.Rows.Where(t => priced.Contains(t.Id)).Select(t => t.Name)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The SELL flow's origin, for SellLookup's proximity-tier math (task 10 scope
    /// ripple, approved): live-only. The manual origin dropdown that used to back this when no
    /// session was live is gone (the ORIGIN chip is display-only now; the route planner's own
    /// Starting Location picker replaces it for the planner flow specifically). Empty, not a
    /// guess, whenever no live location is known - the same honesty rule TradeOriginResolver
    /// already applies to every other unresolved-origin case.</summary>
    internal IReadOnlySet<int> OriginTerminalIds(IReadOnlyList<MarketTerminal> terminals) =>
        App.Locations.LastKnownLocation is { } loc
            ? TradeOriginResolver.TerminalIdsForLocation(loc, terminals)
            : new HashSet<int>();

    private void RefreshContextRow()
    {
        bool live = App.Locations.LastKnownLocation is not null;
        var content = (StackPanel)_originChip.Child;
        content.Children.Clear();

        content.Children.Add(_originDot);
        content.Children.Add(new TextBlock
        {
            Text = "ORIGIN", FontFamily = Hud.Font("UiFont"), FontSize = 9, FontWeight = FontWeights.Bold,
            Foreground = Hud.Br("FgDimBrush"), Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center,
        });

        if (live)
        {
            _originValue.Text = $"{App.Locations.LastKnownLocation} - LIVE";
            _originValue.Foreground = Hud.Br("CyanBrush");   // recolored cyan, not amber - CyanColor's own
                                                                // stated role is "live data readouts" (mock:696-698)
            content.Children.Add(_originValue);
            _originDot.Fill = Hud.Br("CyanBrush");
            // Dressed ONCE per entry into live mode. A refresh that stays live must not re-allocate
            // the glow or restart a Forever loop from full opacity: the dot would visibly snap back
            // to bright on every tick that has nothing to do with it.
            if (!_originDotLive)
            {
                _originDotLive = true;
                _originDot.Effect = new DropShadowEffect { Color = Hud.Col("CyanBrush"), BlurRadius = 7, ShadowDepth = 0, Opacity = 0.8 };
                // Breathe 1900->3800ms (mock MS.breathe*2, index.html:700): PulseDot always uses
                // Motion.BreatheMs (1900) as-is, so this ORIGIN dot needs its own animation rather than
                // Hud.PulseDot to match the mock's slower cadence exactly - a deliberate deviation, noted.
                if (!Motion.Reduced)
                {
                    var anim = new DoubleAnimation(1.0, 0.3, new Duration(TimeSpan.FromMilliseconds(Motion.BreatheMs * 2)))
                    { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = Motion.Breathe };
                    _originDot.BeginAnimation(UIElement.OpacityProperty, anim);
                }
            }
            _originChip.ToolTip = "Auto-detected from your current session.";   // mock:703, verbatim
        }
        else
        {
            if (_originDotLive) { _originDot.BeginAnimation(UIElement.OpacityProperty, null); _originDotLive = false; }
            _originDot.Effect = null;
            _originDot.Fill = Hud.Br("FgDimBrush");
            // Display-only (task 10): the manual dropdown/click-to-change path is gone entirely -
            // this chip only ever reports what the live session is, never a place to pick one.
            _originValue.Text = "No session";
            _originValue.Foreground = Hud.Br("FgDimBrush");
            content.Children.Add(_originValue);
            _originChip.ToolTip = "No active session detected.";
        }

        var snapForAge = App.Market.Snapshot;
        TimeSpan? uexAge = snapForAge is null ? null : DateTime.UtcNow - snapForAge.TradePrices.FetchedUtc;
        _uexAgeValue.Text = uexAge is null ? "no data" : MarketNotice.FormatAge(uexAge.Value);
        // mock:1120 base sentence, extended with the LED rule (item 4: "extend each pill's existing
        // tooltip ... stating the rule in one sentence").
        _uexPill.ToolTip = $"UEX: community price feed, last updated {_uexAgeValue.Text}. " +
                           "LED: green under 24h, amber older, red no data.";
        SetFreshnessDot(_uexPillDot, uexAge);

        RefreshSctAgePill();
    }

    // SCT age pill: shown whenever SctDataEnabled is on (item 1's graduation, 2026-07-30) - this
    // used to ALSO require a snapshot to already exist, which hid the pill entirely during the gap
    // between turning the flag on and the first fetch landing. That gap is now a real state instead
    // (LED red, value "no data" - the same shape the UEX pill's own no-data state already uses), so
    // the pill's only absence rule left is the flag itself: no pill at all while SctDataEnabled is
    // off (mock manifest: "Nothing renders as a placeholder while off - it is absent, not gray",
    // index.html:1161-1163 - "off" here is the flag, not "no data yet"). The LED only exists inside
    // a rendered pill, so there is still zero SCT trace on this row while the flag is off.
    private void RefreshSctAgePill()
    {
        bool show = App.Settings.Current.SctDataEnabled;
        _sctAgePill.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;

        var fetchedUtc = App.Sct.SnapshotFetchedUtc;
        TimeSpan? age = fetchedUtc is null ? null : DateTime.UtcNow - fetchedUtc.Value;
        _sctAgeValue.Text = age is null ? "no data" : MarketNotice.FormatAge(age.Value);
        // mock:1127 base sentence, extended with the LED rule (item 4).
        _sctAgePill.ToolTip = $"SCT: SC Trade Tools, a secondary price source, last updated {_sctAgeValue.Text}. " +
                              "LED: green under 24h, amber older, red no data.";
        SetFreshnessDot(_sctPillDot, age);
    }

    // ── MAP tab hooks (Task 8, starmap MAP tab integration) ──────────────────────────────────
    // Session-only pin (not persisted - a route pin is a "what I'm looking at right now" marker,
    // not a saved preference; nothing in AppSettings' fixed contract has room for it either). The
    // WPF wiring here (PinRoute, the row's PIN chip, PrefillPlannerOriginFromMap's field writes,
    // ShowPricesForTerminal's tab switch) is not unit tested - constructing a real TradePage needs
    // a live App/window context, too heavy for a unit test - but the stale-pin DECISION it depends
    // on is: RoutePlanner.PinSurvivesRefresh (Services/RoutePlanner.cs), unit tested directly in
    // NexusApp.Tests/TradePinnedRouteTests.cs.
    private TradeRoute? _pinnedRoute;
    internal TradeRoute? PinnedRoute => _pinnedRoute;
    internal event Action? PinnedRouteChanged;

    /// <summary>Toggles the session pin: pinning the already-pinned route unpins it (same-route
    /// identity is RoutePlanner.PinSurvivesRefresh's own triple rule, reused here rather than a
    /// second copy of it), pinning null clears with no-op-if-already-clear, and pinning any other
    /// route replaces whatever was pinned before (one pin at a time). Always raises
    /// PinnedRouteChanged on an actual change, never on a no-op.</summary>
    internal void PinRoute(TradeRoute? r)
    {
        if (r is null) { ClearPin(); return; }
        if (_pinnedRoute is { } current && RoutePlanner.PinSurvivesRefresh(current, new[] { r }))
        {
            ClearPin();
            return;
        }
        _pinnedRoute = r;
        Logger.Info("[UI] trade: route pinned");
        PinnedRouteChanged?.Invoke();
    }

    /// <summary>The stale-pin path: RebuildPlanner calls this when the fresh ranking no longer
    /// contains the pinned route's (buy terminal, sell terminal, commodity) triple. No-op when
    /// nothing is pinned, so a rebuild that never had a pin never fires a spurious event.</summary>
    private void ClearPin()
    {
        if (_pinnedRoute is null) return;
        _pinnedRoute = null;
        Logger.Info("[UI] trade: route unpinned");
        PinnedRouteChanged?.Invoke();
    }

    /// <summary>Called when the user picks "set as planner start" on a MAP tab terminal pin:
    /// seeds the ROUTE section's STARTING LOCATION from that terminal's name, switches to the
    /// Planner flow, and reruns it against the new start. A terminal id that does not resolve (a
    /// stale pin from a snapshot that has since changed) is a silent no-op - never half-applies.
    /// Routed through SetStart so the persisted value, the log line, the combo's own selection
    /// refresh, and the rerun all stay on the one path the combo itself uses; picking a start the
    /// planner already has is SetStart's own no-op, so this only ever costs the tab switch.</summary>
    internal void PrefillPlannerOriginFromMap(int terminalId)
    {
        var terminals = App.Market.Snapshot?.Terminals.Rows ?? new List<MarketTerminal>();
        if (TradeOriginResolver.OriginNameForTerminal(terminalId, terminals) is not { } name) return;

        Logger.Info($"[UI] trade: start prefilled from map ({name})");
        SetStart(name);
        SwitchTab(0);
    }
}
