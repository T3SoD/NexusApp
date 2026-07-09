using System;
using System.Collections.Generic;
using System.IO;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Covers name extraction + resolution against the REAL Game.log notification format, including
// skinned variants whose names contain quotes (the case that was being truncated).
public class GameLogBlueprintImporterTests
{
    // Mirrors the real line: ... Added notification "Received Blueprint: <NAME>: " [n] to queue. ...
    private static string RealLine(string name) =>
        "<2026-06-21T13:51:27.151Z> [Notice] <SHUDEvent_OnNotification> Added notification " +
        $"\"Received Blueprint: {name}: \" [32] to queue. New queue size: 2, " +
        "MissionId: [00000000-0000-0000-0000-000000000000], ObjectiveId: [] [Team_CoreGameplayFeatures][Missions][Comms]";

    // The notification-lifecycle line that repeats the same name (Next / StartFade / Remove).
    private static string UpdateLine(string name) =>
        "<2026-06-21T13:51:41.086Z> [Notice] <UpdateNotificationItem> Notification " +
        $"\"Received Blueprint: {name}: \" [32], Action: Next [Team_CoreGameplayFeatures][Missions][Comms]";

    [Fact]
    public void ExtractRawName_BaseWeapon()
    {
        Assert.Equal("Attrition-4 Repeater",
            GameLogBlueprintImporter.ExtractRawName(RealLine("Attrition-4 Repeater")));
    }

    [Fact]
    public void ExtractRawName_SkinnedWeapon_KeepsInternalQuotes()
    {
        Assert.Equal("Atzkav \"Igniter\" Sniper Rifle",
            GameLogBlueprintImporter.ExtractRawName(RealLine("Atzkav \"Igniter\" Sniper Rifle")));
    }

    [Fact]
    public void ExtractRawName_ComponentName_Unchanged()
    {
        Assert.Equal("Mil/1/D Tundra",
            GameLogBlueprintImporter.ExtractRawName(RealLine("Mil/1/D Tundra")));
    }

    [Fact]
    public void ExtractRawName_SimpleFormat_NoQuoteWrapper()
    {
        // The simplified shape used elsewhere (no wrapping quote / " [n] tail) still works.
        Assert.Equal("Bracket Cooler",
            GameLogBlueprintImporter.ExtractRawName("<ts> [Notice] Received Blueprint: Bracket Cooler"));
    }

    [Fact]
    public void ExtractRawName_NonReceiptLine_ReturnsNull()
    {
        Assert.Null(GameLogBlueprintImporter.ExtractRawName("<ts> [Notice] something entirely unrelated"));
    }

    [Fact]
    public void Resolve_MatchesSkinnedAndBaseWeapons_FromRealLines()
    {
        var imp = new GameLogBlueprintImporter(new[]
        {
            "Atzkav \"Igniter\" Sniper Rifle",
            "Attrition-4 Repeater",
        });
        Assert.Equal("Atzkav \"Igniter\" Sniper Rifle", imp.ResolveLine(RealLine("Atzkav \"Igniter\" Sniper Rifle")));
        Assert.Equal("Attrition-4 Repeater", imp.ResolveLine(RealLine("Attrition-4 Repeater")));
    }

    [Fact]
    public void Resolve_StripsStarStringsComponentPrefix()
    {
        var imp = new GameLogBlueprintImporter(new[] { "Tundra" });
        Assert.Equal("Tundra", imp.ResolveLine(RealLine("Mil/1/D Tundra")));
    }

    [Fact]
    public void ExtractRawName_ComponentWithSingleQuotedName()
    {
        Assert.Equal("Civ/1/A 7SA 'Concord'",
            GameLogBlueprintImporter.ExtractRawName(RealLine("Civ/1/A 7SA 'Concord'")));
    }

    [Fact]
    public void ExtractRawName_UpdateNotificationLine_YieldsSameName()
    {
        Assert.Equal("Attrition-4 Repeater",
            GameLogBlueprintImporter.ExtractRawName(UpdateLine("Attrition-4 Repeater")));
    }

    [Fact]
    public void Resolve_SingleQuotedComponent_StripsPrefixToSeedName()
    {
        var imp = new GameLogBlueprintImporter(new[] { "7SA 'Concord'" });
        Assert.Equal("7SA 'Concord'", imp.ResolveLine(RealLine("Civ/1/A 7SA 'Concord'")));
    }

