using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using NexusApp.Services;

namespace NexusApp.Views;

public sealed partial class TradePage
{
    // PriceRowItem (exactly one of Uex/Sct populated per row) now lives in Services/PriceSort.cs,
    // alongside the sort helper itself - moved out of this class (owner live-pass ask, 2026-07-30,
    // item 2) so PriceSort.SortRows and its unit tests can build rows without a WPF UserControl.

    private ComboBox _pricesCommodityCombo = null!;
    private readonly bool[] _priceCols = { true, true, true, false };   // STOCK, STATUS, AGE, +WEEK AVG - session only, not persisted (task brief: "persist nothing new")
    private static readonly string[] PriceColLabels = { "STOCK", "STATUS", "AGE", "+WEEK AVG" };
    private string? _pricesSelectedCommodity;

    // MAP tab terminal filter (Task 8): set only by ShowPricesForTerminal, session-only (same
    // reasoning as _priceCols/_pricesSortColumn above - not part of AppSettings' fixed contract
    // and not something a "last state" restore should reapply on its own). Coexists with a chosen
    // commodity (both filters AND together); when no commodity is chosen while this is set, the
    // results show every commodity traded at that one terminal instead of the usual "one commodity,
    // every terminal" view - see RefreshPricesCommodityBox and RebuildPrices below.
    private int? _pricesTerminalFilter;

    // Sort state (owner live-pass ask, 2026-07-30, item 2): session-only, not persisted - same
    // reasoning as _priceCols above. Default Sell descending is the pre-existing behavior, with
    // the Sell header visually marked active from first paint.
    private PriceSortColumn _pricesSortColumn = PriceSortColumn.Sell;
    private bool _pricesSortDescending = true;

    // Input area (commodity ComboBox + the four column-toggle chips) built ONCE, results the only
    // thing rebuilt - same reasoning as the other two flows. The chips' visuals and the ComboBox's
    // items are updated in place from here on, so a column toggle or an hourly refresh no longer
    // rebuilds the control the user is interacting with (an open dropdown included).
    private StackPanel _pricesInputs = null!;
    private StackPanel _pricesResults = null!;
    private List<string>? _pricesCommodityNames;   // the list currently bound to the ComboBox
    private bool _suppressPricesSelection;          // in-place ItemsSource/SelectedItem writes are not user picks

