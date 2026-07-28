using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// The shared Game.log tail's two decisions, exercised headless (the watcher itself is
// DispatcherTimer-bound, so only the pure pieces are covered here):
//   1. GameLogFanOut - who gets which line, and whether a (re)start should rewind at all.
//   2. GameLogWatcher.ReadSize - how far a tick may read, so a chunk never straddles the
//      boundary between replayed history and live lines.
public class GameLogFeedTests
{
    private static GameLogEntry Line(string raw, bool replay = false) =>
        new() { Raw = raw, Category = LogCategory.Other, IsReplay = replay };

    // ── fan-out ───────────────────────────────────────────────────────────────

    [Fact]
    public void LiveLine_ReachesEverySubscriber()
    {
        var fan = new GameLogFanOut();
        var live = new List<string>();
        var replaying = new List<string>();
        fan.Add(e => live.Add(e.Raw), includeReplay: false);
        fan.Add(e => replaying.Add(e.Raw), includeReplay: true);

        fan.Line(Line("a"));

        Assert.Equal(new[] { "a" }, live);
        Assert.Equal(new[] { "a" }, replaying);
    }

    [Fact]
    public void ReplayedLine_ReachesOnlyTheSubscribersThatAskedForIt()
    {
        var fan = new GameLogFanOut();
        var session = new List<string>();   // the blueprint session: live only
        var hauls = new List<string>();     // hauls + shards: rebuild from the whole log
        fan.Add(e => session.Add(e.Raw), includeReplay: false);
        fan.Add(e => hauls.Add(e.Raw), includeReplay: true);

        fan.Line(Line("history", replay: true));
        fan.Line(Line("now"));

        Assert.Equal(new[] { "now" }, session);
        Assert.Equal(new[] { "history", "now" }, hauls);
    }

    [Fact]
    public void AnyWantsReplay_IsFalseWithoutAReplayConsumer()
    {
        var fan = new GameLogFanOut();
        Assert.False(fan.AnyWantsReplay);

        var sub = fan.Add(_ => { }, includeReplay: false);
        Assert.False(fan.AnyWantsReplay);   // nothing to replay TO: a start must not rewind

        sub.IncludeReplay = true;
        Assert.True(fan.AnyWantsReplay);    // the monitor's "From start of file"
    }

    [Fact]
    public void AnyWantsReplay_FollowsTheReplayConsumerDetaching()
    {
        var fan = new GameLogFanOut();
        fan.Add(_ => { }, includeReplay: false);
        var hauls = fan.Add(_ => { }, includeReplay: true);
        Assert.True(fan.AnyWantsReplay);

        hauls.Dispose();

        Assert.False(fan.AnyWantsReplay);
        Assert.Equal(1, fan.Count);
    }

    [Fact]
    public void DisposedSubscription_StopsReceiving_WhileTheRestKeepFeeding()
    {
        // The advanced monitor's Stop detaches ONE consumer; the tail keeps feeding the others.
        var fan = new GameLogFanOut();
        var stopped = new List<string>();
        var kept = new List<string>();
        var sub = fan.Add(e => stopped.Add(e.Raw), includeReplay: false);
        fan.Add(e => kept.Add(e.Raw), includeReplay: true);

        fan.Line(Line("before"));
        sub.Dispose();
        fan.Line(Line("after"));

        Assert.Equal(new[] { "before" }, stopped);
        Assert.Equal(new[] { "before", "after" }, kept);
    }

    [Fact]
    public void DisposingTwice_IsHarmless()
    {
        var fan = new GameLogFanOut();
        var sub = fan.Add(_ => { }, includeReplay: false);
        sub.Dispose();
        sub.Dispose();
        Assert.Equal(0, fan.Count);
    }

    [Fact]
    public void SubscriberDetachingItselfMidLine_DoesNotDisturbTheOthers()
    {
        // Stop() is reachable from a line handler (a status/state cascade), so dispatch walks a
        // snapshot: every subscriber present when the line arrived still gets it.
        var fan = new GameLogFanOut();
        var seen = new List<string>();
        GameLogSubscription? self = null;
        self = fan.Add(_ => self!.Dispose(), includeReplay: true);
        fan.Add(e => seen.Add(e.Raw), includeReplay: true);

        fan.Line(Line("a"));
        fan.Line(Line("b"));

        Assert.Equal(new[] { "a", "b" }, seen);
        Assert.Equal(1, fan.Count);
    }

