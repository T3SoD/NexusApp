using Xunit;

namespace NexusApp.Tests;

// Issue #34 (34.3): the glue that makes the filter pills govern LIVE results lives in
// MainViewModel, which cannot be constructed under test (its ctor news ScannerService and
// dereferences App statics). ScanResultsFilter.Apply is behaviorally tested; these pins keep the
// wiring itself from silently reverting - each DoesNotContain/Contains pair below fails if the
// derived views go back to reading the raw collection or a rebuild call is dropped.
public class MainViewModelWiringTests
{
    private static string Vm() => SourceFiles.ReadAppSource(@"ViewModels\MainViewModel.cs");

    [Fact]
    public void DerivedViews_ReadTheFilteredCollection()
    {
        var src = Vm();
        Assert.Contains("FilteredScanResults.Count > 0 ? FilteredScanResults[0]", src);
        Assert.Contains("OtherMatches => FilteredScanResults.Skip(1)", src);
        Assert.DoesNotContain("ScanResults.Count > 0 ? ScanResults[0]", src);
    }

    [Fact]
    public void FilterChange_RebuildsBothDerivedCollections()
    {
        var src = Vm();
        var handler = src.Substring(src.IndexOf("partial void OnHistoryFilterChanged"));
        handler = handler.Substring(0, handler.IndexOf("public MainViewModel()"));
        Assert.Contains("RebuildFilteredHistory();", handler);
        Assert.Contains("RebuildFilteredResults();", handler);
    }

    [Fact]
    public void CartRefresh_RebuildsTheFilteredResults()
    {
        var src = Vm();
        var method = src.Substring(src.IndexOf("private void RefreshCartStatus()"));
        method = method.Substring(0, method.IndexOf("[RelayCommand]"));
        Assert.Contains("RebuildFilteredResults();", method);
    }

    [Fact]
    public void SalvageGuards_HoldOnBothCartCommands()
    {
        var src = Vm();
        Assert.Contains("if (r.Method == \"salvage\") return;", src);
        Assert.Contains("if (m.IsSalvage) return;", src);
    }
}
