using System.IO;
using System.Text.Json;
using NexusApp.Models.Cargo;

namespace NexusApp.Services.Cargo;

// Local, per-user overrides for ship cargo grid layouts, hand-corrected in the Cargo Planner's
// Edit Layout mode. Each entry is the COMPLETE corrected grid list for a ship (whole-set
// replacement, keyed by ship id) - that is what makes add and delete clean. Persisted to
// %APPDATA%\NexusApp (never shipped with the app); the catalog merges these on load so the
// planner renders the corrected layout. The file can be promoted into the datamine pipeline so
// corrections survive game patches.
public sealed class CargoGridOverrideStore
{
    private readonly string _path;
    private readonly Dictionary<string, List<GridOverride>> _entries;   // ship id -> full grid set

    public CargoGridOverrideStore(string path)
    {
        _path = path;
        _entries = Load(path);
    }

    public static CargoGridOverrideStore LoadDefault()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NexusApp");
        Directory.CreateDirectory(dir);
        return new CargoGridOverrideStore(Path.Combine(dir, "cargo_grid_overrides.json"));
    }

    public bool Has(string shipId) => _entries.ContainsKey(shipId);

    public IReadOnlyList<GridOverride>? Get(string shipId) =>
        _entries.TryGetValue(shipId, out var g) ? g : null;

    public IReadOnlyCollection<string> EditedShipIds => _entries.Keys;

    /// <summary>Replace a ship's grid set with the corrected one. Persists immediately.</summary>
    public void Set(string shipId, string shipName, IReadOnlyList<GridOverride> grids)
    {
        _entries[shipId] = grids.ToList();
        Save();
        Logger.Info($"[CARGO] grid override saved: {shipName} ({shipId}) grids={grids.Count}");
    }

    /// <summary>Drop a ship's override so it reverts to the embedded layout. Persists immediately.</summary>
    public void Clear(string shipId)
    {
        if (_entries.Remove(shipId))
        {
            Save();
            Logger.Info($"[CARGO] grid override cleared: {shipId}");
        }
    }

    private static Dictionary<string, List<GridOverride>> Load(string path) =>
        JsonFile.LoadOrRecover(path,
            () => new Dictionary<string, List<GridOverride>>(), "cargo grid overrides");

    private void Save()
    {
        try
        {
            JsonFile.AtomicWrite(_path, JsonSerializer.Serialize(_entries,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Logger.Error("Failed to save cargo grid overrides", ex); }
    }
}
