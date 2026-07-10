using System;
using System.IO;
using System.Linq;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Covers the dedicated unmatched-blueprint diagnostics file (issue #17): per-run dedupe, the
// self-contained context line, and map-status wording. The file sits next to the test-isolated
// nexus.log, so these tests never touch the user's real diagnostics.
public class UnmatchedBlueprintLogTests
{
    // Unique per execution: the isolated log directory persists across test runs.
    private static string Unique(string stem) => stem + " " + Guid.NewGuid().ToString("N");

    private static string[] LinesWith(string marker) =>
        TestFiles.ReadSharedLines(UnmatchedBlueprintLog.LogPath).Where(l => l.Contains(marker)).ToArray();

    [Fact]
    public void Record_WritesSelfContainedContextLine()
    {
        var name = Unique("Zux Part");
        Assert.True(UnmatchedBlueprintLog.Record("live", name, $"<ts> Received Blueprint: {name}", 1602));

        var lines = LinesWith(name);
        Assert.Single(lines);
        Assert.Contains("| live |", lines[0]);
        Assert.Contains("map 1602 entries", lines[0]);
        Assert.Contains("Received Blueprint", lines[0]);
    }

    [Fact]
    public void Record_SameNameOncePerRun()
    {
        var name = Unique("Zux Dup");
        Assert.True(UnmatchedBlueprintLog.Record("live", name, "line one", null));
        Assert.False(UnmatchedBlueprintLog.Record("import scan", name, "line two", null));
        Assert.Single(LinesWith(name));
    }

    [Fact]
    public void Record_NullMap_SaysNotLoaded()
    {
        var name = Unique("Zux NoMap");
        UnmatchedBlueprintLog.Record("import scan", name, "raw line", null);
        Assert.Contains("map not loaded", LinesWith(name)[0]);
    }
}
