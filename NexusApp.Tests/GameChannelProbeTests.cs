using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

public class GameChannelProbeTests
{
    private static readonly DateTime Now = new(2026, 7, 29, 12, 0, 0, DateTimeKind.Utc);
    private static GameChannelProbe.LogStat S(string path, int createdMinAgo, int wroteMinAgo) =>
        new(path, Now.AddMinutes(-createdMinAgo), Now.AddMinutes(-wroteMinAgo));

    [Fact]
    public void SelectActive_EmptyList_ReturnsNull()
        => Assert.Null(GameChannelProbe.SelectActive(
            new List<GameChannelProbe.LogStat>(), @"X:\LIVE\Game.log", Now, sessionLive: false));

    // ── Star Citizen closed: pure newest-last-write, current wins ties ────────────────────

    [Fact]
    public void SelectActive_GameClosed_NewestLastWriteWins()
    {
        var stats = new List<GameChannelProbe.LogStat>
        {
            S(@"X:\SC\LIVE\Game.log", createdMinAgo: 120, wroteMinAgo: 60),
            S(@"X:\SC\HOTFIX\Game.log", createdMinAgo: 5, wroteMinAgo: 5),   // last channel played
        };
        Assert.Equal(@"X:\SC\HOTFIX\Game.log",
            GameChannelProbe.SelectActive(stats, @"X:\SC\LIVE\Game.log", Now, sessionLive: false));
    }

    // The documented copy-LIVE-to-PTU habit: an Explorer copy preserves the source's last-write
    // time but stamps a brand new creation time. Ranking by creation would abandon the real log,
    // so the copy must NOT win.
    [Fact]
    public void SelectActive_GameClosed_CopiedLog_NewerCreationSameLastWrite_DoesNotWin()
    {
        var stats = new List<GameChannelProbe.LogStat>
        {
            S(@"X:\SC\LIVE\Game.log", createdMinAgo: 180, wroteMinAgo: 30),
            S(@"X:\SC\PTU\Game.log", createdMinAgo: 2, wroteMinAgo: 30),     // the copy
        };
        Assert.Equal(@"X:\SC\LIVE\Game.log",
            GameChannelProbe.SelectActive(stats, @"X:\SC\LIVE\Game.log", Now, sessionLive: false));
    }

    [Fact]
    public void SelectActive_GameClosed_LastWriteTieBetweenNonCurrent_NewerCreationBreaksIt()
    {
        var stats = new List<GameChannelProbe.LogStat>
        {
            S(@"X:\SC\HOTFIX\Game.log", createdMinAgo: 90, wroteMinAgo: 20),
            S(@"X:\SC\PTU\Game.log", createdMinAgo: 30, wroteMinAgo: 20),
        };
        Assert.Equal(@"X:\SC\PTU\Game.log",
            GameChannelProbe.SelectActive(stats, @"X:\SC\LIVE\Game.log", Now, sessionLive: false));
    }

    // ── Star Citizen running ─────────────────────────────────────────────────────────────

    [Fact]
    public void SelectActive_Running_ActivelyWrittenCurrent_NeverAbandoned()
    {
        var stats = new List<GameChannelProbe.LogStat>
        {
            new(@"X:\SC\LIVE\Game.log", Now.AddHours(-2), Now.AddSeconds(-3)),   // being written NOW
            S(@"X:\SC\PTU\Game.log", createdMinAgo: 1, wroteMinAgo: 1),          // fresher stats
        };
        Assert.Equal(@"X:\SC\LIVE\Game.log",
            GameChannelProbe.SelectActive(stats, @"X:\SC\LIVE\Game.log", Now, sessionLive: true));
    }

    [Fact]
    public void SelectActive_Running_RivalNewerCreationButNotNewerLastWrite_NoSwitch()
    {
        var stats = new List<GameChannelProbe.LogStat>
        {
            S(@"X:\SC\LIVE\Game.log", createdMinAgo: 240, wroteMinAgo: 40),   // outside the active window
            S(@"X:\SC\PTU\Game.log", createdMinAgo: 1, wroteMinAgo: 40),      // copied in just now
        };
        Assert.Equal(@"X:\SC\LIVE\Game.log",
            GameChannelProbe.SelectActive(stats, @"X:\SC\LIVE\Game.log", Now, sessionLive: true));
    }

