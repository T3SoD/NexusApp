using NexusApp.Models;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// The shop-kiosk line shapes, pinned against redacted fixtures captured 2026-08-06 and 2026-08-07.
// client_price is the LINE TOTAL, proven live: client_price[1470] quantity[2] moved the wallet
// by 1470. Never divide or multiply it by quantity.
public class ShopPurchaseParserTests
{
    public const string BuyLine =
        "<2026-08-06T20:50:55.423Z> [Notice] <CEntityComponentShopUIProvider::SendShopBuyRequest> " +
        "Sending SShopBuyRequest - playerId[REDACTED] shopId[751893855885] " +
        "shopName[SCShop_RestStop_Pharmacy-001] kioskId[751893855882] client_price[265.000000] " +
        "itemClassGUID[7d50411f-088c-4c99-b85a-a6eaf95504c3] itemName[crlf_consumable_healing_01] " +
        "quantity[1]  [Team_CoreGameplayFeatures][Shops][UI]";

    // Two missiles for 1,470 TOTAL, captured live 2026-08-07.
    public const string BuyLineQuantityTwo =
        "<2026-08-07T20:26:33.754Z> [Notice] <CEntityComponentShopUIProvider::SendShopBuyRequest> " +
        "Sending SShopBuyRequest - playerId[REDACTED] shopId[747402218584] " +
        "shopName[SCShop_Centermass_NewBabbage] kioskId[747402218570] client_price[1470.000000] " +
        "itemClassGUID[d4408421-e939-4e34-9902-0644fa6934be] itemName[MISL_S03_IR_VNCL_Chaos] " +
        "quantity[2]  [Team_CoreGameplayFeatures][Shops][UI]";

    public const string BuyLineLarge =
        "<2026-08-06T21:07:13.461Z> [Notice] <CEntityComponentShopUIProvider::SendShopBuyRequest> " +
        "Sending SShopBuyRequest - playerId[REDACTED] shopId[751893855885] " +
        "shopName[SCShop_RestStop_Pharmacy-001] kioskId[751893855882] client_price[100000.000000] " +
        "itemClassGUID[21789f4a-ffaa-4d9c-ab24-3de6fd2880b3] " +
        "itemName[Carryable_1H_CY_medical_canister_healing_1] " +
        "quantity[1]  [Team_CoreGameplayFeatures][Shops][UI]";

    // A sell, from a 2025 build. Its trailing tag is [Team_NAPU], not [Team_CoreGameplayFeatures].
    public const string SellLine =
        "<2025-02-23T13:07:31.285Z> [Notice] <CEntityComponentShopUIProvider::SendShopSellRequest> " +
        "Sending SShopSellRequest - playerId[REDACTED] shopId[1677246229841] " +
        "shopName[SCShop_LiveFire_Truckstop02-001] kioskId[1677246229834] client_price[119.000000] " +
        "itemClassGUID[38555f2d-975f-4e3c-8d04-8196b59b17c6] itemName[behr_shotgun_ballistic_01_mag] " +
        "quantity[1]  [Team_NAPU][Shops][UI]";

    public const string SuccessResponseLine =
        "<2026-08-06T20:50:56.650Z> [Notice] <CEntityComponentShopUIProvider::RmShopFlowResponse> " +
        "Received ShopFlowResponse - playerId[REDACTED] shopId[751893855885] " +
        "shopName[SCShop_RestStop_Pharmacy-001] kioskId[751893855882] " +
        "kioskState[BuyRequestProcessing] result[Success] type[Buying] " +
        "[Team_CoreGameplayFeatures][Shops][UI]";

    // The one non-Success result observed in 840 responses across 394 session logs.
    public const string PendingResponseLine =
        "<2026-08-06T20:50:56.650Z> [Notice] <CEntityComponentShopUIProvider::RmShopFlowResponse> " +
        "Received ShopFlowResponse - playerId[REDACTED] shopId[751893855885] " +
        "shopName[SCShop_RestStop_Pharmacy-001] kioskId[751893855882] " +
        "kioskState[BuyRequestProcessing] result[WaitingForPendingResult] type[Buying] " +
        "[Team_CoreGameplayFeatures][Shops][UI]";

    public const string CommodityBuyLine =
        "<2026-08-06T21:15:33.553Z> [Notice] " +
        "<CEntityComponentCommodityUIProvider::SendCommodityBuyRequest> " +
        "Sending SShopCommodityBuyRequest - playerId[REDACTED] shopId[751838335524] " +
        "shopName[SCShop_Admin_lt_base_g] kioskId[751838335532] price[3265.000000] " +
        "shopPricePerCentiSCU[32.643185] resourceGUID[dae3efcb-f82e-4c2a-b24b-81f2be8daa51] " +
        "autoLoading[0] quantity[100.000000 cSCU] Cargo Box Data: boxSize[1.000000] | unitAmount[1] " +
        "[Team_CoreGameplayFeatures][Shops][UI]";

