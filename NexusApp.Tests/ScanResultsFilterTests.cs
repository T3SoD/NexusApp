using System.Linq;
using NexusApp.Models;
using NexusApp.ViewModels;
using Xunit;

namespace NexusApp.Tests;

// Issue #34: the All / Exact+Close / Exact pills used to govern only RECENT SCANS; the reporter
// asked for them to also focus the LIVE result cards. The matcher only ever emits exact or
// <=0.5% close matches, so Exact is the one filter that visibly narrows live output.
public class ScanResultsFilterTests
{
    private static MatchResult Result(string name, bool exact) =>
        new(new Resource { Name = name, BaseRs = 3600, Method = "ship" },
            InputRs: 7200, Nodes: 2, IsExact: exact, ErrorPct: exact ? 0 : 0.3);

    private static readonly MatchResult[] Mixed =
        [Result("Gold", true), Result("Tin", false), Result("Copper", true)];

    [Fact]
    public void All_KeepsEverything()
    {
        Assert.Equal(3, ScanResultsFilter.Apply(Mixed, HistoryFilter.All).Count());
    }

    [Fact]
    public void ExactAndClose_KeepsEverything()
    {
        Assert.Equal(3, ScanResultsFilter.Apply(Mixed, HistoryFilter.ExactAndClose).Count());
    }

    [Fact]
    public void Exact_KeepsOnlyExactMatches_InOrder()
    {
        var kept = ScanResultsFilter.Apply(Mixed, HistoryFilter.Exact).ToList();
        Assert.Equal(["Gold", "Copper"], kept.Select(m => m.Resource.Name));
        Assert.All(kept, m => Assert.True(m.IsExact));
    }

    [Fact]
    public void Exact_CloseOnlyScan_YieldsEmpty()
    {
        Assert.Empty(ScanResultsFilter.Apply([Result("Laranite", false)], HistoryFilter.Exact));
    }

    [Theory]
    [InlineData(HistoryFilter.All)]
    [InlineData(HistoryFilter.ExactAndClose)]
    [InlineData(HistoryFilter.Exact)]
    public void EmptyInput_YieldsEmpty(HistoryFilter filter)
    {
        Assert.Empty(ScanResultsFilter.Apply([], filter));
    }

    [Fact]
    public void Exact_KeepsAnExactSalvageCard()
    {
        // The salvage decode is exact like an ore multiple; the Exact pill must not hide it.
        var salvage = new MatchResult(
            new Resource { Name = "Salvage", BaseRs = 2000, Method = "salvage" },
            InputRs: 16000, Nodes: 8, IsExact: true, ErrorPct: 0);
        var kept = ScanResultsFilter.Apply([salvage, Result("Tin", false)], HistoryFilter.Exact).ToList();
        Assert.Single(kept);
        Assert.Equal("Salvage", kept[0].Resource.Name);
    }
}
