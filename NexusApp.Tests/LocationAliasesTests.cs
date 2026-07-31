using System.IO;
using System.Text;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// In-game display names for logged location tokens (Data/location_aliases.json): jump-point
// gateway stations, RR_* rest stops, and Stanton inventory-key slugs (e.g. Stanton4_NewBabbage).
// Exact, case-insensitive lookup; a miss passes the raw token through unchanged - jurisdiction
// text like "microTech" or "Monitored Space" is already human-readable and is not in the table.
public class LocationAliasesTests
{
    [Fact]
    public void Normalize_JumpPointGatewayToken_ReturnsStationName()
    {
        // the owner's real-world example: the RR_JP_StantonPyro inventory key at the Pyro-bound gate.
        Assert.Equal("Pyro Gateway Station", LocationAliases.Normalize("RR_JP_StantonPyro"));
    }

    [Fact]
    public void Normalize_ReverseJumpPointGatewayToken_ReturnsStationName()
    {
        Assert.Equal("Stanton Gateway Station", LocationAliases.Normalize("RR_JP_PyroStanton"));
    }

    [Fact]
    public void Normalize_InventoryKeySlug_ReturnsReadableName()
    {
        Assert.Equal("New Babbage", LocationAliases.Normalize("Stanton4_NewBabbage"));
    }

    [Theory]
    [InlineData("Stanton1_Lorville", "Lorville")]
    [InlineData("Stanton2_Orison", "Orison")]
    public void Normalize_OtherInventoryKeySlugs_ReturnReadableNames(string token, string expected)
    {
        Assert.Equal(expected, LocationAliases.Normalize(token));
    }

    [Fact]
    public void Normalize_RestStopToken_ReturnsStationName()
    {
        // Mechanically extracted from Data/starmap_locations.json: StarMapObject.RR_HUR_LEO's
        // uexName is "Everus Harbor".
        Assert.Equal("Everus Harbor", LocationAliases.Normalize("RR_HUR_LEO"));
    }

    [Fact]
    public void Normalize_IsCaseInsensitive()
    {
        Assert.Equal("Pyro Gateway Station", LocationAliases.Normalize("rr_jp_stantonpyro"));
    }

    [Fact]
    public void Normalize_JurisdictionNameNotInTable_PassesThroughUnchanged()
    {
        Assert.Equal("microTech", LocationAliases.Normalize("microTech"));
        Assert.Equal("Monitored Space", LocationAliases.Normalize("Monitored Space"));
    }

    [Fact]
    public void Normalize_EmptyOrNull_PassesThroughUnchanged_NeverThrows()
    {
        Assert.Equal("", LocationAliases.Normalize(""));
        Assert.Null(LocationAliases.Normalize(null!));
    }

    // ---- Artifact hygiene: the repo is public. No tooling vocabulary, ASCII only.

    private static string LoadArtifactText()
    {
        using var s = typeof(LocationAliases).Assembly
            .GetManifestResourceStream("NexusApp.Data.location_aliases.json");
        Assert.True(s != null, "embedded resource missing: NexusApp.Data.location_aliases.json");
        using var ms = new MemoryStream();
        s!.CopyTo(ms);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    [Fact]
    public void Artifact_ContainsNoForbiddenVocabulary()
    {
        var text = LoadArtifactText();
        string[] banned = ["datamine", "p4k", "socpak", "starbreaker", "sc-datamine", "DataCore"];
        foreach (var b in banned)
            Assert.DoesNotContain(b, text, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Artifact_IsAsciiOnly()
    {
        var text = LoadArtifactText();
        foreach (var ch in text)
            Assert.True(ch <= 127, $"non-ASCII character found: U+{(int)ch:X4}");
    }
}
