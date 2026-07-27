using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

public class SwapJournalTests : IDisposable
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

    private string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexus-journal-test-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    [Fact]
    public void SaveThenTryLoad_RoundTrips()
    {
        var path = Path.Combine(TempDir(), SwapJournal.FileName);
        var j = new SwapJournal
        {
            Status = SwapJournal.StatusInProgress,
            AttemptedVersion = "6.9.0",
            PreviousVersion = "6.8.1",
            InstallDir = @"C:\Somewhere\NexusApp",
            Ops = { new SwapOp { Rel = "NexusApp.exe", OldMoved = true } },
        };
        j.Save(path);
        var back = SwapJournal.TryLoad(path);
        Assert.NotNull(back);
        Assert.Equal("6.9.0", back!.AttemptedVersion);
        Assert.Equal("6.8.1", back.PreviousVersion);
        Assert.Single(back.Ops);
        Assert.True(back.Ops[0].OldMoved);
        Assert.False(back.Ops[0].NewPlaced);
    }

    // Both versions must parse or TryLoad refuses the file, so every fixture that expects a
    // readable journal back carries them.
    private static SwapJournal Fixture() => new()
    {
        AttemptedVersion = "6.9.0", PreviousVersion = "6.8.1", InstallDir = @"C:\Somewhere\NexusApp",
    };

    [Fact]
    public void Save_LeavesNoTempAndSurvivesOverwrite()
    {
        var path = Path.Combine(TempDir(), SwapJournal.FileName);
        var j = Fixture();
        j.Save(path);
        j.Ops.Add(new SwapOp { Rel = "NexusApp.exe" });
        j.Save(path);
        Assert.False(File.Exists(path + ".tmp"));
        Assert.Single(SwapJournal.TryLoad(path)!.Ops);
    }

    [Fact]
    public void Save_ReplacesAStaleTempFromACrashedWrite()
    {
        var path = Path.Combine(TempDir(), SwapJournal.FileName);
        File.WriteAllText(path + ".tmp", "{ truncated by a crash");
        Fixture().Save(path);
        Assert.False(File.Exists(path + ".tmp"));
        Assert.NotNull(SwapJournal.TryLoad(path));
    }

    [Fact]
    public void TryLoad_RejectsNullOpElement()
    {
        var path = Path.Combine(TempDir(), SwapJournal.FileName);
        File.WriteAllText(path, """{ "Schema": 1, "Status": "InProgress", "AttemptedVersion": "6.9.0", "PreviousVersion": "6.8.1", "InstallDir": "C:\\x", "Ops": [null] }""");
        Assert.Null(SwapJournal.TryLoad(path));
    }

    [Theory]
    // Both versions flow into UpdateNotice.SwapFailedBody verbatim: a journal that cannot
    // supply real ones is refused rather than rendered.
    [InlineData("""{ "Schema": 1, "Status": "InProgress", "AttemptedVersion": "6.9.0; DROP", "PreviousVersion": "6.8.1", "InstallDir": "C:\\x" }""")]
    [InlineData("""{ "Schema": 1, "Status": "InProgress", "AttemptedVersion": "6.9.0", "PreviousVersion": "not a version", "InstallDir": "C:\\x" }""")]
    [InlineData("""{ "Schema": 1, "Status": "InProgress", "AttemptedVersion": "", "PreviousVersion": "", "InstallDir": "C:\\x" }""")]
    [InlineData("""{ "Schema": 1, "Status": "InProgress", "InstallDir": "C:\\x" }""")]   // both missing
    public void TryLoad_RejectsUnparseableVersions(string content)
    {
        var path = Path.Combine(TempDir(), SwapJournal.FileName);
        File.WriteAllText(path, content);
        Assert.Null(SwapJournal.TryLoad(path));
    }

    [Fact]
    public void TryLoad_RejectsAnOversizedFile()
    {
        // Refused on the file length, before the bytes are read into memory.
        var path = Path.Combine(TempDir(), SwapJournal.FileName);
        Fixture().Save(path);
        Assert.NotNull(SwapJournal.TryLoad(path));
        File.WriteAllText(path, File.ReadAllText(path) + new string(' ', SwapJournal.MaxJournalBytes));
        Assert.Null(SwapJournal.TryLoad(path));
    }

    [Fact]
    public void TryLoad_MissingFile_ReturnsNull() =>
        Assert.Null(SwapJournal.TryLoad(Path.Combine(TempDir(), "nope.json")));

    [Theory]
    [InlineData("{ not json")]
    [InlineData("null")]
    [InlineData("{}")]                                                       // defaults leave InstallDir empty
    [InlineData("""{ "Schema": 2, "Status": "InProgress", "InstallDir": "C:\\x" }""")]   // future schema
    [InlineData("""{ "Schema": 1, "Status": "Sideways", "InstallDir": "C:\\x" }""")]     // unknown status
    [InlineData("""{ "Schema": 1, "Status": "InProgress", "InstallDir": "relative\\dir" }""")]
    public void TryLoad_RejectsHostileOrForeignContent(string content)
    {
        var path = Path.Combine(TempDir(), SwapJournal.FileName);
        File.WriteAllText(path, content);
        Assert.Null(SwapJournal.TryLoad(path));
    }

    [Theory]
    [InlineData("NexusApp.exe", true)]
    [InlineData(@"Web\cargo\index.html", true)]
    [InlineData("Web/cargo/index.html", true)]
    [InlineData("", false)]
    [InlineData("..", false)]
    [InlineData(@"..\outside.txt", false)]
    [InlineData(@"Web\..\..\outside.txt", false)]
    [InlineData(@"C:\rooted.txt", false)]
    [InlineData(@"\rooted.txt", false)]
    [InlineData("name:stream.txt", false)]   // NTFS alternate data stream
    [InlineData(@"Web\.\file.txt", false)]
    public void IsSafeRel_Matrix(string rel, bool expected)
    {
        var installDir = TempDir();
        Assert.Equal(expected, SwapJournal.IsSafeRel(installDir, rel));
    }
}
