using System.Globalization;
using NexusApp.Models;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// The three commodity-kiosk line shapes, pinned against the verbatim fixtures. Buys quote
// quantity in cSCU and are divided by 100 at parse; sells are SCU already.
public class CommodityLogParserTests
{
    [Fact]
    public void ParseBuy_ExtractsAllFields()
    {
        var tx = CommodityLogParser.ParseBuy(CommodityLogFixtures.BuyLine);
        Assert.NotNull(tx);
        Assert.Equal(new DateTime(2026, 7, 4, 13, 35, 36, 565, DateTimeKind.Utc), tx!.TimestampUtc);
        Assert.Equal(DateTimeKind.Utc, tx.TimestampUtc.Kind);
        Assert.Equal(TransactionKind.Buy, tx.Kind);
        Assert.Equal(182560L, tx.Amount);
        Assert.Equal("e938ab24-2af8-48b5-9af7-8e82fb26dcb3", tx.ResourceGuid);
        Assert.Equal("SCShop_Pyro_RestStop_Rund_Admin", tx.ShopName);
        Assert.Equal("641510958730", tx.ShopId);
        Assert.Equal("641510958734", tx.KioskId);
        Assert.True(tx.AutoLoading);
        Assert.Null(tx.Voided);
    }

    [Fact]
    public void ParseBuy_ScuIsCentiScuDividedBy100()
    {
        var tx = CommodityLogParser.ParseBuy(CommodityLogFixtures.BuyLine);
        Assert.Equal(128m, tx!.Scu);
    }

    [Fact]
    public void ParseBuy_SingleBoxGroup()
    {
        var tx = CommodityLogParser.ParseBuy(CommodityLogFixtures.BuyLine);
        var box = Assert.Single(tx!.Boxes);
        Assert.Equal(32m, box.BoxSize);
        Assert.Equal(4, box.UnitAmount);
    }

    [Fact]
    public void ParseSell_ExtractsAllFields()
    {
        var tx = CommodityLogParser.ParseSell(CommodityLogFixtures.SellLine);
        Assert.NotNull(tx);
        Assert.Equal(new DateTime(2025, 3, 23, 18, 45, 19, 540, DateTimeKind.Utc), tx!.TimestampUtc);
        Assert.Equal(TransactionKind.Sell, tx.Kind);
        Assert.Equal(1978794L, tx.Amount);
        Assert.Equal(195m, tx.Scu);   // SCU already, no division
        Assert.Equal("4236c16b-c47f-4083-9e26-4313733f2326", tx.ResourceGuid);
        Assert.Equal("SCShop_AdminOffice_GrimHex", tx.ShopName);
        Assert.Equal("2023885866388", tx.ShopId);
        Assert.Equal("2023885866391", tx.KioskId);
        Assert.False(tx.AutoLoading);
    }

    [Fact]
    public void ParseSell_CarriesAllBoxGroups()
    {
        var tx = CommodityLogParser.ParseSell(CommodityLogFixtures.SellLine);
        Assert.Equal(7, tx!.Boxes.Count);
        Assert.Equal(new CargoBoxGroup(1m, 1), tx.Boxes[0]);
        Assert.Equal(new CargoBoxGroup(4m, 2), tx.Boxes[2]);
        Assert.Equal(new CargoBoxGroup(32m, 4), tx.Boxes[6]);
        Assert.Equal(12, tx.Boxes.Sum(b => b.UnitAmount));
    }

    [Fact]
    public void ParseTransactionError_ExtractsStampCodeAndKind()
    {
        var err = CommodityLogParser.ParseTransactionError(CommodityLogFixtures.ErrorLine);
        Assert.NotNull(err);
        Assert.Equal(new DateTime(2026, 7, 4, 13, 36, 12, 278, DateTimeKind.Utc), err!.TimestampUtc);
        Assert.Equal("InsufficentFunds", err.Result);
        Assert.Equal(TransactionKind.Buy, err.Kind);
    }

    [Fact]
    public void ParseTransactionError_SellingType_MapsToSell()
    {
        var line = CommodityLogFixtures.ErrorLine
            .Replace("type[Buying]", "type[Selling]")
            .Replace("result[InsufficentFunds]", "result[InsuffientStock]");
        var err = CommodityLogParser.ParseTransactionError(line);
        Assert.Equal(TransactionKind.Sell, err!.Kind);
        Assert.Equal("InsuffientStock", err.Result);
    }

