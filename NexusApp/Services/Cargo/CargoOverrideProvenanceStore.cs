using System.IO;
using System.Text.Json;
using NexusApp.Models.Cargo;

namespace NexusApp.Services.Cargo;

// Sibling store to the override store: records where each applied override came from (contributor handle,
// summary, notes, dates, merged aspects). Kept SEPARATE so the override format other code reads never
// changes. Persisted to %APPDATA%\NexusApp\cargo_override_provenance.json (never shipped). Shared
// singleton for the default path, like the other cargo stores.
public sealed class CargoOverrideProvenanceStore
{
    private readonly string _path;
    private readonly Dictionary<string, OverrideProvenance> _entries;

    public CargoOverrideProvenanceStore(string path)
    {
        _path = path;
        _entries = Load(path);
    }

    private static CargoOverrideProvenanceStore? _default;
    public static CargoOverrideProvenanceStore LoadDefault()
    {
        if (_default != null) return _default;
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NexusApp");
        Directory.CreateDirectory(dir);
        return _default = new CargoOverrideProvenanceStore(Path.Combine(dir, "cargo_override_provenance.json"));
    }

    public void Reload()
    {
        var fresh = Load(_path);
        _entries.Clear();
        foreach (var kv in fresh) _entries[kv.Key] = kv.Value;
    }

    public bool Has(string shipId) => _entries.ContainsKey(shipId);
    public OverrideProvenance? Get(string shipId) => _entries.TryGetValue(shipId, out var p) ? p : null;

    public void Set(string shipId, OverrideProvenance p)
    {
        _entries[shipId] = p with { ShipId = shipId };
        Save();
        Logger.Info($"[CARGO] override provenance recorded: {shipId} from {p.Handle}");
    }

    public void Clear(string shipId)
    {
        if (_entries.Remove(shipId)) { Save(); Logger.Info($"[CARGO] override provenance cleared: {shipId}"); }
    }

    private static Dictionary<string, OverrideProvenance> Load(string path) =>
        JsonFile.LoadOrRecover(path, () => new Dictionary<string, OverrideProvenance>(), "cargo override provenance");

    private void Save()
    {
        try
        {
            JsonFile.AtomicWrite(_path, JsonSerializer.Serialize(_entries,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Logger.Error("Failed to save cargo override provenance", ex); }
    }
}
