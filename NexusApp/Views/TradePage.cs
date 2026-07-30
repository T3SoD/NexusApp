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
    private readonly ScrollViewer[] _panes = new ScrollViewer[3];
    private readonly TranslateTransform _underlineT = new();
    private readonly SolidColorBrush _underlineBrush;
    private Grid _stripHost = null!;
    private Border _underline = null!;
    private DropShadowEffect _underlineGlow = null!;
    private int _activeIndex = -1;

    private static readonly string[] TabLabels = { "Planner", "Sell", "Prices" };   // mock:1046-1050

    // ── Flow content hosts (empty here; Tasks 12-14 populate them via Rebuild*) ──
    internal readonly StackPanel PlannerHost = new();
    internal readonly StackPanel SellHost = new();
    internal readonly StackPanel PricesHost = new();

    // ── Context row state (mock .ctxrow, index.html:1113-1131) ──
    private Border _originChip = null!;
    private Ellipse _originDot = null!;
    private TextBlock _originValue = null!;
    private ComboBox? _originCombo;
    private bool _originManualOverride;   // in-memory only: "manual override always available" (spec)
                                            // without a persisted mode flag - not in the fixed contract
    private readonly Border[] _scopePills = new Border[4];
    private static readonly string[] Scopes = { "ALL", "STANTON", "PYRO", "NYX" };   // mock:1116
    private Border _uexPill = null!;
    private TextBlock _uexAgeValue = null!;
    private Border _sctAgePill = null!;
    private TextBlock _sctAgeValue = null!;

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
        Grid.SetRow(_stripHost, 1);
        root.Children.Add(_stripHost);

        var contextRow = BuildContextRow();
        Grid.SetRow(contextRow, 2);
        root.Children.Add(contextRow);

        var paneHost = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        _panes[0] = WrapPane(PlannerHost);
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

        // Live data refresh triggers. Always rebuild on Market.Changed regardless of which tab is
        // active (cheap: three StackPanel rebuilds against in-memory data, same cost model as
        // MainWindow.OnMarketDataChanged rebuilding the whole Codex tree on every tick).
        App.Market.Changed += () => Dispatcher.BeginInvoke(Refresh);
        App.Locations.Changed += () => Dispatcher.BeginInvoke(RefreshContextRow);

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

    // ── Shared consent guard (Tasks 12-14 call this first in every Rebuild*) ─────────────────
    // Mirrors the tri-state check used at every other price surface (MainWindow.Codex.cs:1033,
    // MainWindow.WorkOrders.cs:274, OverlayWindow.xaml.cs:1732): only "== true" (explicit opt-in)
    // shows data; null (unanswered) and false (declined) both show the same empty state, and the
    // page-level MarketConsentHost strip (wired in this task's MainWindow.xaml.cs changes) is what
    // asks the question - this guard just keeps the page itself honest meanwhile.
    private const string ConsentEmptyMessage =
        "Turn on live market data (above, or in Settings) to see trade routes, sell prices, and the price browser.";

    private bool EnsureMarketConsent(Panel host)
    {
        if (App.Settings.Current.MarketDataEnabled == true) return true;
        host.Children.Clear();
        host.Children.Add(new TextBlock
        {
            Text = ConsentEmptyMessage, FontFamily = Hud.Font("UiFont"), FontSize = 12.5,
            Foreground = Hud.Br("FgDimBrush"), TextWrapping = TextWrapping.Wrap, MaxWidth = 520,
            Margin = new Thickness(0, 8, 0, 0),
        });
        return false;
    }

    // Task 12/13/14 replace these three bodies outright (interim stub, same pattern as the
    // env-autofollow plan's Task 4/5 "swap the interim expression").
    private void RebuildSell() { }
    private void RebuildPrices() { }

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
        row.Children.Add(Sep());

        _uexPill = BuildPill("UEX", out _uexAgeValue);
        _uexPill.ToolTip = "";   // set live in RefreshContextRow (age changes the tooltip text)
        row.Children.Add(_uexPill);

        _sctAgePill = BuildPill("SCT", out _sctAgeValue);
        _sctAgePill.Margin = new Thickness(8, 0, 0, 0);
        _sctAgePill.Visibility = Visibility.Collapsed;   // Task 11 also owns this pill's absence rule
        row.Children.Add(_sctAgePill);

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
    private static Border BuildPill(string label, out TextBlock valueText)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
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

    // ORIGIN chip (mock index.html:691-716). Live state: cyan pulse dot + "{loc} - LIVE" mono cyan.
    // Manual state: a themed NexusComboBox of terminal names (a deliberate simplification of the
    // mock's hand-rolled popup trigger - the mock's own manifest already flags that popup as
    // "composed, no shipped precedent"; a plain NexusComboBox reuses real shipped chrome instead of
    // inventing a second one, "simplest solution first"). Which state shows is driven by whether a
    // live location is currently known AND not manually overridden - "manual override always
    // available" per the spec is the small ghost link built in RefreshContextRow.
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

    /// <summary>True origin display name for RoutePlanner/SellLookup's FROM HERE anchor: the live
    /// location when known and not overridden, else the persisted manual terminal name.</summary>
    internal string OriginLabel =>
        (!_originManualOverride && App.Locations.LastKnownLocation is { } loc) ? loc : App.Settings.Current.TradeOriginManual;

    internal IReadOnlySet<int> OriginTerminalIds(IReadOnlyList<MarketTerminal> terminals) =>
        (!_originManualOverride && App.Locations.LastKnownLocation is { } loc)
            ? TradeOriginResolver.TerminalIdsForLocation(loc, terminals)
            : TradeOriginResolver.TerminalIdForName(App.Settings.Current.TradeOriginManual, terminals) is { } id
                ? new HashSet<int> { id } : new HashSet<int>();

    private void RefreshContextRow()
    {
        bool live = !_originManualOverride && App.Locations.LastKnownLocation is not null;
        var content = (StackPanel)_originChip.Child;
        content.Children.Clear();

        if (live)
        {
            content.Children.Add(_originDot);
            content.Children.Add(new TextBlock
            {
                Text = "ORIGIN", FontFamily = Hud.Font("UiFont"), FontSize = 9, FontWeight = FontWeights.Bold,
                Foreground = Hud.Br("FgDimBrush"), Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center,
            });
            _originValue.Text = $"{App.Locations.LastKnownLocation} - LIVE";
            _originValue.Foreground = Hud.Br("CyanBrush");   // recolored cyan, not amber - CyanColor's own
                                                                // stated role is "live data readouts" (mock:696-698)
            content.Children.Add(_originValue);
            _originDot.Fill = Hud.Br("CyanBrush");
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
            _originChip.ToolTip = "Auto-detected from your current session.";   // mock:703, verbatim

            var manualLink = new TextBlock
            {
                Text = "Manual", FontFamily = Hud.Font("UiFont"), FontSize = 9, FontWeight = FontWeights.Bold,
                Foreground = Hud.Br("FgDimBrush"), Margin = new Thickness(8, 0, 0, 0), Cursor = Cursors.Hand,
                TextDecorations = TextDecorations.Underline, VerticalAlignment = VerticalAlignment.Center,
            };
            manualLink.MouseLeftButtonUp += (_, _) => { _originManualOverride = true; RefreshContextRow(); RebuildPlanner(); RebuildSell(); };
            content.Children.Add(manualLink);
        }
        else
        {
            _originDot.BeginAnimation(UIElement.OpacityProperty, null);
            content.Children.Add(new TextBlock
            {
                Text = "ORIGIN", FontFamily = Hud.Font("UiFont"), FontSize = 9, FontWeight = FontWeights.Bold,
                Foreground = Hud.Br("FgDimBrush"), Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center,
            });
            _originChip.ToolTip = null;
            var snap = App.Market.Snapshot;
            var names = snap?.Terminals.Rows.Select(t => t.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()
                        ?? new System.Collections.Generic.List<string>();
            _originCombo = new ComboBox
            {
                Style = (Style)Application.Current.FindResource("NexusComboBox"),
                ItemsSource = names, MinWidth = 140,
                SelectedItem = names.Contains(App.Settings.Current.TradeOriginManual) ? App.Settings.Current.TradeOriginManual : names.FirstOrDefault(),
            };
            _originCombo.SelectionChanged += (_, _) =>
            {
                if (_originCombo.SelectedItem is not string name) return;
                App.Settings.Current.TradeOriginManual = name;
                App.Settings.Save();
                Logger.Info($"[UI] Trade origin (manual): {name}");
                RebuildPlanner();
                RebuildSell();
            };
            content.Children.Add(_originCombo);

            if (App.Locations.LastKnownLocation is not null)
            {
                var liveLink = new TextBlock
                {
                    Text = "Live", FontFamily = Hud.Font("UiFont"), FontSize = 9, FontWeight = FontWeights.Bold,
                    Foreground = Hud.Br("FgDimBrush"), Margin = new Thickness(8, 0, 0, 0), Cursor = Cursors.Hand,
                    TextDecorations = TextDecorations.Underline, VerticalAlignment = VerticalAlignment.Center,
                };
                liveLink.MouseLeftButtonUp += (_, _) => { _originManualOverride = false; RefreshContextRow(); RebuildPlanner(); RebuildSell(); };
                content.Children.Add(liveLink);
            }
        }

        var snapForAge = App.Market.Snapshot;
        _uexAgeValue.Text = snapForAge is null ? "no data" : MarketNotice.FormatAge(DateTime.UtcNow - snapForAge.TradePrices.FetchedUtc);
        _uexPill.ToolTip = $"UEX: community price feed, last updated {_uexAgeValue.Text}.";   // mock:1120

        RefreshSctAgePill();
    }

    // SCT age pill: ONLY when SctDataEnabled (owner-only dark flag) AND an SCT snapshot exists -
    // absent otherwise, never a placeholder (mock manifest: "Nothing renders as a placeholder while
    // off - it is absent, not gray", index.html:1161-1163). ASSUMED SctMarketService surface - see
    // this section's gap #1 note; App.Sct/.SnapshotFetchedUtc/.Changed are not in the fixed contract.
    private void RefreshSctAgePill()
    {
        bool show = App.Settings.Current.SctDataEnabled && App.Sct.SnapshotFetchedUtc is DateTime;
        _sctAgePill.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        var age = DateTime.UtcNow - App.Sct.SnapshotFetchedUtc!.Value;
        _sctAgeValue.Text = MarketNotice.FormatAge(age);
        _sctAgePill.ToolTip = $"SCT: SC Trade Tools, a secondary price source, last updated {_sctAgeValue.Text}.";   // mock:1127
    }
}
