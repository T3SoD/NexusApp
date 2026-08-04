using System;
using System.Globalization;

namespace NexusApp.Views;

// The financial rail's pure decisions (spec 2026-08-04-trade-hero-card-financials-design.md,
// Option E). Investment is the buy-side capital a trip ties up; Gross and Net ride in from the
// model verbatim (RoutePlanner sets Net == Gross while no fee schedule exists,
// RoutePlanner.cs:8-10). ROI is null when no capital is tied up: silence over a guessed
// percentage, the same honesty rule every unresolved-origin surface follows. Internal statics so
// the folds are unit-testable (InternalsVisibleTo), the same pattern as TradeBadges.
internal static class TradeFinancials
{
    // Rail tier thresholds, percent. Constants beside the fold so the owner can tune them.
    internal const double RoiHighPct = 25;
    internal const double RoiMidPct = 10;
    // The meter's full-scale reading, percent: a route at or past this pegs the meter.
    internal const double MeterFullScalePct = 50;

    // Gross is the sale PROCEEDS, sell price times quantity - deliberately NOT TradeRoute.Gross,
    // which RoutePlanner stores as the margin (equal to Net while no fee schedule exists). Painting
    // that model field made the rail's GROSS read the same number as NET on every card (caught on
    // the live 2026-08-04 launch check).
    internal static (double Investment, double Gross, double? Roi) Compute(
        double buyPrice, double sellPrice, int tripQty, double net)
    {
        var investment = buyPrice * tripQty;
        return (investment, sellPrice * tripQty, investment > 0 ? net / investment * 100 : null);
    }

    internal static string RoiText(double roi)
        => roi.ToString("F1", CultureInfo.InvariantCulture) + "%";

    internal static RoiTier Tier(double roi)
        => roi >= RoiHighPct ? RoiTier.High
         : roi >= RoiMidPct ? RoiTier.Mid
         : RoiTier.None;

    internal static double MeterFraction(double roi)
        => Math.Clamp(roi, 0, MeterFullScalePct) / MeterFullScalePct;
}

// Tint fold for the rail's ROI readout: None = FgDim, Mid = Accent, High = Ok. DangerBrush is
// deliberately absent - a low ROI is unremarkable, not broken. Public, not internal: xunit test
// methods are public and CS0051 forbids an internal type in their signatures - the same reason
// ScanIndicator and LocationLamp are public.
public enum RoiTier { None, Mid, High }
