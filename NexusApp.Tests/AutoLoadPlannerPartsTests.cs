using NexusApp.Services;
using NexusApp.Views;
using Xunit;

namespace NexusApp.Tests;

public class AutoLoadPlannerPartsTests
{
    private static readonly StarmapCatalog Starmap = StarmapCatalog.LoadEmbedded();
    private static readonly LoadingDockCatalog Docks = LoadingDockCatalog.LoadEmbedded();
    private static readonly AutoLoadTimeTable Dark = AutoLoadTimeTable.LoadEmbedded();     // calibrated false
    private static readonly AutoLoadTimeTable Lit = AutoLoadTimeTable.Load(new MemoryStream(
        """{"schema":1,"gameBuild":"t","calibrated":true,"baseLoadingSeconds":120,"baseUnloadingSeconds":120,"boxLoadingSeconds":{"8":8,"16":12,"24":15,"32":18},"boxUnloadingSeconds":{"8":8}}"""u8.ToArray()));

    private static MarketTerminal Term(string location)
        => new(1, "T", "trading", false, "Stanton", location);

    // Everus Harbor's uexName in starmap_locations.json, confirmed to resolve to
    // StarMapObject.RR_HUR_LEO, which is present in loading_docks.json.
    private static MarketTerminal DockTerm => Term("Everus Harbor");

    [Fact]
    public void NonDockTerminal_NothingRenders()
    {
        var p = AutoLoadPlannerParts.For(Term("ARC-L1 Wide Forest Station"), "8,16", 256, Starmap, Docks, Lit);
        Assert.False(p.ShowDockChip);
        Assert.Null(p.RangeText);
    }

    [Fact]
    public void DockTerminal_Uncalibrated_ChipOnly()
    {
        var p = AutoLoadPlannerParts.For(DockTerm, "8,16,24,32", 256, Starmap, Docks, Dark);
        Assert.True(p.ShowDockChip);
        Assert.Null(p.RangeText);
    }

    [Fact]
    public void DockTerminal_Calibrated_ChipAndRange()
    {
        var p = AutoLoadPlannerParts.For(DockTerm, "8,16,24,32", 256, Starmap, Docks, Lit);
        Assert.True(p.ShowDockChip);
        Assert.Equal("4m 24s - 6m 16s", p.RangeText);
    }

    [Fact]
    public void UnresolvedTerminal_NothingRenders()
    {
        var p = AutoLoadPlannerParts.For(null, "8", 256, Starmap, Docks, Lit);
        Assert.False(p.ShowDockChip);
    }
}
