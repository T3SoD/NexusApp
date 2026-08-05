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

    // Filter pill flips rebuild the derived results and swap or empty the hero without a new
    // scan (issue #34); the view syncs the tracker instead of choreographing, and the sync must
    // not let the next real scan of the same value replay the full reveal.
    [Fact]
    public void Sync_RecordsNameWithoutChoreographing()
    {
        var t = new ScanMotionTracker();
        t.ShouldChoreograph("Quantanium");
        t.Sync("Bexalite");                                // pill flip swapped the hero
        Assert.False(t.ShouldChoreograph("Bexalite"));     // same hero re-notified: settle only
    }

    [Fact]
    public void Sync_WithNull_KeepsTheLastName()
    {
        var t = new ScanMotionTracker();
        t.ShouldChoreograph("Quantanium");
        t.Sync(null);                                      // Exact pill emptied the live list
        Assert.False(t.ShouldChoreograph("Quantanium"));   // back to All: same scan, no replay
    }
}
