using System.Globalization;

namespace NexusApp.Services;

/// <summary>
/// How far SC Trade Tools sits from UEX on one figure, signed and from UEX's point of view.
///
/// UEX stays the number the app shows and ranks on; this is only ever a margin note beside it
/// (2026-08-03: UEX stays the main source, with a +/-% delta shown next to prices and stock).
/// Positive means SCT reports MORE than UEX - a higher price, more stock. Negative means less.
///
/// Deliberately separate from PriceReconciler.DisagreePct, which is absolute and feeds the
/// Corroborated/Disagree decision. Making that one signed would change what a threshold
/// comparison means; a reader wanting direction needs a different number, not the same one
/// re-purposed.
/// </summary>
public static class SctDelta
{
    /// <summary>
    /// Signed percentage difference, or null when there is nothing meaningful to compare.
    ///
    /// Null on a non-positive UEX baseline for two reasons: it is the divisor, and a UEX zero is a
    /// real and common state in this data (a terminal that neither buys nor sells today), where
    /// "SCT is infinitely higher" would be noise rather than information. A zero on the SCT side
    /// IS comparable - "SCT saw none of this in stock" is worth reading as -100%.
    /// </summary>
    public static double? Pct(double uexValue, double sctValue) =>
        uexValue > 0 ? (sctValue - uexValue) / uexValue * 100.0 : null;

    /// <summary>
    /// The margin note itself: an explicit sign always, and no false precision. Under 0.05% reads
    /// as "0%" rather than "+0.0%", because the two sources agreeing exactly is the common case
    /// here (measured median delta on the live snapshots is 0.0%) and a signed zero would imply a
    /// difference that was rounded away. Above 100% drops the decimal - the exact size of a wild
    /// stock disagreement is not what the reader is deciding on.
    /// </summary>
    public static string Format(double pct)
    {
        if (Math.Abs(pct) < 0.05) return "0%";
        var abs = Math.Abs(pct);
        var digits = abs >= 100 ? "0" : "0.0";
        return (pct > 0 ? "+" : "-") + abs.ToString(digits, CultureInfo.InvariantCulture) + "%";
    }
}