    // ── Custom localization (any mod) resolution via the user's global.ini map ─────────────────
    // global2-style no-separator custom string: only the key join (captured in the map) can resolve it.
    private static Dictionary<string, string> LocMap() =>
        new(StringComparer.OrdinalIgnoreCase) { ["2AFR-76"] = "FR-76" };

    [Fact]
    public void Resolve_UsesLocalizationMap_ForCustomModString()
    {
        var imp = new GameLogBlueprintImporter(new[] { "FR-76" });
        Assert.Equal("FR-76", imp.Resolve("2AFR-76", LocMap()));
    }

    [Fact]
    public void Resolve_WithoutLocalizationMap_CustomStringUnresolved()
    {
        var imp = new GameLogBlueprintImporter(new[] { "FR-76" });
        Assert.Null(imp.Resolve("2AFR-76"));
    }

    [Fact]
    public void Resolve_LocalizationMapHit_ButOfficialNotInSeed_ReturnsNull()
    {
        var imp = new GameLogBlueprintImporter(new[] { "Tundra" });   // seed lacks FR-76
        Assert.Null(imp.Resolve("2AFR-76", LocMap()));
    }

    [Fact]
    public void ResolveLine_UsesLocalizationMap_FromRealLine()
    {
        var imp = new GameLogBlueprintImporter(new[] { "FR-76" });
        Assert.Equal("FR-76", imp.ResolveLine(RealLine("2AFR-76"), LocMap()));
    }

    // ── "Furthest back data read": the oldest log timestamp the scan could see ──────────────────
    [Fact]
    public void TryParseLineTimestampUtc_ParsesLeadingZuluTimestamp()
    {
        var ts = GameLogBlueprintImporter.TryParseLineTimestampUtc(
            "<2026-05-19T11:04:00.765Z> BackupNameAttachment=\" Build(11854421)\"  -- used by backup system");
        Assert.Equal(new DateTime(2026, 5, 19, 11, 4, 0, 765, DateTimeKind.Utc), ts);
    }

    [Fact]
    public void TryParseLineTimestampUtc_NoLeadingTimestamp_ReturnsNull()
    {
        Assert.Null(GameLogBlueprintImporter.TryParseLineTimestampUtc("Log started on Tue May 19 11:04:00 2026"));
    }

