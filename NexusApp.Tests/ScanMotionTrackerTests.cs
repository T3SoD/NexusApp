using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Exercises the stateful gate that decides when the RS Decoder plays its full
// match choreography versus a quiet settle: the full reveal fires only when the
// best match actually changes, a no-match clears state, and case is ignored.
public class ScanMotionTrackerTests
{
    [Fact]
    public void FirstMatch_Choreographs()
    {
        var t = new ScanMotionTracker();
        Assert.True(t.ShouldChoreograph("Quantanium"));
    }

    [Fact]
    public void SameMatchAgain_DoesNot()
    {
        var t = new ScanMotionTracker();
        t.ShouldChoreograph("Quantanium");
        Assert.False(t.ShouldChoreograph("Quantanium"));
    }

    [Fact]
    public void SameMatchDifferentCase_DoesNot()
    {
        var t = new ScanMotionTracker();
        t.ShouldChoreograph("Quantanium");
        Assert.False(t.ShouldChoreograph("QUANTANIUM"));
    }

    [Fact]
    public void ChangedMatch_ChoreographsAgain()
    {
        var t = new ScanMotionTracker();
        t.ShouldChoreograph("Quantanium");
        Assert.True(t.ShouldChoreograph("Bexalite"));
    }

    [Fact]
    public void NoMatch_ResetsSoNextMatchChoreographs()
    {
        var t = new ScanMotionTracker();
        t.ShouldChoreograph("Quantanium");
        Assert.False(t.ShouldChoreograph(null));
        Assert.True(t.ShouldChoreograph("Quantanium"));
    }
}
