namespace NexusApp.Services;

// Lifecycle for the Codex hologram render loop: Stopped -> (Show) -> Running <-> Paused,
// any -> (Stop) -> Stopped. Renders only in Running. Transition methods return true when
// the state actually changed, so the caller hooks/unhooks the render event exactly once.
public sealed class HologramState
{
    private enum S { Stopped, Running, Paused }
    private S _s = S.Stopped;

    public bool IsRendering => _s == S.Running;

    public bool Show()
    {
        if (_s == S.Running) return false;
        _s = S.Running;
        return true;
    }

    public bool Pause()
    {
        if (_s != S.Running) return false;
        _s = S.Paused;
        return true;
    }

    public bool Resume()
    {
        if (_s != S.Paused) return false;
        _s = S.Running;
        return true;
    }

    public bool Stop()
    {
        if (_s == S.Stopped) return false;
        _s = S.Stopped;
        return true;
    }
}
