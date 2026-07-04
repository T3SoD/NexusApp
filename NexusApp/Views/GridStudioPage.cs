using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NexusApp.Models.Cargo;
using NexusApp.Services;
using NexusApp.Services.Cargo;

namespace NexusApp.Views;

// Owner-only dev tooling (gated by RSI handle in MainWindow): review and edit ship cargo-grid
// layouts against the 3D view and hull hologram. Kept OUT of the shippable Cargo Planner. Pick a
// ship to see its grids; Edit Layout enables in-view editing (move/resize/add/delete grids); the
// three-item checklist records the review and persists per user. Mouse-driven.
public sealed class GridStudioPage : UserControl
{
    private static readonly Color Void = Color.FromRgb(0x05, 0x07, 0x0A);
    private static readonly Color Panel = Color.FromRgb(0x0B, 0x10, 0x17);
    private static readonly Color Line = Color.FromRgb(0x1C, 0x28, 0x36);
    private static readonly Color Amber = Color.FromRgb(0xFF, 0xB2, 0x3E);
    private static readonly Color Cyan = Color.FromRgb(0x7F, 0xE9, 0xE0);
    private static readonly Color Fg = Color.FromRgb(0xEA, 0xF1, 0xF6);
    private static readonly Color Dim = Color.FromRgb(0x7C, 0x8A, 0x99);
    private static readonly Color Warn = Color.FromRgb(0xFF, 0x8A, 0x3D);

    private readonly CargoWebView _viewport = new();
    private readonly ComboBox _shipSelect = new();
    private readonly CargoSignoffStore _signoff = CargoSignoffStore.LoadDefault();
    private readonly CargoGridOverrideStore _overrides = CargoGridOverrideStore.LoadDefault();
    private readonly Dictionary<CargoSignoffStore.ReviewItem, Button> _checkBtns = new();
    private readonly TextBlock _reviewStatus = new();
    private Button _flagBtn = null!;
    private Button _editBtn = null!;

    // Owner-only: load a contributor's .nexusgrid submission, preview it in the 3D view, and
    // show a summary/diff so the owner can keep (persist as override) or discard (no change).
    private Button _importBtn = null!;
    private readonly StackPanel _importPanel = new();
    private readonly TextBlock _importMeta = new();
    private readonly TextBlock _importDiff = new();
    private (string ShipId, string ShipName, List<GridOverride> Grids)? _pendingImport;

    // Test-fill: drop N generic containers of one SCU size into a single grid to check it.
    private readonly ComboBox _testGrid = new();
    private readonly ComboBox _testSize = new();
    private readonly TextBox _testQty = new();
    private readonly TextBlock _testResult = new();

    private CargoShipCatalog _catalog;
    private ShipCargoDef? _selected;

    private FontFamily Mono => (FontFamily)Application.Current.FindResource("MonoFont");
    private FontFamily Head => (FontFamily)Application.Current.FindResource("HeadFont");

    public GridStudioPage()
    {
        _catalog = CargoShipCatalog.LoadEmbedded().WithOverrides(_overrides);
        _viewport.Studio = true;   // orange, numbered, clickable grids
        _viewport.GridsSaved += OnGridsSaved;
        _viewport.GridsReverted += OnGridsReverted;
        _viewport.TestGridClicked += OnTestGridClicked;
        Build();
        InteractionLog.Nav("Grid Studio");
        _selected = _catalog.Ships.FirstOrDefault();
        RefreshShips();
        Render();
    }

    // Re-shown from the dock: pick up overrides changed elsewhere and re-render.
    public void OnShown()
    {
        _catalog = CargoShipCatalog.LoadEmbedded().WithOverrides(_overrides);
        _selected = (_selected != null ? _catalog.ById(_selected.Id) : null) ?? _catalog.Ships.FirstOrDefault();
        RefreshShips();
        Render();
    }

    // -- layout --------------------------------------------------------------------

