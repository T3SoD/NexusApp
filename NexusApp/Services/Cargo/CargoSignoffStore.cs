using System.IO;
using System.Text.Json;

namespace NexusApp.Services.Cargo;

// Per-ship review checklist for the Cargo Planner catalog: each ship is reviewed across three
// aspects (grid position, hologram, grid properties). A ship is fully signed off when all three
// are checked, and can be flagged when something looks wrong. Mouse-driven; state persists per
// user to a local JSON file under %APPDATA%\NexusApp (never shipped with the app) so a review
// pass survives restarts and the file can be inspected by tooling.
public sealed class CargoSignoffStore
{
    public enum ReviewItem { GridPosition, Hologram, GridProperties }

    public static readonly IReadOnlyList<ReviewItem> Items =
        new[] { ReviewItem.GridPosition, ReviewItem.Hologram, ReviewItem.GridProperties };

    public static string Label(ReviewItem item) => item switch
    {
        ReviewItem.GridPosition => "Cargo grid position",
        ReviewItem.Hologram => "Hologram",
        ReviewItem.GridProperties => "Cargo grid properties",
        _ => item.ToString(),
    };

    public sealed record Entry
    {
        public string Ship { get; init; } = "";
        public Dictionary<string, bool> Checks { get; init; } = new();
        public bool Flagged { get; init; }
        public string When { get; init; } = "";
        // Fingerprint (see GridFingerprint) of the grid set that was reviewed at sign-off. Empty for
        // entries written before fingerprints existed - those show a neutral state, not false drift.
        public string Fingerprint { get; init; } = "";
    }

    private readonly string _path;
    private readonly Dictionary<string, Entry> _entries;   // ship id -> entry

    public CargoSignoffStore(string path)
    {
        _path = path;
        _entries = Load(path);
    }

    // Shared process-wide for the default path (like the override store) so a sign-off made in Grid
    // Studio is visible to the Cargo Planner's visible-catalog filter in the same session. Tests use
    // the public path constructor for isolation.
    private static CargoSignoffStore? _default;
    public static CargoSignoffStore LoadDefault()
    {
        if (_default != null) return _default;
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NexusApp");
        Directory.CreateDirectory(dir);
        return _default = new CargoSignoffStore(Path.Combine(dir, "cargo_ship_signoff.json"));
    }

    // Re-read the file into the in-memory set. Pages call this on show so a change written by another
    // process (or view) is reflected. WPF is single-threaded, so no locking is needed.
    public void Reload()
    {
        var fresh = Load(_path);
        _entries.Clear();
        foreach (var kv in fresh) _entries[kv.Key] = kv.Value;
    }

    public bool IsChecked(string shipId, ReviewItem item) =>
        _entries.TryGetValue(shipId, out var e) && e.Checks.TryGetValue(item.ToString(), out var v) && v;

    public bool IsFlagged(string shipId) =>
        _entries.TryGetValue(shipId, out var e) && e.Flagged;

    public int CheckedCount(string shipId) =>
        _entries.TryGetValue(shipId, out var e)
            ? Items.Count(i => e.Checks.TryGetValue(i.ToString(), out var v) && v)
            : 0;

    public bool IsFullySignedOff(string shipId) => CheckedCount(shipId) == Items.Count;

    public string? When(string shipId) =>
        _entries.TryGetValue(shipId, out var e) ? e.When : null;

    // True when a sign-off was recorded against a grid set whose fingerprint no longer matches the
    // current one (the geometry changed since review). Entries without a stored fingerprint (older
    // sign-offs) are never stale - they show a neutral state.
    public bool IsStale(string shipId, string currentFingerprint) =>
        _entries.TryGetValue(shipId, out var e) && !string.IsNullOrEmpty(e.Fingerprint) && e.Fingerprint != currentFingerprint;

    public int CountFullySignedOff() => _entries.Keys.Count(IsFullySignedOff);
    public int CountFlagged() => _entries.Values.Count(e => e.Flagged);

    /// <summary>Toggle one checklist item for a ship. Persists immediately.</summary>
    /// <summary>Toggle one checklist item. Returns false if the disk write failed (state rolled back).</summary>
    public bool SetCheck(string shipId, string shipName, ReviewItem item, bool value, string fingerprint = "")
    {
        var e = _entries.TryGetValue(shipId, out var cur) ? cur : new Entry();
        var checks = new Dictionary<string, bool>(e.Checks) { [item.ToString()] = value };
        // Record the fingerprint of the grids being reviewed so later geometry changes surface as drift.
        var fp = string.IsNullOrEmpty(fingerprint) ? e.Fingerprint : fingerprint;
        bool ok = Store(shipId, shipName, e with { Checks = checks, Fingerprint = fp });
        if (ok) Logger.Info($"[UI] cargo review: {shipName} {item} -> {value}");
        return ok;
    }

    /// <summary>Flag or unflag a ship. Returns false if the disk write failed (state rolled back).</summary>
    public bool SetFlagged(string shipId, string shipName, bool value)
    {
        var e = _entries.TryGetValue(shipId, out var cur) ? cur : new Entry();
        bool ok = Store(shipId, shipName, e with { Flagged = value });
        if (ok) Logger.Info($"[UI] cargo review: {shipName} flagged -> {value}");
        return ok;
    }

    /// <summary>Clear the given items (e.g. after a ship's geometry is edited). Persists.</summary>
    public bool ClearChecks(string shipId, string shipName, params ReviewItem[] items)
    {
        if (!_entries.TryGetValue(shipId, out var e)) return true;   // nothing to clear
        var checks = new Dictionary<string, bool>(e.Checks);
        foreach (var i in items) checks[i.ToString()] = false;
        bool ok = Store(shipId, shipName, e with { Checks = checks });
        if (ok) Logger.Info($"[UI] cargo review: {shipName} cleared {string.Join(",", items)}");
        return ok;
    }

    // Persist an entry (or remove it when empty), rolling the in-memory state back if the disk write
    // fails so memory always matches disk (a failed write never leaves a phantom review state).
    private bool Store(string shipId, string shipName, Entry e)
    {
        var prev = _entries.TryGetValue(shipId, out var p) ? p : null;
        bool anyCheck = e.Checks.Values.Any(v => v);
        if (!anyCheck && !e.Flagged) _entries.Remove(shipId);
        else _entries[shipId] = e with { Ship = shipName, When = DateTime.Now.ToString("yyyy-MM-dd HH:mm") };
        if (!Save())
        {
            if (prev != null) _entries[shipId] = prev; else _entries.Remove(shipId);
            return false;
        }
        return true;
    }

    private static Dictionary<string, Entry> Load(string path) =>
        JsonFile.LoadOrRecover(path, () => new Dictionary<string, Entry>(), "cargo review state");

    private bool Save()
    {
        try
        {
            JsonFile.AtomicWrite(_path, JsonSerializer.Serialize(_entries,
                new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch (Exception ex) { Logger.Error("Failed to save cargo review state", ex); return false; }
    }
}
