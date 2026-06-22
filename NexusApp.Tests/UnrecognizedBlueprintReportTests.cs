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
    public void HasStarStringsPrefix_DetectsModdedShipComponentNames()
    {
        Assert.True(GameLogBlueprintImporter.HasStarStringsPrefix("Mil/2/B Tundra"));
        Assert.True(GameLogBlueprintImporter.HasStarStringsPrefix("Ind/1/D QuadraCell MX"));
        Assert.False(GameLogBlueprintImporter.HasStarStringsPrefix("Tundra"));
        Assert.False(GameLogBlueprintImporter.HasStarStringsPrefix("A03 Sniper Rifle"));
    }

    [Fact]
    public void Build_IncludesContextHeaderAndEveryName()
    {
        var unmatched = new List<string> { "Mystery Part", "Mil/1/D Foo Cooler" };
        var report = UnrecognizedBlueprintReport.Build(
            appVersion: "5.4.0",
            miningDataVersion: "1.2.4",
            buildLine: "Changelist: 123 Branch: sc-alpha-4.8.0",
            filesScanned: 7,
            matchedCount: 80,
            unmatched: unmatched,
            timestamp: new DateTime(2026, 6, 21, 14, 32, 0));

        Assert.Contains("Nexus v5.4.0", report);
        Assert.Contains("Mining data v1.2.4", report);
        Assert.Contains("2026-06-21 14:32", report);
        Assert.Contains("Changelist: 123", report);
        Assert.Contains("Scanned 7 log file(s) — matched 80, unrecognized 2", report);
        Assert.Contains("StarStrings mod: detected", report);   // "Mil/1/D Foo Cooler" carries the prefix
        Assert.Contains("Mystery Part", report);
        Assert.Contains("Mil/1/D Foo Cooler", report);
    }

    [Fact]
    public void Build_ShowsUnknownBuild_AndNoStarStrings_WhenAbsent()
    {
        var report = UnrecognizedBlueprintReport.Build(
            "5.4.0", "1.2.4", null, 1, 0, new List<string> { "Plain Name" }, new DateTime(2026, 6, 21, 0, 0, 0));
        Assert.Contains("Star Citizen build: unknown", report);
        Assert.Contains("StarStrings mod: not detected", report);
    }
}
