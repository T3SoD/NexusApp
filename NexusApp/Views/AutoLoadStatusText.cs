using System.Globalization;
using NexusApp.Models;
using NexusApp.Services;

namespace NexusApp.Views;

// Pure text folds for AutoLoadStatusLine, kept UI-free so the display contract is testable.
// The calibrated flag gates every predictive string; elapsed time is always shown.
internal static class AutoLoadStatusText
{
    public static string StripWord(AutoLoadEntry newest)
        => newest.Kind == TransactionKind.Buy ? "AUTO-LOAD" : "AUTO-UNLOAD";

    public static string Elapsed(AutoLoadEntry e, DateTime nowUtc)
        => AutoLoadEstimator.FormatDuration((int)(nowUtc - e.StartUtc).TotalSeconds);

    // Countdown display: remaining time against the prediction, held at zero once past it.
    // Falls back to the count-up elapsed when the table is dark or the mix had no prediction.
    public static string Clock(AutoLoadEntry e, AutoLoadTimeTable table, DateTime nowUtc)
        => table.Calibrated && e.PredictedSeconds is { } p
            ? AutoLoadEstimator.FormatDuration(Math.Max(0, p - (int)(nowUtc - e.StartUtc).TotalSeconds))
            : Elapsed(e, nowUtc);

    public static string? OverflowCount(int total) => total >= 2 ? $"+{total - 1}" : null;

    // Row title: commodity when resolved, else the direction word alone.
    public static string Title(AutoLoadEntry e)
        => e.CommodityName is { Length: > 0 } c ? c : StripWord(e);

    // Purchase location: the player's stamped place when one exists, else the cleaned shop
    // token. Matches the profit ledger's own WhereText location rules (ProfitDisplay.WhereText).
    public static string Location(AutoLoadEntry e) => ProfitDisplay.WhereText(e.PlaceLabel, e.PlaceIsArea, e.ShopName);

    public static string CargoLine(AutoLoadEntry e)
    {
        var mix = string.Join(" + ", e.Boxes.Select(b =>
            string.Create(CultureInfo.InvariantCulture, $"{b.UnitAmount} x {b.BoxSize:0.##} SCU")));
        return string.Create(CultureInfo.InvariantCulture, $"{e.Scu:N0} SCU - {mix}");
    }

    public static string? EstLine(AutoLoadEntry e, AutoLoadTimeTable table)
        => table.Calibrated && e.PredictedSeconds is { } p
            ? $"est {AutoLoadEstimator.FormatDuration(p)}"
            : null;

    public static bool IsOver(AutoLoadEntry e, AutoLoadTimeTable table, DateTime nowUtc)
        => table.Calibrated && e.PredictedSeconds is { } p
            && (nowUtc - e.StartUtc).TotalSeconds > p;

    // Load progress against the prediction, clamped full once past it. Null without a usable
    // prediction, which also hides the bar.
    public static double? Progress(AutoLoadEntry e, AutoLoadTimeTable table, DateTime nowUtc)
        => table.Calibrated && e.PredictedSeconds is { } p && p > 0
            ? Math.Clamp((nowUtc - e.StartUtc).TotalSeconds / p, 0.0, 1.0)
            : null;
}