    [Fact]
    public void ScanHistory_ReportsEarliestTimestamp_AcrossLiveAndBackups()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexus_scan_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var backups = Path.Combine(dir, "logbackups");
        Directory.CreateDirectory(backups);
        var live = Path.Combine(dir, "Game.log");
        // The live log is newer; an older session sits in logbackups, so the earliest is the backup's.
        File.WriteAllLines(live, new[] { "<2026-05-25T00:02:00.086Z> [Notice] live session" });
        File.WriteAllLines(Path.Combine(backups, "old.log"), new[] { "<2026-05-19T11:04:00.765Z> [Notice] older session" });
        try
        {
            var imp = new GameLogBlueprintImporter(Array.Empty<string>());
            var scan = imp.ScanHistory(live);
            Assert.Equal(new DateTime(2026, 5, 19, 11, 4, 0, 765, DateTimeKind.Utc), scan.EarliestUtc);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ScanHistory_NoTimestampedLines_EarliestIsNull()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexus_scan_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var live = Path.Combine(dir, "Game.log");
        File.WriteAllLines(live, new[] { "no timestamp here", "still nothing" });
        try
        {
            var imp = new GameLogBlueprintImporter(Array.Empty<string>());
            Assert.Null(imp.ScanHistory(live).EarliestUtc);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ScanHistory_LocalizationMap_ResolvesCustomNamesIntoMatched()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexus_scan_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "Game.log");
        File.WriteAllLines(log, new[] { RealLine("2AFR-76"), RealLine("Tundra") });
        try
        {
            var imp = new GameLogBlueprintImporter(new[] { "FR-76", "Tundra" });
            var scan = imp.ScanHistory(log, null, LocMap());

            Assert.Contains("FR-76", scan.Matched);
            Assert.Contains("Tundra", scan.Matched);
            Assert.DoesNotContain("2AFR-76", scan.Unmatched);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ── Structural name fallback (issue #17): when the user's global.ini map is missing or does
    // not match the log strings (mod updated, stale override, logbackups from an older pack), a
    // custom component string still resolves if a known official name appears word-bounded in it.
    // Candidates are the bundled official component names - no dependence on the user's files. ──

    // The 18 raw strings reported in issue #17 and the official name each must resolve to.
    private static readonly (string Raw, string Official)[] Issue17 =
    {
        ("P IND 0B Defiant", "Defiant"),
        ("P IND 2B Sedulity", "Sedulity"),
        ("Q IND 1B Colossus", "Colossus"),
        ("Q IND 2B Huracan", "Huracan"),
        ("Q IND 3B Agni", "Agni"),
        ("Q MIL 1A VK-00", "VK-00"),
        ("Q MIL 1B Siren", "Siren"),
        ("Q MIL 2A XL-1", "XL-1"),
        ("Q MIL 2B Yeager", "Yeager"),
        ("Q MIL 3A TS-2", "TS-2"),
        ("Q MIL 3B Balandin", "Balandin"),
        ("R IND 0C Surveyor-Go", "Surveyor-Go"),
        ("R IND 1C Surveyor-Lite", "Surveyor-Lite"),
        ("R IND 3C Surveyor-Max", "Surveyor-Max"),
        ("S CIV 3B 6CA 'Bila'", "6CA 'Bila'"),
        ("S IND 1A Palisade", "Palisade"),
        ("S IND 2A Rampart", "Rampart"),
        ("S IND 3A Parapet", "Parapet"),
    };

    private static string[] Issue17Officials()
    {
        var names = new string[Issue17.Length];
        for (int i = 0; i < Issue17.Length; i++) names[i] = Issue17[i].Official;
        return names;
    }

    [Fact]
    public void Resolve_NameFallback_ResolvesAllIssue17Strings_WithNoMap()
    {
        var imp = new GameLogBlueprintImporter(Issue17Officials(), Issue17Officials());
        foreach (var (raw, official) in Issue17)
            Assert.Equal(official, imp.Resolve(raw));
    }

    [Fact]
    public void Resolve_NameFallback_Remix2QuotedFormat()
    {
        // The sibling community pack's real on-disk format: CLASS-SizeGrade "Name".
        var imp = new GameLogBlueprintImporter(
            new[] { "Defiant", "6CA 'Bila'" }, new[] { "Defiant", "6CA 'Bila'" });
        Assert.Equal("Defiant", imp.Resolve("IND-0B \"Defiant\""));
        Assert.Equal("6CA 'Bila'", imp.Resolve("CIV-3B \"6CA 'Bila'\""));
    }

    [Fact]
    public void Resolve_NameFallback_LongestMatchWins()
    {
        // "Surveyor" also word-matches inside "Surveyor-Go" (hyphen is a boundary), so the
        // longer official name must win.
        var names = new[] { "Surveyor", "Surveyor-Go" };
        var imp = new GameLogBlueprintImporter(names, names);
        Assert.Equal("Surveyor-Go", imp.Resolve("R IND 0C Surveyor-Go"));
        Assert.Equal("Surveyor", imp.Resolve("R IND 2C Surveyor"));
    }

    [Fact]
    public void Resolve_NameFallback_RequiresWordBoundary()
    {
        // "Agni" inside "Magnitude" and "FR-76" inside "2AFR-76" are not word-bounded - the
        // no-separator formats still need the localization map, never a substring guess.
        var imp = new GameLogBlueprintImporter(new[] { "Agni", "FR-76" }, new[] { "Agni", "FR-76" });
        Assert.Null(imp.Resolve("IND-3B Magnitude"));
        Assert.Null(imp.Resolve("2AFR-76"));
    }

    [Fact]
    public void Resolve_NameFallback_OfficialNotInSeed_ReturnsNull()
    {
        var imp = new GameLogBlueprintImporter(new[] { "Tundra" }, new[] { "Defiant" });
        Assert.Null(imp.Resolve("P IND 0B Defiant"));
    }

    [Fact]
    public void Resolve_NameFallback_LongestMatchNotInSeed_DoesNotFallThroughToShorter()
    {
        // Real shipped collision: components.ini carries both "Broadspec-Lite" and "BroadSpec"
        // while the seed only knows "BroadSpec". A Broadspec-Lite receipt must stay unresolved
        // (and land in the unrecognized report as a seed gap), never mark BroadSpec owned.
        var imp = new GameLogBlueprintImporter(
            new[] { "BroadSpec" }, new[] { "Broadspec-Lite", "BroadSpec" });
        Assert.Null(imp.Resolve("Broadspec-Lite"));
        Assert.Null(imp.Resolve("R IND 1C Broadspec-Lite"));
    }

    [Fact]
    public void Resolve_NameFallback_NormalizesNbsp_InCandidateAndRaw()
    {
        // The game's localization spells some names with a no-break space (Game.log writes it
        // verbatim) while the seed uses a regular space - both sides normalize before matching.
        var imp = new GameLogBlueprintImporter(
            new[] { "Hellion Scattergun" }, new[] { "Hellion\u00A0Scattergun" });
        Assert.Equal("Hellion Scattergun", imp.Resolve("Hellion\u00A0Scattergun"));
    }

    [Fact]
    public void Resolve_NameFallback_EqualLengthTie_RefusesToGuess()
    {
        // Two DIFFERENT equal-length names in one string means any pick is a guess - refuse and
        // leave the string unresolved (it lands in the unrecognized report instead).
        var names = new[] { "TS-2", "Agni" };
        var imp = new GameLogBlueprintImporter(names, names);
        Assert.Null(imp.Resolve("Q MIL Agni TS-2"));
        // A single unambiguous name still resolves.
        Assert.Equal("Agni", imp.Resolve("Q MIL 3B Agni"));
    }

    [Fact]
    public void Resolve_WithoutComponentNames_FallbackDisabled()
    {
        // Regression guard: the one-arg constructor keeps the pre-fallback behavior.
        var imp = new GameLogBlueprintImporter(new[] { "Defiant" });
        Assert.Null(imp.Resolve("P IND 0B Defiant"));
    }

    [Fact]
    public void Resolve_LocalizationMap_WinsBeforeNameFallback()
    {
        // The user's own global.ini is authoritative when it knows the string; the structural
        // fallback only runs when steps 1-3 all miss.
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        { ["Custom Foo Defiant"] = "Tundra" };
        var imp = new GameLogBlueprintImporter(
            new[] { "Tundra", "Defiant" }, new[] { "Defiant" });
        Assert.Equal("Tundra", imp.Resolve("Custom Foo Defiant", map));
    }

    [Fact]
    public void ScanHistory_NameFallback_ResolvesIssue17Receipts_WithNullMap()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexus_scan_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "Game.log");
        var lines = new List<string>();
        foreach (var (raw, _) in Issue17) lines.Add(RealLine(raw));
        File.WriteAllLines(log, lines);
        try
        {
            var imp = new GameLogBlueprintImporter(Issue17Officials(), Issue17Officials());
            var scan = imp.ScanHistory(log);

            foreach (var (_, official) in Issue17) Assert.Contains(official, scan.Matched);
            Assert.Empty(scan.Unmatched);
            Assert.Equal(Issue17.Length, scan.FallbackMatched);
            Assert.Null(scan.LocalizationEntries);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ScanHistory_FallbackMatched_ExcludesNamesTheMapAlsoResolved()
    {
        // The SAME canonical name arrives as a fallback-only form (live log, scanned first) and a
        // map-resolvable form (backup). FallbackMatched counts names ONLY the fallback recovered,
        // independent of scan order - so this must be 0, not 1.
        var dir = Path.Combine(Path.GetTempPath(), "nexus_scan_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var backups = Path.Combine(dir, "logbackups");
        Directory.CreateDirectory(backups);
        var live = Path.Combine(dir, "Game.log");
        File.WriteAllLines(live, new[] { RealLine("IND-0B \"Defiant\"") });          // fallback-only form
        File.WriteAllLines(Path.Combine(backups, "old.log"), new[] { RealLine("P IND 0B Defiant") }); // map form
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        { ["P IND 0B Defiant"] = "Defiant" };
        try
        {
            var imp = new GameLogBlueprintImporter(new[] { "Defiant" }, new[] { "Defiant" });
            var scan = imp.ScanHistory(live, null, map);

            Assert.Contains("Defiant", scan.Matched);
            Assert.Equal(0, scan.FallbackMatched);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ScanHistory_ReportsLocalizationEntries_WhenMapSupplied()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexus_scan_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var log = Path.Combine(dir, "Game.log");
        File.WriteAllLines(log, new[] { RealLine("Tundra") });
        try
        {
            var imp = new GameLogBlueprintImporter(new[] { "Tundra" });
            var scan = imp.ScanHistory(log, null, LocMap());
            Assert.Equal(1, scan.LocalizationEntries);
            Assert.Equal(0, scan.FallbackMatched);
        }
        finally { Directory.Delete(dir, true); }
    }
}
