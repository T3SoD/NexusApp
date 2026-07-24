using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NexusApp.Models.Cargo;
using NexusApp.Services;
using NexusApp.Services.Cargo;
using static NexusApp.Views.CargoUiKit;

namespace NexusApp.Views;

// Approved-contributor dev tooling (gated by RSI handle in MainWindow): review and edit ship
// cargo-grid layouts against the 3D view and hull hologram. Kept OUT of the shippable Cargo Planner.
// Always in layout-edit mode (drag/resize/add/delete grids, then Save or Revert in the 3D view); the
// three-item checklist records the review and persists per user. Owner-only: import a contributor's
// .nexusgrid submission and selectively merge it. Mouse-driven.
public sealed class GridStudioPage : UserControl
{
    private readonly CargoWebView _viewport = new();
    private readonly ComboBox _shipSelect = new();
    private readonly CargoSignoffStore _signoff = CargoSignoffStore.LoadDefault();
    private readonly CargoGridOverrideStore _overrides = CargoGridOverrideStore.LoadDefault();
    private readonly CargoOverrideProvenanceStore _provenance = CargoOverrideProvenanceStore.LoadDefault();
    private readonly Dictionary<CargoSignoffStore.ReviewItem, Button> _checkBtns = new();
    private readonly TextBlock _reviewStatus = new();
    private readonly TextBlock _provLine = new();   // "applied: handle, date, aspects" for an applied override
    private readonly TextBlock _statusLine = new(); // export / import / apply feedback (top of the rail)
    private Button _flagBtn = null!;

    // Approved contributors (owner + beta testers): load a contributor's .nexusgrid submission, preview
    // it in the 3D view, pick aspects, then keep (persist as override) or discard. The panel is a thin
    // view; this page does the persistence in OnImportApplied.
    private Button _importBtn = null!;
    private readonly GridImportReviewPanel _importPanel;

    // Batch import: the owner can select several .nexusgrid files at once; they are reviewed one at a
    // time (each with the same aspect selection + preview), and a summary is shown when the queue drains.
    private readonly Queue<string> _importQueue = new();
    private int _batchTotal, _batchApplied, _batchDiscarded, _batchSkipped;

    // Test-fill: drop N generic containers of one SCU size into a single grid to check it.
    private readonly ComboBox _testGrid = new();
    private readonly ComboBox _testSize = new();
    private readonly TextBox _testQty = new();
    private readonly TextBlock _testResult = new();

    private CargoShipCatalog _catalog;
    private ShipCargoDef? _selected;

    public GridStudioPage()
    {
        _catalog = CargoShipCatalog.LoadEmbedded().WithOverrides(_overrides);
        _viewport.Studio = true;   // orange, numbered, clickable grids
        _viewport.GridsSaved += OnGridsSaved;
        _viewport.GridsReverted += OnGridsReverted;
        _viewport.TestGridClicked += OnTestGridClicked;

        // The review panel owns no stores/catalog: it merges via these delegates (evaluated lazily so
        // they always read the current catalog/overrides) and this page persists in OnImportApplied.
        _importPanel = new GridImportReviewPanel(CurrentFor, (id, grids) => _catalog.BuildPreview(id, grids));
        _importPanel.Applied += OnImportApplied;
        _importPanel.Discarded += OnImportDiscarded;
        _importPanel.PreviewRequested += OnImportPreview;
        _importPanel.Status += (m, warn) => SetStatus(m, warn ? Warn : Cyan);

        Build();
        InteractionLog.Nav("Grid Studio");
        _selected = _catalog.Ships.FirstOrDefault();
        RefreshShips();
        Render();
    }

    // Re-shown from the dock: pick up overrides/sign-offs changed elsewhere (or by another process)
    // and re-render.
    public void OnShown()
    {
        _overrides.Reload();
        _signoff.Reload();
        _provenance.Reload();
        ReloadCatalog(_selected?.Id);
        RefreshShips();
        Render();
    }

    // -- layout --------------------------------------------------------------------