    [Fact]
    public void SelectActive_Running_RivalStrictlyNewerLastWrite_Switches()
    {
        var stats = new List<GameChannelProbe.LogStat>
        {
            S(@"X:\SC\LIVE\Game.log", createdMinAgo: 240, wroteMinAgo: 40),
            S(@"X:\SC\PTU\Game.log", createdMinAgo: 240, wroteMinAgo: 1),   // the player relaunched on PTU
        };
        Assert.Equal(@"X:\SC\PTU\Game.log",
            GameChannelProbe.SelectActive(stats, @"X:\SC\LIVE\Game.log", Now, sessionLive: true));
    }

    // Recovery: the watched path is gone (channel uninstalled while the path stayed persisted), so
    // the lone survivor wins even though the current file cannot be compared against it.
    [Fact]
    public void SelectActive_DeadCurrentPath_SingleSurvivor_Recovers()
    {
        var stats = new List<GameChannelProbe.LogStat> { S(@"X:\SC\LIVE\Game.log", 300, 200) };
        Assert.Equal(@"X:\SC\LIVE\Game.log",
            GameChannelProbe.SelectActive(stats, @"X:\SC\PTU\Game.log", Now, sessionLive: true));
        Assert.Equal(@"X:\SC\LIVE\Game.log",
            GameChannelProbe.SelectActive(stats, @"X:\SC\PTU\Game.log", Now, sessionLive: false));
    }

    [Fact]
    public void SelectActive_IntactSingleChannel_SelectsItself()
    {
        var stats = new List<GameChannelProbe.LogStat> { S(@"X:\SC\LIVE\Game.log", 300, 200) };
        Assert.Equal(@"X:\SC\LIVE\Game.log",
            GameChannelProbe.SelectActive(stats, @"X:\SC\LIVE\Game.log", Now, sessionLive: false));
    }

    [Fact]
    public void SelectActive_CurrentPathComparison_IsCaseInsensitive()
    {
        var stats = new List<GameChannelProbe.LogStat>
        {
            new(@"X:\SC\LIVE\Game.log", Now.AddHours(-2), Now.AddSeconds(-3)),
            S(@"X:\SC\PTU\Game.log", 1, 1),
        };
        Assert.Equal(@"X:\SC\LIVE\Game.log",
            GameChannelProbe.SelectActive(stats, @"x:\sc\live\GAME.LOG", Now, sessionLive: true));
    }

    [Fact]
    public void RootFrom_KnownChannel_ReturnsParent_CustomReturnsNull()
    {
        Assert.Equal(@"D:\Games\StarCitizen", GameChannelProbe.RootFrom(@"D:\Games\StarCitizen\PTU\Game.log"));
        Assert.Null(GameChannelProbe.RootFrom(@"D:\Games\Odd\Game.log"));
        Assert.Null(GameChannelProbe.RootFrom(""));
    }

    [Fact]
    public void Candidates_EnumeratesExistingSiblings_EmptyForCustom()
    {
        var root = Directory.CreateTempSubdirectory("nexus-probe-test").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "LIVE"));
            Directory.CreateDirectory(Path.Combine(root, "HOTFIX"));
            Directory.CreateDirectory(Path.Combine(root, "PTU"));            // folder without Game.log
            File.WriteAllText(Path.Combine(root, "LIVE", "Game.log"), "x");
            File.WriteAllText(Path.Combine(root, "HOTFIX", "Game.log"), "x");

            var fromLive = GameChannelProbe.Candidates(Path.Combine(root, "LIVE", "Game.log"));
            Assert.Equal(2, fromLive.Count);                                  // LIVE + HOTFIX, not PTU
            Assert.Contains(Path.Combine(root, "HOTFIX", "Game.log"), fromLive);

            Assert.Empty(GameChannelProbe.Candidates(Path.Combine(root, "Odd", "Game.log")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
