namespace NexusApp.Services;

/// <summary>
/// Drops implausible prices from the SCT feed before they are stored.
///
/// SCT listings are user-submitted and unvalidated, so a mistyped price lands in the feed exactly
/// like a real one. The owner caught the case this exists for (2026-08-03): quartz at Rod's Fuel 'n
/// Supplies came through at 84,200 aUEC, a fat-fingered 4,200, against a median of 4,400 across
/// every terminal reporting quartz. Because a lookup takes the newest listing per terminal, that
/// one typo became the number on screen.
///
/// The test is per commodity and per side: a price is rejected when it sits more than
/// <see cref="Multiple"/> times above, or below, the median of every listing for that same
/// commodity and side. Measured against a live snapshot this rejects about 5% of rows, and the
/// rate barely moves between 4x and 10x - almost every outlier is an order-of-magnitude typo
/// (processed food at 21,500 and 61,500 against a 1,500 median) rather than genuine spread, which
/// is what makes a multiple-of-median test the right shape here.
///
/// Deliberately conservative in two ways. The median is only trusted with
/// <see cref="MinSamples"/> listings behind it, so a thinly reported commodity is never judged
/// against one or two of its own rows; and nothing is corrected, only dropped, because the true
/// price of a mistyped listing is not knowable from the listing.
/// </summary>
public static class SctOutlierFilter
{
    /// <summary>How far from its commodity's median a price may sit, in either direction.</summary>
    public const double Multiple = 4.0;

    /// <summary>Listings a commodity and side needs before its median is trusted as a yardstick.</summary>
    public const int MinSamples = 4;

    /// <summary>Returns the listings worth keeping, and how many were dropped as implausible.</summary>
    public static (List<SctListing> Kept, int Dropped) Apply(IReadOnlyList<SctListing> rows)
    {
        var groups = new Dictionary<(string Commodity, bool Buy), List<double>>();
        foreach (var r in rows)
        {
            if (r.Price <= 0) continue;   // not a price; the median must not be dragged by it
            var key = (r.Commodity, IsBuy(r.Transaction));
            if (!groups.TryGetValue(key, out var prices)) groups[key] = prices = new List<double>();
            prices.Add(r.Price);
        }

        var medians = new Dictionary<(string, bool), double>();
        foreach (var (key, prices) in groups)
        {
            if (prices.Count < MinSamples) continue;   // too thin to judge anything against
            prices.Sort();
            medians[key] = prices.Count % 2 == 1
                ? prices[prices.Count / 2]
                : (prices[prices.Count / 2 - 1] + prices[prices.Count / 2]) / 2.0;
        }

        var kept = new List<SctListing>(rows.Count);
        int dropped = 0;
        foreach (var r in rows)
        {
            if (r.Price > 0
                && medians.TryGetValue((r.Commodity, IsBuy(r.Transaction)), out var median)
                && median > 0
                && (r.Price > median * Multiple || r.Price < median / Multiple))
            {
                dropped++;
                continue;
            }
            kept.Add(r);
        }
        return (kept, dropped);
    }

    // SCT's own wording, read literally: this only has to group like with like, so which side is
    // the player's does not matter here (unlike a lookup, where it very much does).
    private static bool IsBuy(string transaction) =>
        transaction.StartsWith("BUY", StringComparison.OrdinalIgnoreCase);
}
