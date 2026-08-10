using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Kiosk box sizes (2026-08-10). The planner snaps a trip to what the buy terminal can supply, and
// UEX ships ONE container_sizes list per commodity that does not vary by terminal. The kiosk states
// its own answer every time it opens, and the two disagree.
public class CommodityBoxParserTests
{
    // Captured verbatim from a live session, build 12344265, MIC-L4 admin office. Note Scrap tops
    // out at 16 here while Waste at the SAME kiosk goes to 32 - the whole reason this exists.
    private const string ScrapLine =
        "<2026-08-10T22:09:25.519Z> [Notice] <CEntityComponentCommodityUIProvider::LoadShopInventoryData::<lambda_1>::operator ()> " +
        "AddingCommodityBox - playerId[204767770981] shopId[747398598793] shopName[SCShop_Admin_lt_base_g] " +
        "commodityName[ResourceType.Scrap] Available Box Sizes:  boxSize[1] boxSize[2] boxSize[4] boxSize[8] boxSize[16] " +
        "[Team_CoreGameplayFeatures][Shops][UI]";

    private const string WasteLine =
        "<2026-08-10T22:09:25.519Z> [Notice] <CEntityComponentCommodityUIProvider::LoadShopInventoryData::<lambda_1>::operator ()> " +
        "AddingCommodityBox - playerId[204767770981] shopId[747398598793] shopName[SCShop_Admin_lt_base_g] " +
        "commodityName[ResourceType.Waste] Available Box Sizes:  boxSize[1] boxSize[2] boxSize[4] boxSize[8] boxSize[16] " +
        "boxSize[24] boxSize[32] [Team_CoreGameplayFeatures][Shops][UI]";

    [Fact]
    public void ParsesTheRealLine()
    {
        var r = CommodityBoxParser.Parse(ScrapLine);
        Assert.NotNull(r);
        Assert.Equal("747398598793", r!.ShopId);
        Assert.Equal("SCShop_Admin_lt_base_g", r.ShopName);
        Assert.Equal("Scrap", r.CommodityName);
        Assert.Equal(new[] { 1, 2, 4, 8, 16 }, r.Sizes);
        Assert.Equal(DateTime.Parse("2026-08-10T22:09:25.519Z").ToUniversalTime(), r.TimestampUtc);
    }

    // The evidence for the whole feature: one counter, two commodities, different sizes.
    [Fact]
    public void TwoCommoditiesAtOneKiosk_CanHaveDifferentSizes()
    {
        var scrap = CommodityBoxParser.Parse(ScrapLine)!;
        var waste = CommodityBoxParser.Parse(WasteLine)!;
        Assert.Equal(scrap.ShopId, waste.ShopId);
        Assert.Equal(16, scrap.Sizes.Max());
        Assert.Equal(32, waste.Sizes.Max());
    }

    [Theory]
    [InlineData("ResourceType.Scrap", "Scrap")]
    [InlineData("ResourceType.Laranite", "Laranite")]
    [InlineData("Scrap", "Scrap")]              // already bare, passes through
    [InlineData("", "")]
    public void CommodityName_StripsTheGamesOwnPrefix(string raw, string expected)
        => Assert.Equal(expected, CommodityBoxParser.CommodityName(raw));

    [Fact]
    public void ToContainerSizes_MatchesTheStringThePlannerAlreadySpeaks()
        => Assert.Equal("1,2,4,8,16", CommodityBoxParser.ToContainerSizes(new[] { 16, 1, 8, 2, 4, 8 }));

    [Fact]
    public void AnUnrelatedLine_IsNotThisShape()
        => Assert.Null(CommodityBoxParser.Parse("<2026-08-10T22:09:25.519Z> [Notice] <Something Else> hello"));

    [Theory]
    [InlineData("")]
    [InlineData("AddingCommodityBox with no fields at all")]
    public void Rubbish_ParsesToNothingRatherThanThrowing(string raw)
        => Assert.Null(CommodityBoxParser.Parse(raw));

    // A kiosk that advertises no sizes tells us nothing, and recording an empty list would blank
    // out a good answer already learned.
    [Fact]
    public void NoBoxSizes_IsNoObservation()
    {
        var line = ScrapLine.Replace("boxSize[1] boxSize[2] boxSize[4] boxSize[8] boxSize[16] ", "");
        Assert.Null(CommodityBoxParser.Parse(line));
    }

    [Fact]
    public void LooksRelevant_KeepsTheRegexOffOrdinaryLines()
    {
        Assert.True(CommodityBoxParser.LooksRelevant(ScrapLine));
        Assert.False(CommodityBoxParser.LooksRelevant("<2026-08-10T22:09:25.519Z> [Notice] <Whatever> nothing here"));
    }

    // Stock levels are NOT in Game.log. Verified by searching a live session for an on-screen
    // figure of 1713 SCU and finding it in no form, decimal or centi-SCU. This line is the closest
    // thing the kiosk emits, and it carries sizes only - so nothing here may imply a quantity.
    [Fact]
    public void TheKioskLine_CarriesNoQuantity()
    {
        var r = CommodityBoxParser.Parse(ScrapLine)!;
        var fields = typeof(KioskBoxSizes).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain("Quantity", fields);
        Assert.DoesNotContain("Stock", fields);
        Assert.DoesNotContain("Available", fields);
        Assert.Equal(5, r.Sizes.Count);
    }
}
