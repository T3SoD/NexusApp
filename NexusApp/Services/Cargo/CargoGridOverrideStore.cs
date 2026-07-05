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

    // The Cargo Planner and Grid Studio both hold this store; they share ONE process-wide instance for
    // the default path so a save made in one view is visible to the other in the same session (each page
    // formerly held its own load-once snapshot). Tests use the public path constructor for isolation.
    private static CargoGridOverrideStore? _default;
    public static CargoGridOverrideStore LoadDefault()
    {
        if (_default != null) return _default;
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NexusApp");
        Directory.CreateDirectory(dir);
        return _default = new CargoGridOverrideStore(Path.Combine(dir, "cargo_grid_overrides.json"));
    }

    // Re-read the file into the in-memory set. Pages call this on show so a change written by another
    // process (or another view) is reflected. WPF is single-threaded, so no locking is needed.
    public void Reload()
    {
        var fresh = Load(_path);
        _entries.Clear();
        foreach (var kv in fresh) _entries[kv.Key] = kv.Value;
    }

    public bool Has(string shipId) => _entries.ContainsKey(shipId);

    public IReadOnlyList<GridOverride>? Get(string shipId) =>
        _entries.TryGetValue(shipId, out var g) ? g : null;

    public IReadOnlyCollection<string> EditedShipIds => _entries.Keys;

    /// <summary>Replace a ship's grid set with the corrected one. Persists immediately. Returns false if
    /// the disk write failed (the in-memory state is rolled back so it matches disk and the caller can
    /// keep the edit rather than report a phantom save).</summary>
    public bool Set(string shipId, string shipName, IReadOnlyList<GridOverride> grids)
    {
        var prev = _entries.TryGetValue(shipId, out var p) ? p : null;
        _entries[shipId] = grids.ToList();
        if (!Save())
        {
            if (prev != null) _entries[shipId] = prev; else _entries.Remove(shipId);
            return false;
        }
        Logger.Info($"[CARGO] grid override saved: {shipName} ({shipId}) grids={grids.Count}");
        return true;
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

    private bool Save()
    {
        try
        {
            JsonFile.AtomicWrite(_path, JsonSerializer.Serialize(_entries,
                new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch (Exception ex) { Logger.Error("Failed to save cargo grid overrides", ex); return false; }
    }
}
