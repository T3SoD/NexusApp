using System.Text;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// The manifest parser is the first thing that touches attacker-reachable bytes after the
// signature check, so it is strict: exact schema, exactly the two known assets, real hex,
// sane sizes, a three-part version. Anything else throws the one friendly exception type.
public class UpdateManifestTests
{
    internal static string ValidJson(
        string version = "9.9.9",
        string setupHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        string portableHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
        long setupSize = 100, long portableSize = 200, int schema = 1) => $$"""
        {
          "schema": {{schema}},
          "version": "{{version}}",
          "published": "2026-07-25T18:00:00Z",
          "assets": [
            { "name": "Nexus_Setup.exe", "sha256": "{{setupHash}}", "size": {{setupSize}} },
            { "name": "NexusApp_portable.zip", "sha256": "{{portableHash}}", "size": {{portableSize}} }
          ]
        }
        """;

    private static UpdateManifest Parse(string json) => UpdateManifest.Parse(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void Parse_ValidManifest_PopulatesEverything()
    {
        var m = Parse(ValidJson());
        Assert.Equal(1, m.Schema);
        Assert.Equal(new Version(9, 9, 9), m.Version);
        Assert.Equal(2, m.Assets.Count);
        Assert.Equal(100, m.AssetFor("Installer")!.Size);
        Assert.Equal("NexusApp_portable.zip", m.AssetFor("Portable")!.Name);
        Assert.Null(m.AssetFor("Unknown"));
    }

    [Fact]
    public void Parse_EmptyOrNull_Throws()
    {
        Assert.Throws<UpdateManifestException>(() => UpdateManifest.Parse(Array.Empty<byte>()));
        Assert.Throws<UpdateManifestException>(() => UpdateManifest.Parse(null!));
    }

    [Fact]
    public void Parse_OversizedDocument_Throws()
    {
        var big = new byte[UpdateManifest.MaxManifestBytes + 1];
        Assert.Throws<UpdateManifestException>(() => UpdateManifest.Parse(big));
    }

    [Fact]
    public void Parse_NotJson_Throws() =>
        Assert.Throws<UpdateManifestException>(() => Parse("not json at all"));

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Parse_WrongSchema_Throws(int schema) =>
        Assert.Throws<UpdateManifestException>(() => Parse(ValidJson(schema: schema)));

    [Theory]
    [InlineData("banana")]
    [InlineData("")]
    [InlineData("6.7")]          // two-part versions are rejected: release tags are always three-part
    [InlineData("6.7.0.1.2")]
    public void Parse_BadVersion_Throws(string v) =>
        Assert.Throws<UpdateManifestException>(() => Parse(ValidJson(version: v)));

    [Fact]
    public void Parse_MissingAsset_Throws()
    {
        var json = """
        { "schema": 1, "version": "9.9.9", "assets": [
          { "name": "Nexus_Setup.exe", "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "size": 1 } ] }
        """;
        Assert.Throws<UpdateManifestException>(() => Parse(json));
    }

    [Fact]
    public void Parse_UnknownAssetName_Throws()
    {
        var json = ValidJson().Replace("NexusApp_portable.zip", "Evil.exe");
        Assert.Throws<UpdateManifestException>(() => Parse(json));
    }

    [Fact]
    public void Parse_DuplicateAssetName_Throws()
    {
        // Two setup entries and no portable entry. The count-of-two guard passes, so the
        // exactly-once check per known name is the only thing rejecting this.
        var json = ValidJson().Replace("NexusApp_portable.zip", "Nexus_Setup.exe");
        Assert.Throws<UpdateManifestException>(() => Parse(json));
    }

    [Fact]
    public void Parse_NullAssetElement_Throws()
    {
        // A JSON null stays in the list as a null element, so the count guard passes and every
        // later per-asset check has to survive a null rather than escaping as a NullReference.
        var oneNull = """
        { "schema": 1, "version": "9.9.9", "assets": [
          null,
          { "name": "Nexus_Setup.exe", "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "size": 1 } ] }
        """;
        Assert.Throws<UpdateManifestException>(() => Parse(oneNull));

        var allNull = """
        { "schema": 1, "version": "9.9.9", "assets": [ null, null ] }
        """;
        Assert.Throws<UpdateManifestException>(() => Parse(allNull));
    }

    [Theory]
    [InlineData("short")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")] // uppercase rejected
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")] // non-hex rejected
    public void Parse_BadHash_Throws(string hash) =>
        Assert.Throws<UpdateManifestException>(() => Parse(ValidJson(setupHash: hash)));

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(UpdateManifest.MaxAssetBytes + 1)]
    public void Parse_BadSize_Throws(long size) =>
        Assert.Throws<UpdateManifestException>(() => Parse(ValidJson(setupSize: size)));

    [Fact]
    public void Parse_MissingPublished_IsTolerated()
    {
        var json = """
        { "schema": 1, "version": "9.9.9", "assets": [
          { "name": "Nexus_Setup.exe", "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "size": 1 },
          { "name": "NexusApp_portable.zip", "sha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "size": 2 } ] }
        """;
        var m = Parse(json);   // published is informational only; the signature covers it either way
        Assert.Equal(new Version(9, 9, 9), m.Version);
    }

    [Theory]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsLowerHex64_Matrix(string? s, bool expected) =>
        Assert.Equal(expected, UpdateManifest.IsLowerHex64(s));
}
