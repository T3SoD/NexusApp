using NexusApp.Models;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Headless: lines straight into Ingest, no watcher tick, no real Game.log. Every tracker writes
// its profit_history.json into an isolated temp directory. Line fixtures are the shared
// CommodityLogFixtures shapes; BuyInWindow re-stamps the buy to sit 137 ms before the error
// response, matching the pairing observed in the source log.
public class ProfitTrackerTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    public void Dispose()
    {
        foreach (var d in _tempDirs)
        {
            try { if (Directory.Exists(d)) Directory.Delete(d, recursive: true); }
            catch { /* best effort */ }
        }
    }

    // A log's opening line: no commodity content, just the first-line stamp the session key latches.
    private const string OpeningLine =
        "<2026-07-04T13:00:00.000Z> [Notice] <SessionStart> Loading environment [Team_Core][Game]";
    private const string OpeningLineNextDay =
        "<2026-07-05T09:00:00.000Z> [Notice] <SessionStart> Loading environment [Team_Core][Game]";

    private const long BuyAmount = 182_560;
    private const long SellAmount = 1_978_794;

    private static readonly string BuyInWindow =
        CommodityLogFixtures.BuyLine.Replace("13:35:36.565", "13:36:12.141");

    private static string Join(string shard) =>
        $"<2026-07-04T13:14:51.882Z> [Notice] <Join PU> address[10.0.0.1] port[64318] shard[{shard}] locationId[1] [x]";

    private const string EndSession =
        "<2026-07-04T13:30:51.596Z> [Notice] <CDisciplineServiceExternal::EndSession> Ending session [AntiCheat][EAC]";

    // Paths handed to OnFeedStarted only; never opened. Shaped like real installs so the channel
    // reads naturally in the test, but the tracker compares them as opaque strings.
    private const string LivePath = @"C:\Games\StarCitizen\LIVE\Game.log";
    private const string PtuPath = @"C:\Games\StarCitizen\PTU\Game.log";

    private ProfitTracker Tracker(out string historyPath, Func<GameChannel>? channel = null,
                                  Action<Action>? flushScheduler = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexus-profit-tracker-test-" + Path.GetRandomFileName());
        _tempDirs.Add(dir);
        historyPath = Path.Combine(dir, "profit_history.json");
        return new ProfitTracker(historyPath: historyPath, channel: channel ?? (() => GameChannel.Live),
                                 flushScheduler: flushScheduler);
    }

    private static void Feed(ProfitTracker t, params string[] lines)
    {
        foreach (var raw in lines) t.Ingest(new GameLogEntry { Raw = raw, Category = LogCategory.Other });
    }

    [Fact]
    public void Replay_SameLogTwice_ChangesNothing()
    {
        var t = Tracker(out _);
        int changed = 0;
        t.Changed += () => changed++;

        Feed(t, OpeningLine, CommodityLogFixtures.BuyLine, CommodityLogFixtures.SellLine);
        var firedOnce = changed;
        Assert.Equal(SellAmount - BuyAmount, t.Ledger.Net);

        // App restart mid-session: the whole current log replays from the top.
        Feed(t, OpeningLine, CommodityLogFixtures.BuyLine, CommodityLogFixtures.SellLine);

        Assert.Equal(SellAmount - BuyAmount, t.Ledger.Net);
        Assert.Equal(2, t.Ledger.Transactions.Count);
        Assert.Equal(firedOnce, changed);   // deduped lines raise nothing
        var live = t.History.Channels.Find(c => c.Channel == GameChannel.Live);
        Assert.Single(live!.Entries);
        Assert.Equal(SellAmount - BuyAmount, ProfitHistory.AllTimeNet(t.History, GameChannel.Live));
    }

    [Fact]
    public void LogReset_RollsSession_OldEntryStaysInHistory()
    {
        var t = Tracker(out _);
        Feed(t, OpeningLine, CommodityLogFixtures.BuyLine, CommodityLogFixtures.SellLine);

        t.Reset();   // what the feed's onLogReset drives on a game restart

        Assert.Empty(t.Ledger.Transactions);
        Assert.Equal(0, t.Ledger.Net);
        var live = t.History.Channels.Find(c => c.Channel == GameChannel.Live);
        var oldEntry = Assert.Single(live!.Entries);
        Assert.Equal(SellAmount - BuyAmount, oldEntry.Net);

        // The new log replays into its own session under its own key.
        Feed(t, OpeningLineNextDay, CommodityLogFixtures.SellLine);

        Assert.Equal(2, live.Entries.Count);
        Assert.NotEqual(live.Entries[0].SessionKey, live.Entries[1].SessionKey);
        Assert.Equal(SellAmount, t.Ledger.Net);
        Assert.Equal((SellAmount - BuyAmount) + SellAmount, ProfitHistory.AllTimeNet(t.History, GameChannel.Live));
    }

    [Fact]
    public void PtuSession_NeverMovesLiveHistory()
    {
        var t = Tracker(out _, channel: () => GameChannel.Ptu);
        Feed(t, OpeningLine, CommodityLogFixtures.SellLine);

        Assert.Null(t.History.Channels.Find(c => c.Channel == GameChannel.Live));
        Assert.Equal(0L, ProfitHistory.AllTimeNet(t.History, GameChannel.Live));
        Assert.Equal(SellAmount, ProfitHistory.AllTimeNet(t.History, GameChannel.Ptu));
        Assert.StartsWith("Ptu|", t.History.Channels[0].Entries[0].SessionKey);
    }

    // Deliberate divergence from HaulTracker: a shard change abandons contracts, not money already
    // settled at a kiosk. The ledger follows the log file, nothing else.
    [Fact]
    public void ShardChangeAndExit_DoNotResetTheSession()
    {
        var t = Tracker(out _);
        Feed(t, OpeningLine, CommodityLogFixtures.BuyLine);

        Feed(t, Join("pub_use1b_12030094_140"), EndSession, Join("pub_use1b_12030094_150"));

        Assert.Single(t.Ledger.Transactions);
        Assert.Equal(-BuyAmount, t.Ledger.Net);
    }

    [Fact]
    public void History_RoundTripsThroughTrackerRestart_ReplayOverwritesNotDuplicates()
    {
        var t1 = Tracker(out var path);
        Feed(t1, OpeningLine, CommodityLogFixtures.BuyLine, CommodityLogFixtures.SellLine);
        t1.Dispose();

        var t2 = new ProfitTracker(historyPath: path, channel: () => GameChannel.Live);
        Assert.Equal(SellAmount - BuyAmount, ProfitHistory.AllTimeNet(t2.History, GameChannel.Live));

        // The restart replays the same log: same key, overwrite, never a duplicate.
        Feed(t2, OpeningLine, CommodityLogFixtures.BuyLine, CommodityLogFixtures.SellLine);
        Assert.Equal(1, ProfitHistory.AllTimeSessionCount(t2.History, GameChannel.Live));
        Assert.Equal(SellAmount - BuyAmount, ProfitHistory.AllTimeNet(t2.History, GameChannel.Live));
        t2.Dispose();
    }

    // Crash-safety: the persisted summary is rewritten on the apply AND on the void, and a session
    // voided back to zero transactions retracts its entry (an all-zero trend row is noise).
    [Fact]
    public void VoidedTransaction_RewritesThePersistedSummary()
    {
        var t = Tracker(out var path);
        Feed(t, OpeningLine, BuyInWindow);

        var onDisk = ProfitHistoryStore.Load(path, out _);
        var entry = Assert.Single(onDisk!.Channels.Find(c => c.Channel == GameChannel.Live)!.Entries);
        Assert.Equal(-BuyAmount, entry.Net);

        Feed(t, CommodityLogFixtures.ErrorLine);   // 137 ms later: voids the buy

        Assert.Equal("InsufficentFunds", t.Ledger.Transactions[0].Voided);
        Assert.Equal(0, t.Ledger.Net);
        onDisk = ProfitHistoryStore.Load(path, out _);
        var live = onDisk!.Channels.Find(c => c.Channel == GameChannel.Live);
        Assert.Empty(live!.Entries);
    }

    // The advanced monitor points the ONE tail at another channel's file, replay routed to itself
    // alone (willReplayToMe false). Live lines appended to the new file still fan out to everyone,
    // so the tracker must roll: the old file's session key must not absorb the new file's money.
    [Fact]
    public void TargetedRePoint_ToAnotherChannelsFile_RollsIntoNewChannelSession()
    {
        var channel = GameChannel.Live;
        var t = Tracker(out _, channel: () => channel);
        t.OnFeedStarted(LivePath, willReplayToMe: true);   // startup point of the tail
        Feed(t, OpeningLine, CommodityLogFixtures.BuyLine, CommodityLogFixtures.SellLine);

        channel = GameChannel.Ptu;
        t.OnFeedStarted(PtuPath, willReplayToMe: false);

        Assert.Empty(t.Ledger.Transactions);
        Feed(t, OpeningLineNextDay, CommodityLogFixtures.SellLine);   // live lines from the new file

        Assert.Equal(SellAmount, t.Ledger.Net);
        var ptu = t.History.Channels.Find(c => c.Channel == GameChannel.Ptu);
        var ptuEntry = Assert.Single(ptu!.Entries);
        Assert.StartsWith("Ptu|", ptuEntry.SessionKey);
        Assert.Equal(SellAmount, ptuEntry.Net);
        var live = t.History.Channels.Find(c => c.Channel == GameChannel.Live);
        var liveEntry = Assert.Single(live!.Entries);
        Assert.Equal(SellAmount - BuyAmount, liveEntry.Net);   // LIVE history untouched
        Assert.Equal(SellAmount - BuyAmount, ProfitHistory.AllTimeNet(t.History, GameChannel.Live));
    }

    // The monitor's "From start of file" on the file already tailed: same file, so the session must
    // stay whole. Rolling here would split it in two entries, and the next full replay would rebuild
    // the first entry with the whole session while the split-off half double counted the all-time.
    [Fact]
    public void TargetedFromTopReRead_SameFile_DoesNotRollTheSession()
    {
        var t = Tracker(out _);
        t.OnFeedStarted(LivePath, willReplayToMe: true);
        Feed(t, OpeningLine, CommodityLogFixtures.BuyLine);

        t.OnFeedStarted(LivePath, willReplayToMe: false);

        Feed(t, CommodityLogFixtures.SellLine);
        Assert.Equal(2, t.Ledger.Transactions.Count);
        Assert.Equal(SellAmount - BuyAmount, t.Ledger.Net);
        var live = t.History.Channels.Find(c => c.Channel == GameChannel.Live);
        Assert.Single(live!.Entries);
    }

    // Startup replays the whole current log in one watcher batch; save and Changed coalesce to one
    // each per batch instead of one per settlement. The scheduler seam stands in for the dispatcher:
    // the tracker posts the flush once, the "dispatcher" runs it once.
    [Fact]
    public void ReplayBurst_OneSaveAndOneChangedForTheWholeBatch()
    {
        var posts = new List<Action>();
        var t = Tracker(out var path, flushScheduler: posts.Add);
        int changed = 0;
        t.Changed += () => changed++;

        // One batch: a buy, a sell, and the error that voids the buy - three flush triggers.
        Feed(t, OpeningLine, BuyInWindow, CommodityLogFixtures.SellLine, CommodityLogFixtures.ErrorLine);

        Assert.Single(posts);              // coalesced: posted once, not once per settlement
        Assert.Equal(0, changed);          // nothing raised until the batch flushes
        Assert.False(File.Exists(path));   // and nothing saved

        posts[0]();   // the dispatcher drains

        Assert.Equal(1, changed);
        var onDisk = ProfitHistoryStore.Load(path, out _);
        var entry = Assert.Single(onDisk!.Channels.Find(c => c.Channel == GameChannel.Live)!.Entries);
        Assert.Equal(SellAmount, entry.Net);     // the void landed in the same save
        Assert.Equal(1, entry.TxCount);
        Assert.Equal(t.Ledger.Net, entry.Net);   // reload equals the live state (crash safety)

        // Replaying the same batch dedupes at the ledger: no new post, no new save, no raise.
        Feed(t, OpeningLine, BuyInWindow, CommodityLogFixtures.SellLine, CommodityLogFixtures.ErrorLine);
        Assert.Single(posts);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void Dispose_FlushesThePendingBatchSynchronously()
    {
        var posts = new List<Action>();
        var t = Tracker(out var path, flushScheduler: posts.Add);
        int changed = 0;
        t.Changed += () => changed++;
        Feed(t, OpeningLine, CommodityLogFixtures.BuyLine, CommodityLogFixtures.SellLine);
        Assert.False(File.Exists(path));

        t.Dispose();   // the queued flush never ran; Dispose must not lose the batch

        Assert.Equal(1, changed);
        var onDisk = ProfitHistoryStore.Load(path, out _);
        Assert.Equal(SellAmount - BuyAmount, ProfitHistory.AllTimeNet(onDisk!, GameChannel.Live));

        foreach (var p in posts) p();   // the stale post finds nothing pending
        Assert.Equal(1, changed);
    }

    // A session roll with a batch still queued: the batch belongs to the outgoing session and must
    // land under the outgoing key before the ledger resets.
    [Fact]
    public void SessionRoll_FlushesThePendingBatchUnderTheOutgoingKey()
    {
        var posts = new List<Action>();
        var channel = GameChannel.Live;
        var t = Tracker(out var path, channel: () => channel, flushScheduler: posts.Add);
        t.OnFeedStarted(LivePath, willReplayToMe: true);
        Feed(t, OpeningLine, CommodityLogFixtures.SellLine);
        Assert.False(File.Exists(path));

        channel = GameChannel.Ptu;
        t.OnFeedStarted(PtuPath, willReplayToMe: false);   // roll: the pending batch flushes first

        var onDisk = ProfitHistoryStore.Load(path, out _);
        var live = onDisk!.Channels.Find(c => c.Channel == GameChannel.Live);
        var entry = Assert.Single(live!.Entries);
        Assert.Equal(SellAmount, entry.Net);
        Assert.StartsWith("Live|", entry.SessionKey);
    }

    [Fact]
    public void Ingest_FeedsShopPurchasesWithoutTouchingTradingTotals()
    {
        using var t = Tracker(out _);
        Feed(t, ShopPurchaseParserTests.BuyLine, ShopPurchaseParserTests.SuccessResponseLine);

        Assert.Single(t.Purchases.Purchases);
        Assert.Null(t.Purchases.Purchases[0].Refused);
        Assert.Equal(0, t.Ledger.Bought);   // a shop purchase is not a trade
        Assert.Equal(0, t.Ledger.Sold);
        Assert.Equal(0, t.Ledger.Net);
    }

    [Fact]
    public void Ingest_DedupesShopPurchasesOnReplay()
    {
        using var t = Tracker(out _);
        Feed(t, ShopPurchaseParserTests.BuyLine, ShopPurchaseParserTests.BuyLine);
        Assert.Single(t.Purchases.Purchases);
    }

    // Finding 1: a shop purchase must repaint the ledger exactly like a commodity trade does, or a
    // player buying at a kiosk with the profit panel open sees nothing until an unrelated event
    // triggers a redraw.
    [Fact]
    public void Ingest_ShopPurchase_RaisesChangedLikeACommodityTrade()
    {
        var t = Tracker(out _);
        int changed = 0;
        t.Changed += () => changed++;

        Feed(t, ShopPurchaseParserTests.BuyLine);

        Assert.Equal(1, changed);
    }

    // A replayed duplicate line changes nothing in the ledger and must not repaint again, or a
    // startup replay of a whole log would redraw once per line instead of once per batch.
    [Fact]
    public void Ingest_ReplayedShopPurchase_DoesNotRaiseChangedAgain()
    {
        var t = Tracker(out _);
        int changed = 0;
        t.Changed += () => changed++;

        Feed(t, ShopPurchaseParserTests.BuyLine);
        Feed(t, ShopPurchaseParserTests.BuyLine);

        Assert.Equal(1, changed);
    }

    // A shop flow result with no matching request is an orphan (dropped, ApplyResult returns
    // false) and must not repaint either.
    [Fact]
    public void Ingest_OrphanShopResult_DoesNotRaiseChanged()
    {
        var t = Tracker(out _);
        int changed = 0;
        t.Changed += () => changed++;

        Feed(t, ShopPurchaseParserTests.SuccessResponseLine);

        Assert.Equal(0, changed);
    }

    // A Success result changes Refused on nothing (the row stays settled) but IS a real, non-
    // replayed apply (ApplyResult returns true), so it still must repaint - the refusal badge and
    // the "answered" state are new information even when the amount does not move.
    [Fact]
    public void Ingest_ShopResultThatConsumesARequest_RaisesChanged()
    {
        var t = Tracker(out _);
        Feed(t, ShopPurchaseParserTests.BuyLine);
        int changed = 0;
        t.Changed += () => changed++;

        Feed(t, ShopPurchaseParserTests.SuccessResponseLine);

        Assert.Equal(1, changed);
    }

    // Finding 3(a): the display name resolves ONCE, right here at apply time, rather than on every
    // render. BuyLineQuantityTwo carries a GUID/token pair the shipped catalog actually knows (see
    // ItemNameCatalogTests.ResolvePurchaseName_ResolvesAKnownGuid).
    [Fact]
    public void Ingest_ShopPurchase_ResolvesAndStampsTheDisplayNameOnce()
    {
        var t = Tracker(out _);
        Feed(t, ShopPurchaseParserTests.BuyLineQuantityTwo);

        var p = Assert.Single(t.Purchases.Purchases);
        Assert.Equal("'Chaos' III Missile", p.DisplayName);
    }

    // A token the catalog does not know leaves DisplayName null rather than the shop-name fallback
    // text: WalletDisplay.PurchaseTitle owns the fallback, so ProfitTracker stores only what the
    // catalog itself resolved (or nothing).
    [Fact]
    public void Ingest_ShopPurchase_UnknownTokenLeavesDisplayNameNull()
    {
        var line = ShopPurchaseParserTests.BuyLine
            .Replace("crlf_consumable_healing_01", "zzz_unit_test_unknown_token_ptx")
            .Replace("7d50411f-088c-4c99-b85a-a6eaf95504c3", "00000000-0000-0000-0000-000000000000");
        var t = Tracker(out _);

        Feed(t, line);

        var p = Assert.Single(t.Purchases.Purchases);
        Assert.Null(p.DisplayName);
    }

    // A replayed duplicate line must not re-run catalog resolution: Purchases.Apply rejects it
    // before ApplyPurchase ever touches ItemNameCatalog, so the original DisplayName survives.
    [Fact]
    public void Ingest_ReplayedShopPurchase_DoesNotReResolveTheDisplayName()
    {
        var t = Tracker(out _);
        Feed(t, ShopPurchaseParserTests.BuyLineQuantityTwo, ShopPurchaseParserTests.BuyLineQuantityTwo);

        var p = Assert.Single(t.Purchases.Purchases);
        Assert.Equal("'Chaos' III Missile", p.DisplayName);
    }

    // The captured ShoppingProvider pair is from a 2026-04-12 session; this file's fixture day is
    // 2026-07-04. Restamping preserves the real 0.541 s request-to-response gap.
    private static string OnFixtureDay(string line) =>
        line.Replace("2026-04-12T03:02:4", "2026-07-04T13:02:4")
            .Replace("2026-08-08T12:06:02.424Z", "2026-07-04T13:06:02.424Z");

    [Fact]
    public void ShoppingProviderBuyAndItsResponseBecomeOneSettledRow()
    {
        var t = Tracker(out _);
        Feed(t, OpeningLine,
             OnFixtureDay(ShopPurchaseParserTests.ShoppingBuyLine),
             OnFixtureDay(ShopPurchaseParserTests.ShoppingResponseLine));

        var row = Assert.Single(t.Purchases.Purchases);
        Assert.Equal("Drink_bottle_vestal_01_a", row.ItemToken);
        Assert.Equal(4L, row.Price);
        Assert.Equal(ShopProvider.Shopping, row.Provider);
        Assert.Null(row.Refused);
    }

    [Fact]
    public void ShoppingProviderBuyNeverTouchesTradingProfit()
    {
        var t = Tracker(out _);
        Feed(t, OpeningLine, OnFixtureDay(ShopPurchaseParserTests.ShoppingBuyShipLine));

        // A 342,720 aUEC ship is not a trade. SessionLedger must not move.
        Assert.Equal(0L, t.Ledger.Bought);
        Assert.Equal(0L, t.Ledger.Sold);
        Assert.Equal(0L, t.Ledger.Net);
        Assert.Single(t.Purchases.Purchases);
    }
}