    private void Build()
    {
        Background = new SolidColorBrush(Void);
        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var rail = BuildRail();
        Grid.SetColumn(rail, 0);

        var center = new Grid { Margin = new Thickness(6, 16, 6, 16) };
        center.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x07, 0x0B, 0x11)),
            BorderBrush = new SolidColorBrush(Line),
            BorderThickness = new Thickness(1),
            Child = _viewport,
        });
        Grid.SetColumn(center, 1);

        root.Children.Add(rail);
        root.Children.Add(center);
        Content = root;
    }

    private Border BuildRail()
    {
        var stack = new StackPanel { Margin = new Thickness(16) };

        stack.Children.Add(Eyebrow("SHIP"));
        _shipSelect.Margin = new Thickness(0, 6, 0, 14);
        StyleCombo(_shipSelect);
        // selection is wired in RefreshShips (OnShipSelected) so it can be detached during rebuilds
        stack.Children.Add(_shipSelect);

        _editBtn = Btn("Edit layout", (_, _) => ToggleEdit());
        _editBtn.ToolTip = "Move, resize, add or delete this ship's cargo grids, then save in the 3D view";
        _editBtn.Margin = new Thickness(0, 0, 6, 16);
        stack.Children.Add(_editBtn);

        var exportBtn = Btn("Export layout", (_, _) => OnExport());
        exportBtn.ToolTip = "Save this ship's cargo grid layout to a .nexusgrid file to share";
        exportBtn.Margin = new Thickness(0, 0, 6, 16);
        stack.Children.Add(exportBtn);

        _importBtn = Btn("Import submission", (_, _) => OnImport());
        _importBtn.ToolTip = "Load a contributor's .nexusgrid file to compare and keep or discard";
        _importBtn.Margin = new Thickness(0, 0, 6, 12);
        _importBtn.Visibility = OwnerGate.IsOwnerActive ? Visibility.Visible : Visibility.Collapsed;
        stack.Children.Add(_importBtn);

        _importPanel.Visibility = Visibility.Collapsed;
        _importPanel.Margin = new Thickness(0, 0, 0, 14);
        _importPanel.Children.Add(Eyebrow("IMPORT REVIEW"));
        _importMeta.FontFamily = Mono; _importMeta.FontSize = 11; _importMeta.TextWrapping = TextWrapping.Wrap;
        _importMeta.Foreground = new SolidColorBrush(Fg); _importMeta.Margin = new Thickness(0, 6, 0, 6);
        _importPanel.Children.Add(_importMeta);
        _importDiff.FontFamily = Mono; _importDiff.FontSize = 11; _importDiff.TextWrapping = TextWrapping.Wrap;
        _importDiff.Foreground = new SolidColorBrush(Dim); _importDiff.Margin = new Thickness(0, 0, 0, 8);
        _importPanel.Children.Add(_importDiff);
        var importBtns = new StackPanel { Orientation = Orientation.Horizontal };
        importBtns.Children.Add(Btn("Keep", (_, _) => ApplyImport(), primary: true));
        importBtns.Children.Add(Btn("Discard", (_, _) => DiscardImport()));
        _importPanel.Children.Add(importBtns);
        stack.Children.Add(_importPanel);

        stack.Children.Add(Eyebrow("LAYOUT REVIEW"));
        var checklist = new StackPanel { Margin = new Thickness(0, 6, 0, 4) };
        foreach (var item in CargoSignoffStore.Items)
        {
            var b = ChecklistBtn(item);
            _checkBtns[item] = b;
            checklist.Children.Add(b);
        }
        stack.Children.Add(checklist);
        _flagBtn = Btn("Flag issue", (_, _) => ToggleFlag());
        _flagBtn.Margin = new Thickness(0, 2, 6, 8);
        stack.Children.Add(_flagBtn);
        _reviewStatus.FontFamily = Mono;
        _reviewStatus.FontSize = 11.5;
        _reviewStatus.Margin = new Thickness(0, 4, 0, 16);
        _reviewStatus.TextWrapping = TextWrapping.Wrap;
        _reviewStatus.Foreground = new SolidColorBrush(Dim);
        stack.Children.Add(_reviewStatus);

        // Test fill: drop generic containers into one grid to check capacity and size limits.
        stack.Children.Add(Eyebrow("TEST FILL"));
        StyleCombo(_testGrid);
        _testGrid.Margin = new Thickness(0, 6, 0, 6);
        _testGrid.SelectionChanged += OnTestGridChanged;
        stack.Children.Add(_testGrid);

        var trow = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        trow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        trow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        StyleCombo(_testSize);
        _testSize.Margin = new Thickness(0, 0, 6, 0);
        foreach (var s in BoxType.SizesDesc.Reverse()) _testSize.Items.Add(new SizeRow(s));
        _testSize.SelectedIndex = 3;   // 8 SCU
        StyleBox(_testQty);
        _testQty.Text = "1";
        _testQty.ToolTip = "Quantity";
        Grid.SetColumn(_testSize, 0); Grid.SetColumn(_testQty, 1);
        trow.Children.Add(_testSize); trow.Children.Add(_testQty);
        stack.Children.Add(trow);

        var testBtns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        testBtns.Children.Add(Btn("Fill grid", (_, _) => TestFill(), primary: true));
        testBtns.Children.Add(Btn("Clear", (_, _) => { _testResult.Text = ""; Render(); }));
        stack.Children.Add(testBtns);

        _testResult.FontFamily = Mono;
        _testResult.FontSize = 11;
        _testResult.TextWrapping = TextWrapping.Wrap;
        _testResult.Foreground = new SolidColorBrush(Dim);
        stack.Children.Add(_testResult);

        return new Border
        {
            Background = new SolidColorBrush(Panel),
            BorderBrush = new SolidColorBrush(Line),
            BorderThickness = new Thickness(1),
            Child = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
        };
    }

    // -- rendering + interactions --------------------------------------------------

    private void Render()
    {
        PopulateTestGrid();
        _viewport.TestSelectedGrid = _testGrid.SelectedIndex;
        _viewport.RenderTrip(null, _selected);   // no cargo: just the ship's grids + hologram
        UpdateReviewStatus();
    }

    private void ToggleEdit()
    {
        if (_selected == null) return;
        _viewport.EditMode = !_viewport.EditMode;
        _editBtn.Content = _viewport.EditMode ? "Done editing" : "Edit layout";
        _editBtn.Background = new SolidColorBrush(_viewport.EditMode ? Cyan : Color.FromRgb(0x14, 0x1B, 0x24));
        _editBtn.Foreground = new SolidColorBrush(_viewport.EditMode ? Void : Fg);
        Logger.Info($"[UI] grid studio edit {(_viewport.EditMode ? "on" : "off")} ship={_selected.Id}");
        Render();
    }

    private void OnExport()
    {
        var ship = _selected;
        if (ship == null) return;

        var grids = _overrides.Has(ship.Id)
            ? _overrides.Get(ship.Id)!.ToList()
            : GridShareService.ToOverrides(ship.Grids);

        var handle = App.Settings?.Current?.DetectedRsiHandle ?? "";
        var dlg = new GridExportDialog(ship.DisplayName, handle) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true) return;

        var save = new Microsoft.Win32.SaveFileDialog
        {
            Filter = $"Nexus grid (*{GridShareService.FileExtension})|*{GridShareService.FileExtension}",
            FileName = $"{ship.Id}-{SanitizeForFile(dlg.Handle)}{GridShareService.FileExtension}",
        };
        if (save.ShowDialog() != true) return;

        var pkg = new GridSharePackage
        {
            ShipId = ship.Id, ShipName = ship.DisplayName, RsiHandle = dlg.Handle,
            Summary = dlg.Summary, Notes = dlg.Notes,
            CreatedUtc = DateTime.UtcNow.ToString("o"), AppVersion = AppInfo.Version,
            Grids = grids,
        };
        try
        {
            GridShareService.Export(pkg, save.FileName);
            Logger.Info($"[UI] cargo grid export saved ship={ship.Id} handle={dlg.Handle} grids={grids.Count}");
            SetResult($"Exported {ship.DisplayName} layout ({grids.Count} grids).", Cyan);
        }
        catch (Exception ex)
        {
            Logger.Error("Cargo grid export failed", ex);
            SetResult($"Export failed: {ex.Message}", Warn);
        }
    }

    private static string SanitizeForFile(string s)
    {
        var cleaned = new string((s ?? "").Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        return string.IsNullOrEmpty(cleaned) ? "anon" : cleaned;
    }

    private void OnImport()
    {
        var open = new Microsoft.Win32.OpenFileDialog
        {
            Filter = $"Nexus grid (*{GridShareService.FileExtension})|*{GridShareService.FileExtension}|All files (*.*)|*.*",
        };
        if (open.ShowDialog() != true) return;

        GridSharePackage pkg;
        try { pkg = GridShareService.Import(open.FileName); }
        catch (Exception ex)
        {
            Logger.Info($"[UI] cargo grid import rejected: {ex.Message}");
            SetResult($"Import rejected: {ex.Message}", Warn);
            return;
        }

        ShipCargoDef? preview;
        try { preview = _catalog.BuildPreview(pkg.ShipId, pkg.Grids); }
        catch (Exception ex)
        {
            Logger.Info($"[UI] cargo grid import rejected: {ex.Message}");
            SetResult($"Import rejected: {ex.Message}", Warn);
            return;
        }
        if (preview == null)
        {
            Logger.Info($"[UI] cargo grid import rejected: ship '{pkg.ShipId}' not in catalog");
            SetResult($"Import rejected: ship '{pkg.ShipId}' is not in the catalog.", Warn);
            return;
        }

        // Validate the diff BEFORE touching any UI state (viewport, panel). A hand-edited file can
        // carry duplicate grid Ids, which GridDiff.Compute rejects; catch that here so a bad file is
        // cleanly refused with nothing changed, instead of rendering a preview and then crashing.
        var targetShip = _catalog.ById(pkg.ShipId)!;
        var current = _overrides.Has(pkg.ShipId)
            ? _overrides.Get(pkg.ShipId)!.ToList()
            : GridShareService.ToOverrides(targetShip.Grids);
        GridDiffResult diff;
        try { diff = GridDiff.Compute(current, pkg.Grids); }
        catch (Exception ex)
        {
            Logger.Info($"[UI] cargo grid import rejected: {ex.Message}");
            SetResult($"Import rejected: {ex.Message}", Warn);
            return;
        }

        // Validated: select the ship for context, then render THEIR layout as a non-destructive preview.
        _selected = targetShip;
        RefreshShips();
        if (_viewport.EditMode) { _viewport.EditMode = false; SyncEditButton(); }
        _viewport.TestSelectedGrid = -1;
        _viewport.RenderTrip(null, preview);

        _pendingImport = (pkg.ShipId, pkg.ShipName, pkg.Grids);
        var handle = string.IsNullOrWhiteSpace(pkg.RsiHandle) ? "(no handle)" : pkg.RsiHandle;
        _importMeta.Text = $"From: {handle}\nShip: {pkg.ShipName}\nSummary: {pkg.Summary}\nNotes: {pkg.Notes}";
        _importDiff.Text = "Changes vs your version:\n  " + string.Join("\n  ", diff.Lines);
        _importPanel.Visibility = Visibility.Visible;
        SetResult($"Previewing submission for {pkg.ShipName}. Keep or Discard.", Cyan);
        Logger.Info($"[UI] cargo grid import loaded ship={pkg.ShipId} handle={pkg.RsiHandle} grids={pkg.Grids.Count} changes={diff.HasChanges}");
    }

    private void ApplyImport()
    {
        if (_pendingImport is not { } imp) return;
        _overrides.Set(imp.ShipId, imp.ShipName, imp.Grids);
        _catalog = CargoShipCatalog.LoadEmbedded().WithOverrides(_overrides);
        _selected = _catalog.ById(imp.ShipId) ?? _selected;
        _signoff.ClearChecks(imp.ShipId, imp.ShipName,
            CargoSignoffStore.ReviewItem.GridPosition, CargoSignoffStore.ReviewItem.GridProperties);
        _viewport.BumpEditRev();
        _pendingImport = null;
        _importPanel.Visibility = Visibility.Collapsed;
        RefreshShips();
        Render();
        Logger.Info($"[UI] cargo grid import kept ship={imp.ShipId} grids={imp.Grids.Count}");
        SetResult($"Kept submission for {imp.ShipName}.", Cyan);
    }

    private void DiscardImport()
    {
        var name = _pendingImport?.ShipName ?? "";
        _pendingImport = null;
        _importPanel.Visibility = Visibility.Collapsed;
        Render();
        Logger.Info($"[UI] cargo grid import discarded ship={name}");
        SetResult(string.IsNullOrEmpty(name) ? "" : $"Discarded submission for {name}.", Dim);
    }

    private void OnGridsSaved(string shipId, List<GridOverride> grids)
    {
        var name = _catalog.Ships.FirstOrDefault(s => s.Id == shipId)?.DisplayName ?? shipId;
        _overrides.Set(shipId, name, grids);
        _catalog = CargoShipCatalog.LoadEmbedded().WithOverrides(_overrides);
        _selected = _catalog.ById(shipId) ?? _selected;
        // Edited geometry invalidates the position and properties review; the hologram is independent.
        _signoff.ClearChecks(shipId, name,
            CargoSignoffStore.ReviewItem.GridPosition, CargoSignoffStore.ReviewItem.GridProperties);
        _viewport.BumpEditRev();
        RefreshShips();
        Render();
        Logger.Info($"[UI] grid studio layout saved for {name} grids={grids.Count}");
    }

    private void OnGridsReverted(string shipId)
    {
        _overrides.Clear(shipId);
        _catalog = CargoShipCatalog.LoadEmbedded().WithOverrides(_overrides);
        _selected = _catalog.ById(shipId) ?? _selected;
        _viewport.BumpEditRev();
        RefreshShips();
        Render();
    }

    // -- review checklist ----------------------------------------------------------

    private void RefreshShips()
    {
        _shipSelect.SelectionChanged -= OnShipSelected;   // avoid re-entrant renders while rebuilding
        _shipSelect.Items.Clear();
        foreach (var ship in _catalog.Ships)
            _shipSelect.Items.Add(new ShipRow(ship, ReviewMarker(ship.Id)));
        foreach (ShipRow r in _shipSelect.Items)
            if (r.Ship.Id == _selected?.Id) { _shipSelect.SelectedItem = r; break; }
        _shipSelect.SelectionChanged += OnShipSelected;
        UpdateReviewStatus();
    }

    private void OnShipSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_shipSelect.SelectedItem is ShipRow r) { _selected = r.Ship; Render(); }
    }

    private string ReviewMarker(string shipId)
    {
        if (_signoff.IsFlagged(shipId)) return "[!] ";
        if (_signoff.IsFullySignedOff(shipId)) return "[OK] ";
        int n = _signoff.CheckedCount(shipId);
        return n > 0 ? $"[{n}/{CargoSignoffStore.Items.Count}] " : "";
    }

    private Button ChecklistBtn(CargoSignoffStore.ReviewItem item)
    {
        var b = new Button
        {
            Padding = new Thickness(9, 5, 9, 5),
            Margin = new Thickness(0, 0, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Cursor = System.Windows.Input.Cursors.Hand,
            FontFamily = Mono, FontSize = 11,
            BorderThickness = new Thickness(1),
        };
        b.Click += (_, _) => ToggleCheck(item);
        return b;
    }

    private void ToggleCheck(CargoSignoffStore.ReviewItem item)
    {
        if (_selected == null) return;
        _signoff.SetCheck(_selected.Id, _selected.DisplayName, item, !_signoff.IsChecked(_selected.Id, item));
        RefreshShips();
    }

    private void ToggleFlag()
    {
        if (_selected == null) return;
        _signoff.SetFlagged(_selected.Id, _selected.DisplayName, !_signoff.IsFlagged(_selected.Id));
        RefreshShips();
    }

    private void UpdateReviewStatus()
    {
        var ship = _selected;
        int total = _catalog.Ships.Count;
        int ok = _signoff.CountFullySignedOff();
        int flagged = _signoff.CountFlagged();
        var tally = $"{ok}/{total} signed off" + (flagged > 0 ? $", {flagged} flagged" : "");

        foreach (var item in CargoSignoffStore.Items)
        {
            var b = _checkBtns[item];
            bool on = ship != null && _signoff.IsChecked(ship.Id, item);
            b.Content = (on ? "[x]  " : "[  ]  ") + CargoSignoffStore.Label(item);
            b.Foreground = new SolidColorBrush(on ? Cyan : Dim);
            b.Background = new SolidColorBrush(on ? Color.FromRgb(0x0D, 0x24, 0x22) : Color.FromRgb(0x14, 0x1B, 0x24));
            b.BorderBrush = new SolidColorBrush(on ? Cyan : Line);
            b.IsEnabled = ship != null;
        }

        bool isFlagged = ship != null && _signoff.IsFlagged(ship.Id);
        _flagBtn.Content = isFlagged ? "Clear flag" : "Flag issue";

        if (ship == null) { _reviewStatus.Text = tally; _reviewStatus.Foreground = new SolidColorBrush(Dim); return; }
        int checkedCount = _signoff.CheckedCount(ship.Id);
        if (isFlagged)
        {
            _reviewStatus.Text = $"FLAGGED  {_signoff.When(ship.Id)}   -   {tally}";
            _reviewStatus.Foreground = new SolidColorBrush(Warn);
        }
        else if (_signoff.IsFullySignedOff(ship.Id))
        {
            _reviewStatus.Text = $"SIGNED OFF  {_signoff.When(ship.Id)}   -   {tally}";
            _reviewStatus.Foreground = new SolidColorBrush(Cyan);
        }
        else
        {
            _reviewStatus.Text = $"{checkedCount}/{CargoSignoffStore.Items.Count} checked   -   {tally}";
            _reviewStatus.Foreground = new SolidColorBrush(Dim);
        }
    }

    // -- test fill -----------------------------------------------------------------

    private void PopulateTestGrid()
    {
        _testGrid.SelectionChanged -= OnTestGridChanged;   // rebuild without firing selection re-renders
        int prev = _testGrid.SelectedIndex;
        _testGrid.Items.Clear();
        if (_selected != null)
            for (int i = 0; i < _selected.Grids.Count; i++)
            {
                var g = _selected.Grids[i];
                _testGrid.Items.Add($"Grid {i + 1}  ({g.W}x{g.D}x{g.H}, {g.Capacity} SCU)");
            }
        int count = _selected?.Grids.Count ?? 0;
        _testGrid.SelectedIndex = count > 0 ? Math.Clamp(prev < 0 ? 0 : prev, 0, count - 1) : -1;
        _testGrid.SelectionChanged += OnTestGridChanged;
    }

    // Dropdown pick or a click on a grid in the 3D view: highlight it (pink) and clear any prior fill.
    private void OnTestGridChanged(object sender, SelectionChangedEventArgs e)
    {
        _viewport.TestSelectedGrid = _testGrid.SelectedIndex;
        _testResult.Text = "";
        _viewport.RenderTrip(null, _selected);
    }

    private void OnTestGridClicked(int index)
    {
        if (_selected == null || index < 0 || index >= _selected.Grids.Count) return;
        if (_testGrid.SelectedIndex != index) _testGrid.SelectedIndex = index;   // fires OnTestGridChanged
        else { _viewport.TestSelectedGrid = index; _viewport.RenderTrip(null, _selected); }
    }

    // Drop N generic containers of one SCU size into a single grid and report the first limit it
    // violates: the grid's accepted-size set, the grid's cell dimensions, or its capacity.
    private void TestFill()
    {
        var ship = _selected;
        if (ship == null || _testGrid.SelectedIndex < 0 || _testGrid.SelectedIndex >= ship.Grids.Count) return;
        if (_testSize.SelectedItem is not SizeRow sr) return;
        if (!int.TryParse(_testQty.Text.Trim(), out int qty) || qty <= 0) { SetResult("Enter a quantity of 1 or more.", Warn); return; }

        int gi = _testGrid.SelectedIndex;
        var grid = ship.Grids[gi];
        int scu = sr.Scu;
        var box = BoxType.Of(scu);

        // Size restriction 1: the grid's accepted-container set.
        if (!grid.Accepts(scu))
        {
            _viewport.RenderTrip(null, ship);
            SetResult($"SIZE RESTRICTION: Grid {gi + 1} does not accept {scu} SCU containers " +
                      $"(accepts {string.Join(", ", grid.AcceptedCaps)} SCU).", Warn);
            return;
        }

        // Size restriction 2: the container footprint must fit the grid's cells in some orientation.
        if (!box.Orientations.Any(o => o.W <= grid.W && o.D <= grid.D && o.H <= grid.H))
        {
            var f = box.Size;
            _viewport.RenderTrip(null, ship);
            SetResult($"SIZE RESTRICTION: a {scu} SCU container ({f.W}x{f.D}x{f.H}) does not fit " +
                      $"Grid {gi + 1} ({grid.W}x{grid.D}x{grid.H} cells).", Warn);
            return;
        }

        // Pack the containers into this one grid using the real packer.
        var occ = new GridOccupancy(grid);
        var placements = new List<Placement>();
        for (int i = 0; i < qty; i++)
        {
            var p = CargoPacker.TryPlaceInGrid(occ, new PackBox { Scu = scu, OrderIndex = i });
            if (p == null) break;
            placements.Add(p);
        }
        int placed = placements.Count;

        var result = new PackResult { Grids = new[] { occ } };
        result.Placed.AddRange(placements);
        if (_viewport.EditMode) { _viewport.EditMode = false; SyncEditButton(); }
        _viewport.TestSelectedGrid = gi;   // keep the filled grid highlighted
        _viewport.RenderTrip(result, ship);

        if (placed < qty)
            SetResult($"CAPACITY: only {placed} of {qty} fit ({placed * scu}/{qty * scu} SCU). " +
                      $"Grid {gi + 1} holds {grid.Capacity} SCU ({grid.W}x{grid.D}x{grid.H}).", Warn);
        else
            SetResult($"OK: {qty} x {scu} SCU fit in Grid {gi + 1} ({placed * scu}/{grid.Capacity} SCU).", Cyan);

        Logger.Info($"[UI] grid studio test-fill ship={ship.Id} grid={gi} scu={scu} qty={qty} placed={placed}");
    }

    private void SetResult(string text, Color color)
    {
        _testResult.Text = text;
        _testResult.Foreground = new SolidColorBrush(color);
    }

    private void SyncEditButton()
    {
        _editBtn.Content = "Edit layout";
        _editBtn.Background = new SolidColorBrush(Color.FromRgb(0x14, 0x1B, 0x24));
        _editBtn.Foreground = new SolidColorBrush(Fg);
    }

    // -- small styled builders -----------------------------------------------------

    private TextBlock Eyebrow(string t) => new()
    {
        Text = t, Foreground = new SolidColorBrush(Amber), FontFamily = Head, FontSize = 10.5,
        FontWeight = FontWeights.SemiBold,
    };

    private Button Btn(string text, RoutedEventHandler onClick, bool primary = false)
    {
        var b = new Button
        {
            Content = text, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 6, 0),
            Cursor = System.Windows.Input.Cursors.Hand, FontFamily = Mono, FontSize = 11,
            Foreground = new SolidColorBrush(primary ? Void : Fg),
            Background = new SolidColorBrush(primary ? Amber : Color.FromRgb(0x14, 0x1B, 0x24)),
            BorderBrush = new SolidColorBrush(primary ? Amber : Line),
            BorderThickness = new Thickness(1),
        };
        b.Click += onClick;
        return b;
    }

    private void StyleCombo(ComboBox c)
    {
        if (Application.Current.TryFindResource("NexusComboBox") is Style s) c.Style = s;
        c.FontFamily = Mono;
        c.FontSize = 11.5;
    }

    private void StyleBox(TextBox t)
    {
        t.Foreground = new SolidColorBrush(Fg);
        t.Background = new SolidColorBrush(Color.FromRgb(0x0E, 0x14, 0x1C));
        t.BorderBrush = new SolidColorBrush(Line);
        t.BorderThickness = new Thickness(1);
        t.Padding = new Thickness(6, 4, 6, 4);
        t.FontFamily = Mono; t.FontSize = 11.5;
        t.CaretBrush = new SolidColorBrush(Cyan);
    }

    private sealed class ShipRow
    {
        public ShipCargoDef Ship { get; }
        private readonly string _marker;
        public ShipRow(ShipCargoDef ship, string marker = "") { Ship = ship; _marker = marker; }
        public override string ToString() => $"{_marker}{Ship.DisplayName}  ({Ship.TotalScu} SCU)";
    }

    private sealed class SizeRow
    {
        public int Scu { get; }
        public SizeRow(int scu) { Scu = scu; }
        public override string ToString() => $"{Scu} SCU";
    }
}
