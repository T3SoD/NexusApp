using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NexusApp.Models.Cargo;
using NexusApp.Services;
using NexusApp.Services.Cargo;
using NexusApp.ViewModels;
using static NexusApp.Views.CargoUiKit;

namespace NexusApp.Views;

// The Cargo Planner module screen: a three-zone MOBIGLAS layout (manifest rail | 3D viewport |
// inspector rail). Enter cargo or import active hauls, pick a ship, see it auto-packed in 3D across
// trips, and rank ships by real footprint fit. Drag-to-adjust is layered on in a later pass.
public sealed class CargoPlannerPage : UserControl
{
    private readonly CargoPlannerViewModel _vm;
    // Cargo 3D view runs the Three.js scene in a WebView2 (RenderTrip posts the packed trip as JSON).
    private readonly CargoWebView _viewport = new();
    private readonly ComboBox _shipSelect = new();
    private readonly TextBox _labelBox = new();
    private readonly TextBox _scuBox = new();
    private readonly ComboBox _capBox = new();
    private readonly StackPanel _manifest = new();
    private readonly TextBlock _totals = new();
    // Layout overrides are authored in Grid Studio; the planner loads them so packing reflects the
    // corrected grids (read-only here - no editing or review UI on the shippable planner).
    private readonly CargoGridOverrideStore _overrides = CargoGridOverrideStore.LoadDefault();
    // Only ships fully signed off in Grid Studio are shown here.
    private readonly CargoSignoffStore _signoff = CargoSignoffStore.LoadDefault();
    private readonly TextBlock _noShipsMsg = new();

    private ColumnDefinition _leftCol = null!;
    private Border _leftRail = null!;
    private Button _expandBtn = null!;

    public CargoPlannerPage()
    {
        _vm = new CargoPlannerViewModel(LoadVisibleCatalog());
        Build();
        InteractionLog.Nav("Cargo Planner");
        RefreshManifest();
        RefreshShips();
        Render();
    }

    // Called each time the dock tile is shown: reload the catalog so newly signed-off ships appear
    // and any Grid Studio layout/property edits (saved as overrides) are applied to the model here.
    public void OnShown()
    {
        _overrides.Reload();
        _signoff.Reload();
        _vm.ReloadCatalog(LoadVisibleCatalog());
        RefreshShips();
        Render();
    }

    // The planner shows ONLY ships fully signed off in Grid Studio, with their saved overrides
    // (grid positions, sizes, accepted-container sets) applied to the model.
    private CargoShipCatalog LoadVisibleCatalog()
    {
        var full = CargoShipCatalog.LoadEmbedded().WithOverrides(_overrides);
        return new CargoShipCatalog(full.Ships.Where(s => _signoff.IsFullySignedOff(s.Id)));
    }

    // -- layout --------------------------------------------------------------------

    private void Build()
    {
        Background = new SolidColorBrush(Bg);
        var root = new Grid { Margin = new Thickness(0) };
        _leftCol = new ColumnDefinition { Width = new GridLength(310) };
        root.ColumnDefinitions.Add(_leftCol);
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _leftRail = BuildManifestRail();
        Grid.SetColumn(_leftRail, 0);
        var center = BuildCenter();
        Grid.SetColumn(center, 1);

        // Shown in a thin sliver of column 0 when the ship panel is collapsed, to bring it back. It
        // must NOT sit over the WebView2 host (column 1): WebView2 is a native HWND that renders on top
        // of any WPF in its region (airspace), which would occlude and swallow clicks on this button.
        _expandBtn = Btn("»", (_, _) => SetShipPanelCollapsed(false));
        _expandBtn.ToolTip = "Show the ship panel";
        _expandBtn.HorizontalAlignment = HorizontalAlignment.Center;
        _expandBtn.VerticalAlignment = VerticalAlignment.Top;
        _expandBtn.Margin = new Thickness(0, 16, 0, 0);
        _expandBtn.Padding = new Thickness(6, 4, 6, 4);
        _expandBtn.Visibility = Visibility.Collapsed;
        Grid.SetColumn(_expandBtn, 0);

        root.Children.Add(_leftRail);
        root.Children.Add(center);
        root.Children.Add(_expandBtn);
        Content = root;
    }

    private void SetShipPanelCollapsed(bool collapsed)
    {
        // Collapse to a thin sliver (not 0) so the WPF restore button in column 0 stays visible and
        // clickable beside the WebView, rather than being hidden behind its HWND.
        _leftCol.Width = collapsed ? new GridLength(30) : new GridLength(310);
        _leftRail.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        _expandBtn.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
    }

