using System.IO;
using System.Text.Json;

namespace NexusApp.Services;

// In-memory cache of the last successfully fetched UEX market data. One MarketDataService owns
// the live instance; MarketSnapshotFile (Task 3) persists it to disk between runs.
internal sealed class MarketSnapshot
{
    public int Schema { get; set; } = 1;
    public string LiveGameVersion { get; set; } = "";
    public MarketDataset<MarketCommodity> Commodities { get; set; } = new();
    public MarketDataset<MarketPriceRow> RawPrices { get; set; } = new();
    public MarketDataset<MarketPriceRow> RefinedPrices { get; set; } = new();
    public MarketDataset<MarketYieldRow> Yields { get; set; } = new();
    public MarketDataset<MarketTerminal> Terminals { get; set; } = new();

    // The freshest of the five dataset fetch stamps. All five default to DateTime.MinValue
    // (never fetched), so a brand new snapshot reports MinValue here too.
    public DateTime NewestFetchUtc =>
        new[]
        {
            Commodities.FetchedUtc,
            RawPrices.FetchedUtc,
            RefinedPrices.FetchedUtc,
            Yields.FetchedUtc,
            Terminals.FetchedUtc,
        }.Max();
}

internal sealed class MarketDataset<T>
{
    public DateTime FetchedUtc { get; set; }  // default(DateTime) = never fetched
    public List<T> Rows { get; set; } = new();
}

internal sealed record MarketCommodity(int Id, string Name, string Slug, bool IsRaw, bool IsRefined, int IdParent);
internal sealed record MarketPriceRow(int TerminalId, int CommodityId, double Sell, double SellAvgWeek, string GameVersion, DateTime ModifiedUtc, string TerminalName);
internal sealed record MarketYieldRow(int TerminalId, int CommodityId, int BonusPct, int BonusPctWeek, DateTime ModifiedUtc, string TerminalName);
internal sealed record MarketTerminal(int Id, string Name, string Type, bool IsRefinery, string System, string Location);

