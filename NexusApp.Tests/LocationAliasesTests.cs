using System.Collections.Generic;
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

    // ---- UexLocationForToken (the owner's live gateway bug fix): raw-token to UEX Location string,
    // a second table from Normalize's display-name one, keyed by raw token because display names
    // are not unique - see LocationAliases.cs's doc comment on _uexLocations for why.

    [Theory]
    [InlineData("RR_JP_StantonPyro", "Pyro Gateway (Stanton)")]
    [InlineData("RR_JP_PyroStanton", "Stanton Gateway (Pyro)")]
    [InlineData("RR_JP_StantonTerra", "Terra Gateway (Stanton)")]
    public void UexLocationForToken_GroundedGatewayToken_ReturnsUexLocationString(string token, string expected)
        => Assert.Equal(expected, LocationAliases.UexLocationForToken(token));

    [Fact]
    public void UexLocationForToken_IsCaseInsensitive()
        => Assert.Equal("Pyro Gateway (Stanton)", LocationAliases.UexLocationForToken("rr_jp_stantonpyro"));

    // The ambiguity this table exists to avoid: RR_JP_PyroStanton and RR_JP_TerraStanton both
    // Normalize to the SAME display name ("Stanton Gateway Station" - see
    // Normalize_ReverseJumpPointGatewayToken_ReturnsStationName above), so a lookup keyed by
    // display name could not tell them apart. RR_JP_TerraStanton has no live UEX terminal (Terra
    // is not a reachable system) and is deliberately NOT in uexLocations, so it must resolve to
    // null here rather than reusing RR_JP_PyroStanton's Location - proof the two tokens are never
    // conflated even though Normalize maps them to the same text.
    [Fact]
    public void UexLocationForToken_AmbiguousDisplayName_DoesNotConflateDifferentTokens()
    {
        Assert.Equal("Stanton Gateway Station", LocationAliases.Normalize("RR_JP_PyroStanton"));
        Assert.Equal("Stanton Gateway Station", LocationAliases.Normalize("RR_JP_TerraStanton"));

        Assert.Equal("Stanton Gateway (Pyro)", LocationAliases.UexLocationForToken("RR_JP_PyroStanton"));
        Assert.Null(LocationAliases.UexLocationForToken("RR_JP_TerraStanton"));
    }

    [Theory]
    [InlineData("microTech")]         // real jurisdiction text, never a gateway token
    [InlineData("Stanton4_NewBabbage")] // has a display alias, but no UEX gateway mapping
    [InlineData("RR_JP_StantonMagnus")] // gateway naming shape, but not live - deliberately excluded
    [InlineData("Nowhere_RR_JP_FakeToken")]
    public void UexLocationForToken_UnmappedToken_ReturnsNull(string token)
        => Assert.Null(LocationAliases.UexLocationForToken(token));

    [Fact]
    public void UexLocationForToken_NullOrEmpty_ReturnsNull_NeverThrows()
    {
        Assert.Null(LocationAliases.UexLocationForToken(null));
        Assert.Null(LocationAliases.UexLocationForToken(""));
    }

    // Canary (the owner's live gateway bug, 2026-07-31): every uexLocations VALUE committed in
    // location_aliases.json must still name a real, currently-priced UEX terminal Location. This
    // fixture is a snapshot of the real live-UEX Locations verified at the time these three
    // mappings were added; if UEX ever renames one of these gateway Locations and this table is
    // not updated to match, this test fails at build time instead of silently reproducing the
    // exact "LIVE produces zero routes at a real, correctly-identified station" bug this table
    // exists to fix.
    private static readonly HashSet<string> VerifiedLiveUexGatewayLocations = new(System.StringComparer.Ordinal)
    {
        "Pyro Gateway (Stanton)",
        "Stanton Gateway (Pyro)",
        "Terra Gateway (Stanton)",
    };

    [Fact]
    public void AllUexLocationValues_AreVerifiedLiveUexLocations()
    {
        foreach (var uexLocation in LocationAliases.AllUexLocationsForTesting.Values)
            Assert.Contains(uexLocation, VerifiedLiveUexGatewayLocations);
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
