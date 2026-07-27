// NexusApp/Services/ExecHangarCycle.cs
namespace NexusApp.Services;

/// <summary>One computed view of the Executive Hangar cycle at a given instant.</summary>
public sealed record ExecHangarSnapshot(
    bool IsOpen,
    int GreensLit,
    bool IsFinalActiveTail,
    TimeSpan TimeToTransition,
    DateTime NextOpenUtc,
    DateTime NextCloseUtc,
    IReadOnlyList<DateTime> UpcomingOpensUtc);

/// <summary>
/// Offline Executive Hangar cycle math (issue #26). The PYAM hangar rotation is globally
/// synchronized and deterministic: 65m 0.093s open then 120m 0.173s closed, anchored to one
/// calibrated open-start per game patch. Constants come from the community-calibrated
/// Exec-Hangar project (MIT) and exec.xyxyll.com; the millisecond tails are load-bearing
/// (rounding the cycle to 185m drifts over 5 seconds per day). All math is long milliseconds
/// in UTC; the caller supplies the clock so every boundary is unit-testable.
/// </summary>
public static class ExecHangarCycle
{
    // 4.9.0-LIVE calibration (updated 2026-07-23, server 12269732). The anchor is the instant
    // an open phase began. A user re-anchor (settings override) replaces the anchor only; the
    // durations are patch-stable.
    private static readonly DateTime AnchorOpenUtc = new(2026, 7, 22, 23, 37, 42, 439, DateTimeKind.Utc);
    private const long OpenMs = 3_900_093;    // 65m 0.093s, last 5 minutes still active
    private const long ClosedMs = 7_200_173;  // 120m 0.173s
    private const long CycleMs = OpenMs + ClosedMs;   // 185m 0.266s

    public const string CalibrationLabel = "4.9.0-LIVE";

    public static ExecHangarSnapshot At(DateTime utcNow, DateTime? anchorOverrideUtc)
    {
        var anchor = anchorOverrideUtc ?? AnchorOpenUtc;
        var deltaMs = (utcNow.Ticks - anchor.Ticks) / TimeSpan.TicksPerMillisecond;
        var pos = ((deltaMs % CycleMs) + CycleMs) % CycleMs;

        var isOpen = pos < OpenMs;
        int greens;
        bool tail = false;
        if (isOpen)
        {
            var openMin = pos / 60_000;
            greens = (int)Math.Max(0, 5 - openMin / 12);
            tail = openMin >= 60;
        }
        else
        {
            var closedMin = (pos - OpenMs) / 60_000;
            greens = (int)Math.Min(4, closedMin / 24);
        }

        var msToTransition = isOpen ? OpenMs - pos : CycleMs - pos;
        var msToNextOpen = CycleMs - pos;   // in both phases the next open-start is the cycle end
        var nextOpen = utcNow.AddMilliseconds(msToNextOpen);
        var nextClose = isOpen
            ? utcNow.AddMilliseconds(msToTransition)
            : nextOpen.AddMilliseconds(OpenMs);

        var upcoming = new List<DateTime>(3);
        for (var i = 0; i < 3; i++)
            upcoming.Add(nextOpen.AddMilliseconds(CycleMs * (long)i));

        return new ExecHangarSnapshot(
            isOpen, greens, tail,
            TimeSpan.FromMilliseconds(msToTransition),
            nextOpen, nextClose, upcoming);
    }

    // "42m 10s" under an hour, "1h 54m 10s" from an hour up; minutes always shown, seconds padded.
    public static string FormatCountdown(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}h {t.Minutes:00}m {t.Seconds:00}s"
            : $"{t.Minutes}m {t.Seconds:00}s";
    }
}
