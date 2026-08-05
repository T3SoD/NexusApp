using NexusApp.Models;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// The pure history rules: entry upsert keyed by SessionKey (channel + log first-line stamp),
// channel isolation, the cap folding pruned nets into the base, and the derived all-time figure
// that never moves when a prune folds.
public class ProfitHistoryTests
{
    private static readonly DateTime T0 = new(2026, 7, 1, 8, 0, 0, DateTimeKind.Utc);

    private static SessionSummary Sum(string key, long net, int txCount,
        GameChannel channel = GameChannel.Live, DateTime? start = null) => new()
    {
        SessionKey = key, Channel = channel,
        StartUtc = start ?? T0, LastSeenUtc = start ?? T0,
        Bought = net < 0 ? -net : 0, Sold = net > 0 ? net : 0,
        Net = net, TxCount = txCount,
    };

    [Fact]
    public void Upsert_NewSession_AddsEntry()
    {
        var state = new ProfitHistoryState();
        ProfitHistory.Upsert(state, Sum("s1", 1000, 2));
        var ch = Assert.Single(state.Channels);
        var entry = Assert.Single(ch.Entries);
        Assert.Equal("s1", entry.SessionKey);
        Assert.Equal(1000L, ProfitHistory.AllTimeNet(state, GameChannel.Live));
        Assert.Equal(1, ProfitHistory.AllTimeSessionCount(state, GameChannel.Live));
    }

    // The current session's summary is rewritten on every transaction; a later replay of the same
    // log lands on the same key and overwrites rather than duplicates.
    [Fact]
    public void Upsert_SameKey_Overwrites_NeverDuplicates()
    {
        var state = new ProfitHistoryState();
        ProfitHistory.Upsert(state, Sum("s1", 1000, 2));
        ProfitHistory.Upsert(state, Sum("s1", 1500, 3));
        var entry = Assert.Single(state.Channels[0].Entries);
        Assert.Equal(1500L, entry.Net);
        Assert.Equal(1500L, ProfitHistory.AllTimeNet(state, GameChannel.Live));
    }

    [Fact]
    public void Upsert_ZeroTransactionSession_WritesNothing()
    {
        var state = new ProfitHistoryState();
        ProfitHistory.Upsert(state, Sum("s1", 0, 0));
        Assert.Empty(state.Channels);
        Assert.Equal(0L, ProfitHistory.AllTimeNet(state, GameChannel.Live));
    }

    // A session whose only settlements were all voided back to zero retracts the entry an earlier
    // rewrite left behind, keeping "zero-transaction sessions write no entry" true under replay.
    [Fact]
    public void Upsert_ZeroTxCount_RetractsExistingEntry()
    {
        var state = new ProfitHistoryState();
        ProfitHistory.Upsert(state, Sum("s1", 1000, 1));
        ProfitHistory.Upsert(state, Sum("s1", 0, 0));
        Assert.Empty(state.Channels[0].Entries);
        Assert.Equal(0L, ProfitHistory.AllTimeNet(state, GameChannel.Live));
    }

    [Fact]
    public void ChannelIsolation_PtuSessionNeverMovesLiveTrend()
    {
        var state = new ProfitHistoryState();
        ProfitHistory.Upsert(state, Sum("live1", 1000, 2, GameChannel.Live));
        ProfitHistory.Upsert(state, Sum("ptu1", 999_999, 5, GameChannel.Ptu));
        Assert.Equal(1000L, ProfitHistory.AllTimeNet(state, GameChannel.Live));
        Assert.Equal(1, ProfitHistory.AllTimeSessionCount(state, GameChannel.Live));
        Assert.Equal(999_999L, ProfitHistory.AllTimeNet(state, GameChannel.Ptu));
        var live = state.Channels.Find(c => c.Channel == GameChannel.Live);
        Assert.Single(live!.Entries);
    }

