using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using NexusApp.Services;
using NexusApp.Services.Map;

namespace NexusApp.Views;

public sealed partial class TradePage
{
    private TextBox _commodityBox = null!;
    private TextBox _qtyBox = null!;
    private Border? _pickerMenu;
    private StackPanel _pickerGrp = null!;        // the commodity box plus the search picker below it
    private ContentControl _prefillSlot = null!;   // the APPLY WORK ORDER chip's slot (chip presence is data-dependent)
    private string? _prefillChipName;              // the commodity the CURRENT chip names; null = no chip rendered

    // Input area built ONCE, results the only thing rebuilt - same reasoning as the planner's
    // (TradePage.Planner.cs): the qty box's own LostFocus fires synchronously inside the mouse
    // handling of whatever was clicked, and the old Host.Children.Clear() destroyed the commodity
    // box, the open picker and the APPLY WORK ORDER chip mid-click.
    private StackPanel _sellInputs = null!;
    private StackPanel _sellResults = null!;

    // Keyed by buyer TERMINAL NAME, not by row index (mock index.html keys the same way): sell rows
    // re-rank whenever the quantity or a new SCT snapshot changes the effective values, so an index
    // would point at whatever row later took that slot. A string key survives a re-rank and keeps
    // the band the user opened open. null = nothing expanded.
    private string? _sellExpanded;

    // A programmatic write to the commodity box (the picker committing a choice, or item H's
    // fallback write-back) must not be read as the user typing: TextChanged would otherwise reopen
    // the search picker on top of a selection that was just made.
    private bool _suppressCommodityText;

    // Session-typed commodity/quantity (architect resolution for this task: in-memory page
    // fields, not AppSettings - same precedent as TradePage.Planner.cs's _budgetText.
    // AppSettings' fixed Trade* contract (TradeActiveFlow/TradeShipId/TradeOriginManual/
    // TradeScope/TradeAnchorFromHere) has no TradeSellCommodity/TradeSellQty and this task does
    // not add any; both fields reset each session, which affects nothing else in this file.
    private string _sellCommodityText = "";
    private string _sellQtyText = "";

    // Reentrancy guard for the qty box's live TextChanged re-rank below (item C) - RebuildSell
    // never writes back to _qtyBox.Text itself, so nothing today re-enters this handler, but the
    // guard is cheap insurance against a future write-back (or IME composition) looping back in.
    private bool _inQtyLiveRerank;

    private string SellCommodity => _sellCommodityText;
    private int SellQty => int.TryParse(new string((_qtyBox?.Text ?? "").Where(char.IsDigit).ToArray()), out var n) ? n : 0;