    [Fact]
    public void LooksCommodityRelevant_TrueForAllThreeShapes()
    {
        Assert.True(CommodityLogParser.LooksCommodityRelevant(CommodityLogFixtures.BuyLine));
        Assert.True(CommodityLogParser.LooksCommodityRelevant(CommodityLogFixtures.SellLine));
        Assert.True(CommodityLogParser.LooksCommodityRelevant(CommodityLogFixtures.ErrorLine));
    }

    [Fact]
    public void LooksCommodityRelevant_FalseForOtherSubsystems()
    {
        Assert.False(CommodityLogParser.LooksCommodityRelevant(HaulLogParserFixtures.MarkerPickup));
        Assert.False(CommodityLogParser.LooksCommodityRelevant(HaulLogParserFixtures.DeliverLine));
    }

    [Theory]
    [InlineData(60)]
    [InlineData(200)]
    public void ParseBuy_TruncatedLine_ReturnsNull(int length) =>
        Assert.Null(CommodityLogParser.ParseBuy(CommodityLogFixtures.BuyLine[..length]));

    [Theory]
    [InlineData(60)]
    [InlineData(200)]
    public void ParseSell_TruncatedLine_ReturnsNull(int length) =>
        Assert.Null(CommodityLogParser.ParseSell(CommodityLogFixtures.SellLine[..length]));

    [Fact]
    public void ParseShapes_NeverCrossMatch()
    {
        Assert.Null(CommodityLogParser.ParseBuy(CommodityLogFixtures.SellLine));
        Assert.Null(CommodityLogParser.ParseBuy(CommodityLogFixtures.ErrorLine));
        Assert.Null(CommodityLogParser.ParseSell(CommodityLogFixtures.BuyLine));
        Assert.Null(CommodityLogParser.ParseSell(CommodityLogFixtures.ErrorLine));
        Assert.Null(CommodityLogParser.ParseTransactionError(CommodityLogFixtures.BuyLine));
        Assert.Null(CommodityLogParser.ParseTransactionError(CommodityLogFixtures.SellLine));
    }

    [Fact]
    public void ParseShapes_NonMatchingSubsystemLine_ReturnsNull()
    {
        Assert.Null(CommodityLogParser.ParseBuy(HaulLogParserFixtures.DeliverLine));
        Assert.Null(CommodityLogParser.ParseSell(HaulLogParserFixtures.DeliverLine));
        Assert.Null(CommodityLogParser.ParseTransactionError(HaulLogParserFixtures.DeliverLine));
    }

    // The numeric fields must parse invariant regardless of the machine's culture: under a
    // comma-decimal culture, "182560.000000" read culture-sensitively would shift by 10^6.
    [Fact]
    public void Parse_UnderCommaDecimalCulture_IsInvariant()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var buy = CommodityLogParser.ParseBuy(CommodityLogFixtures.BuyLine);
            Assert.Equal(182560L, buy!.Amount);
            Assert.Equal(128m, buy.Scu);
            Assert.Equal(32m, buy.Boxes[0].BoxSize);
            var sell = CommodityLogParser.ParseSell(CommodityLogFixtures.SellLine);
            Assert.Equal(1978794L, sell!.Amount);
            Assert.Equal(195m, sell.Scu);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Key_IsStableAcrossReparse_AndDistinctPerLine()
    {
        var first = CommodityLogParser.ParseBuy(CommodityLogFixtures.BuyLine)!;
        var second = CommodityLogParser.ParseBuy(CommodityLogFixtures.BuyLine)!;
        Assert.Equal(first.Key, second.Key);

        var sell = CommodityLogParser.ParseSell(CommodityLogFixtures.SellLine)!;
        Assert.NotEqual(first.Key, sell.Key);

        var otherKiosk = CommodityLogParser.ParseBuy(
            CommodityLogFixtures.BuyLine.Replace("kioskId[641510958734]", "kioskId[641510958735]"))!;
        Assert.NotEqual(first.Key, otherKiosk.Key);
    }
}
