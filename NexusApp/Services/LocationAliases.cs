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

    private static readonly Lazy<IReadOnlyDictionary<string, string>> _aliases = new(LoadEmbeddedAliases);

    // Raw-token to UEX Location string (Data/location_aliases.json's "uexLocations" block). A
    // second, separately keyed table from _aliases above by necessity: display names are NOT
    // unique (RR_JP_PyroStanton, RR_JP_TerraStanton, and RR_JP_MagnusStanton all display as
    // "Stanton Gateway Station"), so a display-name to UEX-location lookup would be ambiguous and
    // could resolve to a terminal in the wrong system. Only the raw token is unique, so this table
    // is keyed by raw token and only holds entries verified against a live UEX snapshot (see the
    // file's "uexLocationNotes"). A miss (every non-gateway token, plus any gateway not yet
    // captured - e.g. the known Nyx gap) returns null by design, same never-throws/lazy-load
    // contract as Normalize.
    private static readonly Lazy<IReadOnlyDictionary<string, string>> _uexLocations = new(LoadEmbeddedUexLocations);

    public static string Normalize(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        return _aliases.Value.TryGetValue(raw, out var display) ? display : raw;
    }

    public static string? UexLocationForToken(string? rawToken)
    {
        if (string.IsNullOrEmpty(rawToken)) return null;
        return _uexLocations.Value.TryGetValue(rawToken, out var uexLocation) ? uexLocation : null;
    }

    // Test-only enumeration hook (internal, NexusApp.Tests via InternalsVisibleTo): lets the test
    // suite assert every committed uexLocations VALUE still names a real terminal Location in a
    // fixture, so a future UEX rename of one of these gateway Locations fails a test instead of
    // silently reproducing the exact live-planner bug this table exists to fix.
    internal static IReadOnlyDictionary<string, string> AllUexLocationsForTesting => _uexLocations.Value;

    private static IReadOnlyDictionary<string, string> LoadEmbeddedAliases() =>
        LoadEmbedded(raw => raw?.Aliases);

    private static IReadOnlyDictionary<string, string> LoadEmbeddedUexLocations() =>
        LoadEmbedded(raw => raw?.UexLocations);

    private static IReadOnlyDictionary<string, string> LoadEmbedded(Func<RawFile?, Dictionary<string, string>?> select)
    {
        try
        {
            using var stream = typeof(LocationAliases).Assembly.GetManifestResourceStream(ResourceName);
            if (stream is null) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var raw = JsonSerializer.Deserialize<RawFile>(stream, opts);

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (select(raw) is { } source)
                foreach (var (token, value) in source)
                    map[token] = value;
            return map;
        }
        catch (Exception)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    // "notes"/"uexLocationNotes" (per-entry verification provenance) are intentionally not
    // modeled here - they are documentation for maintainers, not runtime data, and
    // System.Text.Json simply ignores JSON properties with no matching member (same precedent as
    // SctUexMap's "_meta").
    private sealed class RawFile
    {
        public int Schema { get; set; }
        public Dictionary<string, string>? Aliases { get; set; }
        public Dictionary<string, string>? UexLocations { get; set; }
    }
}
