namespace NexusApp.Views;

// Public, not internal: xunit test methods are public and CS0051 forbids an internal type in their
// signatures - the same reason ProfitState, WalletUiState and RoiTier are public.
public enum ConversionKind { Liquid, Cargo, Expected }

/// <summary>One segment of the conversion bar: what to label it, what to print, and how much of the
/// bar it occupies.</summary>
public sealed record ConversionSegment(ConversionKind Kind, string Label, string Value, long Weight);

// The conversion bar's decisions (spec 2026-08-09 section 4; mock nexus-design-lab/cargo-fusion-d).
// Capital is always in one of three states, and the bar shows the split rather than a single net
// that reads as a loss the moment you buy well.
//
// Absent beats zero throughout: a segment with nothing in it is not rendered, so the bar never
// claims "0 in cargo" for a player who simply has not bought anything. EXPECTED stays null until
// route binding exists and is simply absent until then - it is the only one of the three that needs
// a route's sell price, which is why the other two could ship first.
//
// All arithmetic beyond the bar ratios lives in CargoValue; this only decides what shows and what
// it says, the same split ProfitDisplay and WalletDisplay already keep.
internal static class ConversionDisplay
{
    internal const string LiquidLabel = "LIQUID";
    internal const string CargoLabel = "IN CARGO";
    internal const string ExpectedLabel = "EXPECTED";

    internal const string BarTooltip =
        "Where your money is right now. Buying moves it into cargo rather than spending it, so this "
        + "does not drop when you buy well.";

    /// <summary>The segments to render, in order, skipping any with nothing to say. An empty result
    /// means nothing is known yet and the caller should fall back to its old readout.</summary>
    internal static IReadOnlyList<ConversionSegment> Segments(long? wallet, long inCargo, long? expected)
    {
        var segs = new List<ConversionSegment>(3);
        if (wallet is { } w && w > 0)
            segs.Add(new ConversionSegment(ConversionKind.Liquid, LiquidLabel, ProfitDisplay.Format(w), w));
        if (inCargo > 0)
            segs.Add(new ConversionSegment(ConversionKind.Cargo, CargoLabel, ProfitDisplay.Format(inCargo), inCargo));
        if (expected is { } e && e != 0)
            // Weighted up: the tail is small against a wallet and would otherwise render as a sliver
            // too thin to carry its own label.
            segs.Add(new ConversionSegment(ConversionKind.Expected, ExpectedLabel,
                                           ProfitDisplay.Signed(e), Math.Max(1, Math.Abs(e) * 2)));
        return segs;
    }

    /// <summary>The line under the bar naming what is held. Callers skip it when nothing is held.
    /// Carries its own unit, like every other money surface.</summary>
    internal static string CargoNote(long inCargo) =>
        $"{ProfitDisplay.Format(inCargo)} aUEC in unsold cargo";
}
