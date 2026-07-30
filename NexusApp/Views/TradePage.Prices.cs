using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NexusApp.Services;

namespace NexusApp.Views;

public sealed partial class TradePage
{
    // One row's data source: exactly one of Uex/Sct is populated. Lets SCT-only listings merge
    // into the same sell-descending order as UEX rows (mock LARANITE_ROWS comment, index.html:
    // 629-630) without forcing an SctListing into a fake TradePriceRow.
    private readonly record struct PriceRowItem(double SellValue, TradePriceRow? Uex, SctListing? Sct);

    private ComboBox _pricesCommodityCombo = null!;
    private readonly bool[] _priceCols = { true, true, true, false };   // STOCK, STATUS, AGE, +WEEK AVG - session only, not persisted (task brief: "persist nothing new")
    private static readonly string[] PriceColLabels = { "STOCK", "STATUS", "AGE", "+WEEK AVG" };
    private string? _pricesSelectedCommodity;

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

        var pickerGrp = new StackPanel { Width = 220, Margin = new Thickness(0, 0, 0, 16) };
        pickerGrp.Children.Add(FieldLabel("Commodity"));
        _pricesCommodityCombo = new ComboBox { Style = (Style)Application.Current.FindResource("NexusComboBox") };
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
    private void RefreshPricesCommodityBox(List<string> commodities)
    {
        if (_pricesSelectedCommodity is null || !commodities.Any(c => string.Equals(c, _pricesSelectedCommodity, StringComparison.OrdinalIgnoreCase)))
            _pricesSelectedCommodity = commodities.FirstOrDefault();

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

        if (snap is null || commodities.Count == 0 || _pricesSelectedCommodity is null)
        {
            _pricesResults.Children.Add(EmptyOrStaleNote(snap?.TradePrices.FetchedUtc));
            return;
        }

        // Terminal lookup, built once per rebuild: TerminalId -> MarketTerminal, so each row's
        // System tag is a dictionary read rather than a linear scan of Terminals.Rows.
        var terminals = snap.Terminals.Rows.ToDictionary(t => t.Id);

        var uexRows = snap.TradePrices.Rows
            .Where(r => string.Equals(r.CommodityName, _pricesSelectedCommodity, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // SCT-only rows for the selected commodity, merged into the same list (never a separate
        // section). App.Sct.SctOnlyBuyers self-gates on SctDataEnabled; the outer check here keeps
        // this call site's own trace at zero while dark, same as the Sell flow.
        var sctOnly = App.Settings.Current.SctDataEnabled && uexRows.Count > 0
            ? App.Sct.SctOnlyBuyers(uexRows[0].CommodityId).ToList()
            : new List<SctListing>();

        var merged = uexRows.Select(r => new PriceRowItem(r.Sell, r, null))
            .Concat(sctOnly.Select(s => new PriceRowItem(s.Price, null, s)))
            .OrderByDescending(m => m.SellValue)
            .ToList();
        int totalTerminals = merged.Count;   // includes the SCT-only rows only when they render (sctOnly is empty while dark)
        var top = merged.Take(50).ToList();   // spartan by default; codex row idiom, house rule against clutter on price surfaces

        var cols = new System.Collections.Generic.List<ColumnDefinition> { new() { Width = new GridLength(1, GridUnitType.Star) }, new() { Width = new GridLength(100) }, new() { Width = new GridLength(100) } };
        if (_priceCols[0]) cols.Add(new ColumnDefinition { Width = new GridLength(100) });
        if (_priceCols[1]) cols.Add(new ColumnDefinition { Width = new GridLength(100) });
        if (_priceCols[2]) cols.Add(new ColumnDefinition { Width = new GridLength(100) });
        if (_priceCols[3]) cols.Add(new ColumnDefinition { Width = new GridLength(100) });

        var header = new Grid { Margin = new Thickness(12, 0, 12, 5) };
        foreach (var c in cols) header.ColumnDefinitions.Add(new ColumnDefinition { Width = c.Width });
        int col = 0;
        header.Children.Add(HeaderCell("Terminal", col++, false));
        header.Children.Add(HeaderCell("Sell (/SCU)", col++, true));
        header.Children.Add(HeaderCell("Buy (/SCU)", col++, true));
        if (_priceCols[0]) header.Children.Add(HeaderCell("Stock", col++, true));
        if (_priceCols[1]) header.Children.Add(HeaderCell("Status", col++, true));
        if (_priceCols[2]) header.Children.Add(HeaderCell("Age", col++, true));
        if (_priceCols[3]) header.Children.Add(HeaderCell("Week avg (sell)", col++, true));
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
        Logger.Info($"[UI] Trade prices run: {totalTerminals} terminals, commodity {_pricesSelectedCommodity}, showing {top.Count}{sctSuffix}");
    }

    private static TextBlock HeaderCell(string text, int column, bool right)
    {
        var tb = new TextBlock { Text = text.ToUpperInvariant(), FontFamily = Hud.Font("UiFont"), FontSize = 9, FontWeight = FontWeights.Bold, Foreground = Hud.Br("FgDimBrush"), HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left };
        Grid.SetColumn(tb, column);
        return tb;
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
