using System.Globalization;
using System.Text.RegularExpressions;
using NexusApp.Models;

namespace NexusApp.Services;

public sealed record CommodityErrorInfo(DateTime TimestampUtc, string Result, TransactionKind Kind);

// Pure, stateless parsing of the three commodity-kiosk Game.log line shapes (buy request, sell
// request, error response), sibling of HaulLogParser. Three shapes, never generalized. No file
// I/O, no PII: playerId[..] is matched but never read. Buys quote quantity in cSCU and are
// divided by 100 here, sells are SCU already; every numeric field parses invariant. See
// CommodityLogParserTests for the line fixtures.
public static class CommodityLogParser
{
    private static readonly Regex Buy = new(
        @"<(?<ts>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z)> \[Notice\] " +
        @"<CEntityComponentCommodityUIProvider::SendCommodityBuyRequest> Sending SShopCommodityBuyRequest - " +
        @"playerId\[[^\]]+\] shopId\[(?<shop>\d+)\] shopName\[(?<name>[^\]]+)\] kioskId\[(?<kiosk>\d+)\] " +
        @"price\[(?<price>[0-9.]+)\] shopPricePerCentiSCU\[[0-9.]+\] resourceGUID\[(?<guid>[0-9a-f-]+)\] " +
        @"autoLoading\[(?<auto>[01])\] quantity\[(?<qty>[0-9.]+) cSCU\] Cargo Box Data:",
        RegexOptions.Compiled);

    private static readonly Regex Sell = new(
        @"<(?<ts>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z)> \[Notice\] " +
        @"<CEntityComponentCommodityUIProvider::SendCommoditySellRequest> Sending SShopCommoditySellRequest - " +
        @"playerId\[[^\]]+\] shopId\[(?<shop>\d+)\] shopName\[(?<name>[^\]]+)\] kioskId\[(?<kiosk>\d+)\] " +
        @"amount\[(?<amount>[0-9.]+)\] resourceGUID\[(?<guid>[0-9a-f-]+)\] autoLoading\[(?<auto>[01])\] " +
        @"quantity\[(?<qty>[0-9.]+)\] transactionMode\[[^\]]+\] Cargo Box Data:",
        RegexOptions.Compiled);

    private static readonly Regex Error = new(
        @"<(?<ts>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z)> \[Error\] " +
        @"<CEntityComponentCommodityUIProvider::RmToken_CommodityTransactionResponse> " +
        @"Commodity Transaction Response Error - playerId\[[^\]]+\] result\[(?<code>\w+)\] type\[(?<type>Buying|Selling)\]",
        RegexOptions.Compiled);

    private static readonly Regex Box = new(
        @"boxSize\[(?<size>[0-9.]+)\] \| unitAmount\[(?<n>\d+)\]",
        RegexOptions.Compiled);

    // Cheap pre-filter so the tracker can skip the bulk of lines before running regex. All three
    // shapes come from this one provider.
    public static bool LooksCommodityRelevant(string raw) =>
        raw.Contains("CEntityComponentCommodityUIProvider");

    public static CommodityTransaction? ParseBuy(string raw)
    {
        var m = Buy.Match(raw);
        if (!m.Success) return null;
        return new CommodityTransaction
        {
            TimestampUtc = ParseStamp(m.Groups["ts"].Value),
            Kind = TransactionKind.Buy,
            Amount = ParseAuec(m.Groups["price"].Value),
            // Buys quote centi-SCU; divide by 100 so Scu means SCU on every surface.
            Scu = decimal.Parse(m.Groups["qty"].Value, CultureInfo.InvariantCulture) / 100m,
            ResourceGuid = m.Groups["guid"].Value,
            ShopName = m.Groups["name"].Value,
            ShopId = m.Groups["shop"].Value,
            KioskId = m.Groups["kiosk"].Value,
            AutoLoading = m.Groups["auto"].Value == "1",
            Boxes = ParseBoxes(raw, m.Index + m.Length),
        };
    }

    public static CommodityTransaction? ParseSell(string raw)
    {
        var m = Sell.Match(raw);
        if (!m.Success) return null;
        return new CommodityTransaction
        {
            TimestampUtc = ParseStamp(m.Groups["ts"].Value),
            Kind = TransactionKind.Sell,
            Amount = ParseAuec(m.Groups["amount"].Value),
            Scu = decimal.Parse(m.Groups["qty"].Value, CultureInfo.InvariantCulture),  // SCU already
            ResourceGuid = m.Groups["guid"].Value,
            ShopName = m.Groups["name"].Value,
            ShopId = m.Groups["shop"].Value,
            KioskId = m.Groups["kiosk"].Value,
            AutoLoading = m.Groups["auto"].Value == "1",
            Boxes = ParseBoxes(raw, m.Index + m.Length),
        };
    }

    public static CommodityErrorInfo? ParseTransactionError(string raw)
    {
        var m = Error.Match(raw);
        if (!m.Success) return null;
        var kind = m.Groups["type"].Value == "Buying" ? TransactionKind.Buy : TransactionKind.Sell;
        return new CommodityErrorInfo(ParseStamp(m.Groups["ts"].Value), m.Groups["code"].Value, kind);
    }

    // The Cargo Box Data tail after the matched head: one group on buys, several on sells.
    private static List<CargoBoxGroup> ParseBoxes(string raw, int from)
    {
        var boxes = new List<CargoBoxGroup>();
        for (var m = Box.Match(raw, from); m.Success; m = m.NextMatch())
            boxes.Add(new CargoBoxGroup(
                decimal.Parse(m.Groups["size"].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture)));
        return boxes;
    }

    private static DateTime ParseStamp(string ts) =>
        DateTime.ParseExact(ts, "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    // price[]/amount[] print as "182560.000000"; the value is whole aUEC.
    private static long ParseAuec(string s) =>
        (long)Math.Round(decimal.Parse(s, CultureInfo.InvariantCulture));
}
