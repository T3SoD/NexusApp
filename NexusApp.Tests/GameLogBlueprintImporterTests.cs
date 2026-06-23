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
}
