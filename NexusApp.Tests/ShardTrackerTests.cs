using NexusApp.Models;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

public class ShardTrackerTests
{
    private static GameLogEntry E(string raw) => new() { Raw = raw, Category = LogCategory.Other };
    private static string Join(string shard, string ip = "10.0.0.1") =>
        $"<2026-06-27T13:14:51.882Z> [Notice] <Join PU> address[{ip}] port[64318] shard[{shard}] locationId[1] [x]";
    private static string Leave() =>
        "<2026-06-27T14:30:51.596Z> [Notice] <CDisciplineServiceExternal::EndSession> Ending session [AntiCheat][EAC]";
    private static string LeaveSystemQuit() =>
        "<2026-07-03T03:16:19.651Z> [Notice] <SystemQuit> CSystem::Quit invoked with - cause=30016, reason=User closed the application, exitCode=0, thread id=17232";

    [Fact]
    public void FirstJoin_SetsCurrent_RecentEmpty()
    {
        var t = new ShardTracker(() => new List<ShardSession>(), _ => { });
        t.Ingest(E(Join("pub_use1b_12030094_140")));
        Assert.Equal("pub_use1b_12030094_140", t.Current!.ShardId);
        Assert.Empty(t.Recent);
    }

    [Fact]
    public void SecondDistinctJoin_PushesPriorToRecent()
    {
        var t = new ShardTracker(() => new List<ShardSession>(), _ => { });
        t.Ingest(E(Join("pub_use1b_12030094_140")));
        t.Ingest(E(Join("pub_use1b_12030094_150")));
        Assert.Equal("pub_use1b_12030094_150", t.Current!.ShardId);
        Assert.Single(t.Recent);
        Assert.Equal("pub_use1b_12030094_140", t.Recent[0].ShardId);
    }

    [Fact]
    public void RejoinSameShard_NoChange()
    {
        int changed = 0;
        var t = new ShardTracker(() => new List<ShardSession>(), _ => { });
        t.Changed += () => changed++;
        t.Ingest(E(Join("pub_use1b_12030094_140")));
        t.Ingest(E(Join("pub_use1b_12030094_140")));   // same shard, e.g. reconnect
        Assert.Single(t.All);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void HistoryCappedAtFour_CurrentPlusThree()
    {
        var t = new ShardTracker(() => new List<ShardSession>(), _ => { });
        foreach (var n in new[] { "010", "020", "030", "040", "050" })
            t.Ingest(E(Join($"pub_use1b_12030094_{n}")));
        Assert.Equal(4, t.All.Count);
        Assert.Equal("pub_use1b_12030094_050", t.Current!.ShardId);
        Assert.Equal(3, t.Recent.Count);
        Assert.DoesNotContain(t.All, s => s.Instance == "010");
    }

    [Fact]
    public void Save_InvokedWithHistory_OnEachNewShard()
    {
        IReadOnlyList<ShardSession>? lastSaved = null;
        var t = new ShardTracker(() => new List<ShardSession>(), list => lastSaved = list);
        t.Ingest(E(Join("pub_use1b_12030094_140")));
        Assert.NotNull(lastSaved);
        Assert.Equal("pub_use1b_12030094_140", lastSaved![0].ShardId);
    }

    [Fact]
    public void Load_PersistedHistoryIsRecent_NotCurrentUntilJoin()
    {
        var persisted = new List<ShardSession> { new() { ShardId = "pub_euw1b_1_99", Instance = "99" } };
        var t = new ShardTracker(() => persisted, _ => { });
        Assert.Null(t.Current);                          // not on a shard until a join is seen
        Assert.Equal("pub_euw1b_1_99", t.Recent[0].ShardId);
    }

    [Fact]
    public void Leave_ClearsCurrent_AndMovesItToRecent()
    {
        var t = new ShardTracker(() => new List<ShardSession>(), _ => { });
        t.Ingest(E(Join("pub_use1b_12030094_140")));
        t.Ingest(E(Leave()));
        Assert.Null(t.Current);                                       // no longer on a shard
        Assert.False(t.OnShard);
        Assert.Equal("pub_use1b_12030094_140", t.Recent[0].ShardId);  // the one just left is now recent
    }

    [Fact]
    public void JoinNewShardAfterLeave_SetsCurrent()
    {
        var t = new ShardTracker(() => new List<ShardSession>(), _ => { });
        t.Ingest(E(Join("pub_use1b_12030094_140")));
        t.Ingest(E(Leave()));
        t.Ingest(E(Join("pub_use1b_12030094_150")));
        Assert.Equal("pub_use1b_12030094_150", t.Current!.ShardId);
        Assert.Equal("pub_use1b_12030094_140", t.Recent[0].ShardId);
    }

    [Fact]
    public void RejoinSameShardAfterLeave_RestoresCurrent_NoDuplicate()
    {
        var t = new ShardTracker(() => new List<ShardSession>(), _ => { });
        t.Ingest(E(Join("pub_use1b_12030094_140")));
        t.Ingest(E(Leave()));
        Assert.Null(t.Current);
        t.Ingest(E(Join("pub_use1b_12030094_140")));   // back on the same shard
        Assert.Equal("pub_use1b_12030094_140", t.Current!.ShardId);
        Assert.Single(t.All);                          // not duplicated
    }

    [Fact]
    public void Leave_WhenAlreadyOff_DoesNothing()
    {
        int changed = 0;
        var t = new ShardTracker(() => new List<ShardSession>(), _ => { });
        t.Changed += () => changed++;
        t.Ingest(E(Leave()));          // never joined; already off
        Assert.Equal(0, changed);
        Assert.Null(t.Current);
    }

    [Fact]
    public void NonJoinLine_Ignored()
    {
        var t = new ShardTracker(() => new List<ShardSession>(), _ => { });
        t.Ingest(E("<2026-06-27T13:14:51.882Z> [Notice] <Something> foo"));
        Assert.Null(t.Current);
    }

    // SC 4.8 stopped writing the EAC EndSession line; <SystemQuit> is the leave marker now.
    [Fact]
    public void SystemQuit_ClearsCurrent_AndMovesItToRecent()
    {
        var t = new ShardTracker(() => new List<ShardSession>(), _ => { });
        t.Ingest(E(Join("pub_use1b_12030094_140")));
        t.Ingest(E(LeaveSystemQuit()));
        Assert.Null(t.Current);
        Assert.False(t.OnShard);
        Assert.Equal("pub_use1b_12030094_140", t.Recent[0].ShardId);
    }

    // Replaying a cold leftover log must record history but never claim the player is on a shard.
    [Fact]
    public void StaleReplay_Join_GoesToHistory_NotCurrent()
    {
        var t = new ShardTracker(() => new List<ShardSession>(), _ => { });
        t.BeginStaleReplay();
        t.Ingest(E(Join("pub_use1b_12030094_140")));
        Assert.Null(t.Current);
        Assert.False(t.OnShard);
        Assert.Equal("pub_use1b_12030094_140", t.Recent[0].ShardId);
    }

    [Fact]
    public void StaleReplay_RejoinPersistedShard_StaysOff()
    {
        var persisted = new List<ShardSession> { new() { ShardId = "pub_euw1b_1_99", Instance = "99" } };
        var t = new ShardTracker(() => persisted, _ => { });
        t.BeginStaleReplay();
        t.Ingest(E(Join("pub_euw1b_1_99")));   // same shard as persisted history head
        Assert.Null(t.Current);
        Assert.Single(t.All);
    }
}
