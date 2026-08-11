using NexusApp.Models;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Operations system-view redesign (2026-08-10): the folds behind the right rail.
// These are pure so the whole rail can be proven without a window, a WebView, or a game running.
public class OperationsPanelsTests
{
    private static readonly DateTime Now = new(2026, 8, 10, 20, 0, 0, DateTimeKind.Utc);

    private static ShardSession Shard(string code, string instance, DateTime joined) => new()
    {
        ShardId = $"pub_{code}_12030094_{instance}", RegionCode = code, Region = "US East",
        Instance = instance, JoinedAt = joined, Channel = "LIVE",
    };

    // ---- shard rows ----------------------------------------------------------------------------

    [Fact]
    public void NoShardsEver_ProducesNoRows()
    {
        Assert.Empty(OperationsPanels.ShardRows(Array.Empty<ShardSession>(), onShard: false, Now));
    }

    // The live shard's duration runs to NOW, because it has not ended yet.
    [Fact]
    public void CurrentShard_MeasuresToNow()
    {
        var rows = OperationsPanels.ShardRows(
            new[] { Shard("use1c", "142", Now.AddMinutes(-72)) }, onShard: true, Now);

        var row = Assert.Single(rows);
        Assert.True(row.Live);
        Assert.Equal("USE1C 142", row.Id);
        Assert.Equal("1h 12m", row.Duration);
        Assert.Equal("now", row.When);
    }

    // A past shard ended when the player joined the NEXT one, so its duration is the gap to the
    // newer entry. History arrives newest first, so the newer entry is the PREVIOUS index.
    [Fact]
    public void PastShard_MeasuresToTheNextJoin()
    {
        var rows = OperationsPanels.ShardRows(new[]
        {
            Shard("use1c", "142", Now.AddMinutes(-72)),
            Shard("use1a", "007", Now.AddMinutes(-72 - 124)),   // ran 2h 04m, then the join above
        }, onShard: true, Now);

        Assert.Equal("2h 04m", rows[1].Duration);
        Assert.False(rows[1].Live);
    }

    // Nobody knows how long the newest shard lasted if the player is no longer on one: the game
    // never logs a leave. An unknown duration must read as unknown, not as zero and not as "to now".
    [Fact]
    public void NewestShard_WhenNoLongerOnIt_HasUnknownDuration()
    {
        var rows = OperationsPanels.ShardRows(
            new[] { Shard("use1c", "142", Now.AddMinutes(-72)) }, onShard: false, Now);

        Assert.Null(rows[0].Duration);
        Assert.False(rows[0].Live);
    }

    // Game.log timestamps can arrive out of order across a replay. A negative gap is bad data,
    // and the row must refuse to render it rather than print a negative duration.
    [Fact]
    public void OutOfOrderJoins_RefuseToRenderADuration()
    {
        var rows = OperationsPanels.ShardRows(new[]
        {
            Shard("use1c", "142", Now.AddHours(-5)),
            Shard("use1a", "007", Now.AddHours(-2)),   // newer than the entry above it: impossible
        }, onShard: true, Now);

        Assert.Null(rows[1].Duration);
    }

    [Fact]
    public void ShardRows_AreCappedAndKeepNewestFirst()
    {
        var rows = OperationsPanels.ShardRows(new[]
        {
            Shard("use1c", "142", Now.AddHours(-1)),
            Shard("use1a", "007", Now.AddHours(-4)),
            Shard("euw1b", "021", Now.AddHours(-30)),
            Shard("use1b", "118", Now.AddHours(-32)),
            Shard("use1b", "119", Now.AddHours(-40)),
        }, onShard: true, Now, max: 4);

        Assert.Equal(4, rows.Count);
        Assert.Equal("USE1C 142", rows[0].Id);
        Assert.Equal("4h ago", rows[1].When);
        Assert.Equal("yesterday", rows[2].When);
    }

    // ---- profit history bars -------------------------------------------------------------------

    private static SessionSummary Session(string key, DateTime start, long net, int tx = 3) => new()
    {
        SessionKey = key, Channel = GameChannel.Live, StartUtc = start,
        LastSeenUtc = start.AddHours(1), Net = net, TxCount = tx,
    };

    // Entries are append-ordered and eviction removes from the middle, so the list is NOT sorted.
    // The fold has to sort or the bars come out in storage order, which is meaningless.
    [Fact]
    public void ProfitBars_SortByStartTime_NotStorageOrder()
    {
        var bars = OperationsPanels.ProfitBars(new[]
        {
            Session("c", Now.AddDays(-1), 300),
            Session("a", Now.AddDays(-3), 100),
            Session("b", Now.AddDays(-2), 200),
        }, currentKey: null, count: 7);

        Assert.Equal(new long[] { 100, 200, 300 }, bars.Select(b => b.Net).ToArray());
    }