    private void BuildPricesChrome()
    {
        if (_pricesInputs is not null) return;

        _pricesInputs = new StackPanel();

        // Owner's live-pass ask (2026-07-30, round 3): the CONTROL itself must anchor to the pane's
        // left edge, under the ORIGIN pill. An explicit-Width child of a vertical StackPanel keeps
        // its default Stretch alignment and is therefore CENTERED in the available width - the
        // Planner/Sell inputs dodge this only because they live in horizontal rows. Pin it Left.
        var pickerGrp = new StackPanel { Width = 220, Margin = new Thickness(0, 0, 0, 16), HorizontalAlignment = HorizontalAlignment.Left };
        pickerGrp.Children.Add(FieldLabel("Commodity"));
        // Left-aligned text (owner's live-pass ask, 2026-07-30, item 3): fixed at this usage site
        // only, NOT in the shared NexusComboBox style (GameTheme.xaml) - other pages depend on that
        // style's current look. ItemTemplate below still drives the OPEN dropdown rows correctly
        // (ComboBoxItem's own default template binds its ContentPresenter's ContentTemplate via a
        // direct, ordinary TemplateBinding to ItemTemplate - reliable), but the owner confirmed this
        // alone did NOT fix the CLOSED box: that box is painted by a different, unnamed-in-XAML path
        // (GameTheme.xaml:548-553, x:Name="contentPresenter" - Content/ContentTemplate bound to
        // SelectionBoxItem/SelectionBoxItemTemplate, which WPF mirrors from ItemTemplate itself,
        // off-XAML, inside ComboBox's private UpdateSelectionBoxItem() on selection-changed events -
        // nothing in this file or GameTheme.xaml declares that mirror, so its timing is invisible
        // and unverifiable from here, and evidently did not paint the closed box left-aligned live).
        // Item E's real fix (below, on Loaded) stops depending on that mirror: it grabs the actual
        // "contentPresenter" element once the template has applied and forces it left directly, so
        // the closed box renders correctly regardless of whether SelectionBoxItemTemplate ever gets
        // populated.
        _pricesCommodityCombo = new ComboBox
        {
            Style = (Style)Application.Current.FindResource("NexusComboBox"),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            ItemTemplate = LeftAlignedComboItemTemplate,
        };
        _pricesCommodityCombo.Loaded += (_, _) =>
        {
            if (_pricesCommodityCombo.Template?.FindName("contentPresenter", _pricesCommodityCombo) is ContentPresenter cp)
                cp.HorizontalAlignment = HorizontalAlignment.Left;
        };
        _pricesCommodityCombo.SelectionChanged += (_, _) =>
        {
            if (_suppressPricesSelection) return;
            _pricesSelectedCommodity = _pricesCommodityCombo.SelectedItem as string;
            if (_pricesSelectedCommodity is not null) Logger.Info($"[UI] Trade prices: commodity {_pricesSelectedCommodity}");
            RebuildPrices();
        };
        pickerGrp.Children.Add(_pricesCommodityCombo);
        _pricesInputs.Children.Add(pickerGrp);

        var toggles = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        for (int i = 0; i < PriceColLabels.Length; i++)
        {
            int idx = i;
            var chip = ColumnToggleChip(PriceColLabels[i], _priceCols[i]);
            chip.MouseLeftButtonUp += (_, _) =>
            {
                _priceCols[idx] = !_priceCols[idx];
                SetColumnChipOn(chip, _priceCols[idx]);   // the chip itself lives on: retint it in place
                Logger.Info($"[UI] Trade prices: column {PriceColLabels[idx]} {(_priceCols[idx] ? "ON" : "OFF")}");
                RebuildPrices();
            };
            toggles.Children.Add(chip);
        }
        _pricesInputs.Children.Add(toggles);

        _pricesResults = new StackPanel();
        PricesHost.Children.Add(_pricesInputs);
        PricesHost.Children.Add(_pricesResults);
    }

    // Re-validate on every rebuild (Task 14 rule, and the Sell flow now does the same): an hourly
    // snapshot refresh can drop the previously selected commodity, and a one-time seed would leave
    // the field stuck on a name no longer in `commodities`, producing a blank ComboBox selection and
    // a permanent "0 terminals" render. The ComboBox is updated in place afterwards so the box
    // visibly shows the commodity that was actually rendered - suppressed, since neither write is a
    // user pick, and skipped entirely when nothing changed (an open dropdown must survive a tick).
    //
    // Task 8 addition: while the MAP tab's terminal filter is active, an invalid/null selection
    // falls back to null (no forced pick) instead of the first commodity - that null is the
    // deliberate "every commodity at this terminal" state ShowPricesForTerminal puts the page into,
    // and forcing a default here would silently narrow it back down to one commodity on the very
    // next refresh. Outside terminal-filter mode this is unchanged from before.
    private void RefreshPricesCommodityBox(List<string> commodities)
    {
        bool stillValid = _pricesSelectedCommodity is not null
            && commodities.Any(c => string.Equals(c, _pricesSelectedCommodity, StringComparison.OrdinalIgnoreCase));
        if (!stillValid)
            _pricesSelectedCommodity = _pricesTerminalFilter is null ? commodities.FirstOrDefault() : null;

        _suppressPricesSelection = true;
        try
        {
            if (_pricesCommodityNames is null || !_pricesCommodityNames.SequenceEqual(commodities, StringComparer.Ordinal))
            {
                _pricesCommodityNames = commodities;
                _pricesCommodityCombo.ItemsSource = commodities;
            }
            if (!string.Equals(_pricesCommodityCombo.SelectedItem as string, _pricesSelectedCommodity, StringComparison.Ordinal))
                _pricesCommodityCombo.SelectedItem = _pricesSelectedCommodity;
        }
        finally { _suppressPricesSelection = false; }
    }

