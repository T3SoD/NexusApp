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
        => Assert.Null(GameChannelProbe.SelectActive(new List<GameChannelProbe.LogStat>(), @"X:\LIVE\Game.log", Now));

    [Fact]
    public void SelectActive_NewestCreationWins()
    {
        var stats = new List<GameChannelProbe.LogStat>
        {
            S(@"X:\SC\LIVE\Game.log", createdMinAgo: 120, wroteMinAgo: 60),
            S(@"X:\SC\HOTFIX\Game.log", createdMinAgo: 5, wroteMinAgo: 5),   // just launched
        };
        Assert.Equal(@"X:\SC\HOTFIX\Game.log", GameChannelProbe.SelectActive(stats, @"X:\SC\LIVE\Game.log", Now));
    }

    [Fact]
    public void SelectActive_ActivelyWrittenCurrent_NeverAbandoned()
    {
        var stats = new List<GameChannelProbe.LogStat>
        {
            new(@"X:\SC\LIVE\Game.log", Now.AddHours(-2), Now.AddSeconds(-3)),   // being written NOW
            S(@"X:\SC\PTU\Game.log", createdMinAgo: 1, wroteMinAgo: 1),          // fresher creation
        };
        Assert.Equal(@"X:\SC\LIVE\Game.log", GameChannelProbe.SelectActive(stats, @"X:\SC\LIVE\Game.log", Now));
    }

    [Fact]
    public void SelectActive_FullTie_CurrentWins()
    {
        var a = S(@"X:\SC\LIVE\Game.log", 60, 60);
        var b = S(@"X:\SC\PTU\Game.log", 60, 60);
        Assert.Equal(@"X:\SC\PTU\Game.log",
            GameChannelProbe.SelectActive(new[] { a, b }, @"X:\SC\PTU\Game.log", Now));
    }

    [Fact]
    public void SelectActive_CreationTie_LastWriteBreaksIt()
    {
        var a = S(@"X:\SC\LIVE\Game.log", 60, 50);
        var b = S(@"X:\SC\PTU\Game.log", 60, 10);   // same creation, written more recently
        Assert.Equal(@"X:\SC\PTU\Game.log",
            GameChannelProbe.SelectActive(new[] { a, b }, @"X:\SC\LIVE\Game.log", Now));
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
            GameChannelProbe.SelectActive(stats, @"x:\sc\live\GAME.LOG", Now));
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
