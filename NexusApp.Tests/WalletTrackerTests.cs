using System.IO;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

public class WalletTrackerTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    // The real error fixture sits 35.7 s after BuyLine (an orphan by design); this variant
    // restamps it inside the 5 s void window. Only the stamp differs from the captured line.
    private const string BuyVoidErrorLine =
        "<2026-07-04T13:35:38.000Z> [Error] <CEntityComponentCommodityUIProvider::RmToken_CommodityTransactionResponse> " +
        "Commodity Transaction Response Error - playerId[REDACTED] result[InsufficentFunds] type[Buying] " +
        "[Team_CoreGameplayFeatures][Shops]";

    // BuyLine: 2026-07-04T13:35:36.565Z, price 182,560 (money out).
    private static DateTime U(int h, int m, int s, int ms = 0) => new(2026, 7, 4, h, m, s, ms, DateTimeKind.Utc);

    private sealed class Rig : IDisposable
    {
        public GameChannel Channel = GameChannel.Live;
        public ProfitTracker Profit;
        public WalletTracker Wallet;
        public string WalletPath;
        public List<Action> Posts = new();

        public Rig(string dir, bool immediateFlush = true)
        {
            WalletPath = Path.Combine(dir, "wallet.json");
            Profit = new ProfitTracker(historyPath: Path.Combine(dir, "profit_history.json"),
                                       channel: () => Channel, flushScheduler: a => a());
            Wallet = new WalletTracker(Profit, walletPath: WalletPath, channel: () => Channel,
                                       flushScheduler: immediateFlush ? a => a() : Posts.Add);
        }

        public void FeedProfit(params string[] lines)
        {
            foreach (var raw in lines)
                Profit.Ingest(new GameLogEntry { Raw = raw, Category = LogCategory.Other });
        }

        public void LatchWallet(string raw) =>
            Wallet.Ingest(new GameLogEntry { Raw = raw, Category = LogCategory.Other });

        public void Dispose()
        {
            Wallet.Dispose();
            Profit.Dispose();
        }
    }

    private Rig NewRig(bool immediateFlush = true)
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexus-wallet-tracker-test-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return new Rig(dir, immediateFlush);
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void FirstCaptureAnchorsWithoutARow()
    {
        using var rig = NewRig();
        rig.Wallet.OnBalanceCaptured(5_000_000, U(13, 0, 0), U(13, 0, 1));

        Assert.True(rig.Wallet.HasAnchor);
        Assert.Equal(5_000_000, rig.Wallet.Estimate);
        Assert.Equal("Ocr", rig.Wallet.AnchorSource);
        Assert.Empty(LoadUntracked(rig));
    }

    [Fact]
    public void EstimateAddsOnlyPostAnchorTrades()
    {
        using var rig = NewRig();
        rig.Wallet.OnBalanceCaptured(1_000_000, U(13, 0, 0), U(13, 0, 1));
        rig.FeedProfit(CommodityLogFixtures.BuyLine);   // 13:35:36, after the anchor: counts
        rig.FeedProfit(CommodityLogFixtures.SellLine);  // 2025, before the anchor: never counts

        Assert.Equal(1_000_000 - 182_560, rig.Wallet.Estimate);
    }

    [Fact]
    public void VoidedTradeDoesNotMoveTheEstimate()
    {
        using var rig = NewRig();
        rig.Wallet.OnBalanceCaptured(1_000_000, U(13, 0, 0), U(13, 0, 1));
        rig.FeedProfit(CommodityLogFixtures.BuyLine, BuyVoidErrorLine);

        Assert.Equal(1_000_000, rig.Wallet.Estimate);
    }

    [Fact]
    public void UnexplainedGainRecordsUntrackedIncome()
    {
        using var rig = NewRig();
        rig.Wallet.OnBalanceCaptured(1_000_000, U(13, 0, 0), U(13, 0, 1));
        rig.Wallet.OnBalanceCaptured(1_080_000, U(13, 10, 0), U(13, 10, 1));

        var entry = Assert.Single(LoadUntracked(rig));
        Assert.Equal(80_000, entry.Amount);
        Assert.Equal(U(13, 10, 1), entry.Utc);
        Assert.Equal(1_080_000, rig.Wallet.Estimate);
    }

    [Fact]
    public void UnexplainedSpendRecordsUntrackedPurchase()
    {
        using var rig = NewRig();
        rig.Wallet.OnBalanceCaptured(1_000_000, U(13, 0, 0), U(13, 0, 1));
        rig.Wallet.OnBalanceCaptured(920_000, U(13, 10, 0), U(13, 10, 1));

        var entry = Assert.Single(LoadUntracked(rig));
        Assert.Equal(-80_000, entry.Amount);
    }

    [Fact]
    public void ExactMatchRecordsNothing()
    {
        using var rig = NewRig();
        rig.Wallet.OnBalanceCaptured(1_000_000, U(13, 0, 0), U(13, 0, 1));
        rig.Wallet.OnBalanceCaptured(1_000_000, U(13, 10, 0), U(13, 10, 1));

        Assert.Empty(LoadUntracked(rig));
    }

    [Fact]
    public void TradeExplainedDeltaRecordsNothing()
    {
        using var rig = NewRig();
        rig.Wallet.OnBalanceCaptured(1_000_000, U(13, 0, 0), U(13, 0, 1));
        rig.FeedProfit(CommodityLogFixtures.BuyLine);
        rig.Wallet.OnBalanceCaptured(817_440, U(13, 45, 0), U(13, 45, 1));

        Assert.Empty(LoadUntracked(rig));
        Assert.Equal(817_440, rig.Wallet.Estimate);
    }

    [Fact]
    public void RaceGuardSkipsWhenATradeIsInsideTheWindow()
    {
        using var rig = NewRig();
        rig.Wallet.OnBalanceCaptured(1_000_000, U(13, 0, 0), U(13, 0, 1));
        rig.FeedProfit(CommodityLogFixtures.BuyLine);            // 13:35:36.565
        rig.Wallet.OnBalanceCaptured(1_000_000, U(13, 35, 40), U(13, 35, 41)); // trigger 3.4 s after the buy

        Assert.Empty(LoadUntracked(rig));                         // the phantom +182,560 was not booked
        Assert.Equal(1_000_000, rig.Wallet.Estimate);             // still re-anchored to the scan
    }

    [Fact]
    public void RaceGuardSkipsWhenAVoidIsInsideTheWindow()
    {
        using var rig = NewRig();
        rig.Wallet.OnBalanceCaptured(1_000_000, U(13, 0, 0), U(13, 0, 1));
        rig.FeedProfit(CommodityLogFixtures.BuyLine, BuyVoidErrorLine); // voided at 13:35:38
        // Trigger 8.4 s after the buy request: outside the plain guard, inside the voided-trade
        // guard (6 s + the 5 s void window).
        rig.Wallet.OnBalanceCaptured(1_000_500, U(13, 35, 45), U(13, 35, 46));

        Assert.Empty(LoadUntracked(rig));
        Assert.Equal(1_000_500, rig.Wallet.Estimate);
    }

    [Fact]
    public void OldTradesDoNotTripTheGuard()
    {
        using var rig = NewRig();
        rig.Wallet.OnBalanceCaptured(1_000_000, U(13, 0, 0), U(13, 0, 1));
        rig.FeedProfit(CommodityLogFixtures.BuyLine);
        rig.Wallet.OnBalanceCaptured(817_440 + 25_000, U(13, 50, 0), U(13, 50, 1));

        var entry = Assert.Single(LoadUntracked(rig));
        Assert.Equal(25_000, entry.Amount);
    }

    [Fact]
    public void CrossSessionAnchorStillReconciles()
    {
        using var rig = NewRig();
        rig.Wallet.OnBalanceCaptured(1_000_000, new DateTime(2026, 7, 1, 8, 0, 0, DateTimeKind.Utc),
                                     new DateTime(2026, 7, 1, 8, 0, 1, DateTimeKind.Utc));
        rig.Wallet.OnBalanceCaptured(1_500_000, U(13, 0, 0), U(13, 0, 1)); // days later

        var entry = Assert.Single(LoadUntracked(rig));
        Assert.Equal(500_000, entry.Amount);
    }

    [Fact]
    public void ManualSetNeverRecordsARowAndAbsorbsSessionTrades()
    {
        using var rig = NewRig();
        rig.FeedProfit(CommodityLogFixtures.BuyLine);
        rig.Wallet.SetManualBalance(500_000);

        Assert.Empty(LoadUntracked(rig));
        Assert.Equal("Manual", rig.Wallet.AnchorSource);
        // The typed figure came off the game screen, which already reflects the buy: the ledger
        // trade must not be re-subtracted.
        Assert.Equal(500_000, rig.Wallet.Estimate);
    }

    // Defense in depth for partial reads (live failure 16:44): a confirmed value whose digit
    // string is a strict PREFIX of the current estimate's digits is an animation-truncated
    // read, not money. It must change nothing: no row, no re-anchor.
    [Fact]
    public void PartialPrefixReadIsRejectedEntirely()
    {
        using var rig = NewRig();
        rig.Wallet.OnBalanceCaptured(5_101_948, U(13, 0, 0), U(13, 0, 1));
        rig.Wallet.OnBalanceCaptured(5_101, U(13, 10, 0), U(13, 10, 1));

        Assert.Empty(LoadUntracked(rig));
        Assert.Equal(5_101_948, rig.Wallet.Estimate); // the anchor did not move either
    }

    [Fact]
    public void AGenuineLargeDropStillReconciles()
    {
        using var rig = NewRig();
        rig.Wallet.OnBalanceCaptured(5_101_948, U(13, 0, 0), U(13, 0, 1));
        rig.Wallet.OnBalanceCaptured(4_999_000, U(13, 10, 0), U(13, 10, 1)); // not a digit prefix

        var entry = Assert.Single(LoadUntracked(rig));
        Assert.Equal(-102_948, entry.Amount);
        Assert.Equal(4_999_000, rig.Wallet.Estimate);
    }

    [Fact]
    public void ChannelsAreIsolated()
    {
        using var rig = NewRig();
        rig.Wallet.OnBalanceCaptured(1_000_000, U(13, 0, 0), U(13, 0, 1));

        rig.Channel = GameChannel.Ptu;
        Assert.False(rig.Wallet.HasAnchor);
        rig.Wallet.OnBalanceCaptured(77, U(13, 5, 0), U(13, 5, 1));

        rig.Channel = GameChannel.Live;
        Assert.Equal(1_000_000, rig.Wallet.Estimate);
        Assert.Empty(LoadUntracked(rig)); // the PTU first-anchor recorded nothing on LIVE either
    }

    [Fact]
    public void UntrackedRowsAreCappedOldestFirst()
    {
        using var rig = NewRig();
        rig.Wallet.OnBalanceCaptured(1_000_000, U(13, 0, 0), U(13, 0, 1));
        for (var i = 1; i <= WalletStore.UntrackedCap + 1; i++)
        {
            rig.Wallet.OnBalanceCaptured(1_000_000 + i, U(13, 0, 0).AddMinutes(10 + i),
                                         U(13, 0, 1).AddMinutes(10 + i));
        }

        var entries = LoadUntracked(rig);
        Assert.Equal(WalletStore.UntrackedCap, entries.Count);
        // The first reconciliation's row (at +11 min) was dropped; the second survives.
        Assert.Equal(U(13, 0, 1).AddMinutes(12), entries[0].Utc);
    }

    [Fact]
    public void StatePersistsIntoAFreshTracker()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexus-wallet-tracker-test-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);

        using (var rig = new Rig(dir))
        {
            rig.Wallet.OnBalanceCaptured(1_000_000, U(13, 0, 0), U(13, 0, 1));
            rig.Wallet.OnBalanceCaptured(1_080_000, U(13, 10, 0), U(13, 10, 1));
        }

        using var reloaded = new Rig(dir);
        Assert.True(reloaded.Wallet.HasAnchor);
        Assert.Equal(1_080_000, reloaded.Wallet.Estimate);
        Assert.Equal(80_000, Assert.Single(LoadUntracked(reloaded)).Amount);
    }

    [Fact]
    public void SessionUntrackedShowsOnlyRowsSinceTheLatchedSession()
    {
        using var rig = NewRig();
        rig.Wallet.OnBalanceCaptured(1_000_000, new DateTime(2026, 8, 5, 23, 0, 0, DateTimeKind.Utc),
                                     new DateTime(2026, 8, 5, 23, 0, 1, DateTimeKind.Utc));
        rig.Wallet.OnBalanceCaptured(1_000_100, new DateTime(2026, 8, 5, 23, 30, 0, DateTimeKind.Utc),
                                     new DateTime(2026, 8, 5, 23, 30, 1, DateTimeKind.Utc)); // pre-session row

        rig.LatchWallet(WalletLogFixtures.TriggerLine); // session first line: 2026-08-06T00:26:37.290Z
        rig.Wallet.OnBalanceCaptured(1_000_300, new DateTime(2026, 8, 6, 0, 30, 0, DateTimeKind.Utc),
                                     new DateTime(2026, 8, 6, 0, 30, 1, DateTimeKind.Utc));

        var visible = Assert.Single(rig.Wallet.SessionUntracked);
        Assert.Equal(200, visible.Amount);
        Assert.Equal(2, LoadUntracked(rig).Count); // both rows persist; display filters
    }

    [Fact]
    public void LogResetClearsTheSessionLatchAndKeepsTheAnchor()
    {
        using var rig = NewRig();
        rig.LatchWallet(WalletLogFixtures.TriggerLine);
        rig.Wallet.OnBalanceCaptured(1_000_000, U(13, 0, 0), U(13, 0, 1));
        Assert.NotNull(rig.Wallet.SessionStartUtc);

        rig.Wallet.Reset();

        Assert.Null(rig.Wallet.SessionStartUtc);
        Assert.True(rig.Wallet.HasAnchor);
        Assert.Empty(rig.Wallet.SessionUntracked);
    }

    [Fact]
    public void WalletActivityNeverTouchesProfitFigures()
    {
        using var rig = NewRig();
        rig.FeedProfit(CommodityLogFixtures.BuyLine);
        var netBefore = rig.Profit.Ledger.Net;

        rig.Wallet.OnBalanceCaptured(9_000_000, U(13, 40, 0), U(13, 40, 1));
        rig.Wallet.OnBalanceCaptured(1_000_000, U(13, 50, 0), U(13, 50, 1));
        rig.Wallet.SetManualBalance(123);

        Assert.Equal(netBefore, rig.Profit.Ledger.Net);
        Assert.Equal(1, rig.Profit.Ledger.UnvoidedCount);
    }

    [Fact]
    public void SavesCoalesceThroughTheScheduler()
    {
        using var rig = NewRig(immediateFlush: false);
        var changed = 0;
        rig.Wallet.Changed += () => changed++;

        rig.Wallet.OnBalanceCaptured(1_000_000, U(13, 0, 0), U(13, 0, 1));
        Assert.Single(rig.Posts);
        Assert.False(File.Exists(rig.WalletPath));

        rig.Posts[0]();
        Assert.True(File.Exists(rig.WalletPath));
        Assert.Equal(1, changed);
    }

    // Real notification shape (see WalletLogFixtures.ContractCompleteLine) with a test-chosen
    // stamp and name, so window arithmetic uses the same clock as the captures.
    private static string CompletionLine(DateTime utc, string name) =>
        $"<{utc:yyyy-MM-dd'T'HH:mm:ss.fff'Z'}> [Notice] <SHUDEvent_OnNotification> Added notification " +
        $"\"Contract Complete: {name}: \" [100] to queue. New queue size: 1, " +
        "MissionId: [a3c22670-0a01-4f20-87ce-6e0d3ac098b8], ObjectiveId: [] " +
        "[Team_CoreGameplayFeatures][Missions][Comms]";

    [Fact]
    public void IncomeAfterACompletionGetsTheContractName()
    {
        using var rig = NewRig();
        rig.Wallet.OnBalanceCaptured(1_000_000, U(13, 0, 0), U(13, 0, 1));
        rig.LatchWallet(CompletionLine(U(13, 5, 0), "Security Patrol"));
        rig.Wallet.OnBalanceCaptured(1_080_000, U(13, 10, 0), U(13, 10, 1));

        var entry = Assert.Single(LoadUntracked(rig));
        Assert.Equal(80_000, entry.Amount);
        Assert.Equal("Security Patrol", entry.Label);
    }

    // The window opens at the previous anchor: a completion the anchored balance already
    // contained must not explain later money.
    [Fact]
    public void ACompletionBeforeTheAnchorDoesNotLabel()
    {
        using var rig = NewRig();
        rig.LatchWallet(CompletionLine(U(12, 55, 0), "Security Patrol"));
        rig.Wallet.OnBalanceCaptured(1_000_000, U(13, 0, 0), U(13, 0, 1));
        rig.Wallet.OnBalanceCaptured(1_080_000, U(13, 10, 0), U(13, 10, 1));

        Assert.Null(Assert.Single(LoadUntracked(rig)).Label);
    }

    [Fact]
    public void SeveralCompletionsInTheWindowLabelTheCount()
    {
        using var rig = NewRig();
        rig.Wallet.OnBalanceCaptured(1_000_000, U(13, 0, 0), U(13, 0, 1));
        rig.LatchWallet(CompletionLine(U(13, 3, 0), "Cargo Haul Alpha"));
        rig.LatchWallet(CompletionLine(U(13, 7, 0), "Cargo Haul Beta"));
        rig.Wallet.OnBalanceCaptured(1_150_000, U(13, 10, 0), U(13, 10, 1));

        Assert.Equal("2 contracts completed", Assert.Single(LoadUntracked(rig)).Label);
    }

    // Completions pay in, never out: a spend beside a completion stays plain.
    [Fact]
    public void PurchasesNeverGetALabel()
    {
        using var rig = NewRig();
        rig.Wallet.OnBalanceCaptured(1_000_000, U(13, 0, 0), U(13, 0, 1));
        rig.LatchWallet(CompletionLine(U(13, 5, 0), "Security Patrol"));
        rig.Wallet.OnBalanceCaptured(920_000, U(13, 10, 0), U(13, 10, 1));

        Assert.Null(Assert.Single(LoadUntracked(rig)).Label);
    }

    // Each capture re-anchors, so the next window opens where this one closed: one completion
    // can never explain two rows.
    [Fact]
    public void AConsumedCompletionDoesNotLabelTheNextRow()
    {
        using var rig = NewRig();
        rig.Wallet.OnBalanceCaptured(1_000_000, U(13, 0, 0), U(13, 0, 1));
        rig.LatchWallet(CompletionLine(U(13, 5, 0), "Security Patrol"));
        rig.Wallet.OnBalanceCaptured(1_080_000, U(13, 10, 0), U(13, 10, 1));
        rig.Wallet.OnBalanceCaptured(1_090_000, U(13, 20, 0), U(13, 20, 1));

        var entries = LoadUntracked(rig);
        Assert.Equal(2, entries.Count);
        Assert.Equal("Security Patrol", entries[0].Label);
        Assert.Null(entries[1].Label);
    }

    private static IReadOnlyList<NexusApp.Models.UntrackedEntry> LoadUntracked(Rig rig)
    {
        // Read through the store so assertions see exactly what persists (flushes are immediate
        // in these rigs unless a test opts out).
        var state = WalletStore.Load(rig.WalletPath, out _);
        if (state is null) return Array.Empty<NexusApp.Models.UntrackedEntry>();
        var ch = state.Channels.SingleOrDefault(c => c.Channel == GameChannel.Live);
        return ch?.Untracked ?? new List<NexusApp.Models.UntrackedEntry>();
    }
}
