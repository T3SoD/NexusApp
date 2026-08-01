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
    public void Normalize_NyxGatewayToken_ReturnsStationName()
    {
        // Captured live 2026-08-01 at the Pyro-side Nyx gateway, closing the documented
        // "_gap_nyx" hole: before this, the origin pill showed the raw RR_JP_PyroNyx token.
        Assert.Equal("Nyx Gateway Station", LocationAliases.Normalize("RR_JP_PyroNyx"));
    }

    [Fact]
    public void UexLocationForToken_NyxGatewayToken_ReturnsUexVocabulary()
    {
        // The display name and the UEX name diverge here exactly as they do for the Stanton-Pyro
        // pair, which is why the two tables are keyed separately by raw token.
        Assert.Equal("Nyx Gateway (Pyro)", LocationAliases.UexLocationForToken("RR_JP_PyroNyx"));
    }

    [Fact]
    public void UexLocationForToken_UncapturedNyxTokens_StillReturnNull()
    {
        // Three Nyx-gateway UEX Locations remain in the snapshot with no captured raw token. The
        // file's standing rule is that a naming-convention guess is never enough to add one.
        Assert.Null(LocationAliases.UexLocationForToken("RR_JP_NyxPyro"));
        Assert.Null(LocationAliases.UexLocationForToken("RR_JP_StantonNyx"));
        Assert.Null(LocationAliases.UexLocationForToken("RR_JP_NyxStanton"));
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
    public void UexLocationForToken_GroundedGatewayToken_ReturnsUexLocationString(string token, string expected)
        => Assert.Equal(expected, LocationAliases.UexLocationForToken(token));

    [Fact]
    public void UexLocationForToken_IsCaseInsensitive()
        => Assert.Equal("Pyro Gateway (Stanton)", LocationAliases.UexLocationForToken("rr_jp_stantonpyro"));

    // The conflation this separate, raw-token-keyed table exists to prevent. Until 2026-08-01 the
    // clearest demonstration was two tokens Normalizing to the same display name; removing the
    // Terra and Magnus tokens (systems not in the game) left no such pair behind, so the guard is
    // shown here with the case that still exists and still matters: two ends of ONE jump point,
    // one captured and one not.
    //
    // RR_JP_PyroNyx and RR_JP_NyxPyro are the same gate from opposite sides. Their map objects are
    // both named "<destination> Gateway", and that name repeats across systems, so nothing but the
    // raw token separates them. A lookup that inferred from shape rather than requiring a real
    // capture would hand the uncaptured side its neighbour's Location and put the player in the
    // wrong system - so the uncaptured side must stay null.
    [Fact]
    public void UexLocationForToken_UncapturedOppositeEnd_DoesNotBorrowItsNeighboursLocation()
    {
        Assert.Equal("Nyx Gateway (Pyro)", LocationAliases.UexLocationForToken("RR_JP_PyroNyx"));
        Assert.Null(LocationAliases.UexLocationForToken("RR_JP_NyxPyro"));
    }

    // The Terra and Magnus gateway tokens were removed 2026-08-01 (the owner's ruling: those systems
    // are not in the game, so no player can ever stand at those gates and the tokens can never
    // fire). This pins the removal so a future artifact edit cannot quietly reintroduce coverage
    // for places that do not exist.
    [Theory]
    [InlineData("RR_JP_StantonTerra")]
    [InlineData("RR_JP_TerraStanton")]
    [InlineData("RR_JP_StantonMagnus")]
    [InlineData("RR_JP_MagnusStanton")]
    public void UnreachableSystemGatewayTokens_AreAbsentFromBothTables(string token)
    {
        Assert.Equal(token, LocationAliases.Normalize(token));   // raw passthrough = no alias entry
        Assert.Null(LocationAliases.UexLocationForToken(token));
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
        // Added 2026-08-01 with the RR_JP_PyroNyx raw-token capture. The Location string comes
        // from the same live-UEX survey that grounded the three above (recorded verbatim in the
        // file's own "_gap_nyx" note before the token existed to pair it with).
        "Nyx Gateway (Pyro)",
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
