using System.IO;
using System.Text.Json;

namespace NexusApp.Services;

/// <summary>One remembered kiosk answer: what a location sells a commodity in, and when it said so.</summary>
public sealed record KioskBoxRecord(string Location, string Commodity, string Sizes, DateTime SeenUtc);

/// <summary>
/// Remembers the box sizes each LOCATION sells each commodity in, learned from the kiosk itself
/// (CommodityBoxParser) and kept across restarts. The planner prefers these over UEX's shipped
/// container_sizes, which is one list per commodity and does not vary by terminal.
///
/// <para>KEYED BY LOCATION, NOT SHOP ID, and that is a deliberate compromise. The log gives a
/// shopId, but nothing in this app maps a shopId to a UEX terminal, and shopName is a TEMPLATE
/// shared across stations (SCShop_Admin_lt_base_g appears at every admin office in the game), so
/// neither can address a terminal on its own. The player's location when the kiosk opened can,
/// through the same TradeOriginResolver.TerminalIdsForLocation seam the accepted-route matcher
/// already uses. The cost is that a location with two commodity kiosks selling one commodity in
/// different sizes would be recorded as one answer, which is the same class of ambiguity that
/// matcher already accepts and states.</para>
///
/// <para>Newest wins. A kiosk's sizes can change between game patches, and the point of reading
/// the live log is to track that rather than freeze the first answer ever seen.</para>
/// </summary>
public sealed class KioskBoxSizeStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private readonly Dictionary<string, KioskBoxRecord> _byKey = new(StringComparer.OrdinalIgnoreCase);

    public KioskBoxSizeStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NexusApp", "kiosk_box_sizes.json");
        Load();
    }

    private static string Key(string location, string commodity) => $"{location.Trim()}{commodity.Trim()}";

    /// <summary>Records what a kiosk at <paramref name="location"/> just advertised. A blank
    /// location is dropped: without somewhere to attach it, the answer can never be looked up.</summary>
    public void Record(string? location, KioskBoxSizes observed)
    {
        if (string.IsNullOrWhiteSpace(location) || string.IsNullOrWhiteSpace(observed.CommodityName)) return;
        var sizes = CommodityBoxParser.ToContainerSizes(observed.Sizes);
        if (sizes.Length == 0) return;

        lock (_gate)
        {
            var key = Key(location, observed.CommodityName);
            if (_byKey.TryGetValue(key, out var had) && had.Sizes == sizes)
            {
                // Same answer as last time: refresh the stamp, skip the disk write. A kiosk is
                // re-read every time it opens, which for a trading session is constantly.
                _byKey[key] = had with { SeenUtc = observed.TimestampUtc };
                return;
            }
            _byKey[key] = new KioskBoxRecord(location.Trim(), observed.CommodityName, sizes, observed.TimestampUtc);
            Logger.Info($"[TRADE] kiosk box sizes {observed.CommodityName} at {location.Trim()}: {sizes}");
            Save();
        }
    }

    /// <summary>The remembered sizes for a location and commodity, or null when never seen.</summary>
    public string? SizesFor(string? location, string? commodity)
    {
        if (string.IsNullOrWhiteSpace(location) || string.IsNullOrWhiteSpace(commodity)) return null;
        lock (_gate)
            return _byKey.TryGetValue(Key(location, commodity), out var rec) ? rec.Sizes : null;
    }

    public IReadOnlyList<KioskBoxRecord> All()
    {
        lock (_gate) return _byKey.Values.OrderBy(r => r.Location).ThenBy(r => r.Commodity).ToList();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var records = JsonSerializer.Deserialize<List<KioskBoxRecord>>(File.ReadAllText(_path));
            if (records is null) return;
            foreach (var r in records)
                if (!string.IsNullOrWhiteSpace(r.Location) && !string.IsNullOrWhiteSpace(r.Commodity))
                    _byKey[Key(r.Location, r.Commodity)] = r;
        }
        catch (Exception ex)
        {
            // A corrupt file costs the learned sizes, never the app: the planner simply falls back
            // to UEX's list, which is what it used before any of this existed.
            Logger.Info($"[TRADE] kiosk box sizes unreadable: {ex.Message}");
        }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_path, JsonSerializer.Serialize(_byKey.Values.ToList()));
        }
        catch (Exception ex)
        {
            Logger.Info($"[TRADE] kiosk box sizes not saved: {ex.Message}");
        }
    }
}
