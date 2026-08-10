using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using NexusApp.Models;
using NexusApp.Services;

namespace NexusApp.Views;

// Session profit surfaces (issue #39, spec docs/superpowers/specs/2026-08-05-session-profit-
// tracker.md sections 5/6/9; mock nexus-design-lab/profit-tracker). The SESSION PROFIT chip and
// the expanding panel under it: the 40px net, the derivation line, the ledger rows (voided rows
// stay, dim, with their refusal code), the PROFIT HISTORY strip folded by local calendar day
// (section 9 ruling 4), and the footer caveat. All arithmetic and copy live in ProfitDisplay;
// this control only paints.
//
// Moved off Trade onto Cargo Hauling (task B4, spec 2026-08-09-trade-cargo-fusion-design.md
// section 2.3/3: "Trade is a catalogue you browse, Cargo Hauling is the work you took on, and
// money belongs with the work"). Was TradePage.Profit.cs, a partial of TradePage; is now this
// self-contained control, hosted by HaulingPage. The code moved as-is - no string, colour, size
// or animation changed in the move, only the wiring that gets it on screen.
public sealed class MoneyPanel : UserControl
{
    private Border _profitChip = null!;
    private TextBlock _profitChipValue = null!;
    private RotateTransform _profitChevT = null!;
    private Border _profitPanel = null!;
    private StackPanel _profitBody = null!;
    private bool _profitExpanded;
    private long _profitShownNet;          // the panel's last rendered net: the count-up's from-value
    private bool _profitTrendDrawPending;  // trend draw-on rides the expand, never a data tick

    public MoneyPanel()
    {
        var stack = new StackPanel();
        var chip = BuildProfitChip();
        // The chip lived in TradePage's horizontal context row, where a StackPanel never
        // constrains a child's width regardless of alignment. This control's own root is
        // vertical, so without an explicit Left alignment the chip's Border (default Stretch)
        // would fill the whole row width instead of sizing to its content - a visual change the
        // move must not make.
        chip.HorizontalAlignment = HorizontalAlignment.Left;
        stack.Children.Add(chip);
        stack.Children.Add(BuildProfitPanel());
        Content = stack;

        // Live data refresh triggers, each gated on this control actually being on screen - the
        // same discipline TradePage used while it owned these surfaces: a settlement or a capture
        // that lands while the user is elsewhere is caught by Refresh() on re-entry instead of
        // repainting for nobody. HaulingPage collapses its whole page host on navigation away
        // (MainWindow's SetActivePage), which clears IsVisible here exactly as it did on TradePage.
        App.Profit.Changed += () => Dispatcher.BeginInvoke(() => { if (IsVisible) RefreshProfitSurfaces(); });
        // Wallet ticks (OCR wallet, 2026-08-06): a confirmed capture, an untracked row or a manual
        // set repaints the WALLET block atop the same panel; same visibility gate as the profit tick.
        App.Wallet.Changed += () => Dispatcher.BeginInvoke(() => { if (IsVisible) RefreshProfitSurfaces(); });
        // The profit chip's OFFLINE dimming keys on the game-process probe (StateChanged still
        // fires on process flips even while the log monitor is stopped, per TradePage's own ORIGIN
        // chip ruling, 2026-08-04, on the same feed), so it repaints on the same flips.
        App.GameLog.StateChanged += () => Dispatcher.BeginInvoke(() => { if (IsVisible) RefreshProfitSurfaces(); });
    }

    /// <summary>Called by HaulingPage.Refresh() on every visit, so a settlement, a capture or a
    /// process flip that happened while the user was elsewhere is caught immediately - the same
    /// contract TradePage.Refresh() gave these surfaces before the move.</summary>
    public void Refresh() => RefreshProfitSurfaces();

