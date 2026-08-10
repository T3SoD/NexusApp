using NexusApp.Models;

namespace NexusApp.Services;

/// <summary>
/// The overlay CARGO tab's count badge (2026-08-10): how many auto-loads have FINISHED that the
/// player has not looked at yet.
///
/// <para>Derived, not counted. There is no completion event to subscribe to - AutoLoadTracker
/// raises EntriesChanged when a kiosk line arrives and nothing at all when a load finishes,
/// because finishing is the passage of time rather than an event in the log. So the badge is a
/// pure function of the entries, a "seen" watermark and the clock, which means it cannot drift out
/// of step with the list the way an incremented counter would after a log reset or a replay.</para>
///
/// <para>An entry with no PredictedSeconds is never counted: without a prediction there is no
/// moment to call it finished, and guessing would put a number on the tab for a load that may
/// still be running.</para>
/// </summary>
public static class AutoLoadBadge
{
    /// <summary>When this load is expected to be done, or null when it was never predicted.</summary>
    public static DateTime? CompletesAt(AutoLoadEntry entry) =>
        entry.PredictedSeconds is { } seconds ? entry.StartUtc.AddSeconds(seconds) : null;

    /// <summary>Loads that finished after <paramref name="sinceUtc"/> and are finished by now.
    /// The watermark is bumped when the CARGO tab is opened, so opening the tab clears the badge
    /// without touching, mutating or forgetting any entry.</summary>
    public static int CompletedSince(IEnumerable<AutoLoadEntry> entries, DateTime sinceUtc, DateTime nowUtc)
    {
        var count = 0;
        foreach (var entry in entries)
        {
            if (CompletesAt(entry) is not { } done) continue;
            if (done <= nowUtc && done > sinceUtc) count++;
        }
        return count;
    }

    /// <summary>True while any load is still running. The overlay uses this to run its badge ticker
    /// only when something can actually change, rather than keeping a timer alive for the window's
    /// whole life.</summary>
    public static bool AnyPending(IEnumerable<AutoLoadEntry> entries, DateTime nowUtc)
    {
        foreach (var entry in entries)
            if (CompletesAt(entry) is { } done && done > nowUtc) return true;
        return false;
    }
}
