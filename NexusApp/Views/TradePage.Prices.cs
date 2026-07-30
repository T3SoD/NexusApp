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
    private ComboBox _pricesCommodityCombo = null!;
    private readonly bool[] _priceCols = { true, true, true, false };   // STOCK, STATUS, AGE, +WEEK AVG - session only, not persisted (task brief: "persist nothing new")
    private readonly Border[] _priceColChips = new Border[4];
    private static readonly string[] PriceColLabels = { "STOCK", "STATUS", "AGE", "+WEEK AVG" };
    private string? _pricesSelectedCommodity;

    private void RebuildPrices()
    {
        if (!EnsureMarketConsent(PricesHost)) return;
        PricesHost.Children.Clear();

        var snap = App.Market.Snapshot;
        var commodities = snap?.TradePrices.Rows
            .Select(r => r.CommodityName).Distinct()
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()
            ?? new System.Collections.Generic.List<string>();

        var pickerGrp = new StackPanel { Width = 220, Margin = new Thickness(0, 0, 0, 16) };
        pickerGrp.Children.Add(FieldLabel("Commodity"));
        _pricesSelectedCommodity ??= commodities.FirstOrDefault();
        _pricesCommodityCombo = new ComboBox
        {
            Style = (Style)Application.Current.FindResource("NexusComboBox"),
            ItemsSource = commodities, SelectedItem = _pricesSelectedCommodity,
        };
        _pricesCommodityCombo.SelectionChanged += (_, _) =>
        {
            _pricesSelectedCommodity = _pricesCommodityCombo.SelectedItem as string;
            if (_pricesSelectedCommodity is not null) Logger.Info($"[UI] Trade prices: commodity {_pricesSelectedCommodity}");
            RebuildPrices();
        };
        pickerGrp.Children.Add(_pricesCommodityCombo);
        PricesHost.Children.Add(pickerGrp);

        var toggles = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        for (int i = 0; i < PriceColLabels.Length; i++)
        {
            int idx = i;
            var chip = ColumnToggleChip(PriceColLabels[i], _priceCols[i]);
            chip.MouseLeftButtonUp += (_, _) =>
            {
                _priceCols[idx] = !_priceCols[idx];
                Logger.Info($"[UI] Trade prices: column {PriceColLabels[idx]} {(_priceCols[idx] ? "ON" : "OFF")}");
                RebuildPrices();
            };
            _priceColChips[i] = chip;
            toggles.Children.Add(chip);
        }
        PricesHost.Children.Add(toggles);

        if (snap is null || commodities.Count == 0 || _pricesSelectedCommodity is null)
        {
            PricesHost.Children.Add(EmptyOrStaleNote(snap?.TradePrices.FetchedUtc));
            return;
        }

        var rows = snap.TradePrices.Rows
            .Where(r => string.Equals(r.CommodityName, _pricesSelectedCommodity, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.Sell)
            .ToList();
        int totalTerminals = rows.Count;
        var top = rows.Take(50).ToList();   // spartan by default; codex row idiom, house rule against clutter on price surfaces

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
        PricesHost.Children.Add(header);

        for (int i = 0; i < top.Count; i++)
        {
            var row = BuildPriceRow(top[i], cols);
            CascadeIn(row, i);
            PricesHost.Children.Add(row);
        }

        PricesHost.Children.Add(new TextBlock
        {
            Text = $"{totalTerminals} terminals - showing top {top.Count} by price",   // mock:1039, verbatim format
            FontFamily = Hud.Font("UiFont"), FontSize = 10.5, Foreground = Hud.Br("FgDimBrush"), Margin = new Thickness(0, 2, 0, 0),
        });

        Logger.Info($"[UI] Trade prices run: {totalTerminals} terminals, commodity {_pricesSelectedCommodity}, showing {top.Count}");
    }

    private static TextBlock HeaderCell(string text, int column, bool right)
    {
        var tb = new TextBlock { Text = text.ToUpperInvariant(), FontFamily = Hud.Font("UiFont"), FontSize = 9, FontWeight = FontWeights.Bold, Foreground = Hud.Br("FgDimBrush"), HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left };
        Grid.SetColumn(tb, column);
        return tb;
    }

    private static Border ColumnToggleChip(string label, bool on)
    {
        var text = new TextBlock { Text = label, FontFamily = Hud.Font("UiFont"), FontSize = 10.5, FontWeight = FontWeights.Bold, Foreground = on ? Hud.Br("AccentBrush") : Hud.Br("FgDimBrush") };
        return new Border
        {
            BorderBrush = on ? Hud.Br("AccentStrongBrush") : Hud.Br("BorderBrush"), BorderThickness = new Thickness(1),
            Background = on ? Hud.Br("AccentFaintBrush") : Brushes.Transparent, CornerRadius = new CornerRadius(8),
            Padding = new Thickness(11, 4, 11, 4), Margin = new Thickness(0, 0, 8, 0), Cursor = Cursors.Hand, Child = text,
        };
    }

    private FrameworkElement BuildPriceRow(TradePriceRow r, System.Collections.Generic.List<ColumnDefinition> colTemplate)
    {
        var grid = new Grid();
        foreach (var c in colTemplate) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = c.Width });
        int col = 0;

        var term = new TextBlock { Text = r.TerminalName, FontFamily = Hud.Font("UiFont"), FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Hud.Br("FgBrush"), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(term, col++); grid.Children.Add(term);

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
            var status = new TextBlock { Text = outOfStock ? "OUT OF STOCK" : "ACTIVE", FontFamily = Hud.Font("UiFont"), FontSize = 10, Foreground = outOfStock ? Hud.Br("DangerBrush") : Hud.Br("FgDimBrush"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
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

        return Hud.RowCard(grid, marginBottom: 8);
    }
}