    private Border BuildManifestRail()
    {
        var stack = new StackPanel { Margin = new Thickness(16, 16, 12, 16) };

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var shipEyebrow = Eyebrow("SHIP");
        shipEyebrow.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(shipEyebrow, 0);
        var collapseBtn = Btn("«", (_, _) => SetShipPanelCollapsed(true));
        collapseBtn.ToolTip = "Collapse the ship panel";
        collapseBtn.Padding = new Thickness(8, 2, 8, 2);
        collapseBtn.Margin = new Thickness(0);
        Grid.SetColumn(collapseBtn, 1);
        header.Children.Add(shipEyebrow);
        header.Children.Add(collapseBtn);
        stack.Children.Add(header);

        _shipSelect.Margin = new Thickness(0, 6, 0, 14);
        StyleCombo(_shipSelect);
        // Wired here (and detached during rebuilds in RefreshShips) so repopulating the list cannot
        // fire a spurious selection change and double-render per OnShown.
        _shipSelect.SelectionChanged += OnShipSelected;
        stack.Children.Add(_shipSelect);

        _noShipsMsg.Text = "No ships available yet. Sign off a ship's full checklist in Grid Studio to add it here.";
        _noShipsMsg.Foreground = new SolidColorBrush(Warn);
        _noShipsMsg.FontFamily = Mono;
        _noShipsMsg.FontSize = 11;
        _noShipsMsg.TextWrapping = TextWrapping.Wrap;
        _noShipsMsg.Margin = new Thickness(0, 0, 0, 12);
        _noShipsMsg.Visibility = Visibility.Collapsed;
        stack.Children.Add(_noShipsMsg);

        stack.Children.Add(Eyebrow("ADD CARGO"));
        var form = new Grid { Margin = new Thickness(0, 6, 0, 8) };
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
        form.RowDefinitions.Add(new RowDefinition());

        StyleBox(_labelBox); _labelBox.Margin = new Thickness(0, 0, 6, 0);
        AddPlaceholder(_labelBox, "commodity");
        StyleBox(_scuBox); _scuBox.Margin = new Thickness(0, 0, 6, 0);
        AddPlaceholder(_scuBox, "SCU");
        StyleCombo(_capBox);
        _capBox.Items.Add(new CapRow("Cap: any", null));
        foreach (var s in BoxType.SizesDesc.Reverse()) _capBox.Items.Add(new CapRow($"<= {s}", s));
        _capBox.SelectedIndex = 0;

        Grid.SetColumn(_labelBox, 0); Grid.SetColumn(_scuBox, 1); Grid.SetColumn(_capBox, 2);
        form.Children.Add(_labelBox); form.Children.Add(_scuBox); form.Children.Add(_capBox);
        stack.Children.Add(form);

        var addRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        addRow.Children.Add(Btn("Add line", OnAddLine, primary: true));
        addRow.Children.Add(Btn("Import hauls", OnImport));
        stack.Children.Add(addRow);

        stack.Children.Add(Eyebrow("MANIFEST"));
        _manifest.Margin = new Thickness(0, 6, 0, 10);
        stack.Children.Add(_manifest);

        _totals.Foreground = new SolidColorBrush(Dim);
        _totals.FontFamily = Mono;
        _totals.FontSize = 11.5;
        _totals.TextWrapping = TextWrapping.Wrap;
        stack.Children.Add(_totals);
        var clear = Btn("Clear manifest", (_, _) => { _vm.ClearManifest(); RefreshManifest(); Render(); });
        clear.Margin = new Thickness(0, 10, 0, 0);
        stack.Children.Add(clear);

        return RailBorder(new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
    }

    private Grid BuildCenter()
    {
        var g = new Grid { Margin = new Thickness(6, 16, 6, 16) };
        var frame = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x07, 0x0B, 0x11)),
            BorderBrush = new SolidColorBrush(Line),
            BorderThickness = new Thickness(1),
            Child = _viewport,
        };
        g.Children.Add(frame);
        return g;
    }

    // -- interactions --------------------------------------------------------------

    private void OnAddLine(object? s, RoutedEventArgs e)
    {
        if (!int.TryParse(_scuBox.Text.Trim(), out var scu) || scu <= 0) return;
        int? cap = (_capBox.SelectedItem as CapRow)?.Cap;
        var label = string.IsNullOrWhiteSpace(_labelBox.Text) ? "Cargo" : _labelBox.Text.Trim();
        _vm.AddManualLine(label, scu, cap);
        _labelBox.Text = ""; _scuBox.Text = "";
        RefreshManifest();
        Render();
    }

    private void OnImport(object? s, RoutedEventArgs e)
    {
        _vm.ImportFromHauls(App.Hauls.ActiveHauls);
        RefreshManifest();
        Render();
    }

    // -- rendering -----------------------------------------------------------------

    private void Render()
    {
        _viewport.RenderTrip(_vm.CurrentTrip, _vm.SelectedShip);
        RenderTotals();
    }

    private void RenderTotals()
    {
        var ship = _vm.SelectedShip;
        int cap = ship?.TotalScu ?? 0;
        string line = $"Manifest {_vm.ManifestScu} SCU / {cap} SCU";
        if (_vm.TripCount > 1) line += $"   .   {_vm.TripCount} trips";
        if (_vm.UnplaceableScu > 0) line += $"   .   {_vm.UnplaceableScu} SCU will not fit (shape)";
        _totals.Text = line;
    }

    private void RefreshManifest()
    {
        _manifest.Children.Clear();
        if (_vm.Manifest.Count == 0)
        {
            _manifest.Children.Add(Muted("No cargo yet. Add a line or import your hauls."));
            RenderTotals();
            return;
        }
        foreach (var item in _vm.Manifest.ToList())
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = new TextBlock
            {
                Text = $"{item.Label}  {item.Scu} SCU",
                Foreground = new SolidColorBrush(Fg), FontFamily = Mono, FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(name, 0);

            var capText = item.Cap.HasValue ? $"<= {item.Cap}" : "cap 16?";
            var capColor = item.CapSource == CapSource.Default ? Warn : Dim;
            var capChip = new TextBlock
            {
                Text = capText, Foreground = new SolidColorBrush(capColor), FontFamily = Mono, FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0),
            };
            Grid.SetColumn(capChip, 1);

            var del = new Button { Content = "x", Width = 18, Height = 18, Padding = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand };
            StyleGhost(del);
            var captured = item;
            del.Click += (_, _) => { _vm.RemoveLine(captured); RefreshManifest(); Render(); };
            Grid.SetColumn(del, 2);

            row.Children.Add(name); row.Children.Add(capChip); row.Children.Add(del);
            _manifest.Children.Add(row);
        }
        RenderTotals();
    }

    private void RefreshShips()
    {
        _shipSelect.SelectionChanged -= OnShipSelected;   // avoid a spurious render while rebuilding
        _shipSelect.Items.Clear();
        foreach (var ship in _vm.Ships)
            _shipSelect.Items.Add(new ShipRow(ship));
        SyncShipCombo();
        _shipSelect.SelectionChanged += OnShipSelected;
        bool any = _vm.Ships.Count > 0;
        _shipSelect.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        _noShipsMsg.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnShipSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_shipSelect.SelectedItem is ShipRow r) { _vm.SelectShip(r.Ship); Render(); }
    }

    private void SyncShipCombo()
    {
        foreach (ShipRow r in _shipSelect.Items)
            if (r.Ship == _vm.SelectedShip) { _shipSelect.SelectedItem = r; return; }
    }

    // -- small styled builders -----------------------------------------------------

    private TextBlock Muted(string t) => new()
    {
        Text = t, Foreground = new SolidColorBrush(Dim), FontFamily = Mono, FontSize = 11,
        TextWrapping = TextWrapping.Wrap,
    };

    private static void AddPlaceholder(TextBox box, string hint)
    {
        // Lightweight placeholder: show hint text until the user types.
        box.Text = hint;
        box.Foreground = new SolidColorBrush(Color.FromRgb(0x54, 0x60, 0x6C));
        bool cleared = false;
        box.GotFocus += (_, _) => { if (!cleared) { box.Text = ""; box.Foreground = new SolidColorBrush(Fg); cleared = true; } };
        box.LostFocus += (_, _) => { if (string.IsNullOrWhiteSpace(box.Text)) { box.Text = hint; box.Foreground = new SolidColorBrush(Color.FromRgb(0x54, 0x60, 0x6C)); cleared = false; } };
    }

    private sealed class CapRow
    {
        public int? Cap { get; }
        private readonly string _text;
        public CapRow(string text, int? cap) { _text = text; Cap = cap; }
        public override string ToString() => _text;
    }
}
