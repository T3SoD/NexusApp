using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NexusApp.Models;
using NexusApp.Services;
using NexusApp.Services.Map;
using static NexusApp.Views.UiHelpers;

namespace NexusApp.Views;

// Split from MainWindow.xaml.cs (app review, Task 13): Refinery Tracker / Work Orders.
public partial class MainWindow
{
    // ── Work Orders ──────────────────────────────────────────────────────────

    private System.Windows.Threading.DispatcherTimer? _listTicker;
    private readonly Dictionary<string, TextBlock?> _rowLiveRefs = new();

    // Card add + ready-flash bookkeeping. The gallery fully rebuilds on every WorkOrders change, and a single
    // save fires a rebuild PER re-added order (LoadWorkOrders clears + re-adds all), so an animation started during
    // that storm is wiped by the next rebuild. Detection therefore runs once, deferred, against the final card set.
    private readonly HashSet<string> _knownWorkOrderIds = new();   // ids we've ever built a card for (add = new -> fade+rise)
    private bool _workOrdersSeeded;                                // first build seeds without animating existing cards
    private readonly HashSet<string> _woEverRefining = new();      // ids seen in a pre-ready (refining/active-timer) state
    private readonly HashSet<string> _woFlashedReady = new();      // ids whose ready-flash has already played (once per order)
    private readonly Dictionary<string, WoCardParts> _woCardParts = new();   // final card refs by id, rebuilt each pass
    private bool _woAnimQueued;                                    // coalesces the deferred animation pass across the storm
    private bool _woRebuildQueued;                                 // coalesces RebuildWorkOrderList itself across the same storm

    // Refined sell line (Task 12): ids whose "+N more" expander is open, so an incidental rebuild
    // (e.g. an hourly market refresh via OnMarketDataChanged) does not collapse it - same survives-
    // a-rebuild contract as OtherMatches' _expandedName above.
    private readonly HashSet<string> _expandedWorkOrderSells = new();

    // The animatable pieces of a work-order card, captured at build time so the deferred pass can add/flash them.
    private sealed class WoCardParts
    {
        public FrameworkElement Card = null!;
        public Grid ChipHost = null!;
        public Border Chip = null!;
        public System.Windows.Media.SolidColorBrush? ReadyStroke;
        public WorkOrderStatus Status;
        public bool PreReady;   // refining / mining / has an active timer
    }

    private void NewWorkOrder_Click(object sender, RoutedEventArgs e) => OpenWorkOrderEditor(new WorkOrder());

    // Add/edit now happens in a modal popup (the page is a card gallery). The editor's Save/Delete run the
    // VM commands, which raise CollectionChanged -> RebuildWorkOrderList, so the gallery refreshes itself.
    private void OpenWorkOrderEditor(WorkOrder wo)
        => new WorkOrderEditorDialog(wo, _vm, this).ShowDialog();

    private void RebuildWorkOrderList()
    {
        _rowLiveRefs.Clear();
        _woCardParts.Clear();
        WorkOrderGallery.Children.Clear();

        var orders = _vm.WorkOrders;
        if (orders.Count == 0)
        {
            WorkOrderGallery.Children.Add(EmptyWorkOrders());
        }
        else
        {
            var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 };
            foreach (var wo in orders)
            {
                var card = BuildWorkOrderCard(wo);   // records this card's animatable parts in _woCardParts[wo.Id]
                grid.Children.Add(card);
                // Pre-hide a genuinely new card so it does not paint at full opacity for a frame before the deferred fade+rise.
                if (_workOrdersSeeded && !Motion.Reduced && !_knownWorkOrderIds.Contains(wo.Id)) card.Opacity = 0;
            }
            grid.Children.Add(AddWorkOrderTile());
            WorkOrderGallery.Children.Add(grid);
        }
        ScheduleWorkOrderAnimations();

