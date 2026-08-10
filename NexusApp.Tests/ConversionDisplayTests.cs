using NexusApp.Views;
using Xunit;

namespace NexusApp.Tests;

// Phase A of the trade/cargo fusion (spec 2026-08-09 section 4).
public class ConversionDisplayTests
{
    [Fact]
    public void WalletOnly_IsOneSegment()
    {
        var segs = ConversionDisplay.Segments(1_401_444, inCargo: 0, expected: null);
        var one = Assert.Single(segs);
        Assert.Equal(ConversionKind.Liquid, one.Kind);
        Assert.Equal("LIQUID", one.Label);
        Assert.Equal("1,401,444", one.Value);
    }

    [Fact]
    public void CargoAppearsOnlyWhenSomethingIsHeld()
    {
        Assert.Single(ConversionDisplay.Segments(1_000, 0, null));
        Assert.Equal(2, ConversionDisplay.Segments(1_000, 500, null).Count);
    }

    [Fact]
    public void ExpectedIsAbsentWhenNull()
    {
        var segs = ConversionDisplay.Segments(1_000, 500, expected: null);
        Assert.DoesNotContain(segs, s => s.Kind == ConversionKind.Expected);
    }

    [Fact]
    public void ExpectedRendersSignedWhenPresent()
    {
        var segs = ConversionDisplay.Segments(1_000, 500, expected: 144_160);
        var exp = Assert.Single(segs, s => s.Kind == ConversionKind.Expected);
        Assert.Equal("EXPECTED", exp.Label);
        Assert.Equal("+144,160", exp.Value);
    }

    // No wallet anchor set: the wallet half is unknown, but held cargo is still a fact worth showing.
    [Fact]
    public void NoWallet_StillShowsCargo()
    {
        var segs = ConversionDisplay.Segments(wallet: null, inCargo: 693_600, expected: null);
        var one = Assert.Single(segs);
        Assert.Equal(ConversionKind.Cargo, one.Kind);
    }

    [Fact]
    public void NothingKnown_IsEmptySoTheCallerCanFallBack()
    {
        Assert.Empty(ConversionDisplay.Segments(wallet: null, inCargo: 0, expected: null));
    }

    [Fact]
    public void OrderIsAlwaysLiquidThenCargoThenExpected()
    {
        var segs = ConversionDisplay.Segments(1_000, 500, 250);
        Assert.Equal(new[] { ConversionKind.Liquid, ConversionKind.Cargo, ConversionKind.Expected },
                     segs.Select(s => s.Kind));
    }

    // Weights drive the bar's star split; a zero weight would collapse a segment that is rendered.
    [Fact]
    public void WeightsAreNeverZeroForARenderedSegment()
    {
        foreach (var s in ConversionDisplay.Segments(1, 1, 1)) Assert.True(s.Weight > 0);
    }

    [Fact]
    public void CargoNoteCarriesItsUnit()
    {
        Assert.Equal("693,600 aUEC in unsold cargo", ConversionDisplay.CargoNote(693_600));
    }

    // A negative wallet is possible (WalletUiState.Impossible) and must not render as a segment
    // whose star weight would be negative.
    [Fact]
    public void NegativeWallet_IsNotRendered()
    {
        var segs = ConversionDisplay.Segments(-500, inCargo: 100, expected: null);
        var one = Assert.Single(segs);
        Assert.Equal(ConversionKind.Cargo, one.Kind);
    }

    // ── Source pins: the decisions this phase makes, so a later change cannot quietly undo them ──

    [Fact]
    public void OverlayMoneyBlock_RendersTheConversionBar()
    {
        var src = SourceFiles.ReadAppSource(@"Views\OverlayWindow.xaml.cs");
        Assert.Contains("BuildConversionBar", src);
    }

    // Moved off Trade onto Cargo Hauling (task B4, spec 2026-08-09 section 2.3/3): this panel was
    // TradePage.Profit.cs, a partial of TradePage; it is now MoneyPanel.cs, a self-contained
    // control hosted by HaulingPage.
    [Fact]
    public void MoneyPanel_RendersTheConversionBar()
    {
        var src = SourceFiles.ReadAppSource(@"Views\MoneyPanel.cs");
        Assert.Contains("BuildConversionBar", src);
    }

    // Every aUEC value carries its unit (house rule 2026-08-09). Both bar builders append it.
    [Fact]
    public void BothConversionBars_AppendTheUnit()
    {
        Assert.Contains("\" aUEC\"", SourceFiles.ReadAppSource(@"Views\OverlayWindow.xaml.cs"));
        Assert.Contains("\" aUEC\"", SourceFiles.ReadAppSource(@"Views\MoneyPanel.cs"));
    }

    // EXPECTED is now computed from Loaded accepted routes (task D4). Both call sites share one
    // helper, AcceptedRouteMoney.ExpectedMargin, so the desktop and overlay bars can never derive
    // this figure differently.
    [Fact]
    public void ExpectedIsComputedFromLoadedRoutesOnBothSurfaces()
    {
        Assert.Contains("AcceptedRouteMoney.ExpectedMargin", SourceFiles.ReadAppSource(@"Views\OverlayWindow.xaml.cs"));
        Assert.Contains("AcceptedRouteMoney.ExpectedMargin", SourceFiles.ReadAppSource(@"Views\MoneyPanel.cs"));
    }

    // ── Filter shelves (spec section 2.2) ──────────────────────────────────────────

    [Theory]
    [InlineData(@"Views\TradePage.Planner.cs")]
    [InlineData(@"Views\TradePage.Sell.cs")]
    [InlineData(@"Views\TradePage.Prices.cs")]
    public void EveryTradeFlow_WrapsItsInputsInAFilterShelf(string file)
    {
        Assert.Contains("BuildFilterShelf", SourceFiles.ReadAppSource(file));
    }

    // Collapsing must never hide what is in force: each flow refreshes a summary line naming its
    // own settings. Without this the shelf is a trap rather than a space win.
    [Theory]
    [InlineData(@"Views\TradePage.Planner.cs", "RefreshPlannerFilterSummary")]
    [InlineData(@"Views\TradePage.Sell.cs", "RefreshSellFilterSummary")]
    [InlineData(@"Views\TradePage.Prices.cs", "RefreshPricesFilterSummary")]
    public void EveryTradeFlow_KeepsItsCollapsedSummaryCurrent(string file, string refresher)
    {
        var src = SourceFiles.ReadAppSource(file);
        // defined once, and called from the flow's rebuild path
        Assert.True(src.Split(refresher).Length - 1 >= 2, $"{refresher} must be defined and called");
    }

    // Session-only by design. A persisted key would need a migration and gains nothing.
    [Fact]
    public void FilterShelfState_IsNotPersisted()
    {
        Assert.DoesNotContain("FiltersExpanded", SourceFiles.ReadAppSource(@"Models\AppSettings.cs"));
    }
}