    private void Build()
    {
        Background = new SolidColorBrush(Bg);
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

        // Dedicated feedback line for export / import / apply / batch actions, so they no longer land on
        // the Test Fill result line at the bottom of the rail.
        _statusLine.FontFamily = Mono; _statusLine.FontSize = 11;
        _statusLine.TextWrapping = TextWrapping.Wrap;
        _statusLine.Foreground = new SolidColorBrush(Dim);
        _statusLine.Margin = new Thickness(0, 0, 0, 10);
        _statusLine.Visibility = Visibility.Collapsed;
        stack.Children.Add(_statusLine);

        var exportBtn = Btn("Export layout", (_, _) => OnExport());
        exportBtn.ToolTip = "Save this ship's cargo grid layout to a .nexusgrid file to share";
        exportBtn.Margin = new Thickness(0, 0, 6, 16);
        stack.Children.Add(exportBtn);

        _importBtn = Btn("Import submission", (_, _) => OnImport());
        _importBtn.ToolTip = "Load a contributor's .nexusgrid file to compare and keep or discard";
        _importBtn.Margin = new Thickness(0, 0, 6, 12);
        _importBtn.Visibility = AccessGate.IsApprovedActive ? Visibility.Visible : Visibility.Collapsed;
        stack.Children.Add(_importBtn);

        var patchBtn = Btn("Export to catalog patch", (_, _) => OnExportCatalogPatch());
        patchBtn.ToolTip = "Write this ship's grids as a cargo_ships.json patch to promote into the embedded catalog";
        patchBtn.Margin = new Thickness(0, 0, 6, 12);
        patchBtn.Visibility = OwnerGate.IsOwnerActive ? Visibility.Visible : Visibility.Collapsed;
        stack.Children.Add(patchBtn);

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
        _reviewStatus.Margin = new Thickness(0, 4, 0, 6);
        _reviewStatus.TextWrapping = TextWrapping.Wrap;
        _reviewStatus.Foreground = new SolidColorBrush(Dim);
        stack.Children.Add(_reviewStatus);

        _provLine.FontFamily = Mono;
        _provLine.FontSize = 10.5;
        _provLine.Margin = new Thickness(0, 0, 0, 12);
        _provLine.TextWrapping = TextWrapping.Wrap;
        _provLine.Foreground = new SolidColorBrush(Dim);
        _provLine.Visibility = Visibility.Collapsed;
        stack.Children.Add(_provLine);

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
        testBtns.Children.Add(Btn("Back to editing", (_, _) => { _testResult.Text = ""; Render(); }));
        stack.Children.Add(testBtns);

        _testResult.FontFamily = Mono;
        _testResult.FontSize = 11;
        _testResult.TextWrapping = TextWrapping.Wrap;
        _testResult.Foreground = new SolidColorBrush(Dim);
        stack.Children.Add(_testResult);

        return RailBorder(new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
    }

    // -- rendering + interactions --------------------------------------------------

    private void Render()
    {
        // While an import review is open the viewport is locked to the merged preview; keep it there
        // instead of re-entering the editor, so nothing silently replaces what the owner is judging.
        if (_importPanel.IsActive) { _importPanel.RefreshPreview(); return; }

        // Grid Studio is always in layout-editing mode (no toggle). Test Fill flips to a view render
        // transiently; the next Render restores editing.
        PopulateTestGrid();
        _viewport.TestSelectedGrid = _testGrid.SelectedIndex;
        _viewport.RenderTrip(null, _selected, CargoWebView.CargoViewMode.Edit);
        UpdateReviewStatus();
    }

    // The owner's current grids for a ship: the saved override if present, else the effective embedded
    // layout. Recomputed on demand so it never goes stale against a save made mid-review.
    private List<GridOverride> CurrentFor(string shipId) =>
        _overrides.Has(shipId)
            ? _overrides.Get(shipId)!.ToList()
            : GridShareService.ToOverrides(_catalog.ById(shipId)?.Grids ?? (IReadOnlyList<GridDef>)Array.Empty<GridDef>());

    // Rebuild the catalog with the current overrides and re-select the kept ship (or the first). Used
    // after every save / revert / apply and on show, so that reload-reselect idiom lives in one place.
    private void ReloadCatalog(string? keepSelectedId)
    {
        _catalog = CargoShipCatalog.LoadEmbedded().WithOverrides(_overrides);
        _selected = (keepSelectedId != null ? _catalog.ById(keepSelectedId) : null) ?? _catalog.Ships.FirstOrDefault();
    }

    private void OnExport()
    {
        var ship = _selected;
        if (ship == null) return;
        if (_importPanel.IsActive) { SetStatus("Finish the import review first (Apply or Discard).", Warn); return; }
        if (_viewport.HasUnsavedEdits)
        {
            SetStatus("Save the layout first. Export shares the saved version, not unsaved edits.", Warn);
            return;
        }

        // Export the ship's effective grids (what is on screen: the saved override or the embedded
        // layout), so the file always matches the display and null positions round-trip as null.
        var grids = GridShareService.ToOverrides(ship.Grids);

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
            Flagged = dlg.Flagged, FlagNote = dlg.FlagNote,
            CreatedUtc = DateTime.UtcNow.ToString("o"), AppVersion = AppInfo.Version,
            Grids = grids,
        };
        try
        {
            GridShareService.Export(pkg, save.FileName);
            Logger.Info($"[UI] cargo grid export saved ship={ship.Id} handle={dlg.Handle} grids={grids.Count}");
            SetStatus($"Exported {ship.DisplayName} layout ({grids.Count} grids).", Cyan);
        }
        catch (Exception ex)
        {
            Logger.Error("Cargo grid export failed", ex);
            SetStatus($"Export failed: {ex.Message}", Warn);
        }
    }

