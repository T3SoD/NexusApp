namespace NexusApp.Tests;

// Shared real (redacted) commodity-kiosk Game.log lines for use across profit-tracker test
// classes: the three shapes verbatim from the design spec's section 7, playerId redacted per
// repo convention (never parsed).
public static class CommodityLogFixtures
{
    // Buy request. Quantity is in cSCU (12800 cSCU = 128 SCU); price is the whole spend.
    public const string BuyLine =
        "<2026-07-04T13:35:36.565Z> [Notice] <CEntityComponentCommodityUIProvider::SendCommodityBuyRequest> " +
        "Sending SShopCommodityBuyRequest - playerId[REDACTED] shopId[641510958730] " +
        "shopName[SCShop_Pyro_RestStop_Rund_Admin] kioskId[641510958734] price[182560.000000] " +
        "shopPricePerCentiSCU[14.262432] resourceGUID[e938ab24-2af8-48b5-9af7-8e82fb26dcb3] autoLoading[1] " +
        "quantity[12800.000000 cSCU] Cargo Box Data: boxSize[32.000000] | unitAmount[4] " +
        "[Team_CoreGameplayFeatures][Shops][UI]";

    // Sell request. Quantity is in SCU already, and the box data carries several groups.
    public const string SellLine =
        "<2025-03-23T18:45:19.540Z> [Notice] <CEntityComponentCommodityUIProvider::SendCommoditySellRequest> " +
        "Sending SShopCommoditySellRequest - playerId[REDACTED] shopId[2023885866388] " +
        "shopName[SCShop_AdminOffice_GrimHex] kioskId[2023885866391] amount[1978794.000000] " +
        "resourceGUID[4236c16b-c47f-4083-9e26-4313733f2326] autoLoading[0] quantity[195] transactionMode[Location] " +
        "Cargo Box Data:  [boxSize[1] | unitAmount[1]] [boxSize[2] | unitAmount[1]] [boxSize[4] | unitAmount[2]] " +
        "[boxSize[8] | unitAmount[2]] [boxSize[16] | unitAmount[1]] [boxSize[24] | unitAmount[1]] " +
        "[boxSize[32] | unitAmount[4]] [Team_NAPU][Shops][UI]";

    // Error response. In the source log this voids a buy request 137 ms earlier (13:36:12.141);
    // it sits 35.7 s after BuyLine above, outside the void window.
    public const string ErrorLine =
        "<2026-07-04T13:36:12.278Z> [Error] <CEntityComponentCommodityUIProvider::RmToken_CommodityTransactionResponse> " +
        "Commodity Transaction Response Error - playerId[REDACTED] result[InsufficentFunds] type[Buying] " +
        "[Team_CoreGameplayFeatures][Shops]";
}
