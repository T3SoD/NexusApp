using NexusApp.Models;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

public class ShardTrackerTests
{
    private static GameLogEntry E(string raw) => new() { Raw = raw, Category = LogCategory.Other };
    private static string Join(string shard, string ip = "10.0.0.1") =>
        $"<2026-06-27T13:14:51.882Z> [Notice] <Join PU> address[{ip}] port[64318] shard[{shard}] locationId[1] [x]";

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
    public void Load_RestoresPersistedHistory()
    {
        var persisted = new List<ShardSession> { new() { ShardId = "pub_euw1b_1_99", Instance = "99" } };
        var t = new ShardTracker(() => persisted, _ => { });
        Assert.Equal("pub_euw1b_1_99", t.Current!.ShardId);
    }

    [Fact]
    public void NonJoinLine_Ignored()
    {
        var t = new ShardTracker(() => new List<ShardSession>(), _ => { });
        t.Ingest(E("<2026-06-27T13:14:51.882Z> [Notice] <Something> foo"));
        Assert.Null(t.Current);
    }
}
