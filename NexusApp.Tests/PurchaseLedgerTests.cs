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

    [Fact]
    public void ApplyResult_DoesNotLetAShoppingResponseAnswerAShopUiRequest()
    {
        var ledger = new PurchaseLedger();
        var t = new DateTime(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);
        var shopUi = new ShopPurchase
        {
            TimestampUtc = t, Kind = ShopTransactionKind.Buy, Provider = ShopProvider.ShopUI,
            Price = 500, Quantity = 1, ItemGuid = "guid-a", ShopId = "111", KioskId = "222",
        };
        ledger.Apply(shopUi);

        // Carries no ids, as the real line does. It must find no candidate and be dropped.
        // NOTE: this direction passes even with the provider gate deleted, because the ShopId
        // comparison ("" against "111") already rejects it. The gate's load-bearing direction is
        // the reverse one, covered by the test below. Keep both.
        var stray = new ShopFlowResult(t.AddSeconds(1), "Refused", "", "",
            ShopTransactionKind.Buy, ShopProvider.Shopping);
        Assert.False(ledger.ApplyResult(stray));
        Assert.Null(shopUi.Refused);
    }

    // The direction the provider gate actually protects, and the one that costs real money.
    // Both providers address the same shop entities, so a shopId collision is plausible, and
    // kioskId[0] occurs on both. Without the `p.Provider != r.Provider` check in ApplyResult, a
    // ShopUI refusal would land on the Shopping row and its 342,720 aUEC would reappear as a
    // phantom residual in the wallet reconciliation.
    [Fact]
    public void ApplyResult_DoesNotLetAShopUiRefusalAnswerAShoppingRequestAtTheSameIds()
    {
        var ledger = new PurchaseLedger();
        var t = new DateTime(2026, 8, 8, 12, 6, 2, DateTimeKind.Utc);
        var ship = new ShopPurchase
        {
            TimestampUtc = t, Kind = ShopTransactionKind.Buy, Provider = ShopProvider.Shopping,
            Price = 342_720, Quantity = 1, ItemGuid = "37659ff0-a803-4a4f-97ff-ad59822061ed",
            ShopId = "751893855885", KioskId = "0",
        };
        ledger.Apply(ship);

        // Same shopId, same kioskId, same direction, inside the window. Only the provider differs.
        var foreign = new ShopFlowResult(t.AddSeconds(2), "InsufficientFunds", "751893855885", "0",
            ShopTransactionKind.Buy, ShopProvider.ShopUI);

        Assert.False(ledger.ApplyResult(foreign));
        Assert.Null(ship.Refused);
        Assert.Equal(-342_720, ledger.SettledDeltaBetween(t.AddSeconds(-1), t.AddSeconds(10)));
    }

    [Fact]
    public void ApplyResult_SettlesTheOldestUnansweredShoppingRequest()
    {
        var ledger = new PurchaseLedger();
        var t = new DateTime(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);
        var first = new ShopPurchase
        {
            TimestampUtc = t, Kind = ShopTransactionKind.Buy, Provider = ShopProvider.Shopping,
            Price = 4, Quantity = 1, ItemGuid = "guid-first", ShopId = "9", KioskId = "0",
        };
        var second = new ShopPurchase
        {
            TimestampUtc = t.AddSeconds(1), Kind = ShopTransactionKind.Buy,
            Provider = ShopProvider.Shopping,
            Price = 9, Quantity = 1, ItemGuid = "guid-second", ShopId = "9", KioskId = "0",
        };
        ledger.Apply(first);
        ledger.Apply(second);

        // Refusing the oldest must leave the newer one alone, even though both share kioskId[0].
        Assert.True(ledger.ApplyResult(new ShopFlowResult(
            t.AddSeconds(2), "Refused", "", "", ShopTransactionKind.Buy, ShopProvider.Shopping)));
        Assert.Equal("Refused", first.Refused);
        Assert.Null(second.Refused);
    }

    [Fact]
    public void ApplyResult_PairsAcrossTheMeasuredMaximumGap()
    {
        var ledger = new PurchaseLedger();
        var t = new DateTime(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);
        var buy = new ShopPurchase
        {
            TimestampUtc = t, Kind = ShopTransactionKind.Buy, Provider = ShopProvider.Shopping,
            Price = 4, Quantity = 1, ItemGuid = "guid-gap", ShopId = "9", KioskId = "0",
        };
        ledger.Apply(buy);

        // 5.26s is the widest request-to-response gap measured across 402 corpus logs. The old
        // 5 second window clipped it.
        Assert.True(ledger.ApplyResult(new ShopFlowResult(
            t.AddSeconds(5.26), "Refused", "", "", ShopTransactionKind.Buy,
            ShopProvider.Shopping)));
        Assert.Equal("Refused", buy.Refused);
    }

    [Fact]
    public void Apply_RejectsAPurchaseThatIsNotInAuec()
    {
        var ledger = new PurchaseLedger();
        var t = new DateTime(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);
        var foreign = new ShopPurchase
        {
            TimestampUtc = t, Kind = ShopTransactionKind.Buy, Provider = ShopProvider.Shopping,
            Price = 100, Quantity = 1, ItemGuid = "guid-rec", ShopId = "9", KioskId = "0",
            Currency = "REC",
        };
        Assert.False(ledger.Apply(foreign));
        Assert.Empty(ledger.Purchases);
        Assert.Equal(0, ledger.SettledDeltaBetween(t.AddSeconds(-1), t.AddSeconds(1)));
    }
}
