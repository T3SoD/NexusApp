using System.Globalization;
using NexusApp.Services;

namespace NexusApp.Views;

// All user-facing copy for the SCT corroboration badges, pure and testable, mirroring MarketNotice.
// Text/Tooltip return null for UexOnly (the common, unlit case) - absent, never a placeholder badge
// (mock manifest, index.html:1161-1163: "Nothing renders as a placeholder while off - it is absent").
internal static class TradeBadges
{
    public static string? Text(PriceSourceState state, double disagreePct) => state switch
    {
        PriceSourceState.Corroborated => "CORROBORATED",
        PriceSourceState.Disagree     => $"SOURCES DISAGREE +{disagreePct.ToString("0.#", CultureInfo.InvariantCulture)}%",
        PriceSourceState.SctOnly      => "SCT ONLY",
        _                             => null,
    };

    public static string? Tooltip(PriceSourceState state, double disagreePct) => state switch
    {
        PriceSourceState.Corroborated => "2 sources agree within 3 percent, both under 48h",
        PriceSourceState.Disagree     => $"UEX and SCT differ by {disagreePct.ToString("0.#", CultureInfo.InvariantCulture)} percent, price shown here is UEX's.",
        PriceSourceState.SctOnly      => "SC Trade Tools only, no second source confirms this price.",
        _                             => null,
    };
}
