using NexusApp.Models;
using NexusApp.Services;
using NexusApp.ViewModels;
using Xunit;

namespace NexusApp.Tests;

// Issue #34 (filter toggles): a history row used to take its kind and name from the TOP-ranked
// match only, and FindByRs ranks pinned ores first - so a pinned CLOSE match above an unpinned
// EXACT one recorded the whole scan as Close (amber) and the filters looked arbitrary. A scan
// that decoded exact anywhere is an exact scan.
public class ScanClassificationTests
{
    private static RsMatch Match(string name, bool exact, double errPct = 0, bool pinned = false) =>
        new(new Resource { Name = name, BaseRs = 3600, Method = "ship", IsPinned = pinned },
            Nodes: 2, IsExact: exact, ErrorPct: errPct);

    [Fact]
    public void NoMatches_IsNone()
    {
        var (name, kind) = ScanClassification.Summarize([]);
        Assert.Equal("No match", name);
        Assert.Equal(MatchKind.None, kind);
    }

    [Fact]
    public void TopExact_IsExactWithTopName()
    {
        var (name, kind) = ScanClassification.Summarize([Match("Gold", exact: true), Match("Tin", exact: false, 0.2)]);
        Assert.Equal("Gold", name);
        Assert.Equal(MatchKind.Exact, kind);
    }

    [Fact]
    public void PinnedCloseAboveExact_StillClassifiesExact()
    {
        // FindByRs order: pinned close first, unpinned exact second. The row must read as the
        // exact decode, named for the exact ore.
        var (name, kind) = ScanClassification.Summarize(
            [Match("Bexalite", exact: false, 0.3, pinned: true), Match("Copper", exact: true)]);
        Assert.Equal("Copper", name);
        Assert.Equal(MatchKind.Exact, kind);
    }

    [Fact]
    public void CloseOnly_IsCloseWithTopName()
    {
        var (name, kind) = ScanClassification.Summarize([Match("Laranite", exact: false, 0.4)]);
        Assert.Equal("Laranite", name);
        Assert.Equal(MatchKind.Close, kind);
    }
}