    // Every commodity currently priced anywhere in the snapshot, alphabetical: the list the search
    // picker offers and the list RebuildSell resolves the pick against. Read fresh on every use
    // (the picker's keystroke handler included) rather than captured once, since the inputs it feeds
    // now outlive any single snapshot.
    private static List<(int CommodityId, string CommodityName)> CommodityChoices() =>
        App.Market.Snapshot?.TradePrices.Rows
            .Select(r => (r.CommodityId, r.CommodityName))
            .Distinct()
            .OrderBy(c => c.CommodityName, StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<(int CommodityId, string CommodityName)>();

    private void BuildSellChrome()
    {
        if (_sellInputs is not null) return;

        _sellInputs = new StackPanel();
        var inputRow = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };

        _pickerGrp = new StackPanel { Width = 220, Margin = new Thickness(0, 0, 16, 0) };
        _pickerGrp.Children.Add(FieldLabel("Commodity"));
        _commodityBox = new TextBox { Style = (Style)Application.Current.FindResource("NexusTextBox"), Text = SellCommodity };
        _commodityBox.TextChanged += (_, _) => { if (!_suppressCommodityText) ShowCommodityPicker(); };
        _commodityBox.LostFocus += (_, _) => { /* commit happens via picker item click, not free text - see ShowCommodityPicker */ };
        _pickerGrp.Children.Add(_commodityBox);
        inputRow.Children.Add(_pickerGrp);

        var qtyGrp = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        qtyGrp.Children.Add(FieldLabel("Quantity"));
        _qtyBox = new TextBox
        {
            Style = (Style)Application.Current.FindResource("NexusTextBox"), FontFamily = Hud.Font("MonoFont"),
            Width = 110, Text = _sellQtyText,
        };
        _qtyBox.LostFocus += (_, _) =>
        {
            if (_sellQtyText == _qtyBox.Text) return;   // guard, same pattern as Planner's budget box: no-op blur never logs or rebuilds
            _sellQtyText = _qtyBox.Text;
            Logger.Info("[UI] Trade sell: quantity updated");
            RebuildSell();   // results only: the control the user just clicked is still alive
        };
        // Live re-rank per keystroke (owner's live-pass ask, 2026-07-30, item C; mock index.html:918,
        // onChange re-ranks immediately - was LostFocus-only here, so nothing ranked until the user
        // typed a quantity AND clicked elsewhere). RebuildSell only ever clears/repopulates
        // _sellResults, never _sellInputs (this box's own parent, built once - see this method's
        // opening comment), so this can never recreate the box the user is typing into or steal its
        // focus/caret. Deliberately leaves _sellQtyText and the log line alone: SellQty already
        // reads _qtyBox.Text directly (not _sellQtyText), so a rebuild here re-ranks against the
        // box's live text with no extra bookkeeping - the one "quantity updated" log line stays
        // exclusively on LostFocus-with-change above, so typing does not spam the log.
        _qtyBox.TextChanged += (_, _) =>
        {
            if (_inQtyLiveRerank) return;
            _inQtyLiveRerank = true;
            try { RebuildSell(); }
            finally { _inQtyLiveRerank = false; }
        };
        qtyGrp.Children.Add(_qtyBox);
        inputRow.Children.Add(qtyGrp);

        // Bottom-aligned like the chip it holds, and zero-sized while empty, so the input row lays
        // out identically whether or not there is a work order to apply.
        _prefillSlot = new ContentControl { VerticalAlignment = VerticalAlignment.Bottom, Focusable = false };
        inputRow.Children.Add(_prefillSlot);

        _sellInputs.Children.Add(inputRow);
        _sellInputs.Children.Add(new TextBlock
        {
            Text = "Ranked by effective value for your load. Bars show trip coverage.",   // architect resolution caption, verbatim
            FontFamily = Hud.Font("UiFont"), FontSize = 10.5, Foreground = Hud.Br("FgDimBrush"), Margin = new Thickness(0, 0, 0, 14),
        });

        _sellResults = new StackPanel();
        SellHost.Children.Add(_sellInputs);
        SellHost.Children.Add(_sellResults);
    }

