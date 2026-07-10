using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// The Codex hologram's lifecycle: renders only between Show and Stop, and only while the
// window is active (Pause on deactivate, Resume on activate). Transitions return whether
// anything changed so the control hooks/unhooks CompositionTarget.Rendering exactly once.
public class HologramStateTests
{
    [Fact]
    public void StartsStopped()
    {
        Assert.False(new HologramState().IsRendering);
    }

    [Fact]
    public void Show_StartsRendering()
    {
        var s = new HologramState();
        Assert.True(s.Show());
        Assert.True(s.IsRendering);
        Assert.False(s.Show());   // already showing - no change
    }

    [Fact]
    public void PauseResume_OnlyMeaningfulWhileShown()
    {
        var s = new HologramState();
        Assert.False(s.Pause());    // stopped: deactivating the window is a no-op
        Assert.False(s.Resume());
        s.Show();
        Assert.True(s.Pause());
        Assert.False(s.IsRendering);
        Assert.False(s.Pause());    // already paused
        Assert.True(s.Resume());
        Assert.True(s.IsRendering);
    }

    [Fact]
    public void Stop_EndsEverything_AndResumeStaysDead()
    {
        var s = new HologramState();
        s.Show();
        Assert.True(s.Stop());
        Assert.False(s.IsRendering);
        Assert.False(s.Resume());   // stopped is terminal until the next Show
        Assert.False(s.Stop());
    }

    [Fact]
    public void PausedThenStopped_ShowRestarts()
    {
        var s = new HologramState();
        s.Show(); s.Pause(); s.Stop();
        Assert.True(s.Show());
        Assert.True(s.IsRendering);
    }
}
