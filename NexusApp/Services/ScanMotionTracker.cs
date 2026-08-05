namespace NexusApp.Services;

/// <summary>
/// Decides when the RS Decoder plays the full match choreography: only when the
/// best match actually changes. Re-scanning the same value settles quietly, and a
/// no-match clears state so the next hit lands with full feedback.
/// </summary>
public sealed class ScanMotionTracker
{
    private string? _last;

    public bool ShouldChoreograph(string? bestMatchName)
    {
        if (string.IsNullOrEmpty(bestMatchName)) { _last = null; return false; }
        var changed = !string.Equals(_last, bestMatchName, System.StringComparison.OrdinalIgnoreCase);
        _last = bestMatchName;
        return changed;
    }

    // Filter pill flips swap or empty the hero without a new scan (issue #34). The view records
    // the swap here instead of choreographing; a null (pill emptied the list) keeps the last name
    // so returning to a wider filter cannot replay the reveal for an unchanged scan.
    public void Sync(string? bestMatchName)
    {
        if (!string.IsNullOrEmpty(bestMatchName)) _last = bestMatchName;
    }
}