    private void RebuildPrices()
    {
        BuildPricesChrome();
        if (!EnsureMarketConsent(_pricesResults, _pricesInputs)) return;
        _pricesResults.Children.Clear();

        var snap = App.Market.Snapshot;
        var commodities = snap?.TradePrices.Rows
            .Select(r => r.CommodityName).Distinct()
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()
            ?? new List<string>();

        RefreshPricesCommodityBox(commodities);

        // Task 8: a null selection is only a real empty state when it is NOT the terminal-browse
        // "every commodity at this one terminal" mode RefreshPricesCommodityBox above deliberately
        // leaves null for (ShowPricesForTerminal's whole point) - that mode still has rows to show.
        if (snap is null || commodities.Count == 0 || (_pricesSelectedCommodity is null && _pricesTerminalFilter is null))
        {
            _pricesResults.Children.Add(EmptyOrStaleNote(snap?.TradePrices.FetchedUtc));
            return;
        }

        // Terminal lookup, built once per rebuild: TerminalId -> MarketTerminal, so each row's
        // System tag is a dictionary read rather than a linear scan of Terminals.Rows.
        var terminals = snap.Terminals.Rows.ToDictionary(t => t.Id);

        // Task 8: the commodity filter is now optional (null = every commodity, the terminal-browse
        // mode) and the terminal filter is a second, independently optional AND clause - a MAP tab
        // pin can arrive with either, both, or (outside this feature) neither set.
        var uexRows = snap.TradePrices.Rows
            .Where(r => _pricesSelectedCommodity is null || string.Equals(r.CommodityName, _pricesSelectedCommodity, StringComparison.OrdinalIgnoreCase))
            .Where(r => _pricesTerminalFilter is not { } tid || r.TerminalId == tid)
            .ToList();

        // SCT-only rows, merged into the same list (never a separate section). Only meaningful for
        // a single selected commodity - SctOnlyBuyers takes one CommodityId, and the terminal-browse
        // "every commodity here" mode has no one id to key it off, so that mode shows UEX rows only
        // (never a fabricated per-commodity SCT match). App.Sct.SctOnlyBuyers self-gates on
        // SctDataEnabled; the outer check here keeps this call site's own trace at zero while dark,
        // same as the Sell flow.
        var sctOnly = App.Settings.Current.SctDataEnabled && _pricesSelectedCommodity is not null && uexRows.Count > 0
            ? App.Sct.SctOnlyBuyers(uexRows[0].CommodityId).ToList()
            : new List<SctListing>();

        // The top-50 display cap applies AFTER sorting (unchanged rule) - PriceSort.SortRows sorts
        // the FULL merged list first, Take(50) below only trims what renders.
        var merged = PriceSort.SortRows(
            uexRows.Select(r => new PriceRowItem(r.Sell, r, null))
                .Concat(sctOnly.Select(s => new PriceRowItem(s.Price, null, s)))
                .ToList(),
            _pricesSortColumn, _pricesSortDescending);
        int totalTerminals = merged.Count;   // includes the SCT-only rows only when they render (sctOnly is empty while dark)
        var top = merged.Take(50).ToList();   // spartan by default; codex row idiom, house rule against clutter on price surfaces

        // Task 8: the dismissible "TERMINAL: <name> x" chip, dropped above the header/results the
        // instant the filter clears (mouse-only dismiss - a plain TextBlock click handler, same
        // idiom as the ORIGIN chip's Manual/Live links, TradePage.cs:704-711 - carries no keyboard
        // path at all: nothing here is a Tab stop or has a key binding).
        if (_pricesTerminalFilter is { } filterTid && terminals.TryGetValue(filterTid, out var filterTerm))
            _pricesResults.Children.Add(TerminalFilterChip(filterTerm.Name));

        var cols = new System.Collections.Generic.List<ColumnDefinition> { new() { Width = new GridLength(1, GridUnitType.Star) }, new() { Width = new GridLength(100) }, new() { Width = new GridLength(100) } };
        if (_priceCols[0]) cols.Add(new ColumnDefinition { Width = new GridLength(100) });
        if (_priceCols[1]) cols.Add(new ColumnDefinition { Width = new GridLength(100) });
        if (_priceCols[2]) cols.Add(new ColumnDefinition { Width = new GridLength(100) });
        if (_priceCols[3]) cols.Add(new ColumnDefinition { Width = new GridLength(100) });

        var header = new Grid { Margin = new Thickness(12, 0, 12, 5) };
        foreach (var c in cols) header.ColumnDefinitions.Add(new ColumnDefinition { Width = c.Width });
        int col = 0;
        header.Children.Add(HeaderCell("Terminal", col++, false));   // Terminal stays unsortable
        header.Children.Add(SortableHeaderCell("Sell (/SCU)", col++, PriceSortColumn.Sell));
        header.Children.Add(SortableHeaderCell("Buy (/SCU)", col++, PriceSortColumn.Buy));
        if (_priceCols[0]) header.Children.Add(SortableHeaderCell("Stock", col++, PriceSortColumn.Stock));
        if (_priceCols[1]) header.Children.Add(SortableHeaderCell("Status", col++, PriceSortColumn.Status));
        if (_priceCols[2]) header.Children.Add(SortableHeaderCell("Age", col++, PriceSortColumn.Age));
        if (_priceCols[3]) header.Children.Add(HeaderCell("Week avg (sell)", col++, true));   // not in the sortable set
        _pricesResults.Children.Add(header);

        for (int i = 0; i < top.Count; i++)
        {
            var row = BuildPriceRow(top[i], cols, terminals);
            CascadeIn(row, i);
            _pricesResults.Children.Add(row);
        }

        _pricesResults.Children.Add(new TextBlock
        {
            Text = $"{totalTerminals} terminals - showing top {top.Count} by price",   // mock:1039, verbatim format
            FontFamily = Hud.Font("UiFont"), FontSize = 10.5, Foreground = Hud.Br("FgDimBrush"), Margin = new Thickness(0, 2, 0, 0),
        });

        string sctSuffix = App.Settings.Current.SctDataEnabled ? $", sctOnly {sctOnly.Count}" : "";
        string commodityPart = _pricesSelectedCommodity ?? "ALL (terminal browse)";
        Logger.Info($"[UI] Trade prices run: {totalTerminals} terminals, commodity {commodityPart}, showing {top.Count}{sctSuffix}");
    }

