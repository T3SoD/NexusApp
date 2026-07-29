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
    public void TargetedReplay_ReachesOnlyThatSubscriber_WhileLiveLinesStillReachEveryone()
    {
        // The advanced monitor's "From start of file": the blueprint session re-reads the log, the
        // haul and shard trackers must NOT re-process history they already have - but they must keep
        // getting live lines the moment the tail passes the replay boundary.
        var fan = new GameLogFanOut();
        var session = new List<string>();
        var hauls = new List<string>();
        var sessionSub = fan.Add(e => session.Add(e.Raw), includeReplay: true);
        fan.Add(e => hauls.Add(e.Raw), includeReplay: true);

        fan.Started("Game.log", fromBeginning: true, target: sessionSub);
        fan.Line(Line("history", replay: true));
        fan.Line(Line("now"));

        Assert.Equal(new[] { "history", "now" }, session);
        Assert.Equal(new[] { "now" }, hauls);
    }

    [Fact]
    public void UntargetedReplay_GoesBackToEveryConsumerThatWantsIt()
    {
        // A Game.log path change: the new file is news to everyone.
        var fan = new GameLogFanOut();
        var session = new List<string>();
        var hauls = new List<string>();
        var sessionSub = fan.Add(e => session.Add(e.Raw), includeReplay: true);
        fan.Add(e => hauls.Add(e.Raw), includeReplay: true);

        fan.Started("Game.log", fromBeginning: true, target: sessionSub);
        fan.Started("Other.log", fromBeginning: true, target: null);
        fan.Line(Line("history", replay: true));

        Assert.Equal(new[] { "history" }, session);
        Assert.Equal(new[] { "history" }, hauls);
    }

    [Fact]
    public void Started_TellsEachConsumerWhetherTheReplayIsComingItsWay()
    {
        var fan = new GameLogFanOut();
        bool? toSession = null, toHauls = null;
        var sessionSub = fan.Add(_ => { }, includeReplay: true, onStarted: (_, r) => toSession = r);
        fan.Add(_ => { }, includeReplay: true, onStarted: (_, r) => toHauls = r);

        fan.Started("Game.log", fromBeginning: true, target: sessionSub);

        Assert.True(toSession);
        Assert.False(toHauls);   // a targeted replay must not read as a from-the-top start here
    }

    [Fact]
    public void Started_ReportsNoReplay_WhenTheTailDidNotRewind()
    {
        var fan = new GameLogFanOut();
        bool? sawReplay = null;
        fan.Add(_ => { }, includeReplay: true, onStarted: (_, r) => sawReplay = r);

        fan.Started("Game.log", fromBeginning: false, target: null);

        Assert.False(sawReplay);
    }

    [Fact]
    public void ReplayTargetDetachingMidWindow_DoesNotDumpTheRestOnTheOtherConsumers()
    {
        // Stop pressed part-way through a from-the-top re-read: the remaining history belongs to
        // nobody. Handing the tail end of it to the trackers would be a partial replay - worse than
        // none. Live lines keep flowing.
        var fan = new GameLogFanOut();
        var hauls = new List<string>();
        var sessionSub = fan.Add(_ => { }, includeReplay: true);
        fan.Add(e => hauls.Add(e.Raw), includeReplay: true);
        fan.Started("Game.log", fromBeginning: true, target: sessionSub);

        sessionSub.Dispose();
        fan.Line(Line("history", replay: true));
        fan.Line(Line("now"));

        Assert.Equal(new[] { "now" }, hauls);
    }

    [Fact]
    public void LogReset_ClearsTheReplayTargetLatch()
    {
        // A new SC session ends any routing: its lines are live and belong to every consumer.
        var fan = new GameLogFanOut();
        var hauls = new List<string>();
        var sessionSub = fan.Add(_ => { }, includeReplay: true);
        fan.Add(e => hauls.Add(e.Raw), includeReplay: true);
        fan.Started("Game.log", fromBeginning: true, target: sessionSub);

        fan.LogReset();
        fan.Line(Line("history", replay: true));

        Assert.Equal(new[] { "history" }, hauls);
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
    public void FeedStart_RewindsWhenAReplayConsumerIsAttached()
    {
        // Guards the App startup order: hauls and shards must be attached BEFORE the tail starts,
        // or their from-the-top replay silently never happens.
        var path = MissingLogPath();
        using var feed = new GameLogFeed { PreferredPath = path };
        bool? replayed = null;
        feed.Subscribe(_ => { }, includeReplay: true, onStarted: (_, r) => replayed = r);

        feed.Start(path);

        Assert.True(replayed);
    }

    [Fact]
    public void FeedStart_DoesNotRewindWhenOnlyLiveConsumersAreAttached()
    {
        var path = MissingLogPath();
        using var feed = new GameLogFeed { PreferredPath = path };
        bool? replayed = null;
        feed.Subscribe(_ => { }, includeReplay: false, onStarted: (_, r) => replayed = r);

        feed.Start(path);

        Assert.False(replayed);   // nothing to replay TO: the tail starts at the end of the file
    }

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
        feed.Started += _ => starts++;
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

    // ── auto-follow: channel bookkeeping and start-path selection (issue #28) ─────────────

    [Fact]
    public void Start_UpdatesActiveChannel_AndRaisesChangeOnce()
    {
        using var feed = new GameLogFeed();
        var changes = new List<GameChannel>();
        feed.ChannelChanged += c => changes.Add(c);

        feed.Start(@"X:\SC\HOTFIX\Game.log");
        feed.Start(@"X:\SC\HOTFIX\Game.log");   // same channel: no second event
        feed.Start(@"X:\SC\PTU\Game.log");

        Assert.Equal(GameChannel.Ptu, feed.ActiveChannel);
        Assert.Equal(new[] { GameChannel.Hotfix, GameChannel.Ptu }, changes);
    }

    [Fact]
    public void StartPath_PicksFreshestSiblingChannel()
    {
        var root = Directory.CreateTempSubdirectory("nexus-feed-test").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "LIVE"));
            Directory.CreateDirectory(Path.Combine(root, "HOTFIX"));
            var live = Path.Combine(root, "LIVE", "Game.log");
            var hotfix = Path.Combine(root, "HOTFIX", "Game.log");
            File.WriteAllText(live, "x");
            File.WriteAllText(hotfix, "x");
            // Make LIVE decisively stale, HOTFIX the last-launched channel.
            File.SetCreationTimeUtc(live, DateTime.UtcNow.AddDays(-2));
            File.SetLastWriteTimeUtc(live, DateTime.UtcNow.AddDays(-2));
            File.SetCreationTimeUtc(hotfix, DateTime.UtcNow.AddMinutes(-1));
            File.SetLastWriteTimeUtc(hotfix, DateTime.UtcNow.AddMinutes(-1));

            using var feed = new GameLogFeed { PreferredPath = live };
            Assert.Equal(hotfix, feed.StartPath());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void StartPath_CustomLayout_KeepsPreferredPath()
    {
        using var feed = new GameLogFeed { PreferredPath = @"X:\NotAChannel\Game.log" };
        Assert.Equal(@"X:\NotAChannel\Game.log", feed.StartPath());
    }
}
