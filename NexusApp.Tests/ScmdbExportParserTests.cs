using System.Linq;
using System.Text;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// SCMDB (scmdb.net) blueprint-tracking export parser (issue #3, historical-backfill import).
// Ground truth for the export shape is two real user samples in
// C:\Users\owner\Dev\nexus-assets\scmdb-samples\ (untrusted, kept outside the repo); this
// fixture is a sanitized 12-entry stand-in built from real Nexus blueprint names. Parse must
// never throw - garbage input surfaces as a friendly Result.Error string instead.
public class ScmdbExportParserTests
{
    [Fact]
    public void Parse_ValidV3Sample_ReadsHeaderAndCounts()
    {
        var result = ScmdbExportParser.Parse(ScmdbFixture.LoadV3SampleJson());

        Assert.True(result.Success);
        Assert.Null(result.Error);
        Assert.Equal(3, result.Version);
        Assert.False(result.NewerVersion);
        Assert.Equal("2026-07-01T12:00:00.000Z", result.ExportedAt);
        Assert.Equal(2, result.MissionCount);
        Assert.Equal(8, result.CompletedNames.Count);
        Assert.Equal(2, result.SkippedNotCompleted);
        Assert.Equal(2, result.MalformedEntries);
    }

    [Fact]
    public void Parse_ValidV3Sample_PreservesQuotedSkinNameVerbatim()
    {
        var result = ScmdbExportParser.Parse(ScmdbFixture.LoadV3SampleJson());
        Assert.Contains("Yubarev \"Mirage\" Pistol", result.CompletedNames);
    }

    [Fact]
    public void Parse_ValidV3Sample_ContainsExpectedRealBlueprintNames()
    {
        var result = ScmdbExportParser.Parse(ScmdbFixture.LoadV3SampleJson());
        Assert.Contains("Yubarev Pistol", result.CompletedNames);
        Assert.Contains("Arclight Pistol", result.CompletedNames);
        Assert.Contains("Coda Pistol", result.CompletedNames);
        Assert.Contains("Devastator Shotgun", result.CompletedNames);
        Assert.Contains("Scalpel Sniper Rifle", result.CompletedNames);
        Assert.Contains("S-38 Pistol", result.CompletedNames);
    }

    [Fact]
    public void Parse_CompletedFalse_SkippedAndCountedNotAddedToNames()
    {
        var result = ScmdbExportParser.Parse(ScmdbFixture.LoadV3SampleJson());
        Assert.Equal(2, result.SkippedNotCompleted);
        Assert.DoesNotContain("Quartz Energy SMG", result.CompletedNames);
        Assert.DoesNotContain("P4-AR Rifle", result.CompletedNames);
    }

    [Fact]
    public void Parse_EntryMissingNameOrCompleted_SkippedAndCountedMalformed()
    {
        var result = ScmdbExportParser.Parse(ScmdbFixture.LoadV3SampleJson());
        Assert.Equal(2, result.MalformedEntries);
        Assert.DoesNotContain("Broken Entry No Completed", result.CompletedNames);
    }

    [Fact]
    public void Parse_GarbageJson_ReturnsFriendlyErrorNoException()
    {
        var result = ScmdbExportParser.Parse("{ this is not : valid json ][");
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Empty(result.CompletedNames);
    }

    [Fact]
    public void Parse_WrongRootShape_ReturnsFriendlyError()
    {
        // Valid JSON, but the root is an array rather than an object.
        var result = ScmdbExportParser.Parse("[1, 2, 3]");
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Parse_MissingBlueprintsArray_ReturnsFriendlyError()
    {
        var result = ScmdbExportParser.Parse("{ \"version\": 3, \"exportedAt\": \"2026-01-01T00:00:00.000Z\", \"missions\": [] }");
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Parse_EmptyBlueprintsArray_ParsesWithZeroCounts()
    {
        var json = "{ \"version\": 3, \"exportedAt\": \"2026-01-01T00:00:00.000Z\", \"missions\": [], \"blueprints\": [] }";
        var result = ScmdbExportParser.Parse(json);

        Assert.True(result.Success);
        Assert.Empty(result.CompletedNames);
        Assert.Equal(0, result.SkippedNotCompleted);
        Assert.Equal(0, result.MalformedEntries);
        Assert.Equal(0, result.MissionCount);
    }

    [Fact]
    public void Parse_VersionNewerThanThree_SetsNewerVersionFlagButStillParses()
    {
        var json = "{ \"version\": 4, \"exportedAt\": \"2026-01-01T00:00:00.000Z\", \"missions\": [], " +
                    "\"blueprints\": [ { \"tag\": \"BP_X\", \"name\": \"Arclight Pistol\", \"url\": \"https://scmdb.net/x\", \"completed\": true, \"favorite\": false } ] }";
        var result = ScmdbExportParser.Parse(json);

        Assert.True(result.Success);
        Assert.True(result.NewerVersion);
        Assert.Equal(4, result.Version);
        Assert.Contains("Arclight Pistol", result.CompletedNames);
    }

    [Fact]
    public void Parse_InputOverFiveMegabytes_RefusedWithFriendlyMessageBeforeParsing()
    {
        // A single huge string value inside otherwise-well-formed JSON: if the size guard didn't
        // run before attempting JSON.Parse, this would still parse successfully (it's valid JSON),
        // so a friendly refusal here proves the guard runs first, not just that parsing failed.
        var huge = new string('a', 6 * 1024 * 1024);
        var json = "{ \"version\": 3, \"exportedAt\": \"2026-01-01T00:00:00.000Z\", \"missions\": [], " +
                   "\"blueprints\": [ { \"tag\": \"BP_X\", \"name\": \"" + huge + "\", \"url\": \"x\", \"completed\": true, \"favorite\": false } ] }";
        Assert.True(Encoding.UTF8.GetByteCount(json) > ScmdbExportParser.MaxInputBytes);

        var result = ScmdbExportParser.Parse(json);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Parse_EmptyString_ReturnsFriendlyErrorNoException()
    {
        var result = ScmdbExportParser.Parse("");
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }
}