        // Run the tick while any order still has a running timer OR has a run-out timer not yet flipped to
        // ready (e.g. it elapsed while the app was closed and loaded as Refining). The tick both refreshes the
        // live countdown and performs the auto-flip, so it must stay alive for the latter until the flip lands.
        var needsTick = orders.Any(w => w.HasActiveTimer || w.ShouldAutoFlipToReady(DateTime.UtcNow));
        if (needsTick && _listTicker == null)
        {
            _listTicker = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _listTicker.Tick += ListTicker_Tick;
            _listTicker.Start();
        }
        else if (!needsTick)
        {
            _listTicker?.Stop();
            _listTicker = null;
        }
    }

    private void ListTicker_Tick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;

        // Auto-flip: any refine timer that has run out while the order still sits pre-ready flips to ready
        // through the same canonical path the editor uses (WorkOrderEditorPanel.TryFlipToReady = store write
        // + OrderReadyToCollect), so the gallery rebuilds and the v6.4.0 ready flash plays here on the main
        // window instead of behind the editor modal. TryFlipToReady persists via SaveWorkOrder, which clears
        // and re-adds WorkOrders (a CollectionChanged storm -> RebuildWorkOrderList), so the flip candidates
        // are snapshotted first and the flip runs after the enumeration to avoid mutating the live collection.
        // The status check inside TryFlipToReady keeps it idempotent: if the editor ticker (open) already
        // flipped this order this second, TryFlipToReady returns false here and nothing double-writes.
        List<WorkOrder>? toFlip = null;
        foreach (var wo in _vm.WorkOrders)
            if (wo.ShouldAutoFlipToReady(now))
                (toFlip ??= new()).Add(wo);
        if (toFlip != null)
        {
            foreach (var wo in toFlip)
                if (WorkOrderEditorPanel.TryFlipToReady(wo, _vm))
                    Logger.Info($"[UI] Refinery: order auto-marked ready ({wo.Label})");
            return;   // each flip rebuilt the gallery; the live-countdown pass resumes on the next tick
        }

        bool anyActive = false;
        foreach (var wo in _vm.WorkOrders)
        {
            if (!_rowLiveRefs.TryGetValue(wo.Id, out var timer)) continue;
            if (!wo.HasActiveTimer) continue;
            anyActive = true;
            if (timer != null) timer.Text = string.IsNullOrEmpty(wo.TimerRemainingShort) ? "-" : wo.TimerRemainingShort;
        }
        if (!anyActive)
        {
            _listTicker?.Stop();
            _listTicker = null;
        }
    }

    // One self-contained work-order card (the mock's .wo): name + 3 meta fields + status chip, with an
    // always-present progress bar and a big timer on the footer row. Clicking the card opens the popup editor.
    private FrameworkElement BuildWorkOrderCard(WorkOrder wo)
    {
        var bg2      = (System.Windows.Media.Brush)FindResource("Bg2NavBrush");
        var highlight= (System.Windows.Media.Brush)FindResource("HighlightBrush");
        var accent   = (System.Windows.Media.Brush)FindResource("AccentBrush");
        var fgBrush  = (System.Windows.Media.Brush)FindResource("FgBrush");
        var dimBrush = (System.Windows.Media.Brush)FindResource("FgDimBrush");
        var headFont = (System.Windows.Media.FontFamily)FindResource("HeadFont");
        var dispFont = (System.Windows.Media.FontFamily)FindResource("DisplayFont");
        var monoFont = (System.Windows.Media.FontFamily)FindResource("MonoFont");

        var inner = new StackPanel();

        // top row: name + meta (left) | status chip + delete (right)
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel();
        left.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(wo.Label) ? (string.IsNullOrWhiteSpace(wo.Resources) ? "Work order" : wo.Resources) : wo.Label,
            FontFamily = dispFont, FontSize = 16, FontWeight = FontWeights.Bold, Foreground = fgBrush,
            TextTrimming = System.Windows.TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 6),
        });
        var meta = new WrapPanel { Orientation = Orientation.Horizontal };
        void MetaField(string label, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var tb = new TextBlock { Margin = new Thickness(0, 0, 14, 2), FontFamily = headFont, FontSize = 11, TextTrimming = System.Windows.TextTrimming.CharacterEllipsis, MaxWidth = 220 };
            tb.Inlines.Add(new System.Windows.Documents.Run(label + " ") { Foreground = dimBrush });
            tb.Inlines.Add(new System.Windows.Documents.Run(value) { Foreground = fgBrush, FontWeight = FontWeights.SemiBold });
            meta.Children.Add(tb);
        }
        MetaField("Resources", wo.Resources);
        MetaField("Mined at", wo.Location);
        MetaField("Refinery", wo.Refinery);
        left.Children.Add(meta);
        Grid.SetColumn(left, 0); top.Children.Add(left);

        var rightTop = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        var chip = Hud.StatusChip(wo.Status);
        chip.VerticalAlignment = VerticalAlignment.Center;
        // Hosted in a Grid so the ready-transition can overlay the outgoing status chip and cross-dissolve.
        var chipHost = new Grid { VerticalAlignment = VerticalAlignment.Center };
        chipHost.Children.Add(chip);
        rightTop.Children.Add(chipHost);
        var deleteTb = new TextBlock { Text = "x", FontFamily = monoFont, FontSize = 13, FontWeight = FontWeights.Bold, Foreground = dimBrush, VerticalAlignment = VerticalAlignment.Center };
        var deleteBtn = new Border { Child = deleteTb, Padding = new Thickness(10, 0, 2, 0), Cursor = System.Windows.Input.Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Delete" };
        deleteBtn.MouseEnter += (s, _) => deleteTb.Foreground = _chipDangerBrush;
        deleteBtn.MouseLeave += (s, _) => deleteTb.Foreground = dimBrush;
        rightTop.Children.Add(deleteBtn);
        Grid.SetColumn(rightTop, 1); top.Children.Add(rightTop);
        inner.Children.Add(top);

        // footer row: always-on progress bar (flex) + big timer text
        var footer = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var bar = BuildWorkOrderBar(wo);
        ((FrameworkElement)bar).VerticalAlignment = VerticalAlignment.Center;   // every BuildWorkOrderBar path returns a Grid
        Grid.SetColumn(bar, 0); footer.Children.Add(bar);

        string timerText; System.Windows.Media.Brush timerBrush;
        if (wo.Status == WorkOrderStatus.ReadyToCollect) { timerText = "ready"; timerBrush = BrushFromHex(wo.StatusColorHex); }
        else if (wo.Status == WorkOrderStatus.Complete)  { timerText = "done";  timerBrush = dimBrush; }
        else if (wo.HasActiveTimer && !string.IsNullOrEmpty(wo.TimerRemainingShort)) { timerText = wo.TimerRemainingShort; timerBrush = accent; }
        else { timerText = "-"; timerBrush = dimBrush; }
        var timerTb = new TextBlock { Text = timerText, FontFamily = dispFont, FontSize = 18, FontWeight = FontWeights.Bold, Foreground = timerBrush, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(timerTb, 1); footer.Children.Add(timerTb);
        inner.Children.Add(footer);

        // Refined sell line (market data, Task 12; mock variant B) - silent unless the order is
        // ready/complete, market data is on, and at least one recognized ore in Resources prices out.
        if (BuildWorkOrderSellBlock(wo) is { } sellBlock) inner.Children.Add(sellBlock);

        _rowLiveRefs[wo.Id] = timerTb;

        // Chamfered card with persistent corner brackets (the mock shows them always); hover highlight; click -> editor.
        var host = Hud.CardFrame(inner, out var frame, out var brackets, chamfer: 12, padding: new Thickness(15, 13, 14, 14));
        brackets.Visibility = Visibility.Visible;
        host.Margin = new Thickness(7, 7, 7, 7);
        host.Cursor = System.Windows.Input.Cursors.Hand;

        System.Windows.Media.SolidColorBrush? readyStroke = null;
        if (wo.Status == WorkOrderStatus.ReadyToCollect)
        {
            // Dedicated per-card brush (never a shared/frozen resource) so the border flash can animate its Color.
            readyStroke = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x59, 0x66, 0xE6, 0xA6));   // green-tinted border
            frame.Stroke = readyStroke;
        }
        if (wo.Status == WorkOrderStatus.Complete)
            host.Opacity = 0.72;

        host.MouseEnter += (s, e) => { if (wo.Status != WorkOrderStatus.Complete) frame.Fill = highlight; };
        host.MouseLeave += (s, e) => frame.Fill = bg2;
        host.MouseLeftButtonDown += (s, e) => OpenWorkOrderEditor(wo);

        deleteBtn.MouseLeftButtonDown += (s, e) =>
        {
            e.Handled = true;
            // Delete stays immediate; the model remove raises CollectionChanged -> full RebuildWorkOrderList,
            // which discards this card wholesale, so the 140ms collapse cannot be shown here (allowed skip).
            _vm.DeleteWorkOrderCommand.Execute(wo.Id);
        };

        // Capture the animatable parts; the deferred pass (FlushWorkOrderAnimations) plays add/ready animations once,
        // after the rebuild storm, on the final card - detection is Id-keyed so it survives the rebuilds.
        _woCardParts[wo.Id] = new WoCardParts
        {
            Card = host, ChipHost = chipHost, Chip = chip, ReadyStroke = readyStroke, Status = wo.Status,
            PreReady = wo.Status is WorkOrderStatus.Refining or WorkOrderStatus.Mining || wo.HasActiveTimer,
        };

        return host;
    }

    // ── Refined sell line (market data, Task 12) ───────────────────────────────
    // Mock: nexus-design-lab/market-data/index.html, WorkOrderCard variant="B" (.woSell/.woSellRow/
    // .moreBtn, CSS lines 231-244; manifest rows "Work order sell block" / "+1 more affordance").
    // Placement is the coordinator's explicit, deliberate override of the mock's markup order (mock
    // puts .woSell before .woFoot; this block is inserted AFTER the footer Grid per the brief and
    // committed context, both citing the exact insertion point) - a structural exception, not a
    // silent drift from "mock governs structure".
    //
    // Only for a ready/complete order, with market data on, a snapshot landed, and at least one
    // recognized ore in Resources pricing out via RefinedSellsForOrder - any other case renders
    // nothing at all, so the card is byte-for-byte identical to today's (data-honesty silence rule,
    // same contract as FillHeroMarket). Line 1 is always the top-value ore
    // (RefinedSellsForOrder is already ordered by Hit.Display desc); when more recognized ores
    // exist, a "+N more" ghost affordance expands the rest in place. Expanded state is tracked per
    // order id in _expandedWorkOrderSells so an incidental rebuild (an hourly market refresh while
    // this page is open) does not collapse a card the user has open - same contract as OtherMatches'
    // _expandedName survives a cart-toggle rebuild.
    private FrameworkElement? BuildWorkOrderSellBlock(WorkOrder wo)
    {
        if (wo.Status is not (WorkOrderStatus.ReadyToCollect or WorkOrderStatus.Complete)) return null;
        if (App.Settings.Current.MarketDataEnabled != true) return null;
        if (App.Market.Snapshot is not { } snap) return null;

        var sells = MarketQueries.RefinedSellsForOrder(snap, wo.Resources);
        if (sells.Count == 0) return null;

        // 1px NavBorder top rule, margin-top 11, padding-top 10 (mock .woSell, manifest "Work order
        // sell block") - same hairline-before-content idiom FillHeroMarket uses for the decoder line.
        var block = new StackPanel { Margin = new Thickness(0, 11, 0, 0) };
        block.Children.Add(new Border { Height = 1, Background = Hud.Br("NavBorderBrush"), Margin = new Thickness(0, 0, 0, 10) });
        block.Children.Add(BuildWorkOrderSellRow(sells[0].SeedName, sells[0].Hit, showSellLabel: true));

        if (sells.Count > 1)
        {
            var extraCount = sells.Count - 1;
            var extra = new StackPanel { ClipToBounds = true };   // clips mid-reveal, same as the mock's overflow:hidden wrapper
            for (int i = 1; i < sells.Count; i++)
                extra.Children.Add(BuildWorkOrderSellRow(sells[i].SeedName, sells[i].Hit, showSellLabel: false));

            bool expanded = _expandedWorkOrderSells.Contains(wo.Id);
            extra.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            extra.Height = expanded ? double.NaN : 0;   // static on rebuild when already open - no re-entrance, matching ScanCardComposition
            extra.Opacity = expanded ? 1 : 0;
            block.Children.Add(extra);

            var moreLabel = new TextBlock
            {
                Text = MoreLessText(extraCount, expanded),
                FontFamily = Hud.Font("HeadFont"), FontSize = 11, Foreground = Hud.Br("FgDimBrush"),
            };
            var moreBtn = new Border
            {
                Child = moreLabel, Padding = new Thickness(0, 3, 0, 0), Cursor = System.Windows.Input.Cursors.Hand,
                Background = System.Windows.Media.Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Left,
            };
            // Ghost text button, amber on hover (mock .moreBtn:hover) - mouse-only, no keyboard affordance.
            moreBtn.MouseEnter += (s, e) => moreLabel.Foreground = Hud.Br("AccentBrush");
            moreBtn.MouseLeave += (s, e) => moreLabel.Foreground = Hud.Br("FgDimBrush");
            moreBtn.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                bool willExpand = !_expandedWorkOrderSells.Contains(wo.Id);
                if (willExpand)
                {
                    _expandedWorkOrderSells.Add(wo.Id);
                    InteractionLog.Click("work order sell expand", moreBtn);
                    ExpandRows(extra, animate: !Motion.Reduced);
                }
                else
                {
                    _expandedWorkOrderSells.Remove(wo.Id);
                    InteractionLog.Click("work order sell collapse", moreBtn);
                    CollapseRows(extra, animate: !Motion.Reduced);
                }
                moreLabel.Text = MoreLessText(extraCount, willExpand);
            };
            block.Children.Add(moreBtn);
        }

        return block;
    }

    private static string MoreLessText(int extraCount, bool expanded) =>
        expanded ? "Show less  ▴" : $"+{extraCount} more  ▾";   // same glyph pair as ToggleLink above

    // One priced ore line (mock .woSellRow): SELL label (line 1 only, mono 9 bold FgDim, matching
    // the card's meta-label idiom) + ore name in its rarity colour (12 SemiBold, mock .ore) + the
    // refined sell price (12, GoldBrush, FgDimBrush when stale, mock .val) + terminal name
    // (11, FgDimBrush, ellipsis-truncated, mock .term; the value column is Auto, so the unit added
    // by the 2026-07-27 ruling simply narrows the terminal's star column) + age or - when the freshest row is from an
    // older game patch - the same PatchTagChip the dossier price rows use
    // (mock .age; data-honesty rule: a price never renders without one of the two).
    private FrameworkElement BuildWorkOrderSellRow(string seedName, PriceHit hit, bool showSellLabel)
    {
        var dim  = Hud.Br("FgDimBrush");
        var gold = Hud.Br("GoldBrush");
        var headFont = Hud.Font("HeadFont");
        var monoFont = Hud.Font("MonoFont");

        var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                          // SELL label
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                          // ore
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                          // value
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });     // terminal
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                          // age / patch tag

        if (showSellLabel)
        {
            var lbl = new TextBlock
            {
                Text = "SELL", FontFamily = monoFont, FontSize = 9, FontWeight = FontWeights.Bold,
                Foreground = dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
            };
            Grid.SetColumn(lbl, 0);
            g.Children.Add(lbl);
        }

        var ore = new TextBlock
        {
            Text = seedName, FontFamily = headFont, FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = RarityBrush(RarityOf(seedName)), MinWidth = 78,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
            TextTrimming = System.Windows.TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(ore, 1);
        g.Children.Add(ore);

        var val = new TextBlock
        {
            Text = $"{hit.Display:n0} aUEC/SCU", FontFamily = headFont, FontSize = 12,
            Foreground = hit.Stale ? dim : gold, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        Grid.SetColumn(val, 2);
        g.Children.Add(val);

        // App review 2026-08-01: this used to be a bare terminal NAME, because PriceHit carried no
        // terminal id and so this row could not resolve a MarketTerminal at all. With the id back,
        // one shared helper appends the system and, when a live session places the player in that
        // same system, the distance. Silence rule preserved: an unresolvable terminal returns null
        // and this renders byte-for-byte as it did before.
        var where = PriceLocationLabel.Describe(hit.TerminalId, App.Market.Snapshot?.Terminals.Rows,
                                                App.Map, App.Player.Current);
        var term = new TextBlock
        {
            Text = where is null ? hit.TerminalName : $"{hit.TerminalName}  ({where})",
            FontFamily = headFont, FontSize = 11, Foreground = dim,
            TextTrimming = System.Windows.TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        // Click through to the price browser for this exact terminal, the same jump the Starmap
        // already makes. Mouse-only and not a Tab stop, matching the Trade chips' idiom.
        if (hit.TerminalId > 0)
        {
            term.Cursor = System.Windows.Input.Cursors.Hand;
            term.ToolTip = $"Show all prices at {hit.TerminalName}.";
            term.MouseLeftButtonUp += (_, _) =>
            {
                SetActivePage("trade");
                _tradePage?.ShowPricesForTerminal(hit.TerminalId);
            };
        }
        Grid.SetColumn(term, 3);
        g.Children.Add(term);

        FrameworkElement age;
        if (hit.Stale)
        {
            var chip = PatchTagChip(hit.GameVersion);
            chip.HorizontalAlignment = HorizontalAlignment.Right;
            age = chip;
        }
        else
        {
            age = new TextBlock
            {
                Text = MarketNotice.FormatAge(DateTime.UtcNow - hit.ModifiedUtc), FontSize = 10,
                Foreground = dim, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            };
        }
        Grid.SetColumn(age, 4);
        g.Children.Add(age);

        return g;
    }

    // Per-element animation generation for ExpandRows/CollapseRows, mirroring
    // OverlayTabStrip.TabHost.MotionGen / OverlayWindow._ghostMotionGen: BeginAnimation(prop, null)
    // detaches an animation from its property but its clock keeps running and still raises
    // Completed at the ORIGINAL end time, so a Completed handler must compare its captured
    // generation against the element's current one and no-op if superseded, or an orphaned clock
    // from a rapidly-re-toggled row can stomp state a later call already set. Attached (rather than
    // a plain field) because these are static helpers shared by three independent expander
    // instances with no common host object (the work order sell block's "extra", and the Codex
    // dossier's VALUE + long-tail "target"/"body" expanders) that can be open at the same time.
    private static readonly DependencyProperty RowsMotionGenProperty = DependencyProperty.RegisterAttached(
        "RowsMotionGen", typeof(int), typeof(MainWindow), new PropertyMetadata(0));

    private static int BumpRowsMotionGen(FrameworkElement rows)
    {
        int gen = (int)rows.GetValue(RowsMotionGenProperty) + 1;
        rows.SetValue(RowsMotionGenProperty, gen);
        return gen;
    }

    // Expand/collapse a revealed block: height (0 <-> measured) + opacity over Motion.DrillMs on
    // Motion.Reveal - the app's drill-down reveal vocabulary (Motion.cs:27,31), same pairing the mock's
    // AnimatePresence uses for this exact affordance. Motion.Reduced snaps straight to the final state
    // instead of animating, per the app's one motion-reduction rule (Motion.cs:42). Shared by the work
    // order sell block and the Codex dossier's VALUE and long-tail expanders.
    private static void ExpandRows(FrameworkElement rows, bool animate)
    {
        rows.Visibility = Visibility.Visible;
        int gen = BumpRowsMotionGen(rows);
        if (!animate)
        {
            rows.BeginAnimation(FrameworkElement.HeightProperty, null);
            rows.BeginAnimation(UIElement.OpacityProperty, null);
            rows.Height = double.NaN;
            rows.Opacity = 1;
            return;
        }

        // Measure while Height is still Auto (NaN) FIRST, then pin Height = 0 to animate from. An
        // explicit Height feeds WPF's MinMax as an effective MaxHeight, so MeasureCore clamps
        // DesiredSize.Height to whatever Height already holds - measuring after setting Height = 0
        // (the original bug) always yields target = 0, a no-op DoubleAnimation(0, 0) that leaves the
        // rows invisible for the full 240ms and then pops to full size on the Completed NaN-restore.
        rows.BeginAnimation(FrameworkElement.HeightProperty, null);   // clear any prior collapse animation/explicit value
        rows.Height = double.NaN;
        double availableWidth = rows.Parent is FrameworkElement parent && parent.ActualWidth > 0
            ? parent.ActualWidth : double.PositiveInfinity;
        rows.Measure(new Size(availableWidth, double.PositiveInfinity));
        var target = rows.DesiredSize.Height;

        rows.Height = 0;
        rows.Opacity = 0;

        var dur = TimeSpan.FromMilliseconds(Motion.DrillMs);
        var heightAnim = new System.Windows.Media.Animation.DoubleAnimation(0, target, dur) { EasingFunction = Motion.Reveal };
        heightAnim.Completed += (_, _) =>
        {
            if (gen != (int)rows.GetValue(RowsMotionGenProperty)) return;   // superseded; this clock is orphaned
            rows.BeginAnimation(FrameworkElement.HeightProperty, null); rows.Height = double.NaN;
        };
        var fade = new System.Windows.Media.Animation.DoubleAnimation(0, 1, dur) { EasingFunction = Motion.Reveal };
        rows.BeginAnimation(FrameworkElement.HeightProperty, heightAnim);
        rows.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private static void CollapseRows(FrameworkElement rows, bool animate)
    {
        int gen = BumpRowsMotionGen(rows);
        if (!animate)
        {
            rows.BeginAnimation(FrameworkElement.HeightProperty, null);
            rows.BeginAnimation(UIElement.OpacityProperty, null);
            rows.Height = 0;
            rows.Opacity = 0;
            rows.Visibility = Visibility.Collapsed;
            return;
        }

        var current = double.IsNaN(rows.Height) ? rows.ActualHeight : rows.Height;
        rows.Height = current;
        var dur = TimeSpan.FromMilliseconds(Motion.DrillMs);
        var heightAnim = new System.Windows.Media.Animation.DoubleAnimation(current, 0, dur) { EasingFunction = Motion.Reveal };
        heightAnim.Completed += (_, _) =>
        {
            if (gen != (int)rows.GetValue(RowsMotionGenProperty)) return;   // superseded; this clock is orphaned
            rows.Visibility = Visibility.Collapsed;
            rows.BeginAnimation(FrameworkElement.HeightProperty, null);
            rows.Height = 0;
        };
        var fade = new System.Windows.Media.Animation.DoubleAnimation(1, 0, dur) { EasingFunction = Motion.Reveal };
        rows.BeginAnimation(FrameworkElement.HeightProperty, heightAnim);
        rows.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    // Coalesced rebuild trigger for the WorkOrders.CollectionChanged storm (a single save clears + re-adds
    // every order, firing one rebuild per notification). Mirrors ScheduleWorkOrderAnimations immediately
    // below: guard flag + Dispatcher.BeginInvoke at Loaded priority collapses any number of synchronous
    // notifications raised in the same dispatcher frame into exactly one RebuildWorkOrderList call.
    private void ScheduleWorkOrderRebuild()
    {
        if (_woRebuildQueued) return;
        _woRebuildQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _woRebuildQueued = false;
            RebuildWorkOrderList();
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // Coalesced deferred animation pass. LoadWorkOrders clears + re-adds every order, so a single save fires many
    // rebuilds; scheduling once (guarded) and running at Loaded priority lets us animate the final card set only.
    private void ScheduleWorkOrderAnimations()
    {
        if (_woAnimQueued) return;
        _woAnimQueued = true;
        Dispatcher.BeginInvoke(new Action(FlushWorkOrderAnimations), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void FlushWorkOrderAnimations()
    {
        _woAnimQueued = false;
        foreach (var wo in _vm.WorkOrders)
        {
            if (!_woCardParts.TryGetValue(wo.Id, out var parts)) continue;

            if (parts.PreReady) _woEverRefining.Add(wo.Id);

            // Card add (frozen: fade + rise 12px, 200ms, settle) - a genuinely new order, never on the first seed.
            bool isNew = !_knownWorkOrderIds.Contains(wo.Id);
            _knownWorkOrderIds.Add(wo.Id);
            if (_workOrdersSeeded && isNew) FadeRise(parts.Card);
            else parts.Card.Opacity = 1;   // undo any pre-hide for a card that will not fade

            // Ready flash: once per order, only for the genuine Refining -> Ready transition (seen refining first).
            if (parts.Status == WorkOrderStatus.ReadyToCollect
                && _woEverRefining.Contains(wo.Id)
                && _woFlashedReady.Add(wo.Id)
                && !Motion.Reduced && parts.ReadyStroke != null)
            {
                // Pill crossfade (frozen: 150ms - 75ms out, swap, 75ms in): outgoing REFINING chip dissolves to READY.
                var outgoingChip = Hud.StatusChip(WorkOrderStatus.Refining);
                outgoingChip.VerticalAlignment = VerticalAlignment.Center;
                outgoingChip.IsHitTestVisible = false;
                parts.ChipHost.Children.Add(outgoingChip);
                CrossfadePill(outgoingChip, parts.Chip);
                // Border flash (frozen: amber -> cyan at 45% -> resting, 400ms, ease-out, one shot).
                FlashBorder(parts.ReadyStroke, parts.ReadyStroke.Color);
                Logger.Info($"[UI] Refinery: order ready flash ({wo.Label})");
            }
        }
        _workOrdersSeeded = true;
    }

    // Always-present progress bar: an animated gradient fill for an active timer, else a static status bar
    // (green 100% ready, flat gray 100% complete, faint otherwise) matching the mock's per-state treatments.
    private UIElement BuildWorkOrderBar(WorkOrder wo)
    {
        if (wo.HasActiveTimer)
        {
            var sc = BrushFromHex(wo.StatusColorHex).Color;
            var grid = new Grid { Height = 6 };
            grid.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x1C, sc.R, sc.G, sc.B)) });
            var scale = new System.Windows.Media.ScaleTransform(System.Math.Clamp(wo.TimerFraction, 0, 1), 1);
            var fill = new Border
            {
                CornerRadius = new CornerRadius(3),
                Background = new System.Windows.Media.LinearGradientBrush(System.Windows.Media.Color.FromArgb(0xCC, sc.R, sc.G, sc.B), sc, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                RenderTransform = scale, RenderTransformOrigin = new System.Windows.Point(0, 0.5),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = sc, BlurRadius = 8, ShadowDepth = 0, Opacity = 0.55 },
            };
            grid.Children.Add(fill);
            var remaining = wo.TimerEnd!.Value - DateTime.UtcNow;
            if (remaining > TimeSpan.Zero)
                scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty,
                    new System.Windows.Media.Animation.DoubleAnimation(System.Math.Clamp(wo.TimerFraction, 0, 1), 1.0, remaining) { FillBehavior = System.Windows.Media.Animation.FillBehavior.HoldEnd });
            return grid;
        }

        return wo.Status switch
        {
            WorkOrderStatus.ReadyToCollect => Hud.StateBar(1, Hud.BarState.Green, 6),
            WorkOrderStatus.Complete       => Hud.StateBar(1, Hud.BarState.Gray, 6),
            WorkOrderStatus.Mining         => Hud.StateBar(System.Math.Clamp(wo.TimerFraction, 0, 1), Hud.BarState.Blue, 6),
            _                              => Hud.StateBar(System.Math.Clamp(wo.TimerFraction, 0, 1), Hud.BarState.Amber, 6),
        };
    }

    // The mock's dashed "+ Add work order" tile, as the last cell of the gallery (and the empty state).
    private FrameworkElement AddWorkOrderTile()
    {
        var dim = (System.Windows.Media.Brush)FindResource("FgDimBrush");
        var navBorder = (System.Windows.Media.Brush)FindResource("NavBorderBrush");
        var accent = (System.Windows.Media.Brush)FindResource("AccentBrush");

        var plus = new TextBlock { Text = "+", FontFamily = (System.Windows.Media.FontFamily)FindResource("DisplayFont"), FontSize = 26, FontWeight = FontWeights.Bold, Foreground = dim, HorizontalAlignment = HorizontalAlignment.Center };
        var label = new TextBlock { Text = "ADD WORK ORDER", FontFamily = (System.Windows.Media.FontFamily)FindResource("HeadFont"), FontSize = 11, FontWeight = FontWeights.Bold, Foreground = dim, Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(plus); stack.Children.Add(label);

        var dash = new System.Windows.Shapes.Rectangle
        {
            Stroke = navBorder, StrokeThickness = 1.2,
            StrokeDashArray = new System.Windows.Media.DoubleCollection { 4, 3 }, RadiusX = 3, RadiusY = 3,
        };
        var grid = new Grid { MinHeight = 118, Margin = new Thickness(7, 7, 7, 7), Cursor = System.Windows.Input.Cursors.Hand, Background = System.Windows.Media.Brushes.Transparent };
        grid.Children.Add(dash);
        grid.Children.Add(stack);
        grid.MouseEnter += (s, e) => { plus.Foreground = accent; dash.Stroke = accent; };
        grid.MouseLeave += (s, e) => { plus.Foreground = dim; dash.Stroke = navBorder; };
        grid.MouseLeftButtonDown += (s, e) => OpenWorkOrderEditor(new WorkOrder());
        return grid;
    }

    // Empty state: the ambient scan glyph + a hint + a single add tile.
    private FrameworkElement EmptyWorkOrders()
    {
        var dim = (System.Windows.Media.Brush)FindResource("FgDimBrush");
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 40, 0, 0) };
        var glyph = Hud.AmbientGlyph(Hud.Ambient.OreConveyor, 96);
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        glyph.Margin = new Thickness(0, 0, 0, 18);
        stack.Children.Add(glyph);
        stack.Children.Add(new TextBlock
        {
            Text = "No work orders yet. Add one to track refine timers.",
            Foreground = dim, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 0, 0, 16),
        });
        var tile = AddWorkOrderTile();
        tile.Width = 260;
        tile.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(tile);
        return stack;
    }

}
