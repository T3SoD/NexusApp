using NexusApp.Models;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Shop purchases settle by default and are refused only by an explicit non-Success result.
// That polarity mirrors SessionLedger and is the safe direction: 754 of 755 requests in the
// corpus receive a Success, so a missing response is a response we did not see.
public class PurchaseLedgerTests
{
    private static DateTime U(int h, int m, int s, int ms = 0) =>
        new(2026, 8, 7, h, m, s, ms, DateTimeKind.Utc);

    private static ShopPurchase Buy(DateTime utc, long price, string kiosk = "K1",
                                    string shop = "S1", string guid = "g1") =>
        new()
        {
            TimestampUtc = utc, Kind = ShopTransactionKind.Buy, Price = price, Quantity = 1,
            ItemToken = "tok", ItemGuid = guid, ShopName = "SCShop_Test-001",
            ShopId = shop, KioskId = kiosk,
        };

    private static ShopPurchase Sell(DateTime utc, long price) =>
        new()
        {
            TimestampUtc = utc, Kind = ShopTransactionKind.Sell, Price = price, Quantity = 1,
            ItemToken = "tok", ItemGuid = "g2", ShopName = "SCShop_Test-001",
            ShopId = "S1", KioskId = "K1",
        };

    private static ShopFlowResult Result(DateTime utc, string result, string kiosk = "K1",
                                         string shop = "S1",
                                         ShopTransactionKind kind = ShopTransactionKind.Buy) =>
        new(utc, result, shop, kiosk, kind, ShopProvider.ShopUI);

    [Fact]
    public void Apply_AddsAndDedupesOnReplay()
    {
        var led = new PurchaseLedger();
        var p = Buy(U(13, 0, 0), 1_000);
        Assert.True(led.Apply(p));
        Assert.False(led.Apply(Buy(U(13, 0, 0), 1_000)));   // same Key, replayed line
        Assert.Single(led.Purchases);
    }

    [Fact]
    public void SettledDelta_SubtractsBuysAndAddsSells()
    {
        var led = new PurchaseLedger();
        led.Apply(Buy(U(13, 5, 0), 1_470));
        led.Apply(Sell(U(13, 6, 0), 119));
        Assert.Equal(-1_351, led.SettledDeltaBetween(U(13, 0, 0), U(13, 10, 0)));
    }

    [Fact]
    public void SettledDelta_WindowIsExclusiveOfTheAnchorAndInclusiveOfTheCapture()
    {
        var led = new PurchaseLedger();
        led.Apply(Buy(U(13, 0, 0), 100));    // exactly at the anchor: excluded
        led.Apply(Buy(U(13, 10, 0), 200));   // exactly at the capture: included
        Assert.Equal(-200, led.SettledDeltaBetween(U(13, 0, 0), U(13, 10, 0)));
    }

    [Fact]
    public void ASuccessResultChangesNothingButIsConsumed()
    {
        var led = new PurchaseLedger();
        led.Apply(Buy(U(13, 0, 0), 500));
        Assert.True(led.ApplyResult(Result(U(13, 0, 1), "Success")));
        Assert.Null(led.Purchases[0].Refused);
        Assert.Equal(-500, led.SettledDeltaBetween(U(12, 0, 0), U(14, 0, 0)));
    }

    [Fact]
    public void ANonSuccessResultRefusesAndRemovesTheMoney()
    {
        var led = new PurchaseLedger();
        led.Apply(Buy(U(13, 0, 0), 500));
        Assert.True(led.ApplyResult(Result(U(13, 0, 1), "InsufficientFunds")));
        Assert.Equal("InsufficientFunds", led.Purchases[0].Refused);
        Assert.Equal(0, led.SettledDeltaBetween(U(12, 0, 0), U(14, 0, 0)));
    }

    [Fact]
    public void AResultSettlesTheOldestMatchingRequestAtThatKiosk()
    {
        var led = new PurchaseLedger();
        led.Apply(Buy(U(13, 0, 0), 100, guid: "a"));
        led.Apply(Buy(U(13, 0, 1), 200, guid: "b"));
        led.ApplyResult(Result(U(13, 0, 2), "InsufficientFunds"));

        Assert.Equal("InsufficientFunds", led.Purchases[0].Refused);   // the oldest one
        Assert.Null(led.Purchases[1].Refused);
    }

