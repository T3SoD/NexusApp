namespace NexusApp.Services;

/// <summary>
/// Decision core for the auto-scan "confirm" debounce: requires the same reticle reading on two
/// consecutive ticks before it counts as read, then suppresses a repeat emission of a value
/// already confirmed. Mirrors the GameProcessPresence precedent (ForegroundMonitor.cs) - a
/// single pure Update(...) call per tick, with all pending/pendingCount/lastEmitted state held
/// internally.
/// </summary>
internal sealed class ScanConfirm
{
    private int _pending;
    private int _pendingCount;
    private int _lastEmitted;

    /// <summary>How many consecutive ticks the current pending value has been seen (0 right after a reset).</summary>
    public int PendingCount => _pendingCount;

    /// <summary>Feed every tick's OCR reading (the decoded value, if any, and whether the pin was
    /// found at all). Returns the value newly confirmed by THIS call - the same reading seen on
    /// two consecutive ticks and not already emitted - or null otherwise. pinFound=false clears
    /// all state, including the last-emitted memory, so a later re-read of the same value has to
    /// re-confirm from scratch.</summary>
    public int? Update(int? value, bool pinFound)
    {
        if (!pinFound)
        {
            _pending = 0;
            _pendingCount = 0;
            _lastEmitted = 0;
            return null;
        }

        if (!value.HasValue) return null;

        if (value.Value == _pending)
            _pendingCount++;
        else
        {
            _pending = value.Value;
            _pendingCount = 1;
        }

        if (_pendingCount >= 2 && value.Value != _lastEmitted)
        {
            _lastEmitted = value.Value;
            return value.Value;
        }

        return null;
    }
}