    // Owner-only: promote this ship's effective grids into the embedded catalog by writing a
    // cargo_ships.json-shaped patch to a file, for the owner to paste-merge and commit. No pipeline.
    private void OnExportCatalogPatch()
    {
        var ship = _selected;
        if (ship == null) return;
        if (_importPanel.IsActive) { SetStatus("Finish the import review first (Apply or Discard).", Warn); return; }
        var save = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Cargo catalog patch (*.grids.json)|*.grids.json|JSON (*.json)|*.json",
            FileName = $"{ship.Id}.grids.json",
        };
        if (save.ShowDialog() != true) return;
        try
        {
            System.IO.File.WriteAllText(save.FileName, CatalogPatchExport.ToPatchJson(ship));
            Logger.Info($"[UI] cargo grid catalog patch exported ship={ship.Id}");
            SetStatus($"Exported catalog patch for {ship.DisplayName}.", Cyan);
        }
        catch (Exception ex)
        {
            Logger.Error("Cargo catalog patch export failed", ex);
            SetStatus($"Export failed: {ex.Message}", Warn);
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
            Multiselect = true,
        };
        if (open.ShowDialog() != true) return;

        _importQueue.Clear();
        foreach (var f in open.FileNames) _importQueue.Enqueue(f);
        _batchTotal = _importQueue.Count;
        _batchApplied = _batchDiscarded = _batchSkipped = 0;
        if (_batchTotal > 1) Logger.Info($"[UI] cargo grid batch import: {_batchTotal} files queued");
        PumpImportQueue();
    }

    // Open the next queued submission for review; a file that fails validation is logged, counted, and
    // skipped so one bad file does not abort the batch. When the queue drains, show the batch summary.
    // Single-file import runs through this same path as a one-element queue.
    private void PumpImportQueue()
    {
        while (_importQueue.Count > 0)
        {
            if (TryBeginImport(_importQueue.Dequeue())) return;   // opened; Applied/Discarded pumps the next
            _batchSkipped++;
        }
        if (_batchTotal > 1)
            SetStatus($"Batch complete: {_batchApplied} applied, {_batchDiscarded} discarded, {_batchSkipped} skipped.", Cyan);
    }

    // Validate one .nexusgrid and hand it to the review panel. Returns false (with a logged reason and a
    // status line) if the file is rejected, so the queue can skip it. A hand-edited file can carry
    // duplicate grid Ids, which GridDiff.Compute rejects; that is caught here too.
    private bool TryBeginImport(string path)
    {
        var file = System.IO.Path.GetFileName(path);
        GridSharePackage pkg;
        try { pkg = GridShareService.Import(path); }
        catch (Exception ex) { Logger.Info($"[UI] cargo grid import rejected ({file}): {TextSanitizer.ForLog(ex.Message)}"); SetStatus($"Skipped {file}: {ex.Message}", Warn); return false; }

        ShipCargoDef? preview;
        try { preview = _catalog.BuildPreview(pkg.ShipId, pkg.Grids); }
        catch (Exception ex) { Logger.Info($"[UI] cargo grid import rejected ({file}): {TextSanitizer.ForLog(ex.Message)}"); SetStatus($"Skipped {file}: {ex.Message}", Warn); return false; }
        if (preview == null) { Logger.Info($"[UI] cargo grid import rejected ({file}): ship '{TextSanitizer.ForLog(pkg.ShipId)}' not in catalog"); SetStatus($"Skipped {file}: ship '{pkg.ShipId}' is not in the catalog.", Warn); return false; }

        var targetShip = _catalog.ById(pkg.ShipId)!;
        var current = CurrentFor(pkg.ShipId);
        GridDiffResult diff;
        try { diff = GridDiff.Compute(current, pkg.Grids); }
        catch (Exception ex) { Logger.Info($"[UI] cargo grid import rejected ({file}): {TextSanitizer.ForLog(ex.Message)}"); SetStatus($"Skipped {file}: {ex.Message}", Warn); return false; }

        _selected = targetShip;
        RefreshShips();
        _importPanel.Begin(pkg, targetShip, current, diff);
        var more = _importQueue.Count > 0 ? $" ({_importQueue.Count} more queued)" : "";
        SetStatus($"Reviewing submission for {targetShip.DisplayName}. Pick aspects, then Apply selected.{more}", Cyan);
        // ShipId is the only untrusted field here (RsiHandle/ShipName/etc. are sanitized by
        // GridShareService.Import); route it through the shared log sanitizer as defense-in-depth.
        Logger.Info($"[UI] cargo grid import loaded ship={TextSanitizer.ForLog(pkg.ShipId)} handle={pkg.RsiHandle} grids={pkg.Grids.Count} changes={diff.HasChanges} flagged={pkg.Flagged}");
        return true;
    }

    // -- import review panel handlers ----------------------------------------------

    // The owner accepted a merge: persist it as this ship's override, reload the catalog, and clear the
    // position + properties sign-offs (edited geometry invalidates that review; the hologram is
    // independent). BumpEditRev re-seeds the editor from the reloaded catalog.
    private void OnImportApplied(string shipId, string shipName, List<GridOverride> merged, GridMergeAspect aspects, GridSharePackage pkg)
    {
        if (!_overrides.Set(shipId, shipName, merged))
        {
            SetStatus($"Not applied: could not write {shipName} to disk. See nexus.log.", Warn);
            return;
        }
        _provenance.Set(shipId, new OverrideProvenance
        {
            Handle = pkg.RsiHandle, Summary = pkg.Summary, Notes = pkg.Notes,
            CreatedUtc = pkg.CreatedUtc, AppliedUtc = DateTime.UtcNow.ToString("o"), Aspects = aspects.ToString(),
        });
        ReloadCatalog(shipId);
        _signoff.ClearChecks(shipId, shipName,
            CargoSignoffStore.ReviewItem.GridPosition, CargoSignoffStore.ReviewItem.GridProperties);
        _viewport.BumpEditRev();
        RefreshShips();
        Render();
        Logger.Info($"[UI] cargo grid import applied ship={shipId} aspects={aspects} grids={merged.Count}");
        SetStatus($"Applied submission aspects ({aspects}) to {shipName}.", Cyan);
        _batchApplied++;
        PumpImportQueue();
    }

    private void OnImportDiscarded(string shipName)
    {
        Render();
        Logger.Info($"[UI] cargo grid import discarded ship={shipName}");
        SetStatus(string.IsNullOrEmpty(shipName) ? "" : $"Discarded submission for {shipName}.", Dim);
        _batchDiscarded++;
        PumpImportQueue();
    }

    // The panel asks for a non-destructive render of the merged preview.
    private void OnImportPreview(ShipCargoDef preview)
    {
        _viewport.TestSelectedGrid = -1;
        _viewport.RenderTrip(null, preview, CargoWebView.CargoViewMode.View);
    }

    private void OnGridsSaved(string shipId, List<GridOverride> grids)
    {
        var name = _catalog.Ships.FirstOrDefault(s => s.Id == shipId)?.DisplayName ?? shipId;

        // The editor does not cap grid count; enforce the same ceiling as an imported file so a runaway
        // add cannot bloat the override.
        if (grids.Count > GridShareService.MaxGrids)
        {
            SetStatus($"Not saved: too many grids ({grids.Count}); the limit is {GridShareService.MaxGrids}.", Warn);
            _viewport.AckSave(false, $"Not saved: too many grids (limit {GridShareService.MaxGrids}).");
            return;
        }

        // Same physical-consistency gate as an applied import: the editor lets caps and dimensions be set
        // independently, so refuse to persist a grid that accepts a container it cannot hold.
        ShipCargoDef? built;
        try { built = _catalog.BuildPreview(shipId, grids); }
        catch (Exception ex) { SetStatus($"Not saved: {ex.Message}", Warn); _viewport.AckSave(false, $"Not saved: {ex.Message}"); return; }
        var problem = built != null ? GridValidation.FirstProblem(built.Grids) : null;
        if (problem != null) { SetStatus($"Not saved: {problem}.", Warn); _viewport.AckSave(false, $"Not saved: {problem}."); return; }

        // A schematic ship (no datamined positions) becomes positioned once saved from the editor; note it.
        bool wasSchematic = _catalog.ById(shipId)?.Grids.Any(g => !g.HasPos) ?? false;

        if (!_overrides.Set(shipId, name, grids))
        {
            SetStatus($"Not saved: could not write {name} to disk. See nexus.log.", Warn);
            _viewport.AckSave(false, "Save failed: could not write to disk.");
            return;
        }
        _provenance.Clear(shipId);   // owner-authored edit: no contributor provenance
        ReloadCatalog(shipId);
        // Edited geometry invalidates the position and properties review; the hologram is independent.
        _signoff.ClearChecks(shipId, name,
            CargoSignoffStore.ReviewItem.GridPosition, CargoSignoffStore.ReviewItem.GridProperties);
        _viewport.BumpEditRev();
        RefreshShips();
        Render();
        if (wasSchematic) SetStatus($"Saved {name}. Positions are now set for this ship.", Cyan);
        Logger.Info($"[UI] grid studio layout saved for {name} grids={grids.Count}");
    }

    private void OnGridsReverted(string shipId)
    {
        _overrides.Clear(shipId);
        _provenance.Clear(shipId);
        ReloadCatalog(shipId);
        _viewport.BumpEditRev();
        RefreshShips();
        Render();
    }

    // -- review checklist ----------------------------------------------------------

    private void RefreshShips()
    {
        _shipSelect.SelectionChanged -= OnShipSelected;   // avoid re-entrant renders while rebuilding
        _shipSelect.Items.Clear();
        // Display order is alphabetical for quick scanning; the catalog keeps its hauling-priority
        // order (that still drives the default selection), so we sort only the rows shown here.
        foreach (var ship in _catalog.Ships.OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase))
            _shipSelect.Items.Add(new ShipRow(ship, ReviewMarker(ship.Id)));
        foreach (ShipRow r in _shipSelect.Items)
            if (r.Ship.Id == _selected?.Id) { _shipSelect.SelectedItem = r; break; }
        _shipSelect.SelectionChanged += OnShipSelected;
        UpdateReviewStatus();
    }

    private void OnShipSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_shipSelect.SelectedItem is not ShipRow r) return;
        // Changing ships abandons any open import review (the preview was for the other ship). If a batch
        // was queued behind it, drop the rest and say so, rather than silently stranding the queue.
        if (_importPanel.IsActive && _importPanel.ActiveShipId != r.Ship.Id)
        {
            _importPanel.Cancel();
            int remaining = _importQueue.Count;
            _importQueue.Clear();
            SetStatus(remaining > 0
                ? $"Import review cancelled (ship changed); {remaining} queued file(s) not reviewed."
                : "Import review cancelled (ship changed).", Dim);
        }
        _selected = r.Ship;
        Render();
    }

    private string ReviewMarker(string shipId)
    {
        string review;
        if (_signoff.IsFlagged(shipId)) review = "[!] ";
        else if (_signoff.IsFullySignedOff(shipId))
        {
            // "[OK~]" marks a sign-off whose reviewed geometry has since changed (drift).
            var ship = _catalog.ById(shipId);
            bool stale = ship != null && _signoff.IsStale(shipId, GridFingerprint.Of(ship.Grids));
            review = stale ? "[OK~] " : "[OK] ";
        }
        else
        {
            int n = _signoff.CheckedCount(shipId);
            review = n > 0 ? $"[{n}/{CargoSignoffStore.Items.Count}] " : "";
        }
        // Prefix "*" when the ship carries a local override (an applied submission or a hand edit).
        return _overrides.Has(shipId) ? "*" + (review.Length > 0 ? review : " ") : review;
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
        // Only the geometry-related checks fingerprint the grids; the hologram check is geometry-
        // independent and must NOT reset the fingerprint (that would mask drift on the geometry aspects
        // when a sign-off is completed incrementally across a grid change).
        var fp = item == CargoSignoffStore.ReviewItem.Hologram ? "" : GridFingerprint.Of(_selected.Grids);
        if (!_signoff.SetCheck(_selected.Id, _selected.DisplayName, item, !_signoff.IsChecked(_selected.Id, item), fp))
            SetStatus("Could not save review state to disk. See nexus.log.", Warn);
        RefreshShips();
    }

    private void ToggleFlag()
    {
        if (_selected == null) return;
        if (!_signoff.SetFlagged(_selected.Id, _selected.DisplayName, !_signoff.IsFlagged(_selected.Id)))
            SetStatus("Could not save review state to disk. See nexus.log.", Warn);
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

        // Provenance line: who a currently-applied override came from (empty for owner-authored edits).
        var prov = ship != null ? _provenance.Get(ship.Id) : null;
        if (prov != null)
        {
            var date = prov.AppliedUtc.Length >= 10 ? prov.AppliedUtc[..10] : prov.AppliedUtc;
            var who = string.IsNullOrWhiteSpace(prov.Handle) ? "(no handle)" : prov.Handle;
            _provLine.Text = $"applied: {who}{(date.Length > 0 ? "  " + date : "")}  [{prov.Aspects}]";
            _provLine.Visibility = Visibility.Visible;
        }
        else _provLine.Visibility = Visibility.Collapsed;

        if (ship == null) { _reviewStatus.Text = tally; _reviewStatus.Foreground = new SolidColorBrush(Dim); return; }
        int checkedCount = _signoff.CheckedCount(ship.Id);
        if (isFlagged)
        {
            _reviewStatus.Text = $"FLAGGED  {_signoff.When(ship.Id)}   -   {tally}";
            _reviewStatus.Foreground = new SolidColorBrush(Warn);
        }
        else if (_signoff.IsFullySignedOff(ship.Id))
        {
            bool stale = _signoff.IsStale(ship.Id, GridFingerprint.Of(ship.Grids));
            _reviewStatus.Text = stale
                ? $"SIGNED OFF (reviewed against older data)  {_signoff.When(ship.Id)}   -   {tally}"
                : $"SIGNED OFF  {_signoff.When(ship.Id)}   -   {tally}";
            _reviewStatus.Foreground = new SolidColorBrush(stale ? Warn : Cyan);
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

    // The dropdown pick is the Fill grid target. In edit mode the pink selection is the editor's own
    // (editState.selected); clicking a grid in the 3D view syncs this dropdown via OnTestGridClicked,
    // so Fill grid always targets the grid the user last picked either way. No re-render needed.
    private void OnTestGridChanged(object sender, SelectionChangedEventArgs e)
    {
        _testResult.Text = "";
    }

    // A grid was clicked in the 3D view (posts testGridSelected). Sync the Fill target to it.
    private void OnTestGridClicked(int index)
    {
        if (_selected == null || index < 0 || index >= _selected.Grids.Count) return;
        if (_testGrid.SelectedIndex != index) _testGrid.SelectedIndex = index;
    }

    // Drop N generic containers of one SCU size into a single grid and report the first limit it
    // violates: the grid's accepted-size set, the grid's cell dimensions, or its capacity.
    private void TestFill()
    {
        if (_importPanel.IsActive) { SetResult("Finish the import review first (Apply or Discard).", Warn); return; }
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
            _viewport.RenderTrip(null, ship, CargoWebView.CargoViewMode.View);
            SetResult($"SIZE RESTRICTION: Grid {gi + 1} does not accept {scu} SCU containers " +
                      $"(accepts {string.Join(", ", grid.AcceptedCaps)} SCU).", Warn);
            return;
        }

        // Size restriction 2: the container footprint must fit the grid's cells in some orientation.
        if (!box.Orientations.Any(o => o.W <= grid.W && o.D <= grid.D && o.H <= grid.H))
        {
            var f = box.Size;
            _viewport.RenderTrip(null, ship, CargoWebView.CargoViewMode.View);
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
        _viewport.TestSelectedGrid = gi;   // keep the filled grid highlighted
        _viewport.RenderTrip(result, ship, CargoWebView.CargoViewMode.View);

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

    // Top-of-rail feedback for export / import / apply / batch actions (kept separate from the Test Fill
    // result line so the two do not overwrite each other).
    private void SetStatus(string text, Color color)
    {
        _statusLine.Text = text;
        _statusLine.Foreground = new SolidColorBrush(color);
        _statusLine.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    // -- small styled builders -----------------------------------------------------

    private sealed class SizeRow
    {
        public int Scu { get; }
        public SizeRow(int scu) { Scu = scu; }
        public override string ToString() => $"{Scu} SCU";
    }
}
