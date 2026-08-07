using System.Globalization;

namespace NexusApp.Models;

public enum ShopTransactionKind { Buy, Sell }

// One shop kiosk transaction read off a Game.log line. Price is the client's own figure
// (client_price) and is the LINE TOTAL, proven live 2026-08-07: client_price[1470] quantity[2]
// moved the wallet by 1470. Never multiply or divide it by Quantity.
public sealed class ShopPurchase
{
    public DateTime TimestampUtc { get; init; }
    public ShopTransactionKind Kind { get; init; }
    public long Price { get; init; }
    public int Quantity { get; init; }
    public string ItemToken { get; init; } = "";
    public string ItemGuid { get; init; } = "";
    public string ShopName { get; init; } = "";
    public string ShopId { get; init; } = "";
    public string KioskId { get; init; } = "";

    // null = settled; else the result code that refused it. Settled is the DEFAULT, including on
    // expiry: 754 of 755 requests in the corpus receive a Success, so a missing response is far
    // more likely a response we did not see than a purchase that failed.
    public string? Refused { get; set; }

    // Replay dedupe, the CommodityTransaction.Key idiom. Two different items cannot share a
    // kiosk inside one millisecond.
    public string Key => string.Create(CultureInfo.InvariantCulture,
        $"{TimestampUtc:O}|{Kind}|{Price}|{KioskId}|{ItemGuid}");
}
