using System.Globalization;
using System.Text.RegularExpressions;
using NexusApp.Models;

namespace NexusApp.Services;

// The kiosk's answer to a request: Success settles it, anything else refuses it. Routing is by
// shopId plus kioskId because the response carries no item or price.
public sealed record ShopFlowResult(
    DateTime TimestampUtc, string Result, string ShopId, string KioskId,
    ShopTransactionKind Kind, ShopProvider Provider);

// Pure, stateless parsing of the five non-commodity shop line shapes across two providers,
// sibling of CommodityLogParser. No file I/O, no PII: playerId[..] is matched but never read. The
// trailing team tag is NOT part of any pattern: it reads [Team_NAPU] on 2025 builds and
// [Team_CoreGameplayFeatures] today. The commodity kiosk is a different provider entirely.
public static class ShopPurchaseParser
{
    private const string Stamp =
        @"<(?<ts>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z)> \[Notice\] <";

    private const string ShopUi = "CEntityComponentShopUIProvider::";
    private const string Shopping = "CEntityComponentShoppingProvider::";

    private const string Body =
        @"playerId\[[^\]]+\] shopId\[(?<shop>\d+)\] shopName\[(?<name>[^\]]+)\] kioskId\[(?<kiosk>\d+)\] " +
        @"client_price\[(?<price>[0-9.]+)\] itemClassGUID\[(?<guid>[0-9a-f-]+)\] " +
        @"itemName\[(?<item>[^\]]+)\] quantity\[(?<qty>\d+)\]";

    private static readonly Regex Buy =
        new(Stamp + ShopUi + @"SendShopBuyRequest> Sending SShopBuyRequest - " + Body,
            RegexOptions.Compiled);

    // DORMANT MECHANIC, kept deliberately. Star Citizen does not let a player sell items back to a
    // vendor; only commodity trading sells. This shape was last observed in March 2025 (builds
    // 9508788 and 9593160, 15 lines across 3 sessions) and has not appeared in the 17 months since.
    // The parser is retained so a restored mechanic works without new code, NOT because selling is
    // a thing the game currently does. Do not cite its existence as evidence that it is.
    private static readonly Regex Sell =
        new(Stamp + ShopUi + @"SendShopSellRequest> Sending SShopSellRequest - " + Body,
            RegexOptions.Compiled);

    private static readonly Regex Flow = new(
        Stamp + ShopUi + @"RmShopFlowResponse> Received ShopFlowResponse - " +
        @"playerId\[[^\]]+\] shopId\[(?<shop>\d+)\] shopName\[[^\]]+\] kioskId\[(?<kiosk>\d+)\] " +
        @"kioskState\[[^\]]+\] result\[(?<result>\w+)\] type\[(?<type>Buying|Selling)\]",
        RegexOptions.Compiled);

    // ShoppingProvider prints the SAME payload label and the SAME field order as the ShopUI buy,
    // so Body is reused verbatim, then currencyType is appended. Body's kiosk group accepts the
    // kioskId[0] this provider uses on 59 of 73 corpus events.
    private static readonly Regex ShoppingBuy = new(
        Stamp + Shopping + @"SendStandardItemBuyRequest> Sending SShopBuyRequest - " + Body +
        @" currencyType\[(?<cur>\w+)\]", RegexOptions.Compiled);

    // A DIFFERENT shape from the ShopUI answer: the label reads "Shop Flow Response", and it
    // carries no shopId, no kioskId, and no type. PurchaseLedger therefore routes it by provider
    // and arrival order alone.
    private static readonly Regex ShoppingFlow = new(
        Stamp + Shopping + @"RmShopFlowResponse> Shop Flow Response - " +
        @"playerId\[[^\]]+\] result\[(?<result>\w+)\]", RegexOptions.Compiled);

    // Matches CEntityComponentShopUIProvider and CEntityComponentShoppingProvider, and not
    // CEntityComponentCommodityUIProvider, which is CommodityLogParser's business.
    public static bool LooksShopRelevant(string raw) => raw.Contains("CEntityComponentShop");

    public static ShopPurchase? ParseBuy(string raw) =>
        Parse(Buy, raw, ShopTransactionKind.Buy, ShopProvider.ShopUI)
        ?? Parse(ShoppingBuy, raw, ShopTransactionKind.Buy, ShopProvider.Shopping);

    public static ShopPurchase? ParseSell(string raw) =>
        Parse(Sell, raw, ShopTransactionKind.Sell, ShopProvider.ShopUI);

    private static ShopPurchase? Parse(Regex re, string raw, ShopTransactionKind kind,
        ShopProvider provider)
    {
        var m = re.Match(raw);
        if (!m.Success) return null;
        var cur = m.Groups["cur"];
        return new ShopPurchase
        {
            TimestampUtc = ParseStamp(m.Groups["ts"].Value),
            Kind = kind,
            Provider = provider,
            Price = ParseAuec(m.Groups["price"].Value),
            Quantity = int.Parse(m.Groups["qty"].Value, CultureInfo.InvariantCulture),
            ItemToken = m.Groups["item"].Value,
            ItemGuid = m.Groups["guid"].Value,
            ShopName = m.Groups["name"].Value,
            ShopId = m.Groups["shop"].Value,
            KioskId = m.Groups["kiosk"].Value,
            Currency = cur.Success ? cur.Value : "UEC",
        };
    }

    public static ShopFlowResult? ParseFlowResult(string raw)
    {
        var m = Flow.Match(raw);
        if (m.Success)
            return new ShopFlowResult(
                ParseStamp(m.Groups["ts"].Value), m.Groups["result"].Value,
                m.Groups["shop"].Value, m.Groups["kiosk"].Value,
                m.Groups["type"].Value == "Buying" ? ShopTransactionKind.Buy
                                                   : ShopTransactionKind.Sell,
                ShopProvider.ShopUI);

        m = ShoppingFlow.Match(raw);
        if (!m.Success) return null;
        // No shopId, no kioskId, and no type on this shape. Buy is the only direction the
        // provider has ever emitted. The empty ids must never be compared against a purchase's
        // real ones, which is why PurchaseLedger checks the provider before comparing them.
        return new ShopFlowResult(
            ParseStamp(m.Groups["ts"].Value), m.Groups["result"].Value,
            "", "", ShopTransactionKind.Buy, ShopProvider.Shopping);
    }

    private static DateTime ParseStamp(string ts) =>
        DateTime.ParseExact(ts, "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    // client_price prints as "265.000000"; the value is whole aUEC.
    private static long ParseAuec(string s) =>
        (long)Math.Round(decimal.Parse(s, CultureInfo.InvariantCulture));
}