    [Fact]
    public void Cap_PrunesOldest_FoldsNetAndCountIntoBase()
    {
        var state = new ProfitHistoryState();
        ProfitHistory.Upsert(state, Sum("s1", 1, 1, start: T0), cap: 3);
        ProfitHistory.Upsert(state, Sum("s2", 2, 1, start: T0.AddDays(1)), cap: 3);
        ProfitHistory.Upsert(state, Sum("s3", 4, 1, start: T0.AddDays(2)), cap: 3);
        ProfitHistory.Upsert(state, Sum("s4", 8, 1, start: T0.AddDays(3)), cap: 3);

        var ch = state.Channels[0];
        Assert.Equal(3, ch.Entries.Count);
        Assert.DoesNotContain(ch.Entries, e => e.SessionKey == "s1");
        Assert.Equal(1L, ch.PrunedNet);
        Assert.Equal(1, ch.PrunedCount);
        Assert.Equal(15L, ProfitHistory.AllTimeNet(state, GameChannel.Live));
        Assert.Equal(4, ProfitHistory.AllTimeSessionCount(state, GameChannel.Live));
    }

    [Fact]
    public void AllTime_DoesNotMoveWhenPruneFolds()
    {
        var state = new ProfitHistoryState();
        ProfitHistory.Upsert(state, Sum("s1", 1, 1, start: T0), cap: 3);
        ProfitHistory.Upsert(state, Sum("s2", 2, 1, start: T0.AddDays(1)), cap: 3);
        ProfitHistory.Upsert(state, Sum("s3", 4, 1, start: T0.AddDays(2)), cap: 3);
        var before = ProfitHistory.AllTimeNet(state, GameChannel.Live);

        ProfitHistory.Upsert(state, Sum("s4", 8, 1, start: T0.AddDays(3)), cap: 3);

        // The insert added exactly its own net; the prune that folded s1 moved nothing.
        Assert.Equal(before + 8, ProfitHistory.AllTimeNet(state, GameChannel.Live));
    }

    // Replay overwrites entries and never touches the base, so all-time stays base plus retained.
    [Fact]
    public void AllTime_EqualsBasePlusRetained_UnderReplay()
    {
        var state = new ProfitHistoryState();
        ProfitHistory.Upsert(state, Sum("s1", 1, 1, start: T0), cap: 2);
        ProfitHistory.Upsert(state, Sum("s2", 2, 1, start: T0.AddDays(1)), cap: 2);
        ProfitHistory.Upsert(state, Sum("s3", 4, 1, start: T0.AddDays(2)), cap: 2);

        ProfitHistory.Upsert(state, Sum("s3", 4, 1, start: T0.AddDays(2)), cap: 2);

        var ch = state.Channels[0];
        Assert.Equal(2, ch.Entries.Count);
        Assert.Equal(1L, ch.PrunedNet);
        Assert.Equal(7L, ProfitHistory.AllTimeNet(state, GameChannel.Live));
        Assert.Equal(3, ProfitHistory.AllTimeSessionCount(state, GameChannel.Live));
    }

    // Replaying a log whose session the cap already folded into the base must not re-enter it:
    // the re-added entry would fold into the base a second time on the next prune.
    [Fact]
    public void Upsert_ReplayOfPrunedSession_RefusedStateUnchanged()
    {
        var state = new ProfitHistoryState();
        ProfitHistory.Upsert(state, Sum("s1", 1, 1, start: T0), cap: 2);
        ProfitHistory.Upsert(state, Sum("s2", 2, 1, start: T0.AddDays(1)), cap: 2);
        ProfitHistory.Upsert(state, Sum("s3", 4, 1, start: T0.AddDays(2)), cap: 2);   // prunes s1

        ProfitHistory.Upsert(state, Sum("s1", 1, 1, start: T0), cap: 2);   // replay of the pruned log

        var ch = state.Channels[0];
        Assert.Equal(2, ch.Entries.Count);
        Assert.DoesNotContain(ch.Entries, e => e.SessionKey == "s1");
        Assert.Equal(1L, ch.PrunedNet);
        Assert.Equal(1, ch.PrunedCount);
        Assert.Equal(7L, ProfitHistory.AllTimeNet(state, GameChannel.Live));
        Assert.Equal(3, ProfitHistory.AllTimeSessionCount(state, GameChannel.Live));
    }

