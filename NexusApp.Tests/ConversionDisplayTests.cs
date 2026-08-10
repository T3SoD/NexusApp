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

    // ── Filter chip bar (concept A, approved 2026-08-10) ───────────────────────────
    // The collapsible shelf is GONE from all three flows. Its collapsed line was a comma-joined
    // string, so it could not say which value belonged to which setting, nor which settings were
    // actually constraining the results - the whole job of a summary that stands in for hidden
    // controls. A chip names its setting, carries its value, and there is nothing to collapse.

    [Theory]
    [InlineData(@"Views\TradePage.Planner.cs")]
    [InlineData(@"Views\TradePage.Sell.cs")]
    [InlineData(@"Views\TradePage.Prices.cs")]
    public void EveryTradeFlow_UsesTheChipBar(string file)
    {
        var src = SourceFiles.ReadAppSource(file);
        Assert.Contains("new FilterChipBar", src);
        Assert.DoesNotContain("BuildFilterShelf", src);
    }

    // Every chip re-reads its value on each rebuild, so a snapshot-driven correction (an unresolved
    // start, a commodity the hourly refresh dropped) reaches the bar immediately, not a rebuild late.
    [Theory]
    [InlineData(@"Views\TradePage.Planner.cs", "RefreshPlannerFilterSummary")]
    [InlineData(@"Views\TradePage.Sell.cs", "RefreshSellFilterSummary")]
    [InlineData(@"Views\TradePage.Prices.cs", "RefreshPricesFilterSummary")]
    public void EveryTradeFlow_KeepsItsChipValuesCurrent(string file, string refresher)
    {
        var src = SourceFiles.ReadAppSource(file);
        // defined once, and called from the flow's rebuild path
        Assert.True(src.Split(refresher).Length - 1 >= 2, $"{refresher} must be defined and called");
    }

    // A chip goes amber only when its setting is narrowing the results, so "ANY" and "C2 Hercules"
    // can never look alike. That distinction is the defect the whole concept exists to fix.
    [Fact]
    public void TheChipBar_DistinguishesASetFilterFromADefault()
    {
        var src = SourceFiles.ReadAppSource(@"Views\FilterChipBar.cs");
        Assert.Contains("IsSet", src);
        Assert.Contains("AccentBrush", src);
        Assert.Contains("FgDimBrush", src);
    }

    // Three popovers contain a CommodityPickerBox, which has an OPEN defect where its dropdown
    // closes inside the click that opened it. A self-closing popup wrapped around a self-closing
    // popup makes that worse, so dismissal is owned explicitly here instead of by WPF.
    [Fact]
    public void ChipPopovers_OwnTheirOwnDismissal()
    {
        var src = SourceFiles.ReadAppSource(@"Views\FilterChipBar.cs");
        Assert.Contains("StaysOpen = true", src);
        Assert.Contains("PreviewMouseDown", src);
    }

    // Reported 2026-08-10: a fixed Width clipped the BUDGET row's USE WALLET chip and the START
    // row's LIVE pill straight off. Both groups are a control PLUS a second element beside it, and
    // no single guessed number fits every group, so the width is a floor and the popover grows.
    [Fact]
    public void ChipPopovers_GrowToTheirContentRatherThanClippingIt()
    {
        var src = SourceFiles.ReadAppSource(@"Views\FilterChipBar.cs");
        Assert.Contains("MinWidth = def.PopoverWidth", src);
        // Leading space, so this cannot match the MinWidth form it is guarding against.
        Assert.DoesNotContain(" Width = def.PopoverWidth", src);
    }

    // Reported 2026-08-10, on every chip and every flow: a Popup is its own top-level window and
    // does not follow its owner's activation, so an open popover floated above whatever the user
    // switched to. Minimizing and dragging strand it the same way, for the same reason.
    [Fact]
    public void ChipPopovers_CloseWhenTheAppIsNoLongerInFront()
    {
        var src = SourceFiles.ReadAppSource(@"Views\FilterChipBar.cs");
        Assert.Contains("Deactivated += OnWindowDeactivated", src);
        Assert.Contains("StateChanged += OnWindowStateChanged", src);
        Assert.Contains("LocationChanged += OnWindowMoved", src);
    }

    // ...but NOT when the foreground window is still ours. A chip popover holding a picker opens a
    // second popup for its dropdown, and that is a separate top-level window that deactivates the
    // main one. Closing on that would slam the popover shut the instant the user clicked the
    // control inside it, which is worse than the bug being fixed.
    [Fact]
    public void ChipPopovers_StayOpenForTheirOwnNestedDropdown()
    {
        var src = SourceFiles.ReadAppSource(@"Views\FilterChipBar.cs");
        Assert.Contains("GetForegroundWindow", src);
        Assert.Contains("Environment.ProcessId", src);
    }

    // Session-only by design. A persisted key would need a migration and gains nothing.
    [Fact]
    public void FilterShelfState_IsNotPersisted()
    {
        Assert.DoesNotContain("FiltersExpanded", SourceFiles.ReadAppSource(@"Models\AppSettings.cs"));
    }
}
