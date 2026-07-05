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
    }

    private readonly string _path;
    private readonly Dictionary<string, Entry> _entries;   // ship id -> entry

    public CargoSignoffStore(string path)
    {
        _path = path;
        _entries = Load(path);
    }

    public static CargoSignoffStore LoadDefault()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NexusApp");
        Directory.CreateDirectory(dir);
        return new CargoSignoffStore(Path.Combine(dir, "cargo_ship_signoff.json"));
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

    public int CountFullySignedOff() => _entries.Keys.Count(IsFullySignedOff);
    public int CountFlagged() => _entries.Values.Count(e => e.Flagged);

    /// <summary>Toggle one checklist item for a ship. Persists immediately.</summary>
    public void SetCheck(string shipId, string shipName, ReviewItem item, bool value)
    {
        var e = _entries.TryGetValue(shipId, out var cur) ? cur : new Entry();
        var checks = new Dictionary<string, bool>(e.Checks) { [item.ToString()] = value };
        Store(shipId, shipName, e with { Checks = checks });
        Logger.Info($"[UI] cargo review: {shipName} {item} -> {value}");
    }

    /// <summary>Flag or unflag a ship. Persists immediately.</summary>
    public void SetFlagged(string shipId, string shipName, bool value)
    {
        var e = _entries.TryGetValue(shipId, out var cur) ? cur : new Entry();
        Store(shipId, shipName, e with { Flagged = value });
        Logger.Info($"[UI] cargo review: {shipName} flagged -> {value}");
    }

    /// <summary>Clear the given items (e.g. after a ship's geometry is edited). Persists.</summary>
    public void ClearChecks(string shipId, string shipName, params ReviewItem[] items)
    {
        if (!_entries.TryGetValue(shipId, out var e)) return;
        var checks = new Dictionary<string, bool>(e.Checks);
        foreach (var i in items) checks[i.ToString()] = false;
        Store(shipId, shipName, e with { Checks = checks });
        Logger.Info($"[UI] cargo review: {shipName} cleared {string.Join(",", items)}");
    }

    private void Store(string shipId, string shipName, Entry e)
    {
        bool anyCheck = e.Checks.Values.Any(v => v);
        if (!anyCheck && !e.Flagged) { _entries.Remove(shipId); }
        else _entries[shipId] = e with { Ship = shipName, When = DateTime.Now.ToString("yyyy-MM-dd HH:mm") };
        Save();
    }

    private static Dictionary<string, Entry> Load(string path) =>
        JsonFile.LoadOrRecover(path, () => new Dictionary<string, Entry>(), "cargo review state");

    private void Save()
    {
        try
        {
            JsonFile.AtomicWrite(_path, JsonSerializer.Serialize(_entries,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Logger.Error("Failed to save cargo review state", ex); }
    }
}
