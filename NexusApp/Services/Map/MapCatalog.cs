using System.Globalization;
using System.IO;
using System.Text.Json;
using NexusApp.Services;

namespace NexusApp.Services.Map;

// One UEX alias pair carried on a starmap object: kind is "location", "planetOrMoon", or "orbit"
// (mirrors MarketTerminal's own hierarchy fields), name is the UEX-side string that maps to this
// object. An object can carry more than one alias pair (e.g. a planet is both a planetOrMoon and
// an orbit reference point) - every pair is indexed independently, never just the first.
public sealed record MapUexAlias(string Kind, string Name);

// One object from the embedded starmap catalog (Data/starmap_map.json): a star, planet, moon,
// station, outpost, or similar. Position is absolute, star-relative meters, valid only within a
// single system (System). Parent is the id of the object this one orbits/sits on, or null for a
// system's root (the star). Uex is empty, never null, when the object has no UEX alias pairs.
public sealed record MapObject(int Id, string System, string Name, string Type,
    double X, double Y, double Z, int? Parent, IReadOnlyList<MapUexAlias> Uex);

// Read-only catalog over the embedded full starmap object graph (Data/starmap_map.json),
// extracted from the game's StarMap DataCore via sc-datamine/starbreaker. Distinct from
// StarmapCatalog (Data/starmap_locations.json): that one is a flat, UEX-only position lookup used
// by the trading tab's proximity tiers; this one carries every object with its parent/type/id so
// the MAP tab can render the full system graph, not just UEX-recognized terminals.
// Public-safe: derived positions and ids only, no bulk source data, no PII.
public sealed class MapCatalog
{
    private readonly List<MapObject> _objects;
    private readonly Dictionary<int, MapObject> _byId;

    // Keyed system+name, case-insensitive (UEX/game naming is free text and not guaranteed to
    // agree on case, same reasoning as StarmapCatalog).
    private readonly Dictionary<string, MapObject> _byName;

    // Keyed system+uexKind+uexName, case-insensitive. Every alias pair on every object is indexed
    // here independently (an object with two alias pairs gets two entries), not just one per
    // object - a planet's planetOrMoon alias and its orbit alias must both resolve.
    private readonly Dictionary<string, MapObject> _byAlias;

    private MapCatalog(List<MapObject> objects, Dictionary<int, MapObject> byId,
        Dictionary<string, MapObject> byName, Dictionary<string, MapObject> byAlias)
    {
        _objects = objects;
        _byId = byId;
        _byName = byName;
        _byAlias = byAlias;
    }

    public IReadOnlyList<MapObject> Objects => _objects;
    public int Count => _objects.Count;

    // NexusApp.Data.<filename> - the same embedded-resource naming SctUexMap.LoadEmbedded,
    // CargoShipCatalog.LoadEmbedded, and StarmapCatalog.LoadEmbedded use (folder separators in
    // the resource's own namespace become dots; the class's own folder, Services/Map, is
    // irrelevant to the resource name, which always resolves against Data/<filename>).
    private const string ResourceName = "NexusApp.Data.starmap_map.json";

