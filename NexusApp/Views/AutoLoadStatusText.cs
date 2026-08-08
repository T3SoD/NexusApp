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

    public static string? OverflowCount(int total) => total >= 2 ? $"+{total - 1}" : null;

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
}