    [Fact]
    public void AResultDoesNotReachAnotherKioskOrShop()
    {
        var led = new PurchaseLedger();
        led.Apply(Buy(U(13, 0, 0), 100, kiosk: "K1"));
        Assert.False(led.ApplyResult(Result(U(13, 0, 1), "InsufficientFunds", kiosk: "K2")));
        Assert.False(led.ApplyResult(Result(U(13, 0, 1), "InsufficientFunds", shop: "S9")));
        Assert.Null(led.Purchases[0].Refused);
    }

    [Fact]
    public void AResultDoesNotCrossDirection()
    {
        var led = new PurchaseLedger();
        led.Apply(Buy(U(13, 0, 0), 100));
        Assert.False(led.ApplyResult(
            Result(U(13, 0, 1), "InsufficientFunds", kind: ShopTransactionKind.Sell)));
        Assert.Null(led.Purchases[0].Refused);
    }

    [Fact]
    public void AResultOutsideTheWindowIsAnOrphanAndIsDropped()
    {
        var led = new PurchaseLedger();
        led.Apply(Buy(U(13, 0, 0), 100));
        Assert.False(led.ApplyResult(Result(U(13, 0, 30), "InsufficientFunds")));
        Assert.Null(led.Purchases[0].Refused);   // expiry leaves it SETTLED, never refused
    }

    [Fact]
    public void PendingIsNeitherSuccessNorFailureAndLeavesTheRowSettled()
    {
        var led = new PurchaseLedger();
        led.Apply(Buy(U(13, 0, 0), 500, guid: "a"));
        led.Apply(Buy(U(13, 0, 1), 300, guid: "b"));
        Assert.True(led.ApplyResult(Result(U(13, 0, 2), "WaitingForPendingResult")));
        Assert.Null(led.Purchases[0].Refused);
        Assert.Equal(-800, led.SettledDeltaBetween(U(12, 0, 0), U(14, 0, 0)));

        // A real failure arriving later still refuses the SAME row, not the next one:
        // WaitingForPendingResult must not have marked it answered.
        Assert.True(led.ApplyResult(Result(U(13, 0, 3), "InsufficientFunds")));
        Assert.Equal("InsufficientFunds", led.Purchases[0].Refused);
        Assert.Null(led.Purchases[1].Refused);
    }

    [Fact]
    public void ASuccessAnsweredRowIsNeverReMatchedByALaterResult()
    {
        // Refused == null alone cannot tell "never answered" apart from "answered Success".
        // Without a separate answered marker, the InsufficientFunds below would re-find row a
        // (still the oldest Refused-null row) instead of the row it actually belongs to, b.
        var led = new PurchaseLedger();
        led.Apply(Buy(U(13, 0, 0), 100, guid: "a"));
        led.Apply(Buy(U(13, 0, 1), 200, guid: "b"));
        Assert.True(led.ApplyResult(Result(U(13, 0, 2), "Success")));            // answers a
        Assert.True(led.ApplyResult(Result(U(13, 0, 3), "InsufficientFunds")));  // answers b

        Assert.Null(led.Purchases[0].Refused);                        // a: Success, stays settled
        Assert.Equal("InsufficientFunds", led.Purchases[1].Refused);  // b: the real failure
        Assert.Equal(-100, led.SettledDeltaBetween(U(12, 0, 0), U(14, 0, 0)));
    }

    [Fact]
    public void AnOrphanResultWithNoRequestIsDropped()
    {
        var led = new PurchaseLedger();
        Assert.False(led.ApplyResult(Result(U(13, 0, 0), "Success")));
        Assert.Empty(led.Purchases);
    }

    [Fact]
    public void ReplayedResultsDoNotRefuseTwice()
    {
        var led = new PurchaseLedger();
        led.Apply(Buy(U(13, 0, 0), 100, guid: "a"));
        led.Apply(Buy(U(13, 0, 1), 200, guid: "b"));
        var r = Result(U(13, 0, 2), "InsufficientFunds");
        Assert.True(led.ApplyResult(r));
        Assert.False(led.ApplyResult(r));          // same line replayed
        Assert.Null(led.Purchases[1].Refused);     // must not walk on to the next request
    }

    [Fact]
    public void ResetClearsEverything()
    {
        var led = new PurchaseLedger();
        led.Apply(Buy(U(13, 0, 0), 100));
        led.Reset();
        Assert.Empty(led.Purchases);
        Assert.Equal(0, led.SettledDeltaBetween(U(12, 0, 0), U(14, 0, 0)));
        Assert.True(led.Apply(Buy(U(13, 0, 0), 100)));   // key set cleared too
    }
}
