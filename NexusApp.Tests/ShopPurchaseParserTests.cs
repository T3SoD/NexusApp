using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// The shop-kiosk buy line shape, pinned against redacted fixtures captured 2026-08-06.
// client_price is the client's own figure and is used for ranking only, never for wallet math.
public class ShopPurchaseParserTests
{
    // A one-quantity buy.
    public const string BuyLine =
        "<2026-08-06T20:50:55.423Z> [Notice] <CEntityComponentShopUIProvider::SendShopBuyRequest> " +
        "Sending SShopBuyRequest - playerId[REDACTED] shopId[751893855885] " +
        "shopName[SCShop_RestStop_Pharmacy-001] kioskId[751893855882] client_price[265.000000] " +
        "itemClassGUID[7d50411f-088c-4c99-b85a-a6eaf95504c3] itemName[crlf_consumable_healing_01] " +
        "quantity[1]  [Team_CoreGameplayFeatures][Shops][UI]";

    // A multi-quantity buy. Whether client_price is per unit or per line is unknown, so the
    // parser stores both numbers verbatim and never divides.
    public const string BuyLineQuantityTwo =
        "<2026-08-06T21:01:02.240Z> [Notice] <CEntityComponentShopUIProvider::SendShopBuyRequest> " +
        "Sending SShopBuyRequest - playerId[REDACTED] shopId[751893855885] " +
        "shopName[SCShop_RestStop_Pharmacy-001] kioskId[751893855886] client_price[500.000000] " +
        "itemClassGUID[f10cba0e-bb94-4438-a628-5128913bb995] itemName[crlf_medgun_vial_01] " +
        "quantity[2]  [Team_CoreGameplayFeatures][Shops][UI]";

    // The largest single purchase captured on 2026-08-06.
    public const string BuyLineLarge =
        "<2026-08-06T21:07:13.461Z> [Notice] <CEntityComponentShopUIProvider::SendShopBuyRequest> " +
        "Sending SShopBuyRequest - playerId[REDACTED] shopId[751893855885] " +
        "shopName[SCShop_RestStop_Pharmacy-001] kioskId[751893855882] client_price[100000.000000] " +
        "itemClassGUID[21789f4a-ffaa-4d9c-ab24-3de6fd2880b3] " +
        "itemName[Carryable_1H_CY_medical_canister_healing_1] " +
        "quantity[1]  [Team_CoreGameplayFeatures][Shops][UI]";

    // Same provider, different method. Must not parse as a purchase.
    public const string FlowResponseLine =
        "<2026-08-06T20:50:56.650Z> [Notice] <CEntityComponentShopUIProvider::RmShopFlowResponse> " +
        "Received ShopFlowResponse - playerId[REDACTED] shopId[751893855885] " +
        "shopName[SCShop_RestStop_Pharmacy-001] kioskId[751893855882] " +
        "kioskState[BuyRequestProcessing] result[Success] type[Buying] " +
        "[Team_CoreGameplayFeatures][Shops][UI]";

    // A commodity buy. A different provider entirely; the commodity parser owns it.
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
        Assert.Equal("crlf_consumable_healing_01", buy.ItemToken);
        Assert.Equal(265L, buy.Price);
        Assert.Equal(1, buy.Quantity);
        Assert.Equal("SCShop_RestStop_Pharmacy-001", buy.ShopName);
    }

    [Fact]
    public void ParseBuy_KeepsQuantityAndPriceVerbatim()
    {
        var buy = ShopPurchaseParser.ParseBuy(BuyLineQuantityTwo);
        Assert.NotNull(buy);
        Assert.Equal(500L, buy!.Price);
        Assert.Equal(2, buy.Quantity);
        Assert.Equal("crlf_medgun_vial_01", buy.ItemToken);
    }

    [Fact]
    public void ParseBuy_ReadsLargeAmounts()
    {
        var buy = ShopPurchaseParser.ParseBuy(BuyLineLarge);
        Assert.NotNull(buy);
        Assert.Equal(100000L, buy!.Price);
        Assert.Equal("Carryable_1H_CY_medical_canister_healing_1", buy.ItemToken);
    }

    [Fact]
    public void ParseBuy_IgnoresFlowResponseAndCommodityLines()
    {
        Assert.Null(ShopPurchaseParser.ParseBuy(FlowResponseLine));
        Assert.Null(ShopPurchaseParser.ParseBuy(CommodityBuyLine));
    }

    [Fact]
    public void ParseBuy_ReturnsNullOnGarbage()
    {
        Assert.Null(ShopPurchaseParser.ParseBuy(""));
        Assert.Null(ShopPurchaseParser.ParseBuy("not a log line at all"));
        Assert.Null(ShopPurchaseParser.ParseBuy(
            "<2026-08-06T20:50:55.423Z> [Notice] " +
            "<CEntityComponentShopUIProvider::SendShopBuyRequest> Sending SShopBuyRequest - truncated"));
    }

    [Fact]
    public void LooksShopRelevant_PreFiltersCheaply()
    {
        Assert.True(ShopPurchaseParser.LooksShopRelevant(BuyLine));
        // The pre-filter is deliberately loose: the response line shares the provider name and
        // is rejected by the regex, not by the filter.
        Assert.True(ShopPurchaseParser.LooksShopRelevant(FlowResponseLine));
        Assert.False(ShopPurchaseParser.LooksShopRelevant(CommodityBuyLine));
    }
}
