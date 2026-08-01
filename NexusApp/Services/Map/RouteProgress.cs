namespace NexusApp.Services.Map;

/// <summary>
/// "How far along a run am I", as a 0-1 fraction, from the player's distance to each end of it.
/// The overlay's TRADE cards paint a rail with this (mock: nexus-design-lab/overlay-trade,
/// candidate B), so the one thing a trade panel can say mid-flight that no other surface can is
/// visible at a glance rather than spelled out in a sentence.
///
/// <para>Deliberately NOT a projection onto the buy-to-sell line. The player is rarely on that
/// line, and a projection would run backwards past the ends and need clamping anyway. The ratio of
/// the two distances is monotonic along the run, reads correctly at both ends (0 standing at the
/// buy stop, 1 at the sell stop), and never claims more precision than a straight-line reading
/// deserves.</para>
///
/// <para>Null is a first-class answer and the normal one: it means the player's position is
/// unknown, or in a different system from the route, and the caller must render that as a bare rail
/// with no marker rather than guessing a position. Same absent-not-placeholder rule the rest of the
/// map surfaces follow.</para>
/// </summary>
public static class RouteProgress
{
    /// <param name="metersFromBuy">Straight-line distance player-to-buy-stop, null when unknown.</param>
    /// <param name="metersFromSell">Straight-line distance player-to-sell-stop, null when unknown.</param>
    public static double? Fraction(double? metersFromBuy, double? metersFromSell)
    {
        if (metersFromBuy is not { } fromBuy || metersFromSell is not { } fromSell) return null;
        if (fromBuy < 0 || fromSell < 0) return null;

        var total = fromBuy + fromSell;
        // Both readings zero means the two stops resolved to the same place as the player. Not a
        // real route, but the reading is honest: nothing has been travelled.
        if (total <= 0) return 0;

        var frac = fromBuy / total;
        return frac < 0 ? 0 : frac > 1 ? 1 : frac;
    }
}
