using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Exercises the headless session logic of GameLogSession (auto-mark gating, dedup, the
// per-session tally) by feeding lines straight into Ingest with injected ownership fakes -
// no WPF window and no real Game.log file.
public class GameLogSessionTests
{
    private static GameLogSession Make(out HashSet<string> owned, params string[] knownNames)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        owned = set;
        return new GameLogSession(
            () => knownNames,
            name => set.Contains(name),
            (name, isOwned) => { if (isOwned) set.Add(name); else set.Remove(name); });
    }

    private static GameLogEntry Bp(string name) =>
        new() { Raw = $"<2026.01.01-12.00.00> [Notice] Received Blueprint: {name}", Category = LogCategory.Blueprint };

    [Fact]
    public void AutoMarkOff_DoesNotMark()
    {
        var s = Make(out var owned, "Bracket Cooler");
        s.Ingest(Bp("Bracket Cooler"));
        Assert.Equal(0, s.Count);
        Assert.Empty(owned);
    }

    [Fact]
    public void AutoMarkOn_MarksKnownBlueprint_RecordedOnce()
    {
        var s = Make(out var owned, "Bracket Cooler");
        s.SetAutoMark(true);
        s.Ingest(Bp("Bracket Cooler"));
        Assert.Equal(1, s.Count);
        Assert.Equal("Bracket Cooler", s.Marks[0].Name);
        Assert.Contains("Bracket Cooler", owned);
    }

    [Fact]
    public void DuplicateReceipt_RecordedOnlyOnce()
    {
        var s = Make(out _, "Bracket Cooler");
        s.SetAutoMark(true);
        s.Ingest(Bp("Bracket Cooler"));
        s.Ingest(Bp("Bracket Cooler"));
        Assert.Equal(1, s.Count);
    }

    [Fact]
    public void AlreadyOwned_NotRecordedInSession()
    {
        var s = Make(out var owned, "Bracket Cooler");
        owned.Add("Bracket Cooler");
        s.SetAutoMark(true);
        s.Ingest(Bp("Bracket Cooler"));
        Assert.Equal(0, s.Count);
    }

    [Fact]
    public void UnknownBlueprint_NotMarked()
    {
        var s = Make(out var owned, "Bracket Cooler");
        s.SetAutoMark(true);
        s.Ingest(Bp("Totally Unknown Part"));
        Assert.Equal(0, s.Count);
        Assert.Empty(owned);
    }

    [Fact]
    public void NonBlueprintLine_Ignored()
    {
        var s = Make(out _, "Bracket Cooler");
        s.SetAutoMark(true);
        s.Ingest(new GameLogEntry { Raw = "Received Blueprint: Bracket Cooler", Category = LogCategory.Quantum });
        Assert.Equal(0, s.Count);
    }

    [Fact]
    public void MarkedEvent_FiresOncePerNewBlueprint()
    {
        var s = Make(out _, "Bracket Cooler", "Hellion Cannon");
        s.SetAutoMark(true);
        int fired = 0;
        s.Marked += _ => fired++;
        s.Ingest(Bp("Bracket Cooler"));
        s.Ingest(Bp("Hellion Cannon"));
        s.Ingest(Bp("Bracket Cooler"));   // duplicate - should not re-fire
        Assert.Equal(2, fired);
        Assert.Equal(2, s.Count);
    }

    [Fact]
    public void Marks_AreInChronologicalOrder()
    {
        var s = Make(out _, "Bracket Cooler", "Hellion Cannon");
        s.SetAutoMark(true);
        s.Ingest(Bp("Bracket Cooler"));
        s.Ingest(Bp("Hellion Cannon"));
        Assert.Equal("Bracket Cooler", s.Marks[0].Name);
        Assert.Equal("Hellion Cannon", s.Marks[1].Name);
    }

    [Fact]
    public void AutoTrack_On_AutoStartsTheWatch()
    {
        var s = Make(out _, "Bracket Cooler");
        Assert.False(s.IsRunning);
        s.SetAutoMark(true);
        Assert.True(s.AutoMark);
        Assert.True(s.IsRunning);
    }

    [Fact]
    public void Stop_ClearsAutoTrack()
    {
        var s = Make(out _, "Bracket Cooler");
        s.SetAutoMark(true);
        s.Stop();
        Assert.False(s.AutoMark);
        Assert.False(s.IsRunning);
    }

    [Fact]
    public void AutoTrack_Off_LeavesTheWatchRunning()
    {
        var s = Make(out _, "Bracket Cooler");
        s.SetAutoMark(true);
        s.SetAutoMark(false);
        Assert.False(s.AutoMark);
        Assert.True(s.IsRunning);
    }

    [Fact]
    public void Reset_ClearsTheSessionTally()
    {
        var s = Make(out _, "Bracket Cooler");
        s.SetAutoMark(true);
        s.Ingest(Bp("Bracket Cooler"));
        Assert.Equal(1, s.Count);

        s.Reset();

        Assert.Equal(0, s.Count);
        Assert.Empty(s.Marks);
    }

    [Fact]
    public void StartPath_HonorsPreferredPath_WhenNoActivePath()
    {
        var s = Make(out _);
        s.PreferredPath = @"D:\Games\StarCitizen\LIVE\Game.log";
        Assert.Equal(@"D:\Games\StarCitizen\LIVE\Game.log", s.StartPath());
    }

    // ── Live-tail diagnostics (issue #17 hardening): unresolved receipts must be visible in
    // nexus.log (once per distinct name per session), and fallback-resolved marks attributed. ──

    private static int CountLogLines(string marker)
    {
        var p = Logger.LogPath;
        if (!File.Exists(p)) return 0;
        int n = 0;
        foreach (var line in File.ReadAllLines(p)) if (line.Contains(marker)) n++;
        return n;
    }

    [Fact]
    public void UnresolvedReceipt_LoggedOncePerSession_ResetAllowsRelogging()
    {
        // Unique per EXECUTION: the isolated log file persists across test runs, so a fixed
        // marker would accumulate counts run over run.
        var marker = "Zqx Unknownpart " + Guid.NewGuid().ToString("N");
        var s = Make(out _, "Bracket Cooler");
        s.SetAutoMark(true);
        s.Ingest(Bp(marker));
        s.Ingest(Bp(marker));   // same receipt again (e.g. the notification lifecycle repeats it)
        Assert.Equal(1, CountLogLines(marker));

        s.Reset();              // new SC session - worth one fresh line if it is still unresolved
        s.Ingest(Bp(marker));
        Assert.Equal(2, CountLogLines(marker));
    }

    [Fact]
    public void AutoMark_ViaNameFallback_MarksAndAttributesInLog()
    {
        // Seed knows "Defiant"; the raw string is a custom-renamed receipt only the step-4
        // structural fallback (wired with the bundled official names) can recover.
        var s = Make(out var owned, "Defiant");
        s.SetAutoMark(true);
        s.Ingest(Bp("P IND 0B Defiant"));

        Assert.Contains("Defiant", owned);
        Assert.Equal(1, s.Count);
        Assert.True(CountLogLines("auto-marked blueprint owned: Defiant (via name fallback)") >= 1);
    }

    [Fact]
    public void UnresolvedReceipt_RecordedInDedicatedUnmatchedLog()
    {
        var marker = "Zqx Dedicated " + Guid.NewGuid().ToString("N");
        var s = Make(out _, "Bracket Cooler");
        s.SetAutoMark(true);
        s.Ingest(Bp(marker));

        var hits = File.Exists(UnmatchedBlueprintLog.LogPath)
            ? File.ReadAllLines(UnmatchedBlueprintLog.LogPath).Where(l => l.Contains(marker)).ToArray()
            : Array.Empty<string>();
        Assert.Single(hits);
        Assert.Contains("| live |", hits[0]);
    }

    [Fact]
    public void ScanHistory_RecordsUnmatchedToDedicatedLog()
    {
        var marker = "Zqx Scanned " + Guid.NewGuid().ToString("N");
        var dir = Path.Combine(Path.GetTempPath(), "nexus_scan_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "Game.log");
        File.WriteAllLines(log, new[] { $"<2026-01-01T12:00:00.000Z> [Notice] Received Blueprint: {marker}" });
        try
        {
            var s = Make(out _, "Bracket Cooler");
            s.ScanHistory(log);

            var hits = File.Exists(UnmatchedBlueprintLog.LogPath)
                ? File.ReadAllLines(UnmatchedBlueprintLog.LogPath).Where(l => l.Contains(marker)).ToArray()
                : Array.Empty<string>();
            Assert.Single(hits);
            Assert.Contains("| import scan |", hits[0]);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ── Live-tail localization map invalidation (issue #17 hardening): changing the global.ini
    // setting must drop the cached map so the next line rebuilds it, not wait for a new SC session. ──

    [Fact]
    public void InvalidateLocalizationMap_ForcesRebuildOnNextResolve()
    {
        int builds = 0;
        var s = new GameLogSession(
            () => new[] { "Tundra" },
            _ => false,
            (_, _) => { },
            _ => { builds++; return null; });

        s.Resolve("Received Blueprint: Tundra");
        s.Resolve("Received Blueprint: Tundra");
        Assert.Equal(1, builds);   // cached after the first build

        s.InvalidateLocalizationMap();
        s.Resolve("Received Blueprint: Tundra");
        Assert.Equal(2, builds);   // dropped cache forces a rebuild
    }

    [Fact]
    public void Start_DropsCachedLocalizationMap()
    {
        // Re-pointing the watcher (Settings Game.log path change, monitor path box) moves the
        // DERIVED global.ini path too - the cached live map must not survive it.
        int builds = 0;
        var s = new GameLogSession(
            () => new[] { "Tundra" },
            _ => false,
            (_, _) => { },
            _ => { builds++; return null; });

        s.Resolve("Received Blueprint: Tundra");
        Assert.Equal(1, builds);

        s.Start(Path.Combine(Path.GetTempPath(), "nexus_other_" + Guid.NewGuid().ToString("N"), "Game.log"));
        s.Resolve("Received Blueprint: Tundra");
        Assert.Equal(2, builds);
    }
}
