namespace NexusApp.Services;

/// <summary>One side of one terminal's trade in a commodity, after choosing between the sources.
/// <paramref name="FromSct"/> records which feed won so the UI can say so rather than presenting a
/// number of unknown provenance.</summary>
public readonly record struct SideReading(double Price, int QuantityScu, DateTime AsOfUtc, bool FromSct);

/// <summary>
/// Picks the fresher of the two community feeds for one price/quantity pair
/// (owner, 2026-08-03: "show whichever one has the freshest data as reported by their respective
/// api, if UEX is 2 days old but SCT is 1 hour old show SCT").
///
/// Pure and per SIDE, because the two feeds are shaped differently: a UEX row carries one
/// ModifiedUtc covering both its buy and sell columns, while SCT publishes each side as its own
/// observation with its own timestamp. A terminal can therefore have a one-hour-old SCT buy price
/// beside a two-day-old UEX sell price, and the choice has to be made twice.
/// </summary>
public static class MarketMerge
{
    /// <summary>
    /// The reading to show and to compute with. UEX is the default and the fallback; SCT wins only
    /// when it is genuinely both usable and newer.
    /// </summary>
    public static SideReading Choose(double uexPrice, int uexQuantity, DateTime uexUtc,
                                     double? sctPrice, int? sctQuantity, DateTime? sctUtc,
                                     DateTime nowUtc)
    {
        var uex = new SideReading(uexPrice, uexQuantity, uexUtc, FromSct: false);

        // Nothing to choose between.
        if (sctPrice is not { } price || sctUtc is not { } stamp) return uex;

        // A price of zero is not a cheaper price, it is the absence of one. Substituting it would
        // silently delete a route that UEX can still price.
        if (price <= 0) return uex;

        // Only ever a SUBSTITUTION, never an addition: with no usable UEX price this side is not
        // traded as far as the app's own terminal list is concerned, and promoting an SCT-only
        // observation into a rankable route is a bigger change than choosing the fresher of two
        // readings. The SCT-only surfaces on the Sell and Prices tabs remain where that lives.
        if (uexPrice <= 0) return uex;

        // A stamp from the future is a clock problem, not freshness - the same stance
        // MarketDataService.ShouldFetch and SctMarketService.ShouldAutoRefresh already take.
        if (stamp > nowUtc) return uex;

        if (stamp <= uexUtc) return uex;

        // Price and quantity move TOGETHER, from the same observation. Taking UEX's price beside
        // SCT's stock would report a pair that never existed at any single moment, and the trip
        // size and the profit would then be computed from different snapshots of the terminal.
        return new SideReading(price, Math.Max(sctQuantity ?? 0, 0), stamp, FromSct: true);
    }
}
