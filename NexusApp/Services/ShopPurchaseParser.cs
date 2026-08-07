using System.Globalization;
using System.Text.RegularExpressions;

namespace NexusApp.Services;

// One shop purchase read off a Game.log kiosk line. Price is the client's own figure
// (client_price), kept for ranking candidates only; it never enters wallet arithmetic.
public sealed record ShopPurchase(
    DateTime TimestampUtc, string ItemToken, long Price, int Quantity, string ShopName);

// Pure, stateless parsing of the non-commodity shop buy line, sibling of CommodityLogParser.
// One shape, never generalized. No file I/O, no PII: playerId[..] is matched but never read.
// The commodity kiosk is a different provider and stays with CommodityLogParser.
public static class ShopPurchaseParser
{
    private static readonly Regex Buy = new(
        @"<(?<ts>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z)> \[Notice\] " +
        @"<CEntityComponentShopUIProvider::SendShopBuyRequest> Sending SShopBuyRequest - " +
        @"playerId\[[^\]]+\] shopId\[(?<shop>\d+)\] shopName\[(?<name>[^\]]+)\] kioskId\[(?<kiosk>\d+)\] " +
        @"client_price\[(?<price>[0-9.]+)\] itemClassGUID\[(?<guid>[0-9a-f-]+)\] " +
        @"itemName\[(?<item>[^\]]+)\] quantity\[(?<qty>\d+)\]",
        RegexOptions.Compiled);

    // Cheap pre-filter so the tracker can skip the bulk of lines before running regex.
    public static bool LooksShopRelevant(string raw) =>
        raw.Contains("CEntityComponentShopUIProvider");

    public static ShopPurchase? ParseBuy(string raw)
    {
        var m = Buy.Match(raw);
        if (!m.Success) return null;
        return new ShopPurchase(
            ParseStamp(m.Groups["ts"].Value),
            m.Groups["item"].Value,
            ParseAuec(m.Groups["price"].Value),
            int.Parse(m.Groups["qty"].Value, CultureInfo.InvariantCulture),
            m.Groups["name"].Value);
    }

    private static DateTime ParseStamp(string ts) =>
        DateTime.ParseExact(ts, "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    // client_price prints as "265.000000"; the value is whole aUEC.
    private static long ParseAuec(string s) =>
        (long)Math.Round(decimal.Parse(s, CultureInfo.InvariantCulture));
}
