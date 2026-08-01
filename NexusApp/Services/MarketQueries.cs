using System.Linq;

namespace NexusApp.Services;

// One priced sighting of a commodity at one terminal, the shape every price surface (Cargo
// Hauling, work order suggestions, the Mining Codex) reads. WeekAvg / Instant / GameVersion /
// ModifiedUtc are carried straight from the MarketPriceRow the hit was built from; Stale is
// computed once here so callers never re-derive it.
// TerminalId is LAST and OPTIONAL on purpose: it was added 2026-08-01 (app review) to an existing
// record with construction sites in test fixtures, and a trailing default keeps every one of them
// compiling untouched. 0 means "not known", which is what a hand-built fixture gets.
//
// Why it matters out of proportion to its size: the id was already in hand and thrown away one line
// from where it was needed (RankedHits builds each hit from a MarketPriceRow whose first field is
// TerminalId). Without it, every non-Trade price surface - the Codex dossier and cards, the work
// order sell block, the decoder scan card, the overlay SCAN line - held only a NAME, so none could
// resolve snapshot.Terminals to a MarketTerminal. That single gap is why none of them could print
// which system a price is in, none could compute a distance, and none could click through to
// TradePage.ShowPricesForTerminal(int), which the Starmap already calls.
internal sealed record PriceHit(string TerminalName, double WeekAvg, double Instant, DateTime ModifiedUtc, string GameVersion, bool Stale, int TerminalId = 0)
{
    // The number every surface displays: week average, falling back to instant when week is 0.
    public double Display => WeekAvg > 0 ? WeekAvg : Instant;
}

// The pure query layer over a MarketSnapshot: seed resource name -> UEX raw commodity -> its
// refined counterpart -> price rows, ranked so a fresh row always beats a stale one regardless
// of price. No I/O, no mutation of the snapshot; every method is a straight read. Internal
// because callers are all within NexusApp (price surfaces, the work order flow).
internal static class MarketQueries
{
    // Session-lifetime guard: a seed resource name with no UEX mapping is logged the first time
    // any query touches it and never again, so a resource missing from MarketNameMap (or a typo
    // in caller code) does not spam nexus.log on every repaint of a price surface. Keyed
    // case-insensitively to match MarketNameMap.UexRawNameFor's own lookup.
    private static readonly HashSet<string> _loggedMisses = new(StringComparer.OrdinalIgnoreCase);

    // The single best (freshest, then highest-Display) sell for a raw ore's REFINED counterpart,
    // or null when the snapshot is null, the resource name is unmapped, its refined counterpart
    // cannot be resolved, or there are no qualifying rows. Every price surface quotes refined:
    // UEX's raw ore-sales dataset has had no community reports since patch 4.8, so a raw number
    // would be confidently wrong (amendment 2026-07-27).
    public static PriceHit? BestRefinedSell(MarketSnapshot? s, string seedResourceName) =>
        TopRefinedSells(s, seedResourceName, 1).FirstOrDefault();

    // The top `count` refined sells for a raw ore: fresh rows first (each ranked by Display desc),
    // then stale rows (also ranked by Display desc). Rows with Display <= 0 never appear. The
    // resolution chain is seed name -> UEX raw commodity -> its refined counterpart (idParent
    // link, falling back to the " (Raw)"/" (Ore)" name-strip match) -> that id's refined rows.
    public static List<PriceHit> TopRefinedSells(MarketSnapshot? s, string seedResourceName, int count)
    {
        if (s is null) return new List<PriceHit>();
        var raw = ResolveRawCommodity(s, seedResourceName);
        if (raw is null) return new List<PriceHit>();
        var refined = MarketNameMap.RefinedFor(raw, s.Commodities.Rows);
        if (refined is null) return new List<PriceHit>();
        return RankedHits(s.LiveGameVersion, s.RefinedPrices.Rows, refined.Id, count);
    }

    // For a work order's free text: every seed resource name RecognizeSeedNames finds, resolved
    // to its best refined sell, ordered by Hit.Display descending. A recognized name that fails
    // to resolve to a priced refined row (no commodity match, no rows) is silently dropped, same
    // as everywhere else in this class - never a throw, never a placeholder entry.
    public static List<(string SeedName, PriceHit Hit)> RefinedSellsForOrder(MarketSnapshot? s, string resourcesFreeText)
    {
        var result = new List<(string SeedName, PriceHit Hit)>();
        if (s is null) return result;

        foreach (var name in MarketNameMap.RecognizeSeedNames(resourcesFreeText))
        {
            var hit = BestRefinedSell(s, name);
            if (hit is not null) result.Add((name, hit));
        }

        return result.OrderByDescending(x => x.Hit.Display).ToList();
    }

    // Seed resource name -> its raw MarketCommodity row, via MarketNameMap.UexRawNameFor then a
    // case-insensitive name match against the snapshot's commodity catalogue. Null on either
    // failure; an unmapped name is logged once per session here (RefinedFor's own resolution
    // path also goes through this method, so the miss is never logged twice for one name).
    private static MarketCommodity? ResolveRawCommodity(MarketSnapshot s, string seedResourceName)
    {
        var uexName = MarketNameMap.UexRawNameFor(seedResourceName);
        if (uexName is null)
        {
            LogMissOnce(seedResourceName);
            return null;
        }

        foreach (var c in s.Commodities.Rows)
        {
            if (string.Equals(c.Name, uexName, StringComparison.OrdinalIgnoreCase)) return c;
        }
        return null;
    }

    private static void LogMissOnce(string seedResourceName)
    {
        lock (_loggedMisses)
        {
            if (!_loggedMisses.Add(seedResourceName)) return;
        }
        Logger.Info($"{MarketDataService.Tag} no UEX mapping for '{seedResourceName}'");
    }

    // Rows for one commodity id -> ranked PriceHits: fresh before stale, Display desc within
    // each class, Display <= 0 dropped, truncated to count. The one ranking rule every price
    // surface inherits, kept separate from the resolution chain that feeds it.
    private static List<PriceHit> RankedHits(string liveGameVersion, List<MarketPriceRow> rows, int commodityId, int count)
    {
        var hits = new List<PriceHit>();
        foreach (var row in rows)
        {
            if (row.CommodityId != commodityId) continue;

            // Nothing is stale when the live version is unknown (empty): a snapshot that has
            // never successfully fetched game_versions must not brand every row stale.
            var stale = !string.IsNullOrEmpty(liveGameVersion)
                && !string.Equals(row.GameVersion, liveGameVersion, StringComparison.OrdinalIgnoreCase);

            var hit = new PriceHit(row.TerminalName, row.SellAvgWeek, row.Sell, row.ModifiedUtc, row.GameVersion, stale, row.TerminalId);
            if (hit.Display <= 0) continue;
            hits.Add(hit);
        }

        return hits
            .OrderBy(h => h.Stale)             // false (fresh) sorts before true (stale)
            .ThenByDescending(h => h.Display)
            .Take(count)
            .ToList();
    }
}