    [Fact]
    public void ParseBuy_ExtractsAllFields()
    {
        var buy = ShopPurchaseParser.ParseBuy(BuyLine);
        Assert.NotNull(buy);
        Assert.Equal(new DateTime(2026, 8, 6, 20, 50, 55, 423, DateTimeKind.Utc), buy!.TimestampUtc);
        Assert.Equal(DateTimeKind.Utc, buy.TimestampUtc.Kind);
        Assert.Equal(ShopTransactionKind.Buy, buy.Kind);
        Assert.Equal("crlf_consumable_healing_01", buy.ItemToken);
        Assert.Equal("7d50411f-088c-4c99-b85a-a6eaf95504c3", buy.ItemGuid);
        Assert.Equal(265L, buy.Price);
        Assert.Equal(1, buy.Quantity);
        Assert.Equal("SCShop_RestStop_Pharmacy-001", buy.ShopName);
        Assert.Equal("751893855885", buy.ShopId);
        Assert.Equal("751893855882", buy.KioskId);
        Assert.Null(buy.Refused);
    }

    [Fact]
    public void ParseBuy_TreatsPriceAsTheLineTotal()
    {
        var buy = ShopPurchaseParser.ParseBuy(BuyLineQuantityTwo);
        Assert.NotNull(buy);
        Assert.Equal(1470L, buy!.Price);   // NOT 2940: proven live, the field is the line total
        Assert.Equal(2, buy.Quantity);
        Assert.Equal("MISL_S03_IR_VNCL_Chaos", buy.ItemToken);
    }

    [Fact]
    public void ParseSell_ReadsTheSellShapeAndTheOldTeamTag()
    {
        var sell = ShopPurchaseParser.ParseSell(SellLine);
        Assert.NotNull(sell);
        Assert.Equal(ShopTransactionKind.Sell, sell!.Kind);
        Assert.Equal(119L, sell.Price);
        Assert.Equal("behr_shotgun_ballistic_01_mag", sell.ItemToken);
        Assert.Equal("SCShop_LiveFire_Truckstop02-001", sell.ShopName);
    }

    [Fact]
    public void ParseBuy_AndParseSell_DoNotCrossMatch()
    {
        Assert.Null(ShopPurchaseParser.ParseBuy(SellLine));
        Assert.Null(ShopPurchaseParser.ParseSell(BuyLine));
    }

    [Fact]
    public void ParseFlowResult_ReadsResultAndRouting()
    {
        var r = ShopPurchaseParser.ParseFlowResult(SuccessResponseLine);
        Assert.NotNull(r);
        Assert.Equal("Success", r!.Result);
        Assert.Equal("751893855885", r.ShopId);
        Assert.Equal("751893855882", r.KioskId);
        Assert.Equal(ShopTransactionKind.Buy, r.Kind);

        var pending = ShopPurchaseParser.ParseFlowResult(PendingResponseLine);
        Assert.NotNull(pending);
        Assert.Equal("WaitingForPendingResult", pending!.Result);
    }

    [Fact]
    public void ParseFlowResult_MapsSellingType()
    {
        var line = SuccessResponseLine.Replace("type[Buying]", "type[Selling]");
        Assert.Equal(ShopTransactionKind.Sell, ShopPurchaseParser.ParseFlowResult(line)!.Kind);
    }

    [Fact]
    public void Parsers_IgnoreOtherProvidersAndGarbage()
    {
        Assert.Null(ShopPurchaseParser.ParseBuy(CommodityBuyLine));
        Assert.Null(ShopPurchaseParser.ParseBuy(SuccessResponseLine));
        Assert.Null(ShopPurchaseParser.ParseFlowResult(BuyLine));
        Assert.Null(ShopPurchaseParser.ParseBuy(""));
        Assert.Null(ShopPurchaseParser.ParseBuy("not a log line at all"));
        Assert.Null(ShopPurchaseParser.ParseSell(""));
        Assert.Null(ShopPurchaseParser.ParseFlowResult(""));
    }

    [Fact]
    public void LooksShopRelevant_PreFiltersCheaply()
    {
        Assert.True(ShopPurchaseParser.LooksShopRelevant(BuyLine));
        Assert.True(ShopPurchaseParser.LooksShopRelevant(SellLine));
        Assert.True(ShopPurchaseParser.LooksShopRelevant(SuccessResponseLine));
        Assert.False(ShopPurchaseParser.LooksShopRelevant(CommodityBuyLine));
    }

    [Fact]
    public void Key_DistinguishesOtherwiseIdenticalRows()
    {
        var a = ShopPurchaseParser.ParseBuy(BuyLine)!;
        var b = ShopPurchaseParser.ParseBuy(BuyLine)!;
        var c = ShopPurchaseParser.ParseBuy(BuyLineLarge)!;
        Assert.Equal(a.Key, b.Key);     // replay of the same line
        Assert.NotEqual(a.Key, c.Key);
    }
}