// Tolerant parsing of UEX API responses. Never throws on any input: malformed JSON, an error
// envelope, or an unexpected shape all resolve to an empty/false result. Individual bad rows in
// an otherwise valid array are skipped and counted rather than failing the whole batch.
internal static class MarketParse
{
    // True only when body is valid JSON, is an object, has status == "ok", and has a "data"
    // property. On success, data is a clone that stays valid after this method returns (the
    // backing JsonDocument is disposed here).
    public static bool TryGetData(string body, out JsonElement data)
    {
        data = default;
        if (string.IsNullOrEmpty(body)) return false;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String
                || status.GetString() != "ok")
                return false;
            if (!root.TryGetProperty("data", out var dataEl)) return false;
            data = dataEl.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            // JsonDocument.Parse(string) throws ArgumentException (not JsonException) on
            // malformed UTF-16 input such as an unpaired surrogate. Still just "not parseable."
            return false;
        }
    }

    // /game_versions: data is an object like {"live":"4.9","ptu":"4.10.0"}.
    public static string? ParseLiveGameVersion(string body)
    {
        try
        {
            if (!TryGetData(body, out var data) || data.ValueKind != JsonValueKind.Object) return null;
            if (!data.TryGetProperty("live", out var live) || live.ValueKind != JsonValueKind.String) return null;
            return live.GetString();
        }
        catch (Exception)
        {
            // Trust-boundary guard: this parses arbitrary bytes off the network. The ValueKind
            // checks above are the primary defense; this catch-all is the guarantee that an
            // encoding or shape edge case we didn't anticipate (e.g. ArgumentException from
            // malformed UTF-16) returns null instead of throwing into the caller.
            return null;
        }
    }

    public static List<MarketCommodity> ParseCommodities(string body, out int skipped)
    {
        var result = new List<MarketCommodity>();
        skipped = 0;
        try
        {
            if (!TryGetData(body, out var data) || data.ValueKind != JsonValueKind.Array) return result;
            foreach (var row in data.EnumerateArray())
            {
                if (row.ValueKind == JsonValueKind.Object
                    && TryInt(row, "id", out var id)
                    && TryStr(row, "name", out var name)
                    && TryStr(row, "slug", out var slug)
                    && TryBool01(row, "is_raw", out var isRaw)
                    && TryBool01(row, "is_refined", out var isRefined)
                    && TryInt(row, "id_parent", out var idParent))
                {
                    result.Add(new MarketCommodity(id, name, slug, isRaw, isRefined, idParent));
                }
                else
                {
                    skipped++;
                }
            }
        }
        catch (Exception)
        {
            // Trust-boundary guard: this parses arbitrary bytes off the network. The ValueKind
            // checks above are the primary defense; this catch-all is the guarantee that an
            // encoding or shape edge case we didn't anticipate (e.g. ArgumentException from
            // malformed UTF-16) skips out cleanly instead of throwing into the caller.
            skipped = 0;
            return new List<MarketCommodity>();
        }
        return result;
    }

    // Shared shape for /commodities_raw_prices_all and /commodities_prices.
    public static List<MarketPriceRow> ParsePriceRows(string body, out int skipped)
    {
        var result = new List<MarketPriceRow>();
        skipped = 0;
        try
        {
            if (!TryGetData(body, out var data) || data.ValueKind != JsonValueKind.Array) return result;
            foreach (var row in data.EnumerateArray())
            {
                if (row.ValueKind == JsonValueKind.Object
                    && TryInt(row, "id_terminal", out var terminalId)
                    && TryInt(row, "id_commodity", out var commodityId)
                    && TryDouble(row, "price_sell", out var sell)
                    && TryDouble(row, "price_sell_avg_week", out var sellAvgWeek)
                    && TryStr(row, "game_version", out var gameVersion)
                    && TryEpoch(row, "date_modified", out var modifiedUtc)
                    && TryStr(row, "terminal_name", out var terminalName))
                {
                    // price_sell_avg_week == 0 is a valid reading (queries fall back to the
                    // instant price); it is kept, not treated as missing.
                    result.Add(new MarketPriceRow(terminalId, commodityId, sell, sellAvgWeek, gameVersion,
                        modifiedUtc, terminalName));
                }
                else
                {
                    skipped++;
                }
            }
        }
        catch (Exception)
        {
            // Trust-boundary guard: this parses arbitrary bytes off the network. The ValueKind
            // checks above are the primary defense; this catch-all is the guarantee that an
            // encoding or shape edge case we didn't anticipate (e.g. ArgumentException from
            // malformed UTF-16) skips out cleanly instead of throwing into the caller.
            skipped = 0;
            return new List<MarketPriceRow>();
        }
        return result;
    }

    public static List<MarketYieldRow> ParseYieldRows(string body, out int skipped)
    {
        var result = new List<MarketYieldRow>();
        skipped = 0;
        try
        {
            if (!TryGetData(body, out var data) || data.ValueKind != JsonValueKind.Array) return result;
            foreach (var row in data.EnumerateArray())
            {
                if (row.ValueKind == JsonValueKind.Object
                    && TryInt(row, "id_terminal", out var terminalId)
                    && TryInt(row, "id_commodity", out var commodityId)
                    && TryInt(row, "value", out var bonusPct)
                    && TryInt(row, "value_week", out var bonusPctWeek)
                    && TryEpoch(row, "date_modified", out var modifiedUtc)
                    && TryStr(row, "terminal_name", out var terminalName))
                {
                    result.Add(new MarketYieldRow(terminalId, commodityId, bonusPct, bonusPctWeek,
                        modifiedUtc, terminalName));
                }
                else
                {
                    skipped++;
                }
            }
        }
        catch (Exception)
        {
            // Trust-boundary guard: this parses arbitrary bytes off the network. The ValueKind
            // checks above are the primary defense; this catch-all is the guarantee that an
            // encoding or shape edge case we didn't anticipate (e.g. ArgumentException from
            // malformed UTF-16) skips out cleanly instead of throwing into the caller.
            skipped = 0;
            return new List<MarketYieldRow>();
        }
        return result;
    }

    public static List<MarketTerminal> ParseTerminals(string body, out int skipped)
    {
        var result = new List<MarketTerminal>();
        skipped = 0;
        try
        {
            if (!TryGetData(body, out var data) || data.ValueKind != JsonValueKind.Array) return result;
            foreach (var row in data.EnumerateArray())
            {
                if (row.ValueKind == JsonValueKind.Object
                    && TryInt(row, "id", out var id)
                    && TryStr(row, "name", out var name)
                    && TryStr(row, "type", out var type)
                    && TryBool01(row, "is_refinery", out var isRefinery)
                    && TryStr(row, "star_system_name", out var system))
                {
                    var location = FirstNonEmptyStr(row, "space_station_name", "city_name", "outpost_name");
                    result.Add(new MarketTerminal(id, name, type, isRefinery, system, location));
                }
                else
                {
                    skipped++;
                }
            }
        }
        catch (Exception)
        {
            // Trust-boundary guard: this parses arbitrary bytes off the network. The ValueKind
            // checks above are the primary defense; this catch-all is the guarantee that an
            // encoding or shape edge case we didn't anticipate (e.g. ArgumentException from
            // malformed UTF-16) skips out cleanly instead of throwing into the caller.
            skipped = 0;
            return new List<MarketTerminal>();
        }
        return result;
    }

    private static bool TryInt(JsonElement obj, string prop, out int value)
    {
        value = 0;
        return obj.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out value);
    }

    private static bool TryDouble(JsonElement obj, string prop, out double value)
    {
        value = 0;
        if (!obj.TryGetProperty(prop, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetDouble(out value))
            return false;
        // A number like 1e400 parses without throwing but lands as +/-Infinity; reject it so a
        // bad price never reaches queries/UI as a non-finite value.
        if (!double.IsFinite(value)) { value = 0; return false; }
        return true;
    }

    private static bool TryStr(JsonElement obj, string prop, out string value)
    {
        value = "";
        if (!obj.TryGetProperty(prop, out var el) || el.ValueKind != JsonValueKind.String) return false;
        value = el.GetString() ?? "";
        return true;
    }

    // UEX flags 0/1 as JSON numbers; anything nonzero counts as true.
    private static bool TryBool01(JsonElement obj, string prop, out bool value)
    {
        value = false;
        if (!obj.TryGetProperty(prop, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var n))
            return false;
        value = n != 0;
        return true;
    }

    // DateTimeOffset.FromUnixTimeSeconds throws ArgumentOutOfRangeException above this many
    // seconds (it maps to DateTime.MaxValue); UEX data is never negative or this far out, so
    // both ends are treated as bad data rather than clamped.
    private const long MaxEpochSeconds = 253402300799L;

    // Epoch seconds -> UTC DateTime. Negative, non-numeric, or out-of-range values fail (skip
    // the row) instead of throwing.
    private static bool TryEpoch(JsonElement obj, string prop, out DateTime utc)
    {
        utc = default;
        if (!obj.TryGetProperty(prop, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt64(out var seconds))
            return false;
        if (seconds < 0 || seconds > MaxEpochSeconds) return false;
        utc = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
        return true;
    }

    private static string FirstNonEmptyStr(JsonElement row, params string[] props)
    {
        foreach (var prop in props)
        {
            if (row.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.String)
            {
                var s = el.GetString();
                if (!string.IsNullOrEmpty(s)) return s;
            }
        }
        return "";
    }
}

// Atomic persistence for MarketSnapshot: write to .tmp, then atomic rename.
// Load validates Schema and size before parsing.
internal static class MarketSnapshotFile
{
    public const long MaxLoadBytes = 16 * 1024 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    // Serialize to path + ".tmp", then File.Move(tmp, path, overwrite: true). Creates the directory.
    // Returns false (and logs via Logger.Error) on failure; never throws.
    public static bool Save(string path, MarketSnapshot snapshot)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var tmp = path + ".tmp";
            var json = JsonSerializer.Serialize(snapshot, SerializerOptions);
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to save market snapshot to {path}", ex);
            return false;
        }
    }

    // Null when missing, oversized, unparseable, or Schema != 1; reason describes why (for logging).
    public static MarketSnapshot? Load(string path, out string? reason)
    {
        reason = null;

        // Check if file exists
        if (!File.Exists(path))
        {
            reason = $"Market snapshot file not found: {path}";
            return null;
        }

        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxLoadBytes)
            {
                reason = $"Market snapshot file exceeds max size ({info.Length} > {MaxLoadBytes} bytes): {path}";
                return null;
            }

            var json = File.ReadAllText(path);
            var snapshot = JsonSerializer.Deserialize<MarketSnapshot>(json, SerializerOptions);

            if (snapshot == null)
            {
                reason = $"Failed to deserialize market snapshot: {path}";
                return null;
            }

            if (snapshot.Schema != 1)
            {
                reason = $"Unsupported market snapshot schema (expected 1, got {snapshot.Schema}): {path}";
                return null;
            }

            return snapshot;
        }
        catch (JsonException ex)
        {
            reason = $"Invalid JSON in market snapshot: {path} - {ex.Message}";
            return null;
        }
        catch (ArgumentException ex)
        {
            reason = $"Invalid market snapshot: {path} - {ex.Message}";
            return null;
        }
        catch (Exception ex)
        {
            reason = $"Error loading market snapshot: {path} - {ex.Message}";
            return null;
        }
    }
}