    [Fact]
    public void ResetAndStatus_ReachOnlyAttachedSubscribers()
    {
        var fan = new GameLogFanOut();
        int resets = 0;
        var status = new List<string>();
        var sub = fan.Add(_ => { }, includeReplay: false, onLogReset: () => resets++, onStatus: status.Add);
        fan.Add(_ => { }, includeReplay: true);   // no reset/status callbacks - must not throw

        fan.LogReset();
        fan.Status("Watching");
        sub.Dispose();
        fan.LogReset();
        fan.Status("later");

        Assert.Equal(1, resets);
        Assert.Equal(new[] { "Watching" }, status);
    }

    // ── replay boundary (the per-tick read decision) ───────────────────────────

    [Fact]
    public void ReadSize_StopsAtTheReplayBoundary()
    {
        // Replay window is [0, 100): a tick that could read 500 bytes stops at 100 so the chunk
        // is entirely history and every line in it is flagged as replay.
        Assert.Equal(100, GameLogWatcher.ReadSize(position: 0, length: 500, replayEnd: 100, maxPerTick: 1000));
        Assert.Equal(40, GameLogWatcher.ReadSize(position: 60, length: 500, replayEnd: 100, maxPerTick: 1000));
    }

    [Fact]
    public void ReadSize_PastTheBoundary_ReadsLiveBytesFreely()
    {
        Assert.Equal(400, GameLogWatcher.ReadSize(position: 100, length: 500, replayEnd: 100, maxPerTick: 1000));
        Assert.Equal(500, GameLogWatcher.ReadSize(position: 0, length: 500, replayEnd: 0, maxPerTick: 1000));
    }

    [Fact]
    public void ReadSize_HonorsThePerTickCap_InsideAndOutsideTheReplayWindow()
    {
        Assert.Equal(64, GameLogWatcher.ReadSize(position: 0, length: 10_000, replayEnd: 5_000, maxPerTick: 64));
        Assert.Equal(64, GameLogWatcher.ReadSize(position: 6_000, length: 10_000, replayEnd: 5_000, maxPerTick: 64));
    }

    [Fact]
    public void ReadSize_IsZeroWithNothingNew()
    {
        Assert.Equal(0, GameLogWatcher.ReadSize(position: 500, length: 500, replayEnd: 100, maxPerTick: 1000));
        Assert.Equal(0, GameLogWatcher.ReadSize(position: 500, length: 400, replayEnd: 0, maxPerTick: 1000));
    }

    // ── one tail, several consumers (no file: the path never exists, so nothing is read) ──

    private static string MissingLogPath() =>
        Path.Combine(Path.GetTempPath(), "nexus_feed_" + Guid.NewGuid().ToString("N"), "Game.log");

    private static GameLogSession Session(GameLogFeed feed) =>
        new(() => new[] { "Bracket Cooler" }, _ => false, (_, _) => { }, null, feed);

    [Fact]
    public void StoppingTheBlueprintSession_LeavesTheSharedTailRunningForTheOtherConsumers()
    {
        using var feed = new GameLogFeed { PreferredPath = MissingLogPath() };
        using var hauls = new HaulTracker(feed);
        using var session = Session(feed);

        session.SetAutoMark(true);
        Assert.True(session.IsRunning);
        Assert.True(feed.IsRunning);

        session.Stop();

        Assert.False(session.IsRunning);   // this consumer detached
        Assert.True(feed.IsRunning);       // hauls (and shards) keep being fed
    }

    [Fact]
    public void RestartingTheSessionOnTheSamePath_DoesNotRewindTheTailUnderTheOtherConsumers()
    {
        var path = MissingLogPath();
        using var feed = new GameLogFeed { PreferredPath = path };
        using var hauls = new HaulTracker(feed);
        int starts = 0;
        feed.Started += (_, _) => starts++;
        using var session = Session(feed);

        session.SetAutoMark(true);                      // starts the one tail
        Assert.Equal(1, starts);

        session.Stop();
        session.Start(path, fromBeginning: false);      // plain re-attach: no rewind
        Assert.Equal(1, starts);

        session.Start(path, fromBeginning: true);       // deliberate from-the-top re-read
        Assert.Equal(2, starts);

        session.Start(MissingLogPath(), fromBeginning: false);   // different file: must re-point
        Assert.Equal(3, starts);
    }
}
