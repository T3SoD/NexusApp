using System.IO;
using System.Text.Json;

namespace NexusApp.Services;

/// <summary>One hull as the TRADE surfaces need it: a name, what it carries, and the largest
/// container it can load. No grid geometry - the planner never packs a box, it only asks "how much
/// fits" and "can this terminal's crates go in".</summary>
public sealed record TradeShip(string Id, string DisplayName, string Manufacturer,
                              int TotalScu, int MaxContainerScu);

/// <summary>
/// The trade planner's ship list: every hull a player can actually fly that has cargo space,
/// embedded offline from Data/trade_ships.json.
///
/// WHY THIS IS NOT CargoShipCatalog. That catalog carries full 3D grid geometry and exists to feed
/// the Cargo Planner and Grid Studio, so it only covers the hulls whose grids have been reviewed
/// and signed off - 15 of them. The trade planner needs no geometry, only totals, so widening the
/// grid catalog would have dragged ~80 unreviewed layouts into the 3D surfaces for nothing. The two
/// files are kept honest by TradeShipCatalogTests, which asserts that every ship present in both
/// reports the same capacity and the same max container size.
///
/// Source of truth is the DataCore (per-grid cell dimensions) filtered by RSI's own ship matrix:
/// a hull is listed only if RSI lists it as its own flight-ready entry, which excludes NPC hulls,
/// in-game reward variants and store editions RSI does not sell. Ground vehicles and snubs are
/// dropped as well: neither can fly a route between terminals. Regenerate with
/// sc-datamine/extractors/build_trade_ships.py.
/// </summary>
public sealed class TradeShipCatalog
{
    public IReadOnlyList<TradeShip> Ships { get; }

    public TradeShipCatalog(IEnumerable<TradeShip> ships) =>
        // Plain alphabetical (2026-08-03). The grid catalog's hauling-priority rank is deliberately
        // not carried over: at ~90 entries the list is scanned by name, and ranking a handful of
        // hulls would put them somewhere the alphabet does not explain.
        Ships = ships.OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();

    public static TradeShipCatalog LoadEmbedded()
    {
        using var stream = typeof(TradeShipCatalog).Assembly
            .GetManifestResourceStream("NexusApp.Data.trade_ships.json")
            ?? throw new InvalidOperationException("trade_ships.json embedded resource not found");
        return Load(stream);
    }

    public static TradeShipCatalog Load(Stream stream)
    {
        var raw = JsonSerializer.Deserialize<List<RawShip>>(stream, JsonOpts) ?? new List<RawShip>();
        return new TradeShipCatalog(raw.Select(ToShip));
    }

    /// <summary>Look up a persisted selection. Null when the id is unknown, which callers treat as
    /// "fall back to the first ship" rather than an error - a saved id can outlive a catalog
    /// regeneration.</summary>
    public TradeShip? ById(string id) => Ships.FirstOrDefault(s => s.Id == id);

    private static TradeShip ToShip(RawShip r)
    {
        // A hull with no capacity has no place in a trade planner, and a non-positive max container
        // would make BoxFits reject every terminal, silently emptying the route list for that ship.
        // The generator cannot emit either, so this is a load-time contract check, not a fallback.
        if (r.TotalScu <= 0)
            throw new InvalidDataException($"{r.Name}: trade ship has no cargo capacity");
        if (r.MaxBoxScu <= 0)
            throw new InvalidDataException($"{r.Name}: trade ship has no usable container size");
        return new TradeShip(r.Id, r.Name, r.Manufacturer, r.TotalScu, r.MaxBoxScu);
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private sealed class RawShip
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Manufacturer { get; set; } = "";
        public int TotalScu { get; set; }
        public int MaxBoxScu { get; set; }
    }
}