    // The chip's presence and label are data-dependent (latest completed work order, resolved
    // against the currently-priced commodities), so the slot is refreshed on every results rebuild -
    // but ONLY when the answer actually changed. Re-creating an unchanged chip would put the wave's
    // own mid-click bug back: a rebuild triggered by the quantity box's LostFocus fires while the
    // click that caused it is still resolving on the chip.
    private void RefreshPrefillChip(MarketSnapshot? snap, List<(int CommodityId, string CommodityName)> commodities)
    {
        string? name = TryGetPrefill(snap, commodities, out var prefillCommodity) ? prefillCommodity : null;
        if (name == _prefillChipName) return;
        _prefillChipName = name;
        if (name is null) { _prefillSlot.Content = null; return; }

        var chip = new Border
        {
            BorderBrush = Hud.Br("AccentStrongBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 6, 12, 6), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Bottom,
            Child = new TextBlock { Text = $"APPLY WORK ORDER: {name}", FontFamily = Hud.Font("UiFont"), FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Hud.Br("AccentBrush") },
            // Deviates from the mock's verbatim tooltip (trading-tab/index.html:924, "Prefills
            // the commodity and quantity fields..."): WorkOrder carries no output-quantity
            // field (WorkOrderPrefill.cs), so this task's architect resolution has the chip
            // fill the COMMODITY ONLY - a recorded mock deviation. The tooltip is reworded to
            // match what the chip actually does rather than promise a quantity prefill that
            // never happens.
            ToolTip = "Prefills the commodity field from your latest completed work order.",
        };
        chip.MouseLeftButtonUp += (_, _) =>
        {
            SetCommodity(name);
            Logger.Info($"[UI] Trade sell: prefilled from work order ({name})");
            RebuildSell();
        };
        _prefillSlot.Content = chip;
    }

    /// <summary>Commits a commodity choice: the field the ranking reads AND the box the user sees,
    /// always together (the UI must never name one commodity while ranking another). The box write
    /// is suppressed so it does not reopen the search picker, and any open picker closes - the
    /// commodity has just been decided, so a stale filtered list under the box would be wrong.</summary>
    private void SetCommodity(string commodityName)
    {
        _sellCommodityText = commodityName;
        _suppressCommodityText = true;
        try { _commodityBox.Text = commodityName; }
        finally { _suppressCommodityText = false; }
        CloseCommodityPicker();
    }

    private void RebuildSell()
    {
        BuildSellChrome();
        if (!EnsureMarketConsent(_sellResults, _sellInputs)) return;
        _sellResults.Children.Clear();
        _sellPinChips.Clear();   // the chips belonged to the rows just dropped (same rule as the planner's)

        var snap = App.Market.Snapshot;
        var commodities = CommodityChoices();
        RefreshPrefillChip(snap, commodities);

        if (snap is null || commodities.Count == 0) { _sellResults.Children.Add(EmptyOrStaleNote(snap?.TradePrices.FetchedUtc)); return; }

        var picked = commodities.FirstOrDefault(c => string.Equals(c.CommodityName, SellCommodity, StringComparison.OrdinalIgnoreCase));
        if (picked == default)
        {
            // The chosen commodity is not in this snapshot (nothing chosen yet, or an hourly refresh
            // dropped it). Ranking falls back to the first commodity, and the box is corrected to
            // match: it used to keep showing the stale name while the rows below ranked something
            // else. Guarded on a real change so a settled selection never re-writes the box (which
            // would throw away text the user is still typing).
            picked = commodities[0];
            if (!string.Equals(_sellCommodityText, picked.CommodityName, StringComparison.Ordinal))
                SetCommodity(picked.CommodityName);
        }
        int qty = SellQty;
        if (qty <= 0) { _sellResults.Children.Add(new TextBlock { Text = "Enter a quantity to rank buyers.", FontFamily = Hud.Font("UiFont"), FontSize = 12.5, Foreground = Hud.Br("FgDimBrush") }); return; }

        // Terminal lookup, built once per rebuild: TerminalId -> MarketTerminal. Reused below both
        // for origin resolution and for each ranked buyer row's System tag.
        var terminals = snap.Terminals.Rows.ToDictionary(t => t.Id);
        var originIds = OriginTerminalIds(snap.Terminals.Rows);
        // SellLookup takes ONE optional origin, not a set. Every terminal of one location shares the
        // same hierarchy (system/orbit/planet), so any of them derives the same proximity tier -
        // taking the first is correct for a multi-terminal station, where collapsing to null used to
        // force every buyer to CROSS-SYSTEM. null only for a genuinely empty set.
        int? originId = originIds.Count >= 1 ? originIds.First() : null;
        var buyers = SellLookup.Rank(snap.TradePrices.Rows, terminals, picked.CommodityId, qty, originId, App.Settings.Current.TradeScope);

        if (buyers.Count == 0) { _sellResults.Children.Add(new TextBlock { Text = $"No buyers found for {picked.CommodityName} in this scope.", FontFamily = Hud.Font("UiFont"), FontSize = 12.5, Foreground = Hud.Br("FgDimBrush") }); return; }

        // Origin terminal for the distance tag (owner's ask, 2026-07-30): resolved once per rebuild
        // from the same originId the ranking itself already used, not a second origin lookup.
        MarketTerminal? originTerm = originId is { } oid && terminals.TryGetValue(oid, out var ot) ? ot : null;

        // Sell-only pins for THIS commodity get fresh names and prices from the ranking just run,
        // mirroring RebuildPlanner's RefreshPins call (RoutePlanner.RefreshSellPins owns the rules,
        // including never touching a pin's quantity).
        RefreshSellPinFacts(buyers.Select(b => b.Row).ToList());

        for (int i = 0; i < buyers.Count; i++)
        {
            var row = BuildBuyerRow(buyers[i], qty, terminals, originTerm);
            CascadeIn(row, i);
            _sellResults.Children.Add(row);
        }

        // SCT-only rows: listings SCT has at mapped terminals where UEX has no matching row at
        // all (SctMarketService.SctOnlyBuyers). Always appended AFTER the ranked buyer list, never
        // merged into the ranking (mock:887-890) - and only when there is a ranked list to append
        // after (a zero-buyer commodity keeps the existing "no buyers" empty state above, rather
        // than growing a second, disconnected empty-state branch for this narrow case).
        var sctOnly = App.Settings.Current.SctDataEnabled
            ? App.Sct.SctOnlyBuyers(picked.CommodityId).ToList()
            : new List<SctListing>();
        for (int i = 0; i < sctOnly.Count; i++)
        {
            int idx = buyers.Count + i;
            var row = BuildSctOnlyBuyerRow(sctOnly[i], qty);
            CascadeIn(row, idx);
            _sellResults.Children.Add(row);
        }

        string sctSuffix = App.Settings.Current.SctDataEnabled ? $", sctOnly {sctOnly.Count}" : "";
        Logger.Info($"[UI] Trade sell run: {buyers.Count} buyers, commodity {picked.CommodityName}, qty {qty}, scope {App.Settings.Current.TradeScope}{sctSuffix}");
    }

    // Search-first picker: filters the full commodity list against the box's live text (case-
    // insensitive substring, top 8), rebuilt on every keystroke. Selection commits only via an
    // item click (MouseLeftButtonUp), never via free text or LostFocus - that ordering matters:
    // clicking an item first steals focus from the TextBox (firing its LostFocus), and if that
    // handler tore the menu down there, the pending click on the now-detached item would never
    // resolve. Leaving LostFocus a no-op (see BuildSellChrome above) is what keeps the click reliable.
    private void ShowCommodityPicker()
    {
        CloseCommodityPicker();
        var query = _commodityBox.Text ?? "";
        var matches = CommodityChoices().Where(c => c.CommodityName.Contains(query, StringComparison.OrdinalIgnoreCase)).Take(8).ToList();
        if (matches.Count == 0) return;
        var list = new StackPanel();
        foreach (var m in matches)
        {
            var item = new Border { Padding = new Thickness(12, 8, 12, 8), Cursor = Cursors.Hand, Child = new TextBlock { Text = m.CommodityName, FontFamily = Hud.Font("UiFont"), FontSize = 12.5, Foreground = Hud.Br("FgBrush") } };
            item.MouseEnter += (_, _) => item.Background = Hud.Br("AccentFaintBrush");
            item.MouseLeave += (_, _) => item.Background = Brushes.Transparent;
            item.MouseLeftButtonUp += (_, _) =>
            {
                // Box, field and picker all together (SetCommodity): the box is built once now, so a
                // commit has to write the full name into it here - the rebuild no longer re-creates
                // the box from the field, nor destroys the menu as a side effect of clearing the host.
                SetCommodity(m.CommodityName);
                Logger.Info($"[UI] Trade sell: commodity {m.CommodityName}");
                RebuildSell();
            };
            list.Children.Add(item);
        }
        _pickerMenu = new Border
        {
            Background = Hud.Br("Bg2NavBrush"), BorderBrush = Hud.Br("NavBorderBrush"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(0, 0, 6, 6), Child = list,
        };
        _pickerGrp.Children.Add(_pickerMenu);
    }

    private void CloseCommodityPicker()
    {
        if (_pickerMenu is null) return;
        _pickerGrp.Children.Remove(_pickerMenu);
        _pickerMenu = null;
    }

    // Prefill resolution, per this task's architect override of the brief's ASSUMED
    // WorkOrderPrefillResolver: WorkOrderPrefill.LatestCompleted picks the order, then
    // ResolveCommodityId resolves its free-text Resources field to a UEX commodity id against the
    // FULL commodity catalog (snap.Commodities.Rows - the same chain the Codex/work-order sell
    // hints already use). WorkOrder carries no output-quantity field, so this fills the commodity
    // only (recorded mock deviation) - the Sell flow's quantity is always the user's own entry.
    //
    // The chip is absent entirely (returns false) unless the resolved id is ALSO present in
    // `commodities` - the narrower list built from TradePrices that the search picker itself
    // offers and that RebuildSell's own picked-commodity lookup matches against. A commodity id
    // that resolves in the full catalog but isn't currently priced/traded would apply a work
    // order and silently land on the alphabetically-first commodity instead (RebuildSell's
    // fallback below), which is worse than showing no chip at all.
    private static bool TryGetPrefill(MarketSnapshot? snap, List<(int CommodityId, string CommodityName)> commodities, out string commodityName)
    {
        commodityName = "";
        if (snap is null) return false;

        var order = WorkOrderPrefill.LatestCompleted(App.Data.GetWorkOrders());
        if (order is null) return false;

        var commodityId = WorkOrderPrefill.ResolveCommodityId(order, snap.Commodities.Rows);
        if (commodityId is null) return false;

        foreach (var c in commodities)
        {
            if (c.CommodityId == commodityId.Value) { commodityName = c.CommodityName; return true; }
        }
        return false;
    }

    private FrameworkElement BuildBuyerRow(SellLookup.Buyer b, int qty, Dictionary<int, MarketTerminal> terminals, MarketTerminal? originTerm)
        => WrapBuyerRow(BuildBuyerRowContent(b, qty, terminals, originTerm, out var chevron, out var detailHost), chevron, detailHost,
                        b.Row.TerminalName, sctOnly: false);

    // SCT-only row: a listing SCT has at a terminal UEX has no row for at all. Same row chrome,
    // de-emphasized (mock .buyerRow.sctonly, index.html:286/935: opacity 0.82 + the frame's
    // stroke swapped to the "line-strong" token instead of the normal one). Its expansion key is the
    // SCT location, which by definition names no UEX terminal, so it can never collide with a
    // ranked buyer's terminal name.
    private FrameworkElement BuildSctOnlyBuyerRow(SctListing s, int qty)
        => WrapBuyerRow(BuildSctOnlyBuyerRowContent(s, qty, out var chevron, out var detailHost), chevron, detailHost,
                        s.Location, sctOnly: true);

    private FrameworkElement WrapBuyerRow(UIElement content, Path chevron, Border detailHost, string key, bool sctOnly)
    {
        var host = Hud.CardFrame(content, out var framePath, out _, chamfer: 8, padding: new Thickness(18, 13, 18, 13));
        if (sctOnly) framePath.Stroke = Hud.Br("BorderBrush");   // mock:935, stroke=lineStrong for sctOnly rows
        host.Children.Add(PositionChevron(chevron));
        var wrapper = new Border { Cursor = Cursors.Hand, Child = host, Margin = new Thickness(0, 0, 0, 10) };
        if (sctOnly) wrapper.Opacity = 0.82;   // mock:286, single-source de-emphasis
        // Restored, not animated: this row was already open before the rows re-ranked, so the band
        // and the chevron start in their open state rather than replaying the expand.
        if (string.Equals(_sellExpanded, key, StringComparison.Ordinal))
        {
            detailHost.Visibility = Visibility.Visible;
            chevron.RenderTransform = new RotateTransform(90);
        }
        wrapper.MouseLeftButtonUp += (_, _) =>
        {
            bool nowOpen = !string.Equals(_sellExpanded, key, StringComparison.Ordinal);
            _sellExpanded = nowOpen ? key : null;
            detailHost.Visibility = nowOpen ? Visibility.Visible : Visibility.Collapsed;
            SetChevronOpen(chevron, nowOpen);
        };
        return wrapper;
    }

    private UIElement BuildBuyerRowContent(SellLookup.Buyer b, int qty, Dictionary<int, MarketTerminal> terminals, MarketTerminal? originTerm, out Path chevron, out Border detailHost)
    {
        terminals.TryGetValue(b.Row.TerminalId, out var term);
        string? system = term?.System;
        // Owner's ask, 2026-07-30 (decorating beyond the approved mock): the real gigameter
        // distance from the current origin to this buyer, only when both resolve on the starmap
        // in the same system - DistanceMeters already encodes both the resolution and the
        // same-system gate, so this is a single call, not a duplicated check.
        double? distanceMeters = _starmap.DistanceMeters(originTerm, term);

        // PIN TO OVERLAY on sell rows (the owner, 2026-08-01), same chip and same repaint-in-place
        // rules as the planner's: the click never rebuilds, e.Handled keeps it off the row's own
        // expand toggle, and RefreshPinChips repaints EVERY chip since a cap eviction can dim a
        // chip on either tab. UEX rows only - an SCT-only listing has no terminal id to pin.
        var pinChip = PinChip(IsSellPinned(b.Row.TerminalId, b.Row.CommodityId), sellOnly: true);
        _sellPinChips.Add((b.Row.TerminalId, b.Row.CommodityId, pinChip));
        var pinnedRow = b.Row;
        int pinnedQty = qty;
        pinChip.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            PinSellRow(pinnedRow, pinnedQty);
            RefreshPinChips();
        };

        return BuildBuyerRowCore(b.Row.TerminalName, system, b.Row.Sell, b.Tier, b.Row.SellDemandScu, b.Row.ModifiedUtc,
            b.EffectiveValue, qty, CorroborationBadge(Reconcile(b.Row, "sell")), distanceMeters, b.Row.ContainerSizes, out chevron, out detailHost, pinChip);
    }

    // Tier omitted (never a placeholder): SctOnlyBuyers only ever returns listings at stations
    // with NO UEX terminal id at all - both CommodityId and RawId null in the map, which is
    // exactly what qualifies them as "SCT-only" in the first place (SctMarketService.
    // SctOnlyBuyers doc comment). There is no UEX terminal to resolve a ProximityTiers.Derive
    // pair against, so this is not a reachability shortcut, it is genuinely unresolvable for
    // these rows. The mock's sample data (CROSS-SYSTEM) is invented demo flavor, not derived from
    // any real resolution path.
    private UIElement BuildSctOnlyBuyerRowContent(SctListing s, int qty, out Path chevron, out Border detailHost)
    {
        double effectiveValue = Math.Min(qty, s.Quantity) * s.Price;
        var badge = CorroborationBadge(SctOnlyReconciled(s));
        // system: null - SCT-only listings have no UEX terminal id to resolve a System from
        // (BuildSctOnlyBuyerRowContent's doc comment above), so the tag is correctly omitted.
        // distanceMeters: null, always - same reason, and the spec is explicit that SCT-only rows
        // never carry a distance tag (they have no UEX terminal to resolve a starmap position from).
        // containerSizes: null, always - SctListing carries no container-size data (it is not a
        // UEX TradePriceRow), so the max-container chip is correctly omitted for these rows too.
        return BuildBuyerRowCore(s.Location, null, s.Price, null, s.Quantity, s.TimestampUtc, effectiveValue, qty, badge, null, null, out chevron, out detailHost);
    }

    // Shared buyer-row layout: real UEX buyers (SellLookup.Buyer) and SCT-only listings both
    // render through this one builder so the row chrome lives once. tier is null for SCT-only
    // rows (chip omitted, see BuildSctOnlyBuyerRowContent); badge is whatever CorroborationBadge
    // returned for the caller's reconciled/synthesized state, or null to render none.
    //
    // Location-first display NOT applied to terminalName here (the owner's ask, 2026-07-31 review):
    // callers feed this from TradePriceRow.TerminalName (BuildBuyerRowContent's b.Row.TerminalName)
    // or an SCT Location string, both a different UEX vocabulary from MarketTerminal.Name that
    // TradeOriginResolver.LocationFirst's " - " rule was verified against - see the same note on
    // TradePage.Prices.cs's BuildPriceRow.
    private UIElement BuildBuyerRowCore(string terminalName, string? system, double price, ProximityTier? tier, int demandScu,
        DateTime priceUtc, double effectiveValue, int qty, FrameworkElement? badge, double? distanceMeters, string? containerSizes,
        out Path chevron, out Border detailHost, Border? pinChip = null)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var left = new StackPanel();
        // top is already a horizontal StackPanel (the house idiom's "same horizontal container"
        // case) - the tag slots in right after the terminal name, before price/tier/badge.
        var top = new StackPanel { Orientation = Orientation.Horizontal };
        top.Children.Add(new TextBlock { Text = terminalName, FontFamily = Hud.Font("UiFont"), FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Hud.Br("FgBrush"), Margin = new Thickness(0, 0, 10, 0) });
        if (SystemTag(system) is { } tag) { tag.Margin = new Thickness(0, 0, 10, 1); top.Children.Add(tag); }
        top.Children.Add(new TextBlock { Text = $"{price:n0}/SCU", FontFamily = Hud.Font("MonoFont"), FontSize = 12.5, Foreground = Hud.Br("GoldBrush"), Margin = new Thickness(0, 0, 10, 0) });
        if (tier is { } t) top.Children.Add(TierChip(t));
        if (distanceMeters is { } dm && DistanceTag(MapCatalog.FormatGm(dm)) is { } distTag) top.Children.Add(distTag);
        if (badge is not null) { badge.Margin = new Thickness(8, 0, 0, 0); top.Children.Add(badge); }
        left.Children.Add(top);

        var meta = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 0) };
        var bar = new Grid { Width = 90 };
        string barTier = TradeBarMath.Tier(demandScu, qty);
        bar.Children.Add(TripBar(TradeBarMath.FillFraction(demandScu, qty), TradeBarMath.Color(barTier),
            "This bar shows how much of your trip the demand covers. Green: covers your full trip. Amber: covers at least half. Red: less than half."));
        meta.Children.Add(bar);
        meta.Children.Add(new TextBlock { Text = $"DEMAND {demandScu:n0} SCU", FontFamily = Hud.Font("MonoFont"), FontSize = 10, Foreground = Hud.Br("FgDimBrush"), Margin = new Thickness(8, 0, 8, 0) });
        var age = DateTime.UtcNow - priceUtc;
        meta.Children.Add(FreshChip(FreshChipAge(age), age.TotalHours >= 24));   // shared idiom, see FreshChipAge
        // Max container size (task 2): the sell flow has no ship in scope here, so this is always
        // the plain dim tag, never the AccentBrush warning tint - that only applies on planner legs.
        if (MaxContainerChip(TradeMath.MaxContainerScu(containerSizes ?? "")) is { } maxChip) meta.Children.Add(maxChip);
        if (pinChip is not null) meta.Children.Add(pinChip);
        left.Children.Add(meta);
        Grid.SetColumn(left, 0); Grid.SetRow(left, 0);
        grid.Children.Add(left);

        var right = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(new TextBlock { Text = "EFFECTIVE VALUE", FontFamily = Hud.Font("UiFont"), FontSize = 9, FontWeight = FontWeights.Bold, Foreground = Hud.Br("FgDimBrush"), HorizontalAlignment = HorizontalAlignment.Right });
        var valRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        valRow.Children.Add(new TextBlock { Text = effectiveValue.ToString("n0", CultureInfo.InvariantCulture), FontFamily = Hud.Font("MonoFont"), FontSize = 21, Foreground = Hud.Br("AccentBrush"), Effect = new DropShadowEffect { Color = Hud.Col("AccentBrush"), BlurRadius = 12, ShadowDepth = 0, Opacity = 0.35 } });
        valRow.Children.Add(new TextBlock { Text = " aUEC", FontFamily = Hud.Font("UiFont"), FontSize = 10, Foreground = Hud.Br("FgDimBrush"), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(4, 0, 0, 3) });
        right.Children.Add(valRow);
        // Sublabel per architect resolution: "for your {qty} SCU" plus ", capped by demand {n}"
        // ONLY when demand is strictly less than qty (never at demand == qty - that is full,
        // uncapped coverage).
        string sub = $"for your {qty:n0} SCU" + (demandScu < qty ? $", capped by demand {demandScu:n0}" : "");
        right.Children.Add(new TextBlock { Text = sub, FontFamily = Hud.Font("UiFont"), FontSize = 10, Foreground = Hud.Br("FgDimBrush"), HorizontalAlignment = HorizontalAlignment.Right });
        right.ToolTip = "Price times sellable quantity, capped by the buyer's demand.";   // mock:971, verbatim
        Grid.SetColumn(right, 1); Grid.SetRowSpan(right, 2); Grid.SetRow(right, 0);
        grid.Children.Add(right);

        detailHost = DetailBand(new Thickness(0, 10, 0, 0), new Thickness(0, 10, 0, 0));
        detailHost.Child = new TextBlock
        {
            Text = $"Effective value = min({qty:n0}, demand {demandScu:n0}) SCU x {price:n0} aUEC/SCU = {effectiveValue:n0} aUEC",   // mock:979, verbatim format
            FontFamily = Hud.Font("UiFont"), FontSize = 11.5, Foreground = Hud.Br("FgDimBrush"),
        };
        Grid.SetRow(detailHost, 1); Grid.SetColumnSpan(detailHost, 2);
        grid.Children.Add(detailHost);

        chevron = ChevronGlyph();
        return grid;
    }
}