    /// <summary>Called when the user picks "show prices here" on a MAP tab terminal pin: switches
    /// to the Prices flow and filters results down to that one terminal. Coexists with whatever
    /// commodity is already selected (both filters AND together, RebuildPrices' uexRows Where
    /// chain) - this does not touch _pricesSelectedCommodity, so a prior single-commodity browse
    /// narrows further to "this commodity, at this terminal" rather than resetting.</summary>
    internal void ShowPricesForTerminal(int terminalId)
    {
        var terminals = App.Market.Snapshot?.Terminals.Rows ?? new List<MarketTerminal>();
        var name = TradeOriginResolver.OriginNameForTerminal(terminalId, terminals);
        _pricesTerminalFilter = terminalId;
        SwitchTab(2);
        Logger.Info($"[UI] trade: prices filtered from map{(name is not null ? $" ({name})" : "")}");
        RebuildPrices();
    }

    // Dismissible terminal-filter chip (Task 8). "x" is a plain TextBlock, not a Button - the same
    // mouse-only-dismiss idiom the ORIGIN chip's Manual/Live links already use (TradePage.cs), so
    // it carries no keyboard path (not a Tab stop, no key binding) by construction rather than by
    // suppressing one on a focusable control.
    private Border TerminalFilterChip(string terminalName)
    {
        var label = new TextBlock
        {
            Text = $"TERMINAL: {terminalName.ToUpperInvariant()}", FontFamily = Hud.Font("UiFont"), FontSize = 10.5,
            FontWeight = FontWeights.Bold, Foreground = Hud.Br("AccentBrush"), VerticalAlignment = VerticalAlignment.Center,
        };
        var close = new TextBlock
        {
            Text = "x", FontFamily = Hud.Font("UiFont"), FontSize = 11, FontWeight = FontWeights.Bold,
            Foreground = Hud.Br("FgDimBrush"), Margin = new Thickness(9, 0, 0, 0), Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
        };
        close.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            _pricesTerminalFilter = null;
            Logger.Info("[UI] Trade prices: terminal filter cleared");
            RebuildPrices();
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(label);
        row.Children.Add(close);
        return new Border
        {
            Background = Hud.Br("AccentFaintBrush"), BorderBrush = Hud.Br("AccentStrongBrush"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4), Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 0, 10),
            Child = row, HorizontalAlignment = HorizontalAlignment.Left,
        };
    }

    private static TextBlock HeaderCell(string text, int column, bool right)
    {
        var tb = new TextBlock { Text = text.ToUpperInvariant(), FontFamily = Hud.Font("UiFont"), FontSize = 9, FontWeight = FontWeights.Bold, Foreground = Hud.Br("FgDimBrush"), HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left };
        Grid.SetColumn(tb, column);
        return tb;
    }

    // Click-to-sort header (owner's live-pass ask, 2026-07-30, item 2): SELL/BUY/STOCK/STATUS/AGE
    // only - Terminal and Week avg stay plain HeaderCells above, never wrapped by this. Click an
    // inactive header: sort by that column, descending first. Click the already-active header:
    // flip direction. The chevron only appears on the active header; inactive headers show nothing.
    private FrameworkElement SortableHeaderCell(string text, int column, PriceSortColumn key)
    {
        bool active = _pricesSortColumn == key;
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new TextBlock
        {
            Text = text.ToUpperInvariant(), FontFamily = Hud.Font("UiFont"), FontSize = 9,
            FontWeight = FontWeights.Bold, Foreground = Hud.Br("FgDimBrush"),
        });
        if (active) row.Children.Add(SortChevron(_pricesSortDescending));

        var host = new Border
        {
            Background = Brushes.Transparent, Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Right, Child = row,
        };
        host.MouseLeftButtonUp += (_, _) =>
        {
            if (_pricesSortColumn == key) _pricesSortDescending = !_pricesSortDescending;
            else { _pricesSortColumn = key; _pricesSortDescending = true; }
            // ALL CAPS column name, matching this file's existing [UI] Trade log idiom (column
            // toggles, scope, flow - PriceColLabels/TradeFlows.Ids/Scopes are all upper already).
            Logger.Info($"[UI] Trade prices: sort {key.ToString().ToUpperInvariant()} {(_pricesSortDescending ? "desc" : "asc")}");
            RebuildPrices();
        };
        Grid.SetColumn(host, column);
        return host;
    }

    // Small rotated Path, the same house chevron idiom as ChevronGlyph/SetChevronOpen
    // (TradePage.Planner.cs) - not a unicode arrow, not an emoji. Reuses that method's exact glyph
    // data at a smaller size so it reads as the same visual language, rotated to point down
    // (descending) or up (ascending) instead of ChevronGlyph's closed/open 0/90.
    private static Path SortChevron(bool descending) => new()
    {
        Width = 8, Height = 8, Data = Geometry.Parse("M5,3 L11,8 L5,13"),
        Stroke = Hud.Br("FgDimBrush"), StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round, Fill = Brushes.Transparent,
        Stretch = Stretch.Uniform, RenderTransformOrigin = new Point(0.5, 0.5),
        Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        RenderTransform = new RotateTransform(descending ? 90 : -90),
    };

    // ItemTemplate for the commodity ComboBox's left-alignment fix (item 3 above): a plain
    // left-aligned TextBlock bound directly to the item (a string) via an empty Binding path.
    // Static/shared - stateless, so one instance safely serves every rebuild.
    private static readonly DataTemplate LeftAlignedComboItemTemplate = BuildLeftAlignedComboItemTemplate();

    private static DataTemplate BuildLeftAlignedComboItemTemplate()
    {
        var factory = new FrameworkElementFactory(typeof(TextBlock));
        factory.SetBinding(TextBlock.TextProperty, new Binding());
        factory.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
        factory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        return new DataTemplate { VisualTree = factory };
    }

    private static Border ColumnToggleChip(string label, bool on)
    {
        var text = new TextBlock { Text = label, FontFamily = Hud.Font("UiFont"), FontSize = 10.5, FontWeight = FontWeights.Bold };
        var chip = new Border
        {
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(11, 4, 11, 4), Margin = new Thickness(0, 0, 8, 0), Cursor = Cursors.Hand, Child = text,
        };
        SetColumnChipOn(chip, on);
        return chip;
    }

    // The on/off dressing, applied both at build time and in place on every later toggle (the chip
    // is built once now, so the click has to retint the live control rather than rely on a rebuild).
    private static void SetColumnChipOn(Border chip, bool on)
    {
        ((TextBlock)chip.Child).Foreground = on ? Hud.Br("AccentBrush") : Hud.Br("FgDimBrush");
        chip.BorderBrush = on ? Hud.Br("AccentStrongBrush") : Hud.Br("BorderBrush");
        chip.Background = on ? Hud.Br("AccentFaintBrush") : Brushes.Transparent;
    }

    private FrameworkElement BuildPriceRow(PriceRowItem item, System.Collections.Generic.List<ColumnDefinition> colTemplate,
        System.Collections.Generic.Dictionary<int, MarketTerminal> terminals)
    {
        var grid = new Grid();
        foreach (var c in colTemplate) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = c.Width });
        int col = 0;

        if (item.Uex is { } r)
        {
            // Terminal name lives directly in a Grid cell (the Star column), not a StackPanel, so
            // name+tag are wrapped in a horizontal StackPanel within that same cell (house idiom,
            // matches the SCT-only branch's termPanel below) - this column is the widest of the row
            // and carries no trimming, so the tag never wraps or clips.
            var termPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            termPanel.Children.Add(new TextBlock { Text = r.TerminalName, FontFamily = Hud.Font("UiFont"), FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Hud.Br("FgBrush"), VerticalAlignment = VerticalAlignment.Center });
            string? system = terminals.TryGetValue(r.TerminalId, out var termInfo) ? termInfo.System : null;
            if (SystemTag(system) is { } tag) termPanel.Children.Add(tag);
            Grid.SetColumn(termPanel, col++); grid.Children.Add(termPanel);

            var sell = new TextBlock { Text = r.Sell.ToString("n0", CultureInfo.InvariantCulture), FontFamily = Hud.Font("MonoFont"), FontSize = 13, Foreground = Hud.Br("GoldBrush"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(sell, col++); grid.Children.Add(sell);

            var buy = new TextBlock { Text = r.Buy.ToString("n0", CultureInfo.InvariantCulture), FontFamily = Hud.Font("MonoFont"), FontSize = 13, Foreground = Hud.Br("CyanBrush"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(buy, col++); grid.Children.Add(buy);

            bool outOfStock = r.BuyStockScu == 0;
            if (_priceCols[0])
            {
                var stock = new TextBlock { Text = $"{r.BuyStockScu:n0} SCU", FontFamily = Hud.Font("MonoFont"), FontSize = 12, Foreground = outOfStock ? Hud.Br("DangerBrush") : Hud.Br("FgBrush"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(stock, col++); grid.Children.Add(stock);
            }
            if (_priceCols[1])
            {
                // Label from the real UEX inventory-state code (TradeFlows.BuyStatusLabel), not a
                // binary derived-from-stock guess (task-14 review finding 1). Color rule unchanged:
                // still keyed off BuyStockScu == 0, only the text source changed.
                var status = new TextBlock { Text = TradeFlows.BuyStatusLabel(r.StatusBuy), FontFamily = Hud.Font("UiFont"), FontSize = 10, Foreground = outOfStock ? Hud.Br("DangerBrush") : Hud.Br("FgDimBrush"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(status, col++); grid.Children.Add(status);
            }
            if (_priceCols[2])
            {
                var age = DateTime.UtcNow - r.ModifiedUtc;
                var ageText = new TextBlock { Text = MarketNotice.FormatAge(age), FontFamily = Hud.Font("MonoFont"), FontSize = 10, Foreground = age.TotalHours >= 24 ? Hud.Br("AccentBrush") : Hud.Br("FgDimBrush"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(ageText, col++); grid.Children.Add(ageText);
            }
            if (_priceCols[3])
            {
                // Week-average sell is not part of the TradePriceRow contract (no SellAvgWeek field on
                // this record, unlike the legacy MarketPriceRow); show the instant sell price with a
                // note rather than fabricate an average. Flagged: revisit if a week-avg field is added.
                var wk = new TextBlock { Text = $"{r.Sell:n0}*", FontFamily = Hud.Font("MonoFont"), FontSize = 11, Foreground = Hud.Br("FgDimBrush"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, ToolTip = "No week-average field on this data source yet; showing the instant price." };
                Grid.SetColumn(wk, col++); grid.Children.Add(wk);
            }
        }
        else
        {
            // SCT-only row (mock index.html:1024-1030): terminal name + inline badge, sell = the
            // SCT price, everything else UEX has no equivalent for is a plain dash - never
            // danger-colored (no stock/status data exists to judge "out of stock" from).
            var s = item.Sct!;
            var termPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            termPanel.Children.Add(new TextBlock { Text = s.Location, FontFamily = Hud.Font("UiFont"), FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Hud.Br("FgBrush"), VerticalAlignment = VerticalAlignment.Center });
            // Reuses CorroborationBadge with a synthesized SctOnly ReconciledPrice - the same
            // shape PriceReconciler.Reconcile itself returns for the SCT-only case (shared factory,
            // also used by the sell flow's SCT-only rows).
            if (CorroborationBadge(SctOnlyReconciled(s)) is { } sctBadge)
            {
                sctBadge.Margin = new Thickness(8, 0, 0, 0);
                termPanel.Children.Add(sctBadge);
            }
            Grid.SetColumn(termPanel, col++); grid.Children.Add(termPanel);

            var sell = new TextBlock { Text = s.Price.ToString("n0", CultureInfo.InvariantCulture), FontFamily = Hud.Font("MonoFont"), FontSize = 13, Foreground = Hud.Br("GoldBrush"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(sell, col++); grid.Children.Add(sell);

            var buy = DashCell(13);
            Grid.SetColumn(buy, col++); grid.Children.Add(buy);

            if (_priceCols[0]) { var stock = DashCell(12); Grid.SetColumn(stock, col++); grid.Children.Add(stock); }
            if (_priceCols[1]) { var status = DashCell(10); Grid.SetColumn(status, col++); grid.Children.Add(status); }
            if (_priceCols[2])
            {
                var age = DateTime.UtcNow - s.TimestampUtc;
                var ageText = new TextBlock { Text = MarketNotice.FormatAge(age), FontFamily = Hud.Font("MonoFont"), FontSize = 10, Foreground = age.TotalHours >= 24 ? Hud.Br("AccentBrush") : Hud.Br("FgDimBrush"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(ageText, col++); grid.Children.Add(ageText);
            }
            if (_priceCols[3]) { var wk = DashCell(11); Grid.SetColumn(wk, col++); grid.Children.Add(wk); }   // no week-avg source for SCT-only either
        }

        return Hud.RowCard(grid, marginBottom: 8);
    }

    private static TextBlock DashCell(double fontSize) => new()
    {
        Text = "-", FontFamily = Hud.Font("MonoFont"), FontSize = fontSize, Foreground = Hud.Br("FgDimBrush"),
        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
    };
}
