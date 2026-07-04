namespace NexusApp.Models.Cargo;

// One cargo grid of a ship in cell space. H is the vertical (gravity) extent, normalized at
// load time so the runtime never has to guess which axis is up. Capacity equals the cell count.
public sealed class GridDef
{
    public int Id { get; init; }
    public int W { get; init; }              // x extent, cells
    public int D { get; init; }              // y extent, cells
    public int H { get; init; }              // z extent, cells (vertical)

    // The standard container sizes this grid accepts. A single datamined "max cap" loads as
    // every standard size up to that cap; the layout editor can narrow it to an exact set (some
    // grids reject a size they are physically big enough for, e.g. the Caterpillar front hold).
    public IReadOnlyList<int> AcceptedCaps { get; init; } = Array.Empty<int>();
    public string Name { get; init; } = "";

    // Real ship-space placement datamined from the hull geometry (cells; ship X lateral,
    // Y fore-aft, Z vertical; Pos* is the grid volume CENTER). Null when the geometry does
    // not resolve - the viewport then falls back to the synthetic side-by-side layout.
    public double? PosX { get; init; }
    public double? PosY { get; init; }
    public double? PosZ { get; init; }
    public bool WAlongShipY { get; init; }   // the grid's W axis runs fore-aft, not lateral

    public bool HasPos => PosX.HasValue && PosY.HasValue && PosZ.HasValue;

    public int Capacity => W * D * H;        // == SCU capacity (validated on load)

    // Largest accepted container; the packer still treats this as the grid's cap.
    public int MaxContainerScu => AcceptedCaps.Count > 0 ? AcceptedCaps.Max() : 0;

    public bool Accepts(int scu) => AcceptedCaps.Contains(scu);
}

// A ship's full cargo grid definition, embedded from the verified FleetYards data.
public sealed class ShipCargoDef
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Manufacturer { get; init; } = "";
    public string Classification { get; init; } = "";
    // Hauling-priority rank (1 = most used for hauling; 0 = unranked, sorts last). Orders the
    // catalog so the ships players actually haul with surface first.
    public int Priority { get; init; }
    public IReadOnlyList<GridDef> Grids { get; init; } = Array.Empty<GridDef>();

    public int TotalScu => Grids.Sum(g => g.Capacity);
    public int GridCount => Grids.Count;
    public int MaxContainerScu => Grids.Count == 0 ? 0 : Grids.Max(g => g.MaxContainerScu);
}
