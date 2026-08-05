using NexusApp.Models;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Pure ledger math: apply with replay dedupe by Key, the voiding rule (nearest preceding unvoided
// same-kind within 5 seconds), totals over unvoided rows only, negative net unclamped.
public class SessionLedgerTests
{
    private static readonly DateTime T0 = new(2026, 7, 4, 13, 0, 0, DateTimeKind.Utc);

    private static CommodityTransaction Tx(TransactionKind kind, long amount, DateTime ts) => new()
    {
        TimestampUtc = ts, Kind = kind, Amount = amount, Scu = 1m,
        ResourceGuid = "aaaa1111-2af8-48b5-9af7-000000000001",
        ShopName = "SCShop_Test_Admin", ShopId = "1", KioskId = "2",
    };

    private static CommodityErrorInfo Err(TransactionKind kind, DateTime ts, string code = "InsufficentFunds") =>
        new(ts, code, kind);

    [Fact]
    public void Apply_SumsUnvoidedTotals()
    {
        var ledger = new SessionLedger();
        Assert.True(ledger.Apply(Tx(TransactionKind.Buy, 836_854, T0)));
        Assert.True(ledger.Apply(Tx(TransactionKind.Sell, 1_067_200, T0.AddMinutes(10))));
        Assert.Equal(836_854L, ledger.Bought);
        Assert.Equal(1_067_200L, ledger.Sold);
        Assert.Equal(230_346L, ledger.Net);
        Assert.Equal(2, ledger.UnvoidedCount);
    }

    [Fact]
    public void Net_NegativeStandsUnclamped()
    {
        var ledger = new SessionLedger();
        ledger.Apply(Tx(TransactionKind.Buy, 500_000, T0));
        Assert.Equal(-500_000L, ledger.Net);
    }

    [Fact]
    public void Apply_DuplicateKey_IsIgnored()
    {
        var ledger = new SessionLedger();
        Assert.True(ledger.Apply(Tx(TransactionKind.Buy, 100, T0)));
        Assert.False(ledger.Apply(Tx(TransactionKind.Buy, 100, T0)));
        Assert.Single(ledger.Transactions);
        Assert.Equal(100L, ledger.Bought);
    }

    // Feeding the same log twice changes nothing: applied transactions dedupe by Key, and a
    // replayed error line must not void a second transaction.
    [Fact]
    public void Replay_WholeSession_IsIdempotent()
    {
        var ledger = new SessionLedger();
        var buy1 = Tx(TransactionKind.Buy, 100, T0);
        var buy2 = Tx(TransactionKind.Buy, 200, T0.AddSeconds(2));
        var err = Err(TransactionKind.Buy, T0.AddSeconds(2.2));

        ledger.Apply(buy1);
        ledger.Apply(buy2);
        Assert.True(ledger.ApplyError(err));

        ledger.Apply(Tx(TransactionKind.Buy, 100, T0));
        ledger.Apply(Tx(TransactionKind.Buy, 200, T0.AddSeconds(2)));
        Assert.False(ledger.ApplyError(Err(TransactionKind.Buy, T0.AddSeconds(2.2))));

        Assert.Equal(2, ledger.Transactions.Count);
        Assert.Null(ledger.Transactions[0].Voided);       // buy1 must survive the replayed error
        Assert.NotNull(ledger.Transactions[1].Voided);
        Assert.Equal(100L, ledger.Bought);
    }

    [Fact]
    public void Void_NearestPrecedingSameKind()
    {
        var ledger = new SessionLedger();
        ledger.Apply(Tx(TransactionKind.Buy, 100, T0));
        ledger.Apply(Tx(TransactionKind.Buy, 200, T0.AddSeconds(2)));
        Assert.True(ledger.ApplyError(Err(TransactionKind.Buy, T0.AddSeconds(2.1))));
        Assert.Null(ledger.Transactions[0].Voided);
        Assert.Equal("InsufficentFunds", ledger.Transactions[1].Voided);
        Assert.Equal(100L, ledger.Bought);
    }

    [Fact]
    public void Void_IgnoresOtherKind()
    {
        var ledger = new SessionLedger();
        ledger.Apply(Tx(TransactionKind.Sell, 300, T0));
        Assert.False(ledger.ApplyError(Err(TransactionKind.Buy, T0.AddSeconds(0.1))));
        Assert.Null(ledger.Transactions[0].Voided);
        Assert.Equal(300L, ledger.Sold);
    }

    [Fact]
    public void Void_OutsideWindow_IsOrphan()
    {
        var ledger = new SessionLedger();
        ledger.Apply(Tx(TransactionKind.Buy, 100, T0));
        Assert.False(ledger.ApplyError(Err(TransactionKind.Buy, T0.AddSeconds(6))));
        Assert.Null(ledger.Transactions[0].Voided);
    }

    [Fact]
    public void Void_SecondError_NoCandidateLeft_IsOrphan()
    {
        var ledger = new SessionLedger();
        ledger.Apply(Tx(TransactionKind.Buy, 100, T0));
        Assert.True(ledger.ApplyError(Err(TransactionKind.Buy, T0.AddSeconds(0.1))));
        Assert.False(ledger.ApplyError(Err(TransactionKind.Buy, T0.AddSeconds(0.2), "CargoCreationFailed")));
        Assert.Equal("InsufficentFunds", ledger.Transactions[0].Voided);
    }

    [Fact]
    public void Void_TransactionAfterError_IsNotACandidate()
    {
        var ledger = new SessionLedger();
        ledger.Apply(Tx(TransactionKind.Buy, 100, T0.AddSeconds(1)));
        Assert.False(ledger.ApplyError(Err(TransactionKind.Buy, T0)));
        Assert.Null(ledger.Transactions[0].Voided);
    }

    [Fact]
    public void Void_RowStaysInLedger_OutOfTotals()
    {
        var ledger = new SessionLedger();
        ledger.Apply(Tx(TransactionKind.Buy, 100, T0));
        ledger.Apply(Tx(TransactionKind.Sell, 300, T0.AddMinutes(1)));
        ledger.ApplyError(Err(TransactionKind.Sell, T0.AddMinutes(1).AddSeconds(0.2), "InsuffientStock"));
        Assert.Equal(2, ledger.Transactions.Count);
        Assert.Equal(0L, ledger.Sold);
        Assert.Equal(-100L, ledger.Net);
        Assert.Equal(1, ledger.UnvoidedCount);
    }

    [Fact]
    public void Reset_ClearsEverything()
    {
        var ledger = new SessionLedger();
        var tx = Tx(TransactionKind.Buy, 100, T0);
        ledger.Apply(tx);
        ledger.Reset();
        Assert.Empty(ledger.Transactions);
        Assert.Equal(0L, ledger.Net);
        Assert.True(ledger.Apply(Tx(TransactionKind.Buy, 100, T0)));   // keys cleared too
    }

    [Fact]
    public void BuildSummary_CountsUnvoidedOnly()
    {
        var ledger = new SessionLedger();
        var start = T0.AddMinutes(-5);
        ledger.Apply(Tx(TransactionKind.Buy, 100, T0));
        ledger.Apply(Tx(TransactionKind.Sell, 400, T0.AddMinutes(2)));
        ledger.Apply(Tx(TransactionKind.Buy, 50, T0.AddMinutes(3)));
        ledger.ApplyError(Err(TransactionKind.Buy, T0.AddMinutes(3).AddSeconds(0.2)));

        var key = SessionSummary.MakeKey(GameChannel.Live, start);
        var s = ledger.BuildSummary(key, GameChannel.Live, start);
        Assert.Equal(key, s.SessionKey);
        Assert.Equal(GameChannel.Live, s.Channel);
        Assert.Equal(start, s.StartUtc);
        Assert.Equal(T0.AddMinutes(3), s.LastSeenUtc);
        Assert.Equal(100L, s.Bought);
        Assert.Equal(400L, s.Sold);
        Assert.Equal(300L, s.Net);
        Assert.Equal(2, s.TxCount);
    }

    // End to end on the verbatim fixtures: the error line sits 35.7 s after the fixture buy,
    // outside the window, so it must drop as an orphan.
    [Fact]
    public void FixtureError_OutsideWindowOfFixtureBuy_IsOrphan()
    {
        var ledger = new SessionLedger();
        ledger.Apply(CommodityLogParser.ParseBuy(CommodityLogFixtures.BuyLine)!);
        Assert.False(ledger.ApplyError(CommodityLogParser.ParseTransactionError(CommodityLogFixtures.ErrorLine)!));
        Assert.Null(ledger.Transactions[0].Voided);
        Assert.Equal(182560L, ledger.Bought);
    }

    // The source log's real pairing: request at 13:36:12.141, error 137 ms later.
    [Fact]
    public void FixtureError_VoidsBuy137MsEarlier()
    {
        var line = CommodityLogFixtures.BuyLine.Replace(
            "<2026-07-04T13:35:36.565Z>", "<2026-07-04T13:36:12.141Z>");
        var ledger = new SessionLedger();
        ledger.Apply(CommodityLogParser.ParseBuy(line)!);
        Assert.True(ledger.ApplyError(CommodityLogParser.ParseTransactionError(CommodityLogFixtures.ErrorLine)!));
        Assert.Equal("InsufficentFunds", ledger.Transactions[0].Voided);
        Assert.Equal(0L, ledger.Bought);
    }
}
