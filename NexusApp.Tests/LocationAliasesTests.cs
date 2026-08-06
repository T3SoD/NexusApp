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
        // A real-world example: the RR_JP_StantonPyro inventory key at the Pyro-bound gate.
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
        // TWO Nyx-gateway UEX Locations remain in the snapshot with no captured raw token
        // (RR_JP_NyxPyro left this list on 2026-08-01 when it was captured live, twice). The file's
        // standing rule is unchanged: a naming-convention guess is never enough to add one.
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

    // Added 2026-08-01 after surveying every Location[...] token in the captured sessions rather
    // than fixing only the one token first noticed in the log. SM0-13 was the reported one; SM0-10
    // was the single most frequent unaliased token of all (20 captures) and takes the same pattern.
    [Theory]
    [InlineData("Stanton4_Shubin_SM0_13", "Shubin Mining Facility SM0-13")]
    [InlineData("Stanton4_Shubin_SM0_10", "Shubin Mining Facility SM0-10")]
    [InlineData("Nyx_Levski", "Levski")]
    public void Normalize_SurveyedInventoryKeys_ReturnReadableNames(string token, string expected)
        => Assert.Equal(expected, LocationAliases.Normalize(token));

    [Fact]
    public void Normalize_UnreadTokenIsNotInvented()
    {
        // An outlaw trading post seen 4 times whose token carries no readable name and which nobody
        // has read off the screen. The raw passthrough is the honest state and the visible prompt to
        // capture it - exactly how the Shubin gap surfaced in the first place.
        Assert.Equal("Pyro3_Outpost_col_m_trdpst_otlw_006",
                     LocationAliases.Normalize("Pyro3_Outpost_col_m_trdpst_otlw_006"));
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

    // ---- UexLocationForToken (live gateway bug fix): raw-token to UEX Location string,
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

    // The conflation this separate, raw-token-keyed table exists to prevent, now demonstrable with
    // BOTH ends captured (2026-08-01: RR_JP_NyxPyro appeared twice in a captured Game.log). These
    // two tokens are the same gate from opposite sides. Their DISPLAY names collide outright - the
    // gate you are looking at is named for where it leads, so the Nyx-side one displays "Pyro
    // Gateway Station", exactly like the Stanton-side RR_JP_StantonPyro - and their map objects are
    // both plain "Pyro Gateway", a name that exists in two systems. Nothing but the raw token can
    // tell any of them apart, which is the whole reason UEX Locations are keyed on it.
    [Fact]
    public void UexLocationForToken_BothEndsOfOneGate_ResolveToDifferentLocations()
    {
        Assert.Equal("Nyx Gateway (Pyro)", LocationAliases.UexLocationForToken("RR_JP_PyroNyx"));
        Assert.Equal("Pyro Gateway (Nyx)", LocationAliases.UexLocationForToken("RR_JP_NyxPyro"));
    }

    // The display-name collision itself, pinned: two DIFFERENT gates, in two different systems,
    // showing the player the same words. Anything that keys off the display name is wrong by
    // construction.
    [Fact]
    public void TwoDifferentGates_ShareOneDisplayName()
    {
        Assert.Equal("Pyro Gateway Station", LocationAliases.Normalize("RR_JP_StantonPyro"));
        Assert.Equal("Pyro Gateway Station", LocationAliases.Normalize("RR_JP_NyxPyro"));
        Assert.NotEqual(LocationAliases.UexLocationForToken("RR_JP_StantonPyro"),
                        LocationAliases.UexLocationForToken("RR_JP_NyxPyro"));
    }

    // Magnus gateway tokens stay out: no player can stand at those gates, so the tokens can never
    // fire. RR_JP_TerraStanton (the TERRA-side leg) is also absent - the Stanton-side gateway
    // existing does not establish that a player can stand on the Terra side, and it has never been
    // captured. This pins those so a future artifact edit cannot quietly reintroduce coverage for
    // places nobody has been.
    [Theory]
    [InlineData("RR_JP_TerraStanton")]
    [InlineData("RR_JP_StantonMagnus")]
    [InlineData("RR_JP_MagnusStanton")]
    public void UnreachableGatewayTokens_AreAbsentFromBothTables(string token)
    {
        Assert.Equal(token, LocationAliases.Normalize(token));   // raw passthrough = no alias entry
        Assert.Null(LocationAliases.UexLocationForToken(token));
    }

    [Fact]
    public void StantonSideTerraToken_IsPresentInBothTables()
    {
        // Excluded on 2026-08-01 and restored the same day: Terra Gateway does exist in the game
        // (Magnus does not), so the exclusion was wrong. UEX had been carrying 21 terminals with
        // priced rows at this Location the whole time, which was the evidence that should have
        // prompted a question rather than been treated as noise.
        Assert.Equal("Terra Gateway Station", LocationAliases.Normalize("RR_JP_StantonTerra"));
        Assert.Equal("Terra Gateway (Stanton)", LocationAliases.UexLocationForToken("RR_JP_StantonTerra"));
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

    // Canary (live gateway bug, 2026-07-31): every uexLocations VALUE committed in
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
        // Same survey, same day: the reverse leg, added once RR_JP_NyxPyro was captured twice.
        "Pyro Gateway (Nyx)",
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
