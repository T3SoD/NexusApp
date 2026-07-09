using System;
using System.Collections.Generic;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Covers the headless logic behind the Game.log import's "unrecognized names" report:
// best-effort build-line extraction, StarStrings prefix detection, and payload formatting.
public class UnrecognizedBlueprintReportTests
{
    [Fact]
    public void ExtractBuildLine_FindsChangelistOrBranchLine()
    {
        var lines = new[]
        {
            "<2026.06.20-21.30.45:123> some unrelated line",
            "<2026.06.20-21.30.45:200> [Notice] <CEntitySystem> Changelist: 9876543 Branch: sc-alpha-4.8.0",
            "<2026.06.20-21.30.46:000> another line",
        };
        var result = UnrecognizedBlueprintReport.ExtractBuildLine(lines);
        Assert.NotNull(result);
        Assert.Contains("Changelist: 9876543", result);
    }

    [Fact]
    public void ExtractBuildLine_ReturnsNull_WhenNoBuildKeyword()
    {
        Assert.Null(UnrecognizedBlueprintReport.ExtractBuildLine(new[] { "nothing here", "still nothing" }));
    }

    [Fact]
    public void HasStarStringsComponentSignature_DetectsComponentTokensInAnyLine()
    {
        Assert.True(GameLogBlueprintImporter.HasStarStringsComponentSignature("<…> equipped Mil/1/D Tundra cooler"));
        Assert.True(GameLogBlueprintImporter.HasStarStringsComponentSignature("Civ/3/B Ranger"));
        Assert.False(GameLogBlueprintImporter.HasStarStringsComponentSignature("2026/06/22 a normal log line"));
        Assert.False(GameLogBlueprintImporter.HasStarStringsComponentSignature("Atzkav Sniper Rifle"));
    }

    [Fact]
    public void Build_IncludesContextHeaderAndEveryName()
    {
        var unmatchedLines = new List<string>
        {
            "<ts> Added notification \"Received Blueprint: Mystery Part: \" [1] to queue",
            "<ts> Added notification \"Received Blueprint: Mil/1/D Foo Cooler: \" [2] to queue",
        };
        var report = UnrecognizedBlueprintReport.Build(
            appVersion: "5.4.0",
            miningDataVersion: "1.2.4",
            buildLine: "Changelist: 123 Branch: sc-alpha-4.8.0",
            filesScanned: 7,
            matchedCount: 80,
            unmatchedLines: unmatchedLines,
            starStringsDetected: true,
            timestamp: new DateTime(2026, 6, 21, 14, 32, 0));

        Assert.Contains("Nexus v5.4.0", report);
        Assert.Contains("Mining data v1.2.4", report);
        Assert.Contains("2026-06-21 14:32", report);
        Assert.Contains("Changelist: 123", report);
        Assert.Contains("Scanned 7 log file(s) - matched 80, unrecognized 2", report);
        Assert.Contains("StarStrings mod: detected", report);   // passed in (detected from log scan)
        Assert.Contains("Received Blueprint: Mystery Part", report);
        Assert.Contains("Received Blueprint: Mil/1/D Foo Cooler", report);
    }

    [Fact]
    public void Build_ShowsUnknownBuild_AndNoStarStrings_WhenAbsent()
    {
        var report = UnrecognizedBlueprintReport.Build(
            "5.4.0", "1.2.4", null, 1, 0, new List<string> { "Plain Name" }, false, new DateTime(2026, 6, 21, 0, 0, 0));
        Assert.Contains("Star Citizen build: unknown", report);
        Assert.Contains("StarStrings mod: not detected", report);
    }

    // ── Localization-map status (issue #17): the report must say whether the user's global.ini
    // map loaded, so a "nothing recognized" report is self-diagnosing without their nexus.log. ──

    [Fact]
    public void Build_IncludesLocalizationMapEntryCount_WhenLoaded()
    {
        var report = UnrecognizedBlueprintReport.Build(
            "6.3.0", "1.2.4", null, 1, 0, new List<string> { "Plain Name" }, false,
            new DateTime(2026, 7, 9, 0, 0, 0), localizationEntries: 1602);
        Assert.Contains("Localization map: 1602 entries", report);
    }

    [Fact]
    public void Build_ShowsLocalizationMapNotLoaded_WhenNull()
    {
        var report = UnrecognizedBlueprintReport.Build(
            "6.3.0", "1.2.4", null, 1, 0, new List<string> { "Plain Name" }, false,
            new DateTime(2026, 7, 9, 0, 0, 0), localizationEntries: null);
        Assert.Contains("Localization map: not loaded", report);
    }
}
