using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using NexusApp.Models;
using NexusApp.Models.Cargo;
using NexusApp.Services;
using NexusApp.Services.Cargo;

namespace NexusApp.ViewModels;

// Owns the cargo-planner state and drives the tested packing engine: the manifest, the selected
// ship, the current multi-trip plan, and the selected trip. Pure orchestration (no WPF types), so
// it is unit-testable; the views bind to it. Every repack logs a [CARGO] line (definition-of-done).
public sealed class CargoPlannerViewModel : ObservableObject
{
    private CargoShipCatalog _catalog;

    public CargoPlannerViewModel(CargoShipCatalog catalog, ShipCargoDef? initialShip = null)
    {
        _catalog = catalog;
        _selectedShip = initialShip ?? catalog.Ships.FirstOrDefault();
        Repack();
    }

    // Swap in a rebuilt catalog (e.g. after a grid-layout override changes) and re-resolve the
    // selected ship by id so its corrected grids take effect without losing the manifest.
    public void ReloadCatalog(CargoShipCatalog catalog)
    {
        _catalog = catalog;
        var id = SelectedShip?.Id;
        SelectedShip = (id != null ? catalog.ById(id) : null) ?? catalog.Ships.FirstOrDefault();
        Repack();
        OnPropertyChanged(nameof(Ships));
    }

    public ObservableCollection<ManifestItem> Manifest { get; } = new();
    public IReadOnlyList<ShipCargoDef> Ships => _catalog.Ships;

    private ShipCargoDef? _selectedShip;
    public ShipCargoDef? SelectedShip
    {
        get => _selectedShip;
        private set => SetProperty(ref _selectedShip, value);
    }

    private CargoPlan? _plan;
    public CargoPlan? Plan
    {
        get => _plan;
        private set => SetProperty(ref _plan, value);
    }

    private int _selectedTripIndex;
    public int SelectedTripIndex
    {
        get => _selectedTripIndex;
        set => SetProperty(ref _selectedTripIndex, value);
    }

    public int ManifestScu => Manifest.Sum(i => i.Scu);
    public int TripCount => Plan?.TripCount ?? 0;
    public int UnplaceableScu => Plan?.Unplaceable.Sum(b => b.Scu) ?? 0;

    public PackResult? CurrentTrip =>
        Plan != null && SelectedTripIndex >= 0 && SelectedTripIndex < Plan.Trips.Count
            ? Plan.Trips[SelectedTripIndex]
            : null;

    public ManifestItem AddManualLine(string label, int scu, int? cap)
    {
        var item = new ManifestItem
        {
            Label = string.IsNullOrWhiteSpace(label) ? "Cargo" : label.Trim(),
            Scu = Math.Max(0, scu),
            Cap = cap,
            CapSource = cap.HasValue ? CapSource.User : CapSource.Default,
            Source = ManifestSource.Manual,
            ColorId = Manifest.Count,
        };
        Manifest.Add(item);
        Repack();
        return item;
    }

    public void RemoveLine(ManifestItem item)
    {
        if (Manifest.Remove(item)) Repack();
    }

    public void ClearManifest()
    {
        if (Manifest.Count == 0) return;
        Manifest.Clear();
        Repack();
    }

    public void ImportFromHauls(IReadOnlyList<Haul> hauls)
    {
        var imported = CargoImportService.FromActiveHauls(hauls);
        foreach (var item in imported) Manifest.Add(item);
        int fromOcr = imported.Count(i => i.CapSource == CapSource.Ocr);
        Logger.Info($"[CARGO] import lines={imported.Count} caps_from_ocr={fromOcr}");
        Repack();
    }

    public void SelectShip(ShipCargoDef ship)
    {
        SelectedShip = ship;
        Logger.Info($"[CARGO] ship-select {ship.Id} grids={ship.GridCount} scu={ship.TotalScu}");
        Repack();
    }

    public List<ShipFitResult> RankShips() => ShipFitSearch.Rank(_catalog.Ships, Manifest.ToList());

    private void Repack()
    {
        if (SelectedShip is null)
        {
            Plan = null;
            return;
        }
        var boxes = CargoPacker.BuildBoxes(Manifest.ToList(), SelectedShip.MaxContainerScu);
        var plan = MultiTripPlanner.Plan(SelectedShip.Grids, boxes);
        Plan = plan;
        SelectedTripIndex = 0;
        Logger.Info($"[CARGO] pack ship={SelectedShip.Id} scu={ManifestScu} " +
                    $"trips={plan.TripCount} unplaceable_scu={UnplaceableScu}");
        OnPropertyChanged(nameof(ManifestScu));
        OnPropertyChanged(nameof(TripCount));
        OnPropertyChanged(nameof(CurrentTrip));
    }
}
