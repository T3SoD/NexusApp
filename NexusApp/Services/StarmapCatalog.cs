using System.Globalization;
using System.IO;
using System.Text.Json;

namespace NexusApp.Services;

// Derived inter-location distances (Data/starmap_locations.json). Added decoration
// (2026-07-30) on top of the approved trading-tab mock: true gigameter distances alongside the
// existing proximity tiers - a recorded, deliberate deviation beyond the mock.
// Positions are absolute, star-relative meters and are valid ONLY within a single system; two
// positions from different systems are never compared (jump-point travel is not Euclidean).
// Public-safe: derived positions only, no bulk source data, no PII.
internal readonly record struct StarmapPosition(double X, double Y, double Z);

internal sealed class StarmapCatalog
{
    // Composite key: system + kind + uexName, all matched case-insensitively (mirrors SctUexMap's
    // OrdinalIgnoreCase keying - MarketTerminal's own hierarchy fields and this catalog's uexName
    // strings are both free text drawn from the UEX API / the game's own naming, not guaranteed to
    // agree on case).
    private readonly Dictionary<string, StarmapPosition> _places;

    private StarmapCatalog(Dictionary<string, StarmapPosition> places)
    {
        _places = places;
    }

    public int PlaceCount => _places.Count;

    // NexusApp.Data.<filename> - the same embedded-resource naming SctUexMap.LoadEmbedded and
    // CargoShipCatalog.LoadEmbedded use (folder separators become dots).
    private const string ResourceName = "NexusApp.Data.starmap_locations.json";

    public static StarmapCatalog LoadEmbedded()
    {
        using var stream = typeof(StarmapCatalog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource not found: {ResourceName}");
        return Load(stream);
    }

    // Never throws: a malformed stream (should not happen for an embedded, build-controlled
    // resource, but this loader is also exercised directly in tests) resolves to an empty catalog
    // rather than crashing whatever called it - same contract as SctUexMap.Load.
    public static StarmapCatalog Load(Stream stream)
    {
        try
        {
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var raw = JsonSerializer.Deserialize<RawCatalog>(stream, opts) ?? new RawCatalog();

            var places = new Dictionary<string, StarmapPosition>(StringComparer.OrdinalIgnoreCase);
            if (raw.Places is not null)
            {
                foreach (var p in raw.Places)
                {
                    if (string.IsNullOrEmpty(p.System) || string.IsNullOrEmpty(p.Kind) || string.IsNullOrEmpty(p.UexName))
                        continue;
                    places[Key(p.System, p.Kind, p.UexName)] = new StarmapPosition(p.X, p.Y, p.Z);
                }
            }

            return new StarmapCatalog(places);
        }
        catch (Exception)
        {
            return new StarmapCatalog(new Dictionary<string, StarmapPosition>(StringComparer.OrdinalIgnoreCase));
        }
    }

    // Joins with the ASCII unit-separator control character (0x1F) - not a character any real
    // system/kind/uexName string carries, so joining with it cannot collide two distinct triples
    // into the same key (a plain concatenation could, e.g. system "A"+kind "BC" vs system
    // "AB"+kind "C").
    private const char KeySep = '';
    private static string Key(string system, string kind, string name) => string.Concat(system, KeySep, kind, KeySep, name);

    // Fallback order: location (a terminal's own station/city, most precise) beats planetOrMoon
    // (the body it orbits) beats orbit (the wider Lagrange/orbital region). An empty field on the
    // terminal skips that level outright; a non-empty field that simply has no match in the catalog
    // also falls through to the next level rather than failing the whole resolution - UEX's own
    // location strings do not always line up 1:1 with the starmap's naming, so a coarser resolved
    // position is better than none. Scoped to t.System throughout: this never crosses systems.
    public StarmapPosition? Resolve(MarketTerminal? t)
    {
        if (t is null || string.IsNullOrEmpty(t.System)) return null;

        if (!string.IsNullOrEmpty(t.Location) && _places.TryGetValue(Key(t.System, "location", t.Location), out var loc))
            return loc;
        if (!string.IsNullOrEmpty(t.PlanetOrMoon) && _places.TryGetValue(Key(t.System, "planetOrMoon", t.PlanetOrMoon), out var pom))
            return pom;
        if (!string.IsNullOrEmpty(t.Orbit) && _places.TryGetValue(Key(t.System, "orbit", t.Orbit), out var orb))
            return orb;

        return null;
    }

    // Straight-line meters between two terminals, or null when either fails to resolve a position
    // or the two terminals are not in the same system (jump-point travel is not Euclidean, so a
    // cross-system "distance" would be meaningless and is never computed).
    public double? DistanceMeters(MarketTerminal? a, MarketTerminal? b)
    {
        if (a is null || b is null) return null;
        if (!string.Equals(a.System, b.System, StringComparison.OrdinalIgnoreCase)) return null;
        if (Resolve(a) is not { } pa || Resolve(b) is not { } pb) return null;

        double dx = pa.X - pb.X, dy = pa.Y - pb.Y, dz = pa.Z - pb.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    // "22.9 Gm" / "1.85 Gm" / "0.46 Gm" / "<0.1 Gm". Large distances round to one decimal (extra
    // precision is noise at that scale); anything under 10 Gm gets two decimals; anything under
    // 0.1 Gm is too small to usefully round to two decimals, so it reports the floor literal instead.
    public static string FormatGm(double meters)
    {
        double gm = meters / 1_000_000_000.0;
        if (gm >= 10) return gm.ToString("0.0", CultureInfo.InvariantCulture) + " Gm";
        if (gm >= 0.1) return gm.ToString("0.00", CultureInfo.InvariantCulture) + " Gm";
        return "<0.1 Gm";
    }

    private sealed class RawCatalog
    {
        public List<RawPlace>? Places { get; set; }
    }

    private sealed class RawPlace
    {
        public string? System { get; set; }
        public string? Kind { get; set; }
        public string? UexName { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
    }
}
