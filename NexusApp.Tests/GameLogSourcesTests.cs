using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Issue #48 follow-up, from a real report: exporting 7-10 Aug returned only 9 Aug.
//
// Star Citizen writes ONLY the current session to LIVE\Game.log and files each finished session
// under LIVE\logbackups\ as its own "Game Build(...) 07 Aug 26 (20 54 39).log". The first version
// read the single active file, so no range could reach a past session. Verified against a real
// install: Game.log held one evening while logbackups held twelve earlier sessions.
public class GameLogSourcesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"gls_{Guid.NewGuid():N}");
    private readonly string _live;
    private readonly string _backups;

    public GameLogSourcesTests()
    {
        _live = Path.Combine(_root, "LIVE");
        _backups = Path.Combine(_live, GameLogSources.BackupFolder);
        Directory.CreateDirectory(_backups);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string WriteLog(string name, DateTime writtenUtc, params string[] lines)
    {
        var path = name == "Game.log" ? Path.Combine(_live, name) : Path.Combine(_backups, name);
        File.WriteAllLines(path, lines);
        File.SetLastWriteTimeUtc(path, writtenUtc);
        return path;
    }

    private string ActivePath => Path.Combine(_live, "Game.log");

    private static string Line(string day, string body = "event") =>
        $"<2026-08-{day}T12:00:00.000Z> {body}";

    [Fact]
    public void Discover_FindsTheActiveLogAndEveryBackup()
    {
        WriteLog("Game.log", new DateTime(2026, 8, 9, 20, 0, 0, DateTimeKind.Utc), Line("09"));
        WriteLog("Game Build(1) 07 Aug 26 (20 54 39).log", new DateTime(2026, 8, 7, 21, 0, 0, DateTimeKind.Utc), Line("07"));
        WriteLog("Game Build(1) 08 Aug 26 (21 13 17).log", new DateTime(2026, 8, 8, 22, 0, 0, DateTimeKind.Utc), Line("08"));

        var found = GameLogSources.Discover(ActivePath, null);

        Assert.Equal(3, found.Count);
    }

    // Oldest first, so the exported file reads forwards in time across sessions.
    [Fact]
    public void Discover_OrdersOldestFirst()
    {
        WriteLog("Game.log", new DateTime(2026, 8, 9, 20, 0, 0, DateTimeKind.Utc), Line("09"));
        WriteLog("Game Build(1) 07 Aug 26 (20 54 39).log", new DateTime(2026, 8, 7, 21, 0, 0, DateTimeKind.Utc), Line("07"));
        WriteLog("Game Build(1) 08 Aug 26 (21 13 17).log", new DateTime(2026, 8, 8, 22, 0, 0, DateTimeKind.Utc), Line("08"));

        var names = GameLogSources.Discover(ActivePath, null).Select(Path.GetFileName).ToList();

        Assert.Equal("Game Build(1) 07 Aug 26 (20 54 39).log", names[0]);
        Assert.Equal("Game Build(1) 08 Aug 26 (21 13 17).log", names[1]);
        Assert.Equal("Game.log", names[2]);
    }

    // The game only ever appends, so a file whose last write predates the range cannot hold a line
    // in it. Skipping those unopened keeps a wide range from reading sessions it would discard.
    [Fact]
    public void Discover_SkipsFilesWrittenEntirelyBeforeTheRange()
    {
        WriteLog("Game.log", new DateTime(2026, 8, 9, 20, 0, 0, DateTimeKind.Utc), Line("09"));
        WriteLog("Game Build(1) 01 Aug 26 (10 00 00).log", new DateTime(2026, 8, 1, 11, 0, 0, DateTimeKind.Utc), Line("01"));

        var found = GameLogSources.Discover(ActivePath, new DateTime(2026, 8, 8, 0, 0, 0, DateTimeKind.Utc));

        Assert.Single(found);
        Assert.Equal("Game.log", Path.GetFileName(found[0]));
    }

    [Fact]
    public void Discover_WithNoBackupFolder_StillReturnsTheActiveLog()
    {
        Directory.Delete(_backups);
        WriteLog("Game.log", DateTime.UtcNow, Line("09"));
        Assert.Single(GameLogSources.Discover(ActivePath, null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Discover_WithNoPathAtAll_FindsNothing(string? path)
        => Assert.Empty(GameLogSources.Discover(path, null));

    [Fact]
    public void Discover_IgnoresAMissingActiveLogButStillReturnsBackups()
    {
        WriteLog("Game Build(1) 07 Aug 26 (20 54 39).log", new DateTime(2026, 8, 7, 21, 0, 0, DateTimeKind.Utc), Line("07"));
        var found = GameLogSources.Discover(ActivePath, null);
        Assert.Single(found);
    }

    // The game holds the active log open for writing; a plain read throws exactly when a live
    // session is what the user wants to report on.
    [Fact]
    public void Read_OpensAFileTheGameIsStillWritingTo()
    {
        var path = WriteLog("Game.log", DateTime.UtcNow, Line("09", "one"), Line("09", "two"));
        using var holder = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);

        var source = GameLogSources.Read(path);

        Assert.Equal("Game.log", source.Name);
        Assert.Equal(2, source.Lines.Count);
    }

    // The full path runs through the Windows user folder, which can be a real name.
    [Fact]
    public void Read_NamesTheFileWithoutItsPath()
    {
        var path = WriteLog("Game Build(1) 07 Aug 26 (20 54 39).log", DateTime.UtcNow, Line("07"));
        Assert.Equal("Game Build(1) 07 Aug 26 (20 54 39).log", GameLogSources.Read(path).Name);
    }

    // ---- the export across several sessions ---------------------------------------------------

    private static GameLogSource Src(string name, params string[] lines) => new(name, lines);

    [Fact]
    public void Build_AcrossSessions_KeepsEveryFileWithLinesInRange()
    {
        var sources = new[]
        {
            Src("07 Aug.log", Line("07", "seven")),
            Src("08 Aug.log", Line("08", "eight")),
            Src("Game.log",   Line("09", "nine")),
        };

        var r = GameLogExport.Build(sources, null, null, false, null, null, "v", DateTime.UtcNow);

        Assert.Equal(3, r.LinesKept);
        Assert.Equal(3, r.FilesWithContent);
        Assert.Contains("seven", r.Text);
        Assert.Contains("eight", r.Text);
        Assert.Contains("nine", r.Text);
    }

    // The exact reported symptom, now covered: a four-day range must reach the older sessions.
    [Fact]
    public void Build_AWideRange_ReachesPastSessionsNotJustTheLiveOne()
    {
        var sources = new[]
        {
            Src("07 Aug.log", Line("07", "seven")),
            Src("08 Aug.log", Line("08", "eight")),
            Src("Game.log",   Line("09", "nine")),
        };
        var from = new DateTime(2026, 8, 7, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 8, 10, 23, 59, 0, DateTimeKind.Utc);

        var r = GameLogExport.Build(sources, from, to, false, null, null, "v", DateTime.UtcNow);

        Assert.Equal(3, r.LinesKept);
        Assert.Contains("seven", r.Text);
    }

    [Fact]
    public void Build_MarksEachSessionBoundary()
    {
        var sources = new[] { Src("07 Aug.log", Line("07", "seven")), Src("Game.log", Line("09", "nine")) };
        var r = GameLogExport.Build(sources, null, null, false, null, null, "v", DateTime.UtcNow);
        Assert.Contains("--- 07 Aug.log (1 lines) ---", r.Text);
        Assert.Contains("--- Game.log (1 lines) ---", r.Text);
    }

    // A file that contributed nothing is NAMED rather than silently dropped, so a user who exports
    // four days and gets one session can see the others were read and had nothing in range.
    [Fact]
    public void Build_NamesTheSessionsThatHadNothingInRange()
    {
        var sources = new[] { Src("07 Aug.log", Line("07")), Src("Game.log", Line("09")) };
        var from = new DateTime(2026, 8, 9, 0, 0, 0, DateTimeKind.Utc);

        var r = GameLogExport.Build(sources, from, null, false, null, null, "v", DateTime.UtcNow);

        Assert.Equal(1, r.FilesWithContent);
        Assert.Equal(2, r.FilesScanned);
        Assert.Contains("Session files: 1 of 2 had lines in range", r.Text);
        Assert.Contains("Nothing in range from: 07 Aug.log", r.Text);
    }

    // A continuation line inherits the stamp of the entry above it, but never across a file
    // boundary: the next session's header lines are not part of the previous session's last entry.
    [Fact]
    public void Build_DoesNotCarryAStampAcrossASessionBoundary()
    {
        var sources = new[]
        {
            Src("07 Aug.log", Line("07", "seven")),
            Src("Game.log", "header with no stamp", Line("09", "nine")),
        };
        var from = new DateTime(2026, 8, 8, 0, 0, 0, DateTimeKind.Utc);

        var r = GameLogExport.Build(sources, from, null, false, null, null, "v", DateTime.UtcNow);

        Assert.DoesNotContain("header with no stamp", r.Text);
        Assert.Contains("nine", r.Text);
    }

    // ---- source pin ---------------------------------------------------------------------------

    [Fact]
    public void Dialog_ExportsFromEverySessionNotJustTheActiveFile()
    {
        var src = SourceFiles.ReadAppSource(@"Views\GameLogExportDialog.cs");
        Assert.Contains("GameLogSources.Discover", src);
        Assert.Contains("GameLogSources.Read", src);
    }
}
