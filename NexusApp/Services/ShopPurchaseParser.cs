using System.Globalization;
using System.Text.RegularExpressions;
using NexusApp.Models;

namespace NexusApp.Services;

// The kiosk's answer to a request: Success settles it, anything else refuses it. Routing is by
// shopId plus kioskId because the response carries no item or price.
public sealed record ShopFlowResult(
    DateTime TimestampUtc, string Result, string ShopId, string KioskId, ShopTransactionKind Kind);

// Pure, stateless parsing of the three non-commodity shop line shapes, sibling of
// CommodityLogParser. No file I/O, no PII: playerId[..] is matched but never read. The trailing
// team tag is NOT part of any pattern: it reads [Team_NAPU] on 2025 builds and
// [Team_CoreGameplayFeatures] today. The commodity kiosk is a different provider entirely.
public static class ShopPurchaseParser
{
    private const string Head =
        @"<(?<ts>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z)> \[Notice\] " +
        @"<CEntityComponentShopUIProvider::";

    private const string Body =
        @"playerId\[[^\]]+\] shopId\[(?<shop>\d+)\] shopName\[(?<name>[^\]]+)\] kioskId\[(?<kiosk>\d+)\] " +
        @"client_price\[(?<price>[0-9.]+)\] itemClassGUID\[(?<guid>[0-9a-f-]+)\] " +
        @"itemName\[(?<item>[^\]]+)\] quantity\[(?<qty>\d+)\]";

    private static readonly Regex Buy =
        new(Head + @"SendShopBuyRequest> Sending SShopBuyRequest - " + Body, RegexOptions.Compiled);

    // DORMANT MECHANIC, kept deliberately. Star Citizen does not let a player sell items back to a
    // vendor; only commodity trading sells. This shape was last observed in March 2025 (builds
    // 9508788 and 9593160, 15 lines across 3 sessions) and has not appeared in the 17 months since.
    // The parser is retained so a restored mechanic works without new code, NOT because selling is
    // a thing the game currently does. Do not cite its existence as evidence that it is.
    private static readonly Regex Sell =
        new(Head + @"SendShopSellRequest> Sending SShopSellRequest - " + Body, RegexOptions.Compiled);

    private static readonly Regex Flow = new(
        Head + @"RmShopFlowResponse> Received ShopFlowResponse - " +
        @"playerId\[[^\]]+\] shopId\[(?<shop>\d+)\] shopName\[[^\]]+\] kioskId\[(?<kiosk>\d+)\] " +
        @"kioskState\[[^\]]+\] result\[(?<result>\w+)\] type\[(?<type>Buying|Selling)\]",
        RegexOptions.Compiled);

    // Cheap pre-filter so the trackers can skip the bulk of lines before running regex.
    public static bool LooksShopRelevant(string raw) =>
        raw.Contains("CEntityComponentShopUIProvider");

    public static ShopPurchase? ParseBuy(string raw) => Parse(Buy, raw, ShopTransactionKind.Buy);

    public static ShopPurchase? ParseSell(string raw) => Parse(Sell, raw, ShopTransactionKind.Sell);

    private static ShopPurchase? Parse(Regex re, string raw, ShopTransactionKind kind)
    {
        var m = re.Match(raw);
        if (!m.Success) return null;
        return new ShopPurchase
        {
            TimestampUtc = ParseStamp(m.Groups["ts"].Value),
            Kind = kind,
            Price = ParseAuec(m.Groups["price"].Value),
            Quantity = int.Parse(m.Groups["qty"].Value, CultureInfo.InvariantCulture),
            ItemToken = m.Groups["item"].Value,
            ItemGuid = m.Groups["guid"].Value,
            ShopName = m.Groups["name"].Value,
            ShopId = m.Groups["shop"].Value,
            KioskId = m.Groups["kiosk"].Value,
        };
    }

    public static ShopFlowResult? ParseFlowResult(string raw)
    {
        var m = Flow.Match(raw);
        if (!m.Success) return null;
        return new ShopFlowResult(
            ParseStamp(m.Groups["ts"].Value),
            m.Groups["result"].Value,
            m.Groups["shop"].Value,
            m.Groups["kiosk"].Value,
            m.Groups["type"].Value == "Buying" ? ShopTransactionKind.Buy : ShopTransactionKind.Sell);
    }

    private static DateTime ParseStamp(string ts) =>
        DateTime.ParseExact(ts, "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    // client_price prints as "265.000000"; the value is whole aUEC.
    private static long ParseAuec(string s) =>
        (long)Math.Round(decimal.Parse(s, CultureInfo.InvariantCulture));
}
