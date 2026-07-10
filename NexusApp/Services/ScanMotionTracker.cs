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
}
