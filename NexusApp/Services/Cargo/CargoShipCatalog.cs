using System.IO;
using System.Text.Json;
using NexusApp.Models.Cargo;

namespace NexusApp.Services.Cargo;

// The embedded, offline catalog of ship cargo grids (FleetYards data, verified and normalized at
// build time). Loaded once from the embedded resource; no network. Also backs the ship-fit search.
public sealed class CargoShipCatalog
{
    public IReadOnlyList<ShipCargoDef> Ships { get; }

    public CargoShipCatalog(IEnumerable<ShipCargoDef> ships) =>
        // Hauling priority first (1 = most used; 0 = unranked, sorts last), then name.
        Ships = ships
            .OrderBy(s => s.Priority <= 0 ? int.MaxValue : s.Priority)
            .ThenBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static CargoShipCatalog LoadEmbedded()
    {
        using var stream = typeof(CargoShipCatalog).Assembly
            .GetManifestResourceStream("NexusApp.Data.cargo_ships.json")
            ?? throw new InvalidOperationException("cargo_ships.json embedded resource not found");
        return Load(stream);
    }

    public static CargoShipCatalog Load(Stream stream)
    {
        var raw = JsonSerializer.Deserialize<List<RawShip>>(stream, JsonOpts)
                  ?? new List<RawShip>();
        return new CargoShipCatalog(raw.Select(ToDef));
    }

    public ShipCargoDef? ById(string id) =>
        Ships.FirstOrDefault(s => s.Id == id);

    public IEnumerable<ShipCargoDef> Search(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return Ships;
        var q = query.Trim();
        return Ships.Where(s =>
            s.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            s.Manufacturer.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    // Apply per-user local grid corrections on top of the embedded catalog. Each edited ship is
    // rebuilt from its full override grid set (recomputing TotalScu and layout); unedited ships
    // pass through unchanged. A corrupt override falls back to the embedded layout rather than
    // crashing the planner. Returns this instance when nothing is overridden.
    public CargoShipCatalog WithOverrides(CargoGridOverrideStore overrides)
    {
        if (overrides.EditedShipIds.Count == 0) return this;
        var merged = Ships.Select(s =>
        {
            var ov = overrides.Get(s.Id);
            if (ov == null) return s;
            try
            {
                var grids = ov.Select(o =>
                    BuildGrid(o.Id, o.W, o.D, o.H, o.Cap, o.Accepts, o.Px, o.Py, o.Pz, o.Wy, s.DisplayName)).ToList();
                return new ShipCargoDef
                {
                    Id = s.Id, DisplayName = s.DisplayName, Manufacturer = s.Manufacturer,
                    Classification = s.Classification, Priority = s.Priority, Grids = grids,
                };
            }
            catch (Exception ex)
            {
                Logger.Error($"Invalid grid override for {s.Id}; using embedded layout", ex);
                return s;
            }
        });
        return new CargoShipCatalog(merged);
    }

    // Build a single ship with a caller-supplied grid set (an imported submission or a preview),
    // validated by the same BuildGrid rules as the embedded load. Returns null if the ship id is
    // unknown; throws InvalidDataException if any grid is invalid.
    public ShipCargoDef? BuildPreview(string shipId, IReadOnlyList<GridOverride> grids)
    {
        var baseShip = ById(shipId);
        if (baseShip == null) return null;
        var built = grids.Select(o =>
            BuildGrid(o.Id, o.W, o.D, o.H, o.Cap, o.Accepts, o.Px, o.Py, o.Pz, o.Wy, baseShip.DisplayName)).ToList();
        return new ShipCargoDef
        {
            Id = baseShip.Id, DisplayName = baseShip.DisplayName, Manufacturer = baseShip.Manufacturer,
            Classification = baseShip.Classification, Priority = baseShip.Priority, Grids = built,
        };
    }

    private static ShipCargoDef ToDef(RawShip r)
    {
        var grids = new List<GridDef>(r.Grids.Count);
        foreach (var g in r.Grids)
            grids.Add(BuildGrid(g.Id, g.W, g.D, g.H, g.Cap, g.Accepts, g.Px, g.Py, g.Pz, g.Wy, r.Name));
        return new ShipCargoDef
        {
            Id = r.Id, DisplayName = r.Name, Manufacturer = r.Manufacturer,
            Classification = r.Class, Priority = r.Priority, Grids = grids,
        };
    }

    // Shared, validated construction of one grid (embedded load and overrides both use it). An
    // explicit accepted-size set wins; otherwise a single cap expands to every standard size up
    // to it, preserving the datamined data's meaning.
    // A real ship's grid is a few cells per axis; these ceilings sit far above any real hull but stop
    // a hostile .nexusgrid from overflowing Capacity (W*D*H) or allocating a giant GridOccupancy.
    private const int MaxCellsPerAxis = 512;
    private const long MaxCellVolume = 200_000;
    private const double MaxPositionCells = 5_000;

    private static GridDef BuildGrid(int id, int w, int d, int h, int cap, List<int>? accepts,
        double? px, double? py, double? pz, bool wy, string shipName)
    {
        if (w <= 0 || d <= 0 || h <= 0)
            throw new InvalidDataException($"{shipName}: grid {id} has a non-positive dimension");
        if (w > MaxCellsPerAxis || d > MaxCellsPerAxis || h > MaxCellsPerAxis)
            throw new InvalidDataException($"{shipName}: grid {id} dimension exceeds {MaxCellsPerAxis} cells");
        if ((long)w * d * h > MaxCellVolume)
            throw new InvalidDataException($"{shipName}: grid {id} volume exceeds {MaxCellVolume} cells");
        foreach (var p in new[] { px, py, pz })
            if (p is { } v && (!double.IsFinite(v) || Math.Abs(v) > MaxPositionCells))
                throw new InvalidDataException($"{shipName}: grid {id} has an out-of-range position");
        return new GridDef
        {
            Id = id, W = w, D = d, H = h, Name = $"Grid {id + 1}",
            AcceptedCaps = ResolveAccepts(accepts, cap, id, shipName),
            PosX = px, PosY = py, PosZ = pz, WAlongShipY = wy,
        };
    }

    private static IReadOnlyList<int> ResolveAccepts(List<int>? accepts, int cap, int id, string shipName)
    {
        if (accepts is { Count: > 0 })
        {
            foreach (var a in accepts)
                if (!BoxType.IsStandard(a))
                    throw new InvalidDataException($"{shipName}: grid {id} has a non-standard accepted size {a}");
            return accepts.Distinct().OrderBy(a => a).ToList();
        }
        if (!BoxType.IsStandard(cap))
            throw new InvalidDataException($"{shipName}: grid {id} has a non-standard cap {cap}");
        return BoxType.SizesDesc.Where(s => s <= cap).OrderBy(s => s).ToList();
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private sealed class RawShip
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Manufacturer { get; set; } = "";
        public string Class { get; set; } = "";
        public bool Flyable { get; set; }
        public int Priority { get; set; }   // hauling-priority rank (1 = highest; 0 = unranked)
        public List<RawGrid> Grids { get; set; } = new();
    }

    private sealed class RawGrid
    {
        public int Id { get; set; }
        public int W { get; set; }
        public int D { get; set; }
        public int H { get; set; }
        public int Cap { get; set; }
        public List<int>? Accepts { get; set; }   // exact accepted-size set (optional; else derived from Cap)
        public double? Px { get; set; }   // ship-space grid center, cells (optional)
        public double? Py { get; set; }
        public double? Pz { get; set; }
        public bool Wy { get; set; }      // W axis runs fore-aft
    }
}