    // At the cap the current session's summary is rewritten per settlement; an ancient replayed
    // log must not fold a fresh copy of itself into the base on every one of those rewrites.
    [Fact]
    public void Upsert_AtCap_AncientReplayNeverCompoundsBase()
    {
        var state = new ProfitHistoryState();
        ProfitHistory.Upsert(state, Sum("s1", 1, 1, start: T0), cap: 2);
        ProfitHistory.Upsert(state, Sum("s2", 2, 1, start: T0.AddDays(1)), cap: 2);
        ProfitHistory.Upsert(state, Sum("s3", 4, 1, start: T0.AddDays(2)), cap: 2);   // folds s1

        for (var settlement = 1; settlement <= 3; settlement++)
            ProfitHistory.Upsert(state, Sum("ancient", 100 * settlement, settlement,
                start: T0.AddDays(-30)), cap: 2);

        var ch = state.Channels[0];
        Assert.Equal(1L, ch.PrunedNet);
        Assert.Equal(1, ch.PrunedCount);
        Assert.Equal(T0, ch.FirstSessionUtc);   // refused means unchanged, FirstSessionUtc included
        Assert.Equal(7L, ProfitHistory.AllTimeNet(state, GameChannel.Live));
        Assert.Equal(3, ProfitHistory.AllTimeSessionCount(state, GameChannel.Live));
    }

    // When the entry being upserted is itself the oldest at the cap, the prune folds the oldest
    // OTHER entry; folding the live session would refuse its own next per-settlement rewrite.
    [Fact]
    public void Cap_NeverEvictsTheSessionBeingUpserted()
    {
        var state = new ProfitHistoryState();
        ProfitHistory.Upsert(state, Sum("s2", 2, 1, start: T0.AddDays(1)), cap: 2);
        ProfitHistory.Upsert(state, Sum("s3", 4, 1, start: T0.AddDays(2)), cap: 2);
        ProfitHistory.Upsert(state, Sum("s1", 1, 1, start: T0), cap: 2);   // older than both, no prune yet

        var ch = state.Channels[0];
        Assert.Contains(ch.Entries, e => e.SessionKey == "s1");
        Assert.DoesNotContain(ch.Entries, e => e.SessionKey == "s2");   // oldest OTHER entry folded
        Assert.Equal(2L, ch.PrunedNet);
        Assert.Equal(1, ch.PrunedCount);
        Assert.Equal(7L, ProfitHistory.AllTimeNet(state, GameChannel.Live));
    }

    // The watermark refuses only keys that are not retained; a retained entry older than the
    // watermark still rewrites in place.
    [Fact]
    public void Upsert_RetainedEntryOlderThanWatermark_StillRewrites()
    {
        var state = new ProfitHistoryState();
        ProfitHistory.Upsert(state, Sum("s2", 2, 1, start: T0.AddDays(1)), cap: 2);
        ProfitHistory.Upsert(state, Sum("s3", 4, 1, start: T0.AddDays(2)), cap: 2);
        ProfitHistory.Upsert(state, Sum("s1", 1, 1, start: T0), cap: 2);   // folds s2; s1 predates the watermark

        ProfitHistory.Upsert(state, Sum("s1", 10, 2, start: T0), cap: 2);   // next settlement's rewrite

        var ch = state.Channels[0];
        var s1 = ch.Entries.Find(e => e.SessionKey == "s1");
        Assert.Equal(10L, s1!.Net);
        Assert.Equal(16L, ProfitHistory.AllTimeNet(state, GameChannel.Live));
    }

    [Fact]
    public void FirstSessionUtc_SurvivesPrune()
    {
        var state = new ProfitHistoryState();
        ProfitHistory.Upsert(state, Sum("s1", 1, 1, start: T0), cap: 2);
        ProfitHistory.Upsert(state, Sum("s2", 2, 1, start: T0.AddDays(1)), cap: 2);
        ProfitHistory.Upsert(state, Sum("s3", 4, 1, start: T0.AddDays(2)), cap: 2);
        Assert.Equal(T0, state.Channels[0].FirstSessionUtc);
    }

    [Fact]
    public void MakeKey_CombinesChannelAndFirstLineStamp()
    {
        var stamp = new DateTime(2026, 7, 4, 12, 0, 0, DateTimeKind.Utc);
        var key = SessionSummary.MakeKey(GameChannel.Ptu, stamp);
        Assert.Equal(key, SessionSummary.MakeKey(GameChannel.Ptu, stamp));
        Assert.NotEqual(key, SessionSummary.MakeKey(GameChannel.Live, stamp));
        Assert.NotEqual(key, SessionSummary.MakeKey(GameChannel.Ptu, stamp.AddSeconds(1)));
    }
}
