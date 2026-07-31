using System;
using System.Collections.Generic;
using System.Text.Json;

namespace NexusApp.Services;

// In-game display names for logged location tokens (Data/location_aliases.json): jump-point
// gateway stations, RR_* rest stops, and Stanton inventory-key slugs (e.g. Stanton4_NewBabbage
// -> "New Babbage"). LocationTracker.Apply is the single choke point that normalizes a place
// through here before storing or logging it, so the pill, OriginLabel, and any resolver that
// reads LastKnownLocation all inherit the readable name. A miss passes the raw token through
// unchanged by design: jurisdiction text like "microTech" or "Monitored Space" is already
// human-readable and is never in this table. Exact, case-insensitive lookup; never throws (a
// missing or malformed embedded resource resolves to an empty table, same as ContractCapCatalog).
public static class LocationAliases
{
    private const string ResourceName = "NexusApp.Data.location_aliases.json";

    private static readonly Lazy<IReadOnlyDictionary<string, string>> _aliases = new(LoadEmbedded);

    public static string Normalize(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        return _aliases.Value.TryGetValue(raw, out var display) ? display : raw;
    }

    private static IReadOnlyDictionary<string, string> LoadEmbedded()
    {
        try
        {
            using var stream = typeof(LocationAliases).Assembly.GetManifestResourceStream(ResourceName);
            if (stream is null) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var raw = JsonSerializer.Deserialize<RawFile>(stream, opts);

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (raw?.Aliases is not null)
                foreach (var (token, display) in raw.Aliases)
                    map[token] = display;
            return map;
        }
        catch (Exception)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    // "notes" (per-entry verification provenance) is intentionally not modeled here - it is
    // documentation for maintainers, not runtime data, and System.Text.Json simply ignores JSON
    // properties with no matching member (same precedent as SctUexMap's "_meta").
    private sealed class RawFile
    {
        public int Schema { get; set; }
        public Dictionary<string, string>? Aliases { get; set; }
    }
}
