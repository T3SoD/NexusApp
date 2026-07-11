namespace NexusApp.Services;

/// <summary>
/// Two-tap destructive-action guard: the first tap arms for a confirmation window,
/// a second tap inside the window executes, and an expired arm simply re-arms.
/// Pure logic with an injected clock so the UI drives visuals from IsArmed.
/// </summary>
public sealed class TwoTapConfirm
{
    private readonly TimeSpan _window;
    private readonly Action _onConfirmed;
    private DateTime? _armedAt;

    public TwoTapConfirm(TimeSpan window, Action onConfirmed)
    {
        _window = window;
        _onConfirmed = onConfirmed;
    }

    /// <summary>True = armed (show "Sure?"); false = executed.</summary>
    public bool Tap(DateTime now)
    {
        if (_armedAt.HasValue && now - _armedAt.Value < _window)
        {
            _armedAt = null;
            _onConfirmed();
            return false;
        }
        _armedAt = now;
        return true;
    }

    public bool IsArmed(DateTime now) => _armedAt.HasValue && now - _armedAt.Value < _window;

    public void Reset() => _armedAt = null;
}
