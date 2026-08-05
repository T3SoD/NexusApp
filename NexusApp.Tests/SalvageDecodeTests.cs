using NexusApp.Models;
using NexusApp.Services;
using NexusApp.ViewModels;
using Xunit;

namespace NexusApp.Tests;

// Issue #34: salvageable ship-debris panels signature at exactly 2000 RS each (datamined: every
// salvageablerepairable_shipdebris_* record carries 2000 as its only nonzero signature), so an
// n*2000 reading decodes as n panels. Salvage is a synthetic decode-only entry - never a seed
// row, so the Codex, cart and refinery surfaces must not see it.
public class SalvageDecodeTests
{
    [Fact]
    public void ExactMultiple_DecodesAsPanels()
    {
        var m = SalvageDecode.TryMatch(16000);
        Assert.NotNull(m);
        Assert.Equal("Salvage", m!.Resource.Name);
        Assert.Equal("salvage", m.Resource.Method);
        Assert.Equal(8, m.Nodes);
        Assert.True(m.IsExact);
    }

    [Fact]
    public void SinglePanel_Decodes()
    {
        var m = SalvageDecode.TryMatch(2000);
        Assert.NotNull(m);
        Assert.Equal(1, m!.Nodes);
        Assert.True(m.IsExact);
    }

    [Fact]
    public void CloseReading_DecodesAsClose()
    {
        // 2010/2000 computes just under 0.5% in double arithmetic, inside the close band.
        var m = SalvageDecode.TryMatch(2010);
        Assert.NotNull(m);
        Assert.False(m!.IsExact);
    }

    [Fact]
    public void FarOffReadings_DoNotMatch()
    {
        Assert.Null(SalvageDecode.TryMatch(1000));
        Assert.Null(SalvageDecode.TryMatch(3000));
        // 2011/2000 is 0.55% off: just past the 0.5% close band. Pins the band edge so a silent
        // widening cannot slip through with the far-off cases alone.
        Assert.Null(SalvageDecode.TryMatch(2011));
    }

    [Fact]
    public void PanelCount_IsUncapped()
    {
        // Debris fields are not rock clusters: no rarity tier, no cluster cap.
        var m = SalvageDecode.TryMatch(100 * 2000);
        Assert.NotNull(m);
        Assert.Equal(100, m!.Nodes);
    }

    [Fact]
    public void MatchResult_Salvage_ReadsPanelsAndHidesCart()
    {
        var m = SalvageDecode.TryMatch(16000)!;
        var result = new MatchResult(m.Resource, 16000, m.Nodes, m.IsExact, m.ErrorPct);
        Assert.Equal("×8 panels", result.NodesText);
        Assert.False(result.CanCart);
        Assert.Equal("EXACT", result.BadgeText);
    }

    [Fact]
    public void MatchResult_SinglePanel_ReadsSingular()
    {
        var m = SalvageDecode.TryMatch(2000)!;
        var result = new MatchResult(m.Resource, 2000, m.Nodes, m.IsExact, m.ErrorPct);
        Assert.Equal("×1 panel", result.NodesText);
    }

    [Fact]
    public void MatchResult_Ore_KeepsNodesWordingAndCart()
    {
        var ore = new Resource { Name = "Gold", BaseRs = 3600, Rarity = "rare", Method = "ship" };
        var result = new MatchResult(ore, 7200, 2, true, 0);
        Assert.Equal("×2 nodes", result.NodesText);
        Assert.True(result.CanCart);
    }
}