    public static MapCatalog LoadEmbedded()
    {
        using var stream = typeof(MapCatalog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource not found: {ResourceName}");
        return Load(stream);
    }

    // Never throws: a malformed stream (should not happen for an embedded, build-controlled
    // resource, but this loader is also exercised directly in tests) resolves to an empty catalog
    // rather than crashing whatever called it - same contract as StarmapCatalog.Load.
    public static MapCatalog Load(Stream stream)
    {
        try
        {
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var raw = JsonSerializer.Deserialize<RawCatalog>(stream, opts) ?? new RawCatalog();

            var objects = new List<MapObject>();
            var byId = new Dictionary<int, MapObject>();
            var byName = new Dictionary<string, MapObject>(StringComparer.OrdinalIgnoreCase);
            var byAlias = new Dictionary<string, MapObject>(StringComparer.OrdinalIgnoreCase);

            if (raw.Objects is not null)
            {
                foreach (var o in raw.Objects)
                {
                    if (string.IsNullOrEmpty(o.System) || string.IsNullOrEmpty(o.Name))
                        continue;

                    var aliases = BuildAliases(o.Uex);
                    var obj = new MapObject(o.Id, o.System, o.Name, o.Type ?? "", o.X, o.Y, o.Z, o.Parent, aliases);

                    objects.Add(obj);
                    byId[obj.Id] = obj;
                    byName[NameKey(obj.System, obj.Name)] = obj;

                    foreach (var alias in aliases)
                        byAlias[AliasKey(obj.System, alias.Kind, alias.Name)] = obj;
                }
            }

            return new MapCatalog(objects, byId, byName, byAlias);
        }
        catch (Exception)
        {
            return new MapCatalog(new List<MapObject>(), new Dictionary<int, MapObject>(),
                new Dictionary<string, MapObject>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, MapObject>(StringComparer.OrdinalIgnoreCase));
        }
    }

    private static IReadOnlyList<MapUexAlias> BuildAliases(List<RawUexAlias>? raw)
    {
        if (raw is null || raw.Count == 0) return Array.Empty<MapUexAlias>();

        var result = new List<MapUexAlias>(raw.Count);
        foreach (var a in raw)
        {
            if (string.IsNullOrEmpty(a.Kind) || string.IsNullOrEmpty(a.Name)) continue;
            result.Add(new MapUexAlias(a.Kind, a.Name));
        }
        return result;
    }

    // Joins with the ASCII unit-separator control character (0x1F) - not a character any real
    // system/kind/name string carries, so joining with it cannot collide two distinct tuples into
    // the same key (a plain concatenation could, e.g. system "A"+name "BC" vs system "AB"+name
    // "C"). Mirrors StarmapCatalog.Key.
    private const char KeySep = '';
    private static string NameKey(string system, string name) => string.Concat(system, KeySep, name);
    private static string AliasKey(string system, string kind, string name) => string.Concat(system, KeySep, kind, KeySep, name);

    public MapObject? ById(int id) => _byId.TryGetValue(id, out var o) ? o : null;

    public MapObject? ByName(string system, string name)
    {
        if (string.IsNullOrEmpty(system) || string.IsNullOrEmpty(name)) return null;
        return _byName.TryGetValue(NameKey(system, name), out var o) ? o : null;
    }

    // Fallback order: location (a terminal's own station/city, most precise) beats planetOrMoon
    // (the body it orbits) beats orbit (the wider Lagrange/orbital region). An empty field on the
    // terminal skips that level outright; a non-empty field that simply has no match in the
    // catalog also falls through to the next level rather than failing the whole resolution -
    // same fallback semantics as StarmapCatalog.Resolve (StarmapCatalog.cs:84). Scoped to
    // t.System throughout: this never crosses systems.
    public MapObject? ResolveTerminal(MarketTerminal? t)
    {
        if (t is null || string.IsNullOrEmpty(t.System)) return null;

        if (!string.IsNullOrEmpty(t.Location) && _byAlias.TryGetValue(AliasKey(t.System, "location", t.Location), out var loc))
            return loc;
        if (!string.IsNullOrEmpty(t.PlanetOrMoon) && _byAlias.TryGetValue(AliasKey(t.System, "planetOrMoon", t.PlanetOrMoon), out var pom))
            return pom;
        if (!string.IsNullOrEmpty(t.Orbit) && _byAlias.TryGetValue(AliasKey(t.System, "orbit", t.Orbit), out var orb))
            return orb;

        return null;
    }

    // Straight-line meters between two objects, or null when either is null or the two objects
    // are not in the same system (jump-point travel is not Euclidean, so a cross-system
    // "distance" would be meaningless and is never computed).
    public double? DistanceMeters(MapObject? a, MapObject? b)
    {
        if (a is null || b is null) return null;
        if (!string.Equals(a.System, b.System, StringComparison.OrdinalIgnoreCase)) return null;

        double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    // "22.9 Gm" / "1.85 Gm" / "0.46 Gm" / "<0.1 Gm". Large distances round to one decimal (extra
    // precision is noise at that scale); anything under 10 Gm gets two decimals; anything under
    // 0.1 Gm is too small to usefully round to two decimals, so it reports the floor literal
    // instead. Copied verbatim from StarmapCatalog.FormatGm (StarmapCatalog.cs:114-120).
    public static string FormatGm(double meters)
    {
        double gm = meters / 1_000_000_000.0;
        if (gm >= 10) return gm.ToString("0.0", CultureInfo.InvariantCulture) + " Gm";
        if (gm >= 0.1) return gm.ToString("0.00", CultureInfo.InvariantCulture) + " Gm";
        return "<0.1 Gm";
    }

    private sealed class RawCatalog
    {
        public int Schema { get; set; }
        public string? GameBuild { get; set; }
        public List<RawObject>? Objects { get; set; }
    }

    private sealed class RawObject
    {
        public int Id { get; set; }
        public string? System { get; set; }
        public string? Name { get; set; }
        public string? Type { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        public int? Parent { get; set; }
        public List<RawUexAlias>? Uex { get; set; }
    }

    private sealed class RawUexAlias
    {
        public string? Kind { get; set; }
        public string? Name { get; set; }
    }
}