    // ── Chip (mock .plChip): pill chrome, deliberately NO lamp (S5, F14 one-lamp rule). State
    // rides the value color: green positive, red negative, dim zero-or-empty; OFFLINE dims the
    // whole chip to 0.55 (S6). ──
    private Border BuildProfitChip()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new TextBlock
        {
            Text = ProfitDisplay.ChipLabel, FontFamily = Hud.Font("UiFont"), FontSize = 9,
            FontWeight = FontWeights.Bold, Foreground = Hud.Br("FgDimBrush"),
            Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center,
        });
        _profitChipValue = new TextBlock
        {
            FontFamily = Hud.Font("MonoFont"), FontSize = 11, FontWeight = FontWeights.Medium,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(_profitChipValue);
        _profitChevT = new RotateTransform();
        row.Children.Add(new Path
        {
            Width = 9, Height = 9, Stretch = Stretch.Uniform,          // mock .chev 9x9
            Data = Geometry.Parse("M6,3 L11,8 L6,13"),
            Stroke = Hud.Br("FgDimBrush"), StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            RenderTransform = _profitChevT, RenderTransformOrigin = new Point(0.5, 0.5),
            Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        });

        _profitChip = new Border
        {
            Background = Hud.Br("Bg2NavBrush"), BorderBrush = Hud.Br("NavBorderBrush"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
            Padding = new Thickness(11, 3, 9, 3),          // mock .plChip padding 3 9 3 11
            Cursor = Cursors.Hand, Child = row, VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Open the session ledger",           // mock, verbatim
        };
        _profitChip.MouseEnter += (_, _) => { _profitChip.Background = Hud.Br("Bg3Brush"); _profitChip.BorderBrush = Hud.Br("AccentStrongBrush"); };
        _profitChip.MouseLeave += (_, _) => { _profitChip.Background = Hud.Br("Bg2NavBrush"); _profitChip.BorderBrush = Hud.Br("NavBorderBrush"); };
        _profitChip.MouseLeftButtonUp += (_, _) => ToggleProfitPanel();
        RefreshProfitChip();
        return _profitChip;
    }

    // ── Panel shell (mock .plPanel: panel fill, 1px line, padding 18 20 16). The mock fuses it
    // under a bordered context row; this control has no context row of its own, so the panel
    // stands 8px below the chip as its own card instead. Collapsed by default, session-only state. ──
    private FrameworkElement BuildProfitPanel()
    {
        _profitBody = new StackPanel();
        _profitPanel = new Border
        {
            Background = Hud.Br("Bg2NavBrush"), BorderBrush = Hud.Br("NavBorderBrush"),
            BorderThickness = new Thickness(1), Padding = new Thickness(20, 18, 20, 16),
            Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed,
            Child = _profitBody,
        };
        return _profitPanel;
    }

    private void ToggleProfitPanel()
    {
        _profitExpanded = !_profitExpanded;
        InteractionLog.Click("Session profit", _profitChip);
        Logger.Info($"[UI] trade: profit panel {(_profitExpanded ? "expanded" : "collapsed")}");

        // Chevron rotates 0 -> 90 on ChipFadeMs (mock), snapping under Reduced.
        if (Motion.Reduced)
        {
            _profitChevT.BeginAnimation(RotateTransform.AngleProperty, null);
            _profitChevT.Angle = _profitExpanded ? 90 : 0;
        }
        else
        {
            _profitChevT.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(_profitExpanded ? 90 : 0, TimeSpan.FromMilliseconds(Motion.ChipFadeMs)) { EasingFunction = Motion.Settle });
        }

        if (!_profitExpanded) { _profitPanel.Visibility = Visibility.Collapsed; return; }

        _profitTrendDrawPending = true;
        RebuildProfitPanel(entrance: true);
        _profitPanel.Visibility = Visibility.Visible;
        // The frozen expand idiom (quick-add unfold): QuickRevealMs fade + 12px rise on Settle.
        // The mock animates height; WPF has no cheap height-to-auto, and this is the house expand.
        if (Motion.Reduced)
        {
            _profitPanel.BeginAnimation(OpacityProperty, null);
            _profitPanel.Opacity = 1;
            _profitPanel.RenderTransform = null;
            return;
        }
        var shift = new TranslateTransform(0, 12);
        _profitPanel.RenderTransform = shift;
        _profitPanel.Opacity = 0;
        var dur = TimeSpan.FromMilliseconds(Motion.QuickRevealMs);
        _profitPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = Motion.Settle });
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0, dur) { EasingFunction = Motion.Settle });
    }

    // Called on App.Profit.Changed, App.Wallet.Changed, a game-process flip and every
    // HaulingPage.Refresh() (routed through this control's own Refresh()).
    private void RefreshProfitSurfaces()
    {
        RefreshProfitChip();
        if (_profitExpanded) RebuildProfitPanel(entrance: false);
    }

    private void RefreshProfitChip()
    {
        var ledger = App.Profit.Ledger;
        var state = ProfitDisplay.State(ledger.UnvoidedCount, ledger.Net, App.GameLogFeed.IsSessionLive);
        // Units ruling (2026-08-05, chip exemption overridden same day): the value carries
        // the dim aUEC suffix like every other money surface, never after the empty dashes.
        _profitChipValue.Inlines.Clear();
        _profitChipValue.Inlines.Add(new Run(ProfitDisplay.ChipValue(ledger.UnvoidedCount, ledger.Net)));
        if (ledger.UnvoidedCount > 0)
            _profitChipValue.Inlines.Add(new Run(" aUEC")
            {
                FontFamily = Hud.Font("UiFont"), FontSize = 9, Foreground = Hud.Br("FgDimBrush"),
            });
        _profitChipValue.Foreground = ProfitBrush(state);
        _profitChip.Opacity = state == ProfitState.Offline ? 0.55 : 1.0;   // mock .plChip.offline
    }

    // Money never wears cyan: positive = OkBrush, negative = DangerBrush, everything else dim.
    private static Brush ProfitBrush(ProfitState state) => state switch
    {
        ProfitState.Positive => Hud.Br("OkBrush"),
        ProfitState.Negative => Hud.Br("DangerBrush"),
        _ => Hud.Br("FgDimBrush"),
    };

    // ── Panel content. Rebuilt whole on every applied/voided settlement while expanded; only the
    // net animates (from its PREVIOUS value, never restarting at zero - CountUp.cs restarts, so
    // the local ProfitCount mirror below is seeded from the prior value instead). ──
    private void RebuildProfitPanel(bool entrance)
    {
        var ledger = App.Profit.Ledger;
        var txs = ledger.Transactions;
        bool live = App.GameLogFeed.IsSessionLive;
        int sells = 0, buys = 0, voided = 0;
        foreach (var t in txs)
        {
            if (t.Voided is not null) { voided++; continue; }
            if (t.Kind == TransactionKind.Sell) sells++; else buys++;
        }
        int settled = ledger.UnvoidedCount;
        long net = ledger.Net;

        _profitBody.Children.Clear();

        // WALLET block first (OCR wallet ruling 2026-08-06: top of this panel), then the rule,
        // then the SESSION PROFIT surfaces unchanged below it.
        BuildWalletBlock();

        // Eyebrow (Hud.Header idiom: amber dash + amber 10.5 bold).
        var eye = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 9) };
        eye.Children.Add(new Border
        {
            Width = 16, Height = 2, Background = Hud.Br("AccentBrush"), Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Effect = new DropShadowEffect { Color = Hud.Col("AccentColor"), BlurRadius = 7, ShadowDepth = 0, Opacity = 0.8 },
        });
        eye.Children.Add(new TextBlock
        {
            Text = ProfitDisplay.ChipLabel, FontFamily = Hud.Font("UiFont"), FontSize = 10.5,
            FontWeight = FontWeights.Bold, Foreground = Hud.Br("AccentBrush"),
        });
        _profitBody.Children.Add(eye);

        // The net: 40px mono, sign always rendered, tone from the ledger alone - OFFLINE keeps
        // the last session's color and says so in the derivation line instead (mock).
        var big = new TextBlock
        {
            FontFamily = Hud.Font("MonoFont"), FontSize = 40, FontWeight = FontWeights.Medium,
            Foreground = ProfitBrush(ProfitDisplay.State(settled, net, sessionLive: true)),
        };
        if (settled == 0)
        {
            big.Text = ProfitDisplay.NoneValue;
            _profitShownNet = 0;
        }
        else if (entrance || Motion.Reduced || _profitShownNet == net)
        {
            big.BeginAnimation(ProfitCountProperty, null);
            big.Text = ProfitDisplay.Signed(net);
            _profitShownNet = net;
        }
        else
        {
            AnimateProfitNet(big, _profitShownNet, net);
            _profitShownNet = net;
        }
        var bigRow = new StackPanel { Orientation = Orientation.Horizontal };
        bigRow.Children.Add(big);
        bigRow.Children.Add(new TextBlock
        {
            Text = "aUEC", FontFamily = Hud.Font("UiFont"), FontSize = 13, FontWeight = FontWeights.SemiBold,
            Foreground = Hud.Br("FgDimBrush"), Margin = new Thickness(9, 0, 0, 4),
            VerticalAlignment = VerticalAlignment.Bottom,
        });
        _profitBody.Children.Add(bigRow);

        // Derivation line; the offline prefix leads bold so the state is words, not only opacity.
        var derivText = ProfitDisplay.DerivationLine(sells, ledger.Sold, buys, ledger.Bought, voided, live);
        var deriv = new TextBlock
        {
            FontFamily = Hud.Font("UiFont"), FontSize = 11.5, Foreground = Hud.Br("FgDimBrush"),
            TextWrapping = TextWrapping.Wrap, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 9, 0, 0),
        };
        // Both offline shapes (last-session prefix and the empty offline waiting copy) lead with
        // the same bolded words, so the state is words, not only opacity.
        if (derivText.StartsWith("Game offline.", StringComparison.Ordinal))
        {
            deriv.Inlines.Add(new Run("Game offline.") { FontWeight = FontWeights.SemiBold, Foreground = Hud.Br("FgBrush") });
            deriv.Inlines.Add(new Run(derivText["Game offline.".Length..]));
        }
        else deriv.Text = derivText;
        _profitBody.Children.Add(deriv);

        BuildProfitLedgerSection(txs, App.Wallet.SessionUntracked, voided, entrance, live);
        BuildProfitHistoryStrip();
        BuildProfitFooter(ProfitDisplay.State(settled, net, sessionLive: true) == ProfitState.Negative);
    }

    // ── CascadeIn: hand-duplicated per page/control by house convention (confirmed: NOT a shared
    // Hud helper; CommandPage.cs and MainWindow.Codex.cs each keep their own copy on purpose, and
    // TradePage.cs kept one for its own partials). MoneyPanel keeps its own copy here since the
    // move off TradePage (spec 2026-08-09) took this one call site with it. ──
    private static void CascadeIn(FrameworkElement fe, int index)
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

    // ── Ledger (mock .ledgerHead/.row): newest first, voided rows stay at 0.45 opacity with the
    // verbatim refusal code (section 9 ruling 2). The rows live in their own scroll region
    // (review fix, 2026-08-05: the panel used to grow one row per transaction with nothing above
    // it scrolling, so a long session clipped the history strip and footer off the page), capped
    // at the latest LedgerRowCap rows with a dim note naming the fold. ──
    private void BuildProfitLedgerSection(IReadOnlyList<CommodityTransaction> txs,
        IReadOnlyList<UntrackedEntry> untracked, int voided, bool entrance, bool live)
    {
        var purchases = App.Profit.Purchases.Purchases;

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(ProfitSectionTitle("This session"));
        var count = ProfitSectionTitle(
            $"{txs.Count} transaction{(txs.Count == 1 ? "" : "s")}{(voided > 0 ? $", {voided} voided" : "")}"
            + (purchases.Count > 0 ? $", {purchases.Count} purchase{(purchases.Count == 1 ? "" : "s")}" : "")
            + (untracked.Count > 0 ? $", {untracked.Count} untracked" : ""));
        Grid.SetColumn(count, 1);
        head.Children.Add(count);
        _profitBody.Children.Add(new Border
        {
            BorderBrush = Hud.Br("NavBorderBrush"), BorderThickness = new Thickness(0, 1, 0, 0),
            Margin = new Thickness(0, 18, 0, 8), Padding = new Thickness(0, 14, 0, 0), Child = head,
        });

        if (ProfitDisplay.LedgerIsEmpty(txs.Count, untracked.Count, purchases.Count))
        {
            _profitBody.Children.Add(new TextBlock
            {
                Text = live ? ProfitDisplay.EmptyLedger : ProfitDisplay.EmptyLedgerOffline,
                FontFamily = Hud.Font("UiFont"), FontSize = 12,
                Foreground = Hud.Br("FgDimBrush"), TextWrapping = TextWrapping.Wrap, MaxWidth = 520,
                HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 16, 10, 16),
            });
            return;
        }

        var rows = new StackPanel();
        int i = 0;
        // Untracked wallet rows interleave by stamp (spec 11.4): one money timeline, one cap.
        foreach (var item in WalletDisplay.MergeRows(txs, untracked, purchases, ProfitDisplay.LedgerRowCap))
        {
            var row = item switch
            {
                CommodityTransaction tx => ProfitRow(tx),
                ShopPurchase p => PurchaseRow(p),
                _ => UntrackedRow((UntrackedEntry)item),
            };
            // Cascade rides the expand only, the CommandPage entrance convention - a data tick
            // repaints statically under the reader.
            if (entrance) CascadeIn(row, Math.Min(i, 6));
            rows.Children.Add(row);
            i++;
        }
        // The fold count must match MergeRows' own total, purchases included, or the note
        // undercounts what the cap silently dropped (review fix, 2026-08-07).
        int hidden = ProfitDisplay.LedgerHiddenCount(txs.Count + untracked.Count + purchases.Count);
        if (hidden > 0)
        {
            // The fold, named at the bottom of the scroll region where the older rows would sit.
            rows.Children.Add(new TextBlock
            {
                Text = ProfitDisplay.LedgerEarlierNote(hidden), FontFamily = Hud.Font("UiFont"),
                FontSize = 10.5, Foreground = Hud.Br("FgDimBrush"), Padding = new Thickness(10, 6, 10, 4),
            });
        }
        // The panel's only scroll region, and the page never scrolls around it, so the wheel is
        // unambiguous here (the same reason the planner scopes its scroll to results only).
        _profitBody.Children.Add(new ScrollViewer
        {
            Content = rows, MaxHeight = 300,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        });
    }

    private static TextBlock ProfitSectionTitle(string text) => new()
    {
        Text = text.ToUpperInvariant(), FontFamily = Hud.Font("UiFont"), FontSize = 9,
        FontWeight = FontWeights.Bold, Foreground = Hud.Br("FgDimBrush"),
    };

    // One ledger row: time, direction arrow, what moved at which shop, signed amount
    // (mock .row grid 52 / 20 / 1fr / auto, gap 12, padding 9 10).
    private static Border ProfitRow(CommodityTransaction tx)
    {
        bool sell = tx.Kind == TransactionKind.Sell;
        bool isVoided = tx.Voided is not null;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(new TextBlock
        {
            Text = tx.TimestampUtc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture),
            FontFamily = Hud.Font("MonoFont"), FontSize = 10.5, Foreground = Hud.Br("FgDimBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        });

        // 16x16 arrow, 1.6 round stroke: up green for sells, down red for buys, dim when voided.
        var arrow = new Path
        {
            Width = 16, Height = 16,
            Data = Geometry.Parse(sell ? "M8,13 L8,3 M8,3 L4,7 M8,3 L12,7" : "M8,3 L8,13 M8,13 L4,9 M8,13 L12,9"),
            Stroke = isVoided ? Hud.Br("FgDimBrush") : sell ? Hud.Br("OkBrush") : Hud.Br("DangerBrush"),
            StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
        };
        Grid.SetColumn(arrow, 1);
        grid.Children.Add(arrow);

        var mainLine = new StackPanel { Orientation = Orientation.Horizontal };
        mainLine.Children.Add(new TextBlock
        {
            // D10a: the commodity's display name from the embedded GUID table, when known.
            Text = ProfitDisplay.RowTitle(sell, tx.Scu, CommodityNameCatalog.Instance.Resolve(tx.ResourceGuid)),
            FontFamily = Hud.Font("UiFont"), FontSize = 12.5, Foreground = Hud.Br("FgBrush"),
        });
        if (isVoided)
        {
            // The refusal code, CIG's spelling verbatim (mock .voidCode).
            mainLine.Children.Add(new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0x6B, 0x6B)),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = tx.Voided, FontFamily = Hud.Font("MonoFont"), FontSize = 9,
                    Foreground = Hud.Br("DangerBrush"),
                },
            });
        }
        var main = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        main.Children.Add(mainLine);
        main.Children.Add(new TextBlock
        {
            // The stamped player location when known; the shop token is a kiosk TEMPLATE shared
            // across stations (recon 2026-08-02, confirmed live 2026-08-05) and never shows as
            // a place. Raw token and resource id stay inspectable in the tooltip (S3).
            Text = ProfitDisplay.WhereText(tx.PlaceLabel, tx.PlaceIsArea, tx.ShopName),
            FontFamily = Hud.Font("UiFont"), FontSize = 10.5,
            Foreground = Hud.Br("FgDimBrush"), Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = $"shopName[{tx.ShopName}]  resourceGUID[{tx.ResourceGuid}]"
                    + (tx.PlaceLabel is null ? "" : $"  location at settlement: {tx.PlaceLabel}")
                    + (isVoided ? $"  result[{tx.Voided}]" : ""),
        });
        Grid.SetColumn(main, 2);
        grid.Children.Add(main);

        var amount = new TextBlock
        {
            FontFamily = Hud.Font("MonoFont"), FontSize = 14, TextAlignment = TextAlignment.Right,
            Foreground = isVoided ? Hud.Br("FgDimBrush") : sell ? Hud.Br("OkBrush") : Hud.Br("DangerBrush"),
            Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        amount.Inlines.Add(new Run(ProfitDisplay.Signed(sell ? tx.Amount : -tx.Amount)));
        amount.Inlines.Add(new Run(" aUEC")
        {
            FontFamily = Hud.Font("UiFont"), FontSize = 10, Foreground = Hud.Br("FgDimBrush"),
        });
        Grid.SetColumn(amount, 3);
        grid.Children.Add(amount);

        var row = new Border
        {
            Padding = new Thickness(10, 9, 10, 9), CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1), BorderBrush = Brushes.Transparent,
            Background = Brushes.Transparent, Child = grid,
        };
        if (isVoided) row.Opacity = 0.45;   // mock .row.voidRow, section 9 ruling 2
        row.MouseEnter += (_, _) => { row.Background = Hud.Br("Bg3Brush"); row.BorderBrush = Hud.Br("NavBorderBrush"); };
        row.MouseLeave += (_, _) => { row.Background = Brushes.Transparent; row.BorderBrush = Brushes.Transparent; };
        return row;
    }

    private bool _walletEditorOpen;   // survives panel rebuilds; the TextBox itself does not

    // The conversion bar, desktop variant. A per-surface copy rather than a shared helper, the
    // house convention for chrome builders (CascadeIn is hand-duplicated per page/control on
    // purpose): this one renders every segment and uses the shared Hud accessors, while the
    // overlay's copy is capped at two segments by its 320px width.
    private Border BuildConversionBar(IReadOnlyList<ConversionSegment> segs, double height)
    {
        var grid = new Grid { Height = height };
        for (int i = 0; i < segs.Count; i++)
        {
            var s = segs[i];
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(Math.Max(1, s.Weight), GridUnitType.Star),
                MinWidth = 112,
            });

            var (fill, keyBrush, valBrush) = s.Kind switch
            {
                ConversionKind.Liquid => (ConversionLiquidFill, Hud.Br("CyanBrush"), Hud.Br("FgBrush")),
                ConversionKind.Cargo => (Hud.Br("AccentFaintBrush"), Hud.Br("AccentBrush"), Hud.Br("FgBrush")),
                _ => (ConversionGainFill, Hud.Br("OkBrush"), Hud.Br("OkBrush")),
            };

            var inner = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0) };
            inner.Children.Add(new TextBlock
            {
                Text = s.Label, FontFamily = Hud.Font("UiFont"), FontSize = 9,
                FontWeight = FontWeights.Bold, Foreground = keyBrush,
            });
            var value = new TextBlock
            {
                FontFamily = Hud.Font("MonoFont"), FontSize = 15, Foreground = valBrush,
                Margin = new Thickness(0, 2, 0, 0),
            };
            value.Inlines.Add(new Run(s.Value));
            value.Inlines.Add(new Run(" aUEC")
            {
                FontFamily = Hud.Font("UiFont"), FontSize = 9.5, Foreground = Hud.Br("FgDimBrush"),
            });
            inner.Children.Add(value);

            var cell = new Border { Background = fill, Child = inner, ToolTip = ConversionDisplay.BarTooltip };
            Grid.SetColumn(cell, i);
            grid.Children.Add(cell);
        }
        return new Border { CornerRadius = new CornerRadius(4), ClipToBounds = true, Child = grid };
    }

    private static readonly Brush ConversionLiquidFill = new SolidColorBrush(Color.FromArgb(0x1F, 0x7F, 0xE9, 0xE0));
    private static readonly Brush ConversionGainFill = new SolidColorBrush(Color.FromArgb(0x24, 0x66, 0xE6, 0xA6));

    // ── WALLET block (OCR wallet spec sections 5/6/11; ruling 2026-08-06: top of this panel).
    // Estimate, provenance, state chip, inline SET BALANCE editor. All words and arithmetic come
    // from WalletDisplay and WalletTracker; this paints. Colors follow the superseded mock's
    // .wpBig/.stateChip values, which cite the app's own palette tokens. ──
    private void BuildWalletBlock()
    {
        var wallet = App.Wallet;
        var state = WalletDisplay.State(wallet.HasAnchor, wallet.Estimate, wallet.AnchorUtc,
                                        DateTime.UtcNow, App.GameLogFeed.IsSessionLive);

        // Conversion bar first (spec 2026-08-09 section 4): where the money IS, before the wallet
        // detail explains one third of it. The desktop has room for every segment, unlike the 320px
        // overlay. EXPECTED (task D4) is absent whenever no route is Loaded yet - never a guess.
        var inCargo = CargoValue.TotalCost(App.Profit.Ledger.Transactions);
        // EXPECTED (task D4): the sum of what every LOADED accepted route should return once its
        // cargo sells. Shared with the overlay's own money block through AcceptedRouteMoney so the
        // two surfaces can never derive this figure differently.
        var segs = ConversionDisplay.Segments(wallet.Estimate, inCargo,
            expected: AcceptedRouteMoney.ExpectedMargin(App.Settings.Current.PinnedRoutes));
        if (segs.Count > 0)
        {
            _profitBody.Children.Add(BuildConversionBar(segs, 34));
            if (inCargo > 0)
                _profitBody.Children.Add(new TextBlock
                {
                    Text = ConversionDisplay.CargoNote(inCargo), FontFamily = Hud.Font("UiFont"),
                    FontSize = 10.5, Foreground = Hud.Br("FgDimBrush"), Margin = new Thickness(0, 6, 0, 14),
                });
        }

        var head = new Grid { Margin = new Thickness(0, 0, 0, 9) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var eye = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        eye.Children.Add(new Border
        {
            Width = 16, Height = 2, Background = Hud.Br("AccentBrush"), Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Effect = new DropShadowEffect { Color = Hud.Col("AccentColor"), BlurRadius = 7, ShadowDepth = 0, Opacity = 0.8 },
        });
        eye.Children.Add(new TextBlock
        {
            Text = WalletDisplay.BlockLabel, FontFamily = Hud.Font("UiFont"), FontSize = 10.5,
            FontWeight = FontWeights.Bold, Foreground = Hud.Br("AccentBrush"),
        });
        head.Children.Add(eye);

        var stateChip = new Border
        {
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 4, 10, 4), VerticalAlignment = VerticalAlignment.Center,
            BorderBrush = WalletStateBorder(state),
            Child = new TextBlock
            {
                Text = WalletDisplay.StateChipWord(state), FontFamily = Hud.Font("UiFont"),
                FontSize = 9.5, FontWeight = FontWeights.Bold, Foreground = WalletStateBrush(state),
            },
        };
        Grid.SetColumn(stateChip, 1);
        head.Children.Add(stateChip);
        _profitBody.Children.Add(head);

        // The estimate: 28px mono, deliberately under the 40px SESSION PROFIT hero below - the
        // panel keeps one hero number. N0 renders the minus sign; IMPOSSIBLE stays red, unclamped.
        var bigRow = new StackPanel { Orientation = Orientation.Horizontal };
        bigRow.Children.Add(new TextBlock
        {
            FontFamily = Hud.Font("MonoFont"), FontSize = 28, FontWeight = FontWeights.Medium,
            Foreground = WalletValueBrush(state),
            Text = wallet.Estimate is { } est ? ProfitDisplay.Format(est) : ProfitDisplay.NoneValue,
        });
        if (wallet.HasAnchor)
        {
            bigRow.Children.Add(new TextBlock
            {
                Text = "aUEC", FontFamily = Hud.Font("UiFont"), FontSize = 11, FontWeight = FontWeights.SemiBold,
                Foreground = Hud.Br("FgDimBrush"), Margin = new Thickness(8, 0, 0, 3),
                VerticalAlignment = VerticalAlignment.Bottom,
            });
        }
        _profitBody.Children.Add(bigRow);

        // Provenance and the editor affordance on one line; the editor itself unfolds below.
        var provRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 0) };
        provRow.Children.Add(new TextBlock
        {
            Text = WalletDisplay.Provenance(wallet.AnchorSource, wallet.AnchorUtc, DateTime.UtcNow),
            FontFamily = Hud.Font("UiFont"), FontSize = 11.5, Foreground = Hud.Br("FgDimBrush"),
            TextWrapping = TextWrapping.Wrap, MaxWidth = 420, VerticalAlignment = VerticalAlignment.Center,
        });
        var editLink = new TextBlock
        {
            Text = wallet.HasAnchor ? "RE-ANCHOR" : "SET BALANCE",
            FontFamily = Hud.Font("UiFont"), FontSize = 10, FontWeight = FontWeights.Bold,
            Foreground = Hud.Br("AccentBrush"), Margin = new Thickness(14, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand,
        };
        editLink.MouseLeftButtonUp += (_, _) =>
        {
            _walletEditorOpen = !_walletEditorOpen;
            InteractionLog.Click("Wallet set balance", editLink);
            RebuildProfitPanel(entrance: false);
        };
        provRow.Children.Add(editLink);
        _profitBody.Children.Add(provRow);

        if (_walletEditorOpen) _profitBody.Children.Add(BuildWalletEditor());

        // The rule between the WALLET block and the SESSION PROFIT eyebrow.
        _profitBody.Children.Add(new Border
        {
            BorderBrush = Hud.Br("NavBorderBrush"), BorderThickness = new Thickness(0, 1, 0, 0),
            Margin = new Thickness(0, 14, 0, 14),
        });
    }

    private static Brush WalletValueBrush(WalletUiState state) => state switch
    {
        WalletUiState.Current => Hud.Br("FgBrush"),
        WalletUiState.Aging => Hud.Br("AccentBrush"),
        WalletUiState.Impossible => Hud.Br("DangerBrush"),
        _ => Hud.Br("FgDimBrush"),
    };

    private static Brush WalletStateBrush(WalletUiState state) => state switch
    {
        WalletUiState.Current => Hud.Br("OkBrush"),
        WalletUiState.Aging => Hud.Br("AccentBrush"),
        WalletUiState.Impossible => Hud.Br("DangerBrush"),
        _ => Hud.Br("FgDimBrush"),
    };

    // Mock .stateChip border alphas: tracking rgba(102,230,166,0.42), amber-strong 0.42,
    // impossible rgba(255,107,107,0.5); dim states ride the shared nav border.
    private static Brush WalletStateBorder(WalletUiState state) => state switch
    {
        WalletUiState.Current => new SolidColorBrush(Color.FromArgb(0x6B, 0x66, 0xE6, 0xA6)),
        WalletUiState.Aging => new SolidColorBrush(Color.FromArgb(0x6B, 0xFF, 0xB2, 0x3E)),
        WalletUiState.Impossible => new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0x6B, 0x6B)),
        _ => Hud.Br("NavBorderBrush"),
    };

    // The inline anchor editor (mock .anchorBox: amber-strong border on Bg3, explicit SET,
    // digits stripped on commit, no keyboard-only path).
    private FrameworkElement BuildWalletEditor()
    {
        var box = new TextBox
        {
            FontFamily = Hud.Font("MonoFont"), FontSize = 13, Width = 170,
            Background = Hud.Br("Bg2NavBrush"), Foreground = Hud.Br("FgBrush"),
            BorderBrush = Hud.Br("NavBorderBrush"), Padding = new Thickness(8, 5, 8, 5),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var set = new Border
        {
            Background = Hud.Br("AccentFaintBrush"), BorderBrush = Hud.Br("AccentBrush"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3),
            Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(10, 0, 0, 0),
            Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "SET", FontFamily = Hud.Font("UiFont"), FontSize = 10,
                FontWeight = FontWeights.Bold, Foreground = Hud.Br("AccentBrush"),
            },
        };
        set.MouseLeftButtonUp += (_, _) =>
        {
            var digits = new string(box.Text.Where(char.IsDigit).ToArray());
            if (digits.Length == 0 || !long.TryParse(digits, out var value)) return;
            InteractionLog.Click("Wallet balance SET", set);
            _walletEditorOpen = false;
            App.Wallet.SetManualBalance(value);   // its Changed repaints this panel
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(box);
        row.Children.Add(set);

        var content = new StackPanel();
        content.Children.Add(row);
        content.Children.Add(new TextBlock
        {
            Text = "Type the balance the game shows. Trades already in this session's ledger count as included.",
            FontFamily = Hud.Font("UiFont"), FontSize = 11, Foreground = Hud.Br("FgDimBrush"),
            TextWrapping = TextWrapping.Wrap, MaxWidth = 430, Margin = new Thickness(0, 8, 0, 0),
        });

        return new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x6B, 0xFF, 0xB2, 0x3E)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
            Background = Hud.Br("Bg3Brush"), Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 12, 0, 0), Child = content,
        };
    }

    // One untracked row (spec 11.4): the ledger grid with an amber diamond, the UNTRACKED badge,
    // and the signed unexplained amount. Amber family, never cyan, never counted into any total.
    private static Border UntrackedRow(UntrackedEntry entry)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(new TextBlock
        {
            Text = entry.Utc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture),
            FontFamily = Hud.Font("MonoFont"), FontSize = 10.5, Foreground = Hud.Br("FgDimBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        });

        // 16x16 diamond, the ledger arrow's stroke treatment in amber: neither a sell nor a buy.
        var diamond = new Path
        {
            Width = 16, Height = 16,
            Data = Geometry.Parse("M8,3 L13,8 L8,13 L3,8 Z"),
            Stroke = Hud.Br("AccentBrush"), StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
        };
        Grid.SetColumn(diamond, 1);
        grid.Children.Add(diamond);

        var mainLine = new StackPanel { Orientation = Orientation.Horizontal };
        mainLine.Children.Add(new TextBlock
        {
            Text = WalletDisplay.UntrackedTitle(entry.Amount, entry.Label),
            FontFamily = Hud.Font("UiFont"), FontSize = 12.5, Foreground = Hud.Br("FgBrush"),
        });
        mainLine.Children.Add(new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xB2, 0x3E)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = WalletDisplay.UntrackedBadge, FontFamily = Hud.Font("MonoFont"), FontSize = 9,
                Foreground = Hud.Br("AccentBrush"),
            },
        });
        var main = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        main.Children.Add(mainLine);
        main.Children.Add(new TextBlock
        {
            Text = WalletDisplay.UntrackedWhere,
            FontFamily = Hud.Font("UiFont"), FontSize = 10.5,
            Foreground = Hud.Br("FgDimBrush"), Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = "Unexplained wallet delta: an OCR capture read a balance the trade ledger "
                    + "cannot account for. Mission pay, fees, rentals and ship purchases land here.",
        });
        Grid.SetColumn(main, 2);
        grid.Children.Add(main);

        var amount = new TextBlock
        {
            FontFamily = Hud.Font("MonoFont"), FontSize = 14, TextAlignment = TextAlignment.Right,
            Foreground = Hud.Br("AccentBrush"),
            Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        amount.Inlines.Add(new Run(ProfitDisplay.Signed(entry.Amount)));
        amount.Inlines.Add(new Run(" aUEC")
        {
            FontFamily = Hud.Font("UiFont"), FontSize = 10, Foreground = Hud.Br("FgDimBrush"),
        });
        Grid.SetColumn(amount, 3);
        grid.Children.Add(amount);

        var row = new Border
        {
            Padding = new Thickness(10, 9, 10, 9), CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1), BorderBrush = Brushes.Transparent,
            Background = Brushes.Transparent, Child = grid,
        };
        row.MouseEnter += (_, _) => { row.Background = Hud.Br("Bg3Brush"); row.BorderBrush = Hud.Br("NavBorderBrush"); };
        row.MouseLeave += (_, _) => { row.Background = Brushes.Transparent; row.BorderBrush = Brushes.Transparent; };
        return row;
    }

    // One shop purchase row: time, a neutral square glyph, the item/quantity/shop, and the signed
    // amount (same grid as ProfitRow and UntrackedRow, so columns line up). Neither OkBrush nor
    // DangerBrush applies here: a purchase is spending, not a profitable sale or a loss.
    private static Border PurchaseRow(ShopPurchase p)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        bool sell = p.Kind == ShopTransactionKind.Sell;
        bool refused = p.Refused is not null;

        grid.Children.Add(new TextBlock
        {
            Text = p.TimestampUtc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture),
            FontFamily = Hud.Font("MonoFont"), FontSize = 10.5, Foreground = Hud.Br("FgDimBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        });

        // 16x16 square on the trade arrow's stroke treatment: the SHAPE says this is a shop
        // purchase rather than a trade or an untracked delta, the COLOUR says which way the money
        // went, exactly as the arrow does. Dim when the kiosk refused it.
        var glyph = new Path
        {
            Width = 16, Height = 16,
            Data = Geometry.Parse("M4,4 L12,4 L12,12 L4,12 Z"),
            Stroke = refused ? Hud.Br("FgDimBrush") : sell ? Hud.Br("OkBrush") : Hud.Br("DangerBrush"),
            StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0),
        };
        Grid.SetColumn(glyph, 1);
        grid.Children.Add(glyph);

        var mainLine = new StackPanel { Orientation = Orientation.Horizontal };
        mainLine.Children.Add(new TextBlock
        {
            Text = ProfitDisplay.PurchaseRowTitle(sell, p.Quantity, WalletDisplay.PurchaseTitle(p)),
            FontFamily = Hud.Font("UiFont"), FontSize = 12.5,
            Foreground = Hud.Br(refused ? "FgDimBrush" : "FgBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (refused)
            mainLine.Children.Add(new Border
            {
                BorderBrush = Hud.Br("DangerBrush"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3), Padding = new Thickness(6, 1, 6, 1),
                Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = p.Refused, FontFamily = Hud.Font("MonoFont"), FontSize = 9,
                    Foreground = Hud.Br("DangerBrush"),
                },
            });

        var main = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        main.Children.Add(mainLine);
        main.Children.Add(new TextBlock
        {
            Text = ProfitDisplay.ShopLabel(p.ShopName),
            FontFamily = Hud.Font("UiFont"), FontSize = 10.5,
            Foreground = Hud.Br("FgDimBrush"), Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = "Bought at a shop kiosk, read from the game log. Shop tokens are kiosk "
                    + "templates shared across stations, so this names the shop, not the place.",
        });
        Grid.SetColumn(main, 2);
        grid.Children.Add(main);

        // A refused row keeps its amount and goes dim: the badge above says it did not settle.
        var amount = new TextBlock
        {
            FontFamily = Hud.Font("MonoFont"), FontSize = 14, TextAlignment = TextAlignment.Right,
            Foreground = refused ? Hud.Br("FgDimBrush") : sell ? Hud.Br("OkBrush") : Hud.Br("DangerBrush"),
            Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        amount.Inlines.Add(new Run(ProfitDisplay.Signed(sell ? p.Price : -p.Price)));
        amount.Inlines.Add(new Run(" aUEC")
        {
            FontFamily = Hud.Font("UiFont"), FontSize = 10, Foreground = Hud.Br("FgDimBrush"),
        });
        Grid.SetColumn(amount, 3);
        grid.Children.Add(amount);

        var row = new Border
        {
            Padding = new Thickness(10, 9, 10, 9), CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1), BorderBrush = Brushes.Transparent,
            Background = Brushes.Transparent, Child = grid,
        };
        row.MouseEnter += (_, _) => { row.Background = Hud.Br("Bg3Brush"); row.BorderBrush = Hud.Br("NavBorderBrush"); };
        row.MouseLeave += (_, _) => { row.Background = Brushes.Transparent; row.BorderBrush = Brushes.Transparent; };
        return row;
    }

    // ── PROFIT HISTORY strip (S3b/S5; mock .histSec): the ALL TIME header outlives the chart
    // cap, per-day bars over a gold cumulative trend, active channel only. ──
    private void BuildProfitHistoryStrip()
    {
        var channel = App.GameLogFeed.ActiveChannel;
        var history = App.Profit.History;
        var ch = history.Channels.Find(c => c.Channel == channel);
        long allNet = ProfitHistory.AllTimeNet(history, channel);
        int allCount = ProfitHistory.AllTimeSessionCount(history, channel);
        var days = ProfitDisplay.FoldByLocalDay(ch?.Entries ?? Enumerable.Empty<SessionSummary>());

        var strip = new StackPanel();

        var head = new Grid { Margin = new Thickness(0, 0, 0, 7) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(ProfitSectionTitle("Profit history"));
        var boundary = new TextBlock
        {
            Text = ProfitDisplay.Boundary(channel), FontFamily = Hud.Font("UiFont"), FontSize = 9,
            FontWeight = FontWeights.Bold, Foreground = Hud.Br("FgDimBrush"),
        };
        Grid.SetColumn(boundary, 1);
        head.Children.Add(boundary);
        strip.Children.Add(head);

        if (allCount > 0 && ch is not null)
        {
            var summary = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            summary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            summary.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var left = new StackPanel { Orientation = Orientation.Horizontal };
            left.Children.Add(new TextBlock
            {
                Text = "ALL TIME", FontFamily = Hud.Font("UiFont"), FontSize = 9, FontWeight = FontWeights.Bold,
                Foreground = Hud.Br("FgDimBrush"), Margin = new Thickness(0, 0, 7, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            var allValue = new TextBlock
            {
                FontFamily = Hud.Font("MonoFont"), FontSize = 15, FontWeight = FontWeights.Medium,
                Foreground = allNet >= 0 ? Hud.Br("OkBrush") : Hud.Br("DangerBrush"),
                Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            allValue.Inlines.Add(new Run(ProfitDisplay.Compact(allNet)));
            allValue.Inlines.Add(new Run(" aUEC")
            {
                FontFamily = Hud.Font("UiFont"), FontSize = 10, FontWeight = FontWeights.Normal,
                Foreground = Hud.Br("FgDimBrush"),
            });
            left.Children.Add(allValue);
            left.Children.Add(new TextBlock
            {
                Text = ProfitDisplay.AllTimeLine(allCount, ch.FirstSessionUtc),
                FontFamily = Hud.Font("UiFont"), FontSize = 12, Foreground = Hud.Br("FgDimBrush"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            summary.Children.Add(left);
            if (ch.PrunedCount > 0)
            {
                var window = new TextBlock
                {
                    Text = ProfitDisplay.ChartWindowNote, FontFamily = Hud.Font("MonoFont"), FontSize = 9.5,
                    Foreground = Hud.Br("FgDimBrush"), VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(window, 1);
                summary.Children.Add(window);
            }
            strip.Children.Add(summary);
        }

        if (days.Count < 2)
        {
            // The draw-on flag rides the expand; with no chart built to consume it, drop it here
            // or the data tick that grows a second day would play the entrance mid-read,
            // violating the no-entrance-on-tick rule (review fix, 2026-08-05).
            _profitTrendDrawPending = false;
            strip.Children.Add(new TextBlock
            {
                Text = allCount == 0 ? ProfitDisplay.HistoryEmptyNone : ProfitDisplay.HistoryEmptyOne,
                FontFamily = Hud.Font("UiFont"), FontSize = 12, Foreground = Hud.Br("FgDimBrush"),
                TextWrapping = TextWrapping.Wrap, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 8, 10, 8),
            });
        }
        else
        {
            var canvas = new Canvas { Height = 130, ClipToBounds = true, Background = Brushes.Transparent };
            canvas.SizeChanged += (_, _) => DrawProfitChart(canvas, days, channel);
            strip.Children.Add(canvas);

            var dates = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            dates.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            dates.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            dates.Children.Add(ProfitDateLabel(days[0].LocalDate));
            var last = ProfitDateLabel(days[^1].LocalDate);
            Grid.SetColumn(last, 1);
            dates.Children.Add(last);
            strip.Children.Add(dates);
        }

        _profitBody.Children.Add(new Border
        {
            BorderBrush = Hud.Br("NavBorderBrush"), BorderThickness = new Thickness(0, 1, 0, 0),
            Margin = new Thickness(0, 16, 0, 0), Padding = new Thickness(0, 14, 0, 0), Child = strip,
        });
    }

    private static TextBlock ProfitDateLabel(DateTime day) => new()
    {
        Text = day.ToString("MMM d", CultureInfo.InvariantCulture).ToUpperInvariant(),
        FontFamily = Hud.Font("MonoFont"), FontSize = 9.5, Foreground = Hud.Br("FgDimBrush"),
    };

    // Bars green up, red down on ONE shared money scale; the gold cumulative trend has its own
    // vertical scale (cumulative min to max) - both per the mock's HistoryChart. Gold, not cyan:
    // cyan is reserved for live location, green and red carry per-day money, so the running total
    // takes the gold emphasis tone.
    private void DrawProfitChart(Canvas canvas, List<ProfitDay> days, GameChannel channel)
    {
        canvas.Children.Clear();
        double w = canvas.ActualWidth;
        if (w < 40) return;
        const double h = 130, padL = 8, padR = 8, padT = 10, padB = 6;
        double chartH = h - padT - padB;
        double maxPos = Math.Max(1, days.Max(d => Math.Max(0L, d.Net)));
        double maxNeg = Math.Max(0, days.Max(d => Math.Max(0L, -d.Net)));
        double scale = chartH / (maxPos + maxNeg);
        double baseY = padT + maxPos * scale;
        double slot = (w - padL - padR) / days.Count;
        double barW = Math.Min(slot * 0.5, 34);
        double Cx(int i) => padL + i * slot + slot / 2;

        canvas.Children.Add(new Line
        {
            X1 = 0, Y1 = baseY, X2 = w, Y2 = baseY,
            Stroke = Hud.Br("NavBorderBrush"), StrokeThickness = 1,
        });

        var bars = new Rectangle[days.Count];
        for (int i = 0; i < days.Count; i++)
        {
            var day = days[i];
            double barH = Math.Max(2, Math.Abs((double)day.Net) * scale);
            var bar = new Rectangle
            {
                Width = barW, Height = barH,
                Fill = day.Net >= 0 ? Hud.Br("OkBrush") : Hud.Br("DangerBrush"),
                Opacity = 0.72,
            };
            Canvas.SetLeft(bar, Cx(i) - barW / 2);
            Canvas.SetTop(bar, day.Net >= 0 ? baseY - barH : baseY);
            canvas.Children.Add(bar);
            bars[i] = bar;
        }

        long running = 0;
        var cum = new double[days.Count];
        for (int i = 0; i < days.Count; i++) { running += days[i].Net; cum[i] = running; }
        double cmin = cum.Min(), cmax = cum.Max();
        double Cy(double v) => cmax == cmin ? padT + chartH / 2 : padT + (1 - (v - cmin) / (cmax - cmin)) * chartH;
        var trend = new Polyline
        {
            Stroke = Hud.Br("GoldBrush"), StrokeThickness = 1.5, Opacity = 0.9,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        };
        for (int i = 0; i < days.Count; i++) trend.Points.Add(new Point(Cx(i), Cy(cum[i])));
        canvas.Children.Add(trend);

        // Hover: the bar brightens and the tooltip names the day (date, day net, session count,
        // transaction count - section 9 ruling 4).
        for (int i = 0; i < days.Count; i++)
        {
            int idx = i;
            var hit = new Rectangle { Width = slot, Height = h, Fill = Brushes.Transparent };
            Canvas.SetLeft(hit, padL + i * slot);
            Canvas.SetTop(hit, 0);
            hit.ToolTip = ProfitDayTip(days[i], channel);
            ToolTipService.SetInitialShowDelay(hit, 0);
            hit.MouseEnter += (_, _) => bars[idx].Opacity = 1.0;
            hit.MouseLeave += (_, _) => bars[idx].Opacity = 0.72;
            canvas.Children.Add(hit);
        }

        // Trend draw-on (600ms, SlideOut), the sparkline idiom, on expand only - never a tick.
        if (_profitTrendDrawPending)
        {
            _profitTrendDrawPending = false;
            if (!Motion.Reduced && trend.Points.Count >= 2)
            {
                double lenPx = 0;
                for (int i = 1; i < trend.Points.Count; i++)
                    lenPx += (trend.Points[i] - trend.Points[i - 1]).Length;
                if (lenPx > 0)
                {
                    double dashLen = lenPx / trend.StrokeThickness;
                    trend.StrokeDashArray = new DoubleCollection { dashLen };
                    trend.StrokeDashOffset = dashLen;
                    var anim = new DoubleAnimation(dashLen, 0, TimeSpan.FromMilliseconds(600)) { EasingFunction = Motion.SlideOut };
                    anim.Completed += (_, _) =>
                    {
                        trend.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
                        trend.StrokeDashArray = null;
                    };
                    trend.BeginAnimation(Shape.StrokeDashOffsetProperty, anim);
                }
            }
        }
    }

    private static ToolTip ProfitDayTip(ProfitDay day, GameChannel channel)
    {
        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = day.LocalDate.ToString("MMM d", CultureInfo.InvariantCulture).ToUpperInvariant()
                 + ", " + GameChannels.FolderName(channel),
            FontFamily = Hud.Font("UiFont"), FontSize = 10, FontWeight = FontWeights.Bold,
            Foreground = Hud.Br("FgDimBrush"),
        });
        var dayNet = new TextBlock
        {
            FontFamily = Hud.Font("MonoFont"), FontSize = 12,
            Foreground = day.Net >= 0 ? Hud.Br("OkBrush") : Hud.Br("DangerBrush"),
            Margin = new Thickness(0, 2, 0, 0),
        };
        dayNet.Inlines.Add(new Run(ProfitDisplay.Signed(day.Net)));
        dayNet.Inlines.Add(new Run(" aUEC")
        {
            FontFamily = Hud.Font("UiFont"), FontSize = 9.5, Foreground = Hud.Br("FgDimBrush"),
        });
        content.Children.Add(dayNet);
        content.Children.Add(new TextBlock
        {
            Text = $"{day.Sessions} session{(day.Sessions == 1 ? "" : "s")}, "
                 + $"{day.TxCount} transaction{(day.TxCount == 1 ? "" : "s")}",
            FontFamily = Hud.Font("UiFont"), FontSize = 10, Foreground = Hud.Br("FgDimBrush"),
            Margin = new Thickness(0, 2, 0, 0),
        });
        return new ToolTip
        {
            Background = Hud.Br("Bg3Brush"), BorderBrush = Hud.Br("BorderBrush"),
            BorderThickness = new Thickness(1), Padding = new Thickness(10, 7, 10, 7), Content = content,
        };
    }

    // ── Footer caveat (S5/S6): permanent boundary statement; NEGATIVE leads with the unsold-cargo
    // rule in words, bolded, instead of letting the red carry it alone. ──
    private void BuildProfitFooter(bool negative)
    {
        var caveat = new TextBlock
        {
            FontFamily = Hud.Font("UiFont"), FontSize = 10.5, Foreground = Hud.Br("FgDimBrush"),
            TextWrapping = TextWrapping.Wrap, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Left,
        };
        if (negative)
        {
            caveat.Inlines.Add(new Run(ProfitDisplay.CaveatNegativeLead)
            {
                FontWeight = FontWeights.SemiBold, Foreground = Hud.Br("FgBrush"),
            });
            caveat.Inlines.Add(new Run(" " + ProfitDisplay.CaveatBody));
        }
        else
        {
            caveat.Text = ProfitDisplay.CaveatBody + ProfitDisplay.CaveatUnsoldTail;
        }
        _profitBody.Children.Add(new Border
        {
            BorderBrush = Hud.Br("NavBorderBrush"), BorderThickness = new Thickness(0, 1, 0, 0),
            Margin = new Thickness(0, 14, 0, 0), Padding = new Thickness(0, 13, 0, 0), Child = caveat,
        });
    }

    // ── Count-up from the PREVIOUS value. CommandPage.CountUpRun's mirror (same DP idiom, same
    // CountUpMs and Settle), seeded from the prior net instead of 0: S5 mandates the number never
    // restarts at zero, which is exactly what CountUp.cs does for fresh RS readings. ──
    private static readonly DependencyProperty ProfitCountProperty = DependencyProperty.RegisterAttached(
        "ProfitCount", typeof(double), typeof(MoneyPanel), new PropertyMetadata(0.0, OnProfitCountChanged));

    private static void OnProfitCountChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is TextBlock tb) tb.Text = ProfitDisplay.Signed((long)Math.Round((double)e.NewValue));
    }

    private static void AnimateProfitNet(TextBlock tb, long from, long to)
    {
        tb.BeginAnimation(ProfitCountProperty, null);
        tb.SetValue(ProfitCountProperty, (double)from);
        tb.Text = ProfitDisplay.Signed(from);
        var anim = new DoubleAnimation(from, to, new Duration(TimeSpan.FromMilliseconds(Motion.CountUpMs)))
        {
            EasingFunction = Motion.Settle,
        };
        tb.BeginAnimation(ProfitCountProperty, anim);
    }
}