    [Fact]
    public void ProfitBars_KeepTheMostRecentN()
    {
        var many = Enumerable.Range(0, 12)
            .Select(i => Session($"s{i}", Now.AddDays(-12 + i), i * 1000)).ToArray();

        var bars = OperationsPanels.ProfitBars(many, currentKey: null, count: 7);

        Assert.Equal(7, bars.Count);
        Assert.Equal(5000, bars[0].Net);     // sessions 0 to 4 dropped off the front
        Assert.Equal(11000, bars[^1].Net);
    }

    // A losing or break-even session is still a session. It renders as a stub bar, never as a gap,
    // or the chart silently lies about how many times you played.
    [Fact]
    public void ProfitBars_KeepZeroAndNegativeSessions()
    {
        var bars = OperationsPanels.ProfitBars(new[]
        {
            Session("a", Now.AddDays(-2), 0),
            Session("b", Now.AddDays(-1), -4200),
        }, currentKey: null, count: 7);

        Assert.Equal(2, bars.Count);
        Assert.Equal(0, bars[0].Net);
        Assert.Equal(-4200, bars[1].Net);
    }

    [Fact]
    public void ProfitBars_MarkTheLiveSession()
    {
        var bars = OperationsPanels.ProfitBars(new[]
        {
            Session("old", Now.AddDays(-1), 100),
            Session("live", Now, 412600),
        }, currentKey: "live", count: 7);

        Assert.False(bars[0].Current);
        Assert.True(bars[1].Current);
        Assert.Equal("NOW", bars[1].Label);
    }

    // ---- wallet transaction rows ---------------------------------------------------------------

    private static CommodityTransaction Tx(DateTime at, TransactionKind kind, long amount,
        string guid = "g1", string shop = "TDD Orison", string? voided = null, decimal scu = 96) => new()
    {
        TimestampUtc = at, Kind = kind, Amount = amount, Scu = scu,
        ResourceGuid = guid, ShopName = shop, Voided = voided,
    };

    private static string Name(string guid) => guid == "g1" ? "Quantainium" : "";

    // A refused transaction never moved any money, so it must never appear in a money list.
    [Fact]
    public void VoidedTransactions_NeverAppear()
    {
        var rows = OperationsPanels.TransactionRows(new[]
        {
            Tx(Now.AddMinutes(-10), TransactionKind.Sell, 396600),
            Tx(Now.AddMinutes(-5), TransactionKind.Sell, 100000, voided: "InsufficientFunds"),
        }, Array.Empty<UntrackedEntry>(), Name, Now);

        var row = Assert.Single(rows);
        Assert.Equal(396600, row.Amount);
    }

    // A buy is money leaving. The row carries the sign so no caller has to remember the rule.
    [Fact]
    public void BuysAreNegative_SellsArePositive()
    {
        var rows = OperationsPanels.TransactionRows(new[]
        {
            Tx(Now.AddMinutes(-20), TransactionKind.Buy, 47500),
            Tx(Now.AddMinutes(-10), TransactionKind.Sell, 396600),
        }, Array.Empty<UntrackedEntry>(), Name, Now);

        Assert.Equal(396600, rows[0].Amount);    // newest first
        Assert.Equal(-47500, rows[1].Amount);
        Assert.Equal(TxKind.Buy, rows[1].Kind);
    }

    // The wallet knows its balance moved even when nothing explains it. Hiding that would make the
    // list look complete when it is not, so an unattributed movement is a row of its own.
    [Fact]
    public void UnattributedWalletMovements_AreShownAsTheirOwnKind()
    {
        var rows = OperationsPanels.TransactionRows(
            new[] { Tx(Now.AddMinutes(-10), TransactionKind.Sell, 396600) },
            new[] { new UntrackedEntry { Utc = Now.AddMinutes(-2), Amount = -18700 } },
            Name, Now);

        Assert.Equal(TxKind.Unattributed, rows[0].Kind);
        Assert.Equal(-18700, rows[0].Amount);
        Assert.Equal("unknown", rows[0].Where);
    }

    [Fact]
    public void TransactionRows_AreCappedNewestFirst()
    {
        var txs = Enumerable.Range(0, 20)
            .Select(i => Tx(Now.AddMinutes(-i), TransactionKind.Sell, 1000 + i)).ToArray();

        var rows = OperationsPanels.TransactionRows(txs, Array.Empty<UntrackedEntry>(), Name, Now, max: 6);

        Assert.Equal(6, rows.Count);
        Assert.Equal(1000, rows[0].Amount);   // i=0 is the newest
    }

    // An unresolved GUID is normal: names arrive on a later log line. The row still has to say
    // something truthful rather than render an empty label.
    [Fact]
    public void UnresolvedCommodity_StillDescribesTheRow()
    {
        var rows = OperationsPanels.TransactionRows(
            new[] { Tx(Now.AddMinutes(-3), TransactionKind.Sell, 5000, guid: "unknown-guid") },
            Array.Empty<UntrackedEntry>(), Name, Now);

        Assert.Contains("96 SCU", rows[0].What);
        Assert.DoesNotContain("g1", rows[0].What);
    }
}
