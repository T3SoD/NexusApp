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

// Derived location data (Data/starmap_map.json): named objects for the three systems with
// absolute star-relative positions in meters and UEX alias joins. Distinct from
// StarmapCatalog (Data/starmap_locations.json, the trade distance embed): that one is a flat,
// UEX-only position lookup used by the trading tab's proximity tiers; this catalog carries the
// full object set, hierarchy, and alias pairs for the map surface. Positions are valid only
// within a single system; two positions from different systems are never compared (jump-point
// travel is not Euclidean).
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

    // Objects belonging to star systems that are not in the game (the owner, 2026-08-01: "star map still
    // mentions terra jump point and it shouldnt" - the same call he made on the gateway aliases).
    // They are in the artifact because it is derived from the game's own object catalog, which
    // carries unreleased content: real data, but not places any player can reach. Left in, they
    // turn up in search results and the SELECTION panel and imply the map covers somewhere it does
    // not.
    //
    // Filtered at LOAD rather than deleted from the artifact on purpose. The artifact is generated,
    // so a regeneration would silently restore them; this exclusion survives that. Keyed by
    // (system, name) rather than by id for the same reason - ids come from the generator and are
    // not stable across runs.
    //
    // Note what is deliberately absent: "Terra Mills HydroFarm" is a real Hurston outpost that
    // shares only the word "Terra". Matching is exact, never a substring, precisely so that
    // an unrelated name cannot be swept up.
    //
    // The Castra entry was raised as a question (its object sits in Nyx, which IS live, so an
    // unactivated jump point might have been something a player could fly to and see) and the owner
    // ruled it out on the same terms as the rest. The destination system is what decides, not the
    // system the object happens to sit in.
    //
    // TERRA WAS REMOVED FROM THIS LIST 2026-08-01, same day it was added: the owner corrected the
    // premise - "terra gateway does exist in the game, magnus does not". That also explains the
    // thing that looked wrong at the time, namely UEX carrying 21 real terminals at
    // "Terra Gateway (Stanton)" with priced rows. The data was right and the exclusion was wrong.
    //
    // Every remaining entry is alias-free, which is asserted by a test rather than assumed - see
    // ExcludedObjects_CarryNoUexAliases. That guard is what is left of the carve-out this list once
    // needed: while Terra was excluded, its aliases had to stay resolvable for distance or 21
    // terminals would have silently lost the figure the planner had always shown. Rather than keep
    // a second resolve path with no members, the test now fails if anyone excludes an alias-carrying
    // object again, which is the moment that problem would come back.
    private static readonly HashSet<string> ExcludedObjects = new(StringComparer.OrdinalIgnoreCase)
    {
        NameKey("Stanton", "Stanton - Magnus Jump Point"),
        NameKey("Nyx", "Nyx - Castra Jump Point"),
    };

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

                    if (ExcludedObjects.Contains(NameKey(o.System, o.Name)))
                        continue;

                    objects.Add(new MapObject(o.Id, o.System, o.Name, o.Type ?? "", o.X, o.Y, o.Z,
                                              o.Parent, BuildAliases(o.Uex)));
                }

                // An excluded object can be some SURVIVING object's parent, which would leave a
                // dangling parent id behind. That is not hypothetical: Stanton's "Nyx Gateway" is
                // parented to the Stanton-Magnus Jump Point in the source data, and Nyx is a real,
                // reachable system. Re-root those to the system instead of leaving a reference to
                // nothing - the scene walks the parent chain to decide cluster collapse and the
                // SELECTION panel resolves the parent's name, and both should see a catalog whose
                // every parent id actually resolves.
                var presentIds = new HashSet<int>();
                foreach (var o in objects) presentIds.Add(o.Id);
                for (int i = 0; i < objects.Count; i++)
                    if (objects[i].Parent is { } parentId && !presentIds.Contains(parentId))
                        objects[i] = objects[i] with { Parent = null };

                foreach (var obj in objects)
                {
                    byId[obj.Id] = obj;
                    byName[NameKey(obj.System, obj.Name)] = obj;

                    foreach (var alias in obj.Uex)
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

    // Location search (MAP tab search box). Covers every system in the catalog - there is no
    // "active system" concept here, that gating belongs to the caller (MapPage), never this pure
    // seam. Case-insensitive substring match; a name that STARTS WITH the query ranks above a name
    // that merely contains it elsewhere (a search for "Ever" surfaces "Everus Harbor" ahead of, say,
    // "Nevermind"). Ties within the same rank break shorter-name-first, then ordinal name order, so
    // results are stable and deterministic run to run. Empty/whitespace query (including null) and a
    // non-positive limit both resolve to an empty list rather than the full catalog or an exception -
    // never throws.
    public IReadOnlyList<MapObject> Search(string query, int limit)
    {
        if (string.IsNullOrWhiteSpace(query) || limit <= 0) return Array.Empty<MapObject>();

        string q = query.Trim();
        return _objects
            .Where(o => o.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderBy(o => o.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(o => o.Name.Length)
            .ThenBy(o => o.Name, StringComparer.Ordinal)
            .Take(limit)
            .ToList();
    }

    // Raw Game.log token -> (system, MapObject.Name) for the gateway stations whose in-game
    // display name repeats across systems ("Pyro Gateway Station" names an object in BOTH Stanton
    // and Nyx; "Stanton Gateway Station" names an object in BOTH Pyro and Nyx; "Nyx Gateway
    // Station" names an object in BOTH Stanton and Pyro - see
    // ResolvePlayerLocation). Only the raw token is unique enough to tell them apart, same reasoning
    // as LocationAliases.UexLocationForToken, and this table deliberately mirrors that one's
    // ground-every-entry discipline. Every gateway token that can actually fire is here; the two
    // still-uncaptured Nyx tokens are deliberately absent, so a miss falls through to null instead
    // of guessing at a system.
    //
    // RR_JP_NyxPyro is the sharpest illustration of why this table exists: standing at the Nyx-side
    // gate, the object you are at is called "Pyro Gateway" - the SAME name as the Stanton-side one -
    // and nothing but the raw token separates them.
    private static readonly IReadOnlyDictionary<string, (string System, string Name)> RawTokenGateways =
        new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["RR_JP_StantonPyro"] = ("Stanton", "Pyro Gateway"),   // Stanton-side gate to Pyro
            ["RR_JP_PyroStanton"] = ("Pyro", "Stanton Gateway"),   // Pyro-side gate to Stanton
            ["RR_JP_PyroNyx"]     = ("Pyro", "Nyx Gateway"),       // Pyro-side gate to Nyx (captured live 2026-08-01)
            ["RR_JP_NyxPyro"]     = ("Nyx", "Pyro Gateway"),       // Nyx-side gate to Pyro (captured live twice, 2026-08-01)
        };

    // LocationTracker.LastKnownLocation display names that do not exact-match a MapObject.Name but
    // are still unambiguous (exactly one system carries that object name) - see ResolvePlayerLocation.
    // "Pyro Gateway Station"/"Stanton Gateway Station" are deliberately absent even though they are
    // also near-misses: both target object names exist in more than one system, so aliasing them
    // here by display name alone could mark the wrong system. Those two are resolved only through
    // RawTokenGateways above, before this table is ever consulted.
    // "Terra Gateway Station" was pulled from here earlier on 2026-08-01 and restored the same day
    // when the owner corrected the premise: Terra Gateway DOES exist in the game (Magnus does not). It is
    // unambiguous - "Terra Gateway" names an object in Stanton only - so a plain display-name alias
    // is right for it, unlike the gateways whose object names repeat across systems and can only be
    // told apart by raw token.
    private static readonly IReadOnlyDictionary<string, string> PlayerLocationAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Area 18"] = "Area18",
            ["Checkmate Station"] = "Checkmate",
            ["Terra Gateway Station"] = "Terra Gateway",
        };

    // Resolves LocationTracker.LastKnownLocation (an alias-normalized DISPLAY name) plus its raw
    // Game.log token to the MapObject it names, for the MAP tab's player marker. Never throws; a
    // null/empty displayName with no raw-token hit, or a name with no resolution anywhere, returns
    // null - callers must treat null as "no live location to show", never as an error or a guess.
    //
    // Resolution order:
    //   1. RawTokenGateways - the only source that can tell the Pyro-side "Stanton Gateway Station"
    //      from the Nyx-side one, since the display name repeats across systems.
    //   2. Exact case-insensitive MapObject.Name match, searched across every system (35 of the 41
    //      possible alias display names already hit this tier directly).
    //   3. PlayerLocationAliases - the near-misses proven unambiguous (a single system carries
    //      that object name): Area 18, Checkmate Station.
    //   4. null - any unrecognized text, and any gateway display name with no raw token to place it.
    public MapObject? ResolvePlayerLocation(string? displayName, string? rawToken)
    {
        if (!string.IsNullOrEmpty(rawToken) && RawTokenGateways.TryGetValue(rawToken, out var gw))
        {
            var byToken = ByName(gw.System, gw.Name);
            if (byToken != null) return byToken;
        }

        if (string.IsNullOrEmpty(displayName)) return null;

        foreach (var obj in _objects)
            if (string.Equals(obj.Name, displayName, StringComparison.OrdinalIgnoreCase))
                return obj;

        if (PlayerLocationAliases.TryGetValue(displayName, out var aliasName))
            foreach (var obj in _objects)
                if (string.Equals(obj.Name, aliasName, StringComparison.OrdinalIgnoreCase))
                    return obj;

        return null;
    }

    /// <summary>Resolves a MINING SEED location string to a map object, or null. Separate from
    /// ResolvePlayerLocation because the seed's vocabulary is its own: these are hand-authored
    /// strings from the mining data, not Game.log tokens.
    ///
    /// <para>Coverage is genuinely partial and that is the point of returning null rather than
    /// guessing. Of the 48 distinct seed strings only about 15 exact-match an object name; a large
    /// class never resolves at all (Aaron Halo, Glaciem Ring, the Lagrange entries, the belts,
    /// Breaker Stations, Hathor Caves) because they are regions rather than catalogued objects.
    /// Callers must resolve-then-decorate per row, never present a whole list as navigable, or it
    /// looks broken exactly where miners spend their time.</para>
    ///
    /// <para>The parenthetical fallback is what lifts coverage materially: the seed writes Pyro's
    /// planets as "Pyro II (Monox)" while the catalog knows them as "Monox".</para></summary>
    public MapObject? ResolveSeedLocation(string? seedLocation)
    {
        if (string.IsNullOrWhiteSpace(seedLocation)) return null;

        var name = seedLocation.Trim();
        foreach (var obj in _objects)
            if (string.Equals(obj.Name, name, StringComparison.OrdinalIgnoreCase))
                return obj;

        int open = name.IndexOf('(');
        int close = name.LastIndexOf(')');
        if (open >= 0 && close > open + 1)
        {
            var inner = name[(open + 1)..close].Trim();
            if (inner.Length > 0)
                foreach (var obj in _objects)
                    if (string.Equals(obj.Name, inner, StringComparison.OrdinalIgnoreCase))
                        return obj;
        }

        return null;
    }

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

        return Look("location", t.Location) ?? Look("planetOrMoon", t.PlanetOrMoon) ?? Look("orbit", t.Orbit);

        MapObject? Look(string kind, string? value)
            => !string.IsNullOrEmpty(value) && _byAlias.TryGetValue(AliasKey(t.System, kind, value), out var hit)
                ? hit : null;
    }

    /// <summary>Straight-line meters between two market terminals, or null when either fails to
    /// resolve or they sit in different systems (jump travel is not Euclidean, so a cross-system
    /// "distance" would be meaningless and is never computed). This is the seam that let Trade drop
    /// its own second geometry catalog: same contract as the StarmapCatalog.DistanceMeters it
    /// replaces, resolving through the excluded-aware path so no terminal loses a distance it had.</summary>
    public double? DistanceMeters(MarketTerminal? a, MarketTerminal? b)
    {
        if (a is null || b is null) return null;
        if (!string.Equals(a.System, b.System, StringComparison.OrdinalIgnoreCase)) return null;
        return DistanceMeters(ResolveTerminal(a), ResolveTerminal(b));
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
