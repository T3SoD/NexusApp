using NexusApp.Services;
using NexusApp.Services.Map;
using Xunit;

namespace NexusApp.Tests;

public class MapCatalogTests
{
    // Load once per class (pattern: StarmapCatalogTests, CargoShipCatalogTests) - the embedded
    // artifact is immutable within a test run, so 963 objects need not be parsed per test.
    private static readonly MapCatalog Catalog = MapCatalog.LoadEmbedded();

    [Fact]
    public void LoadEmbedded_HasExpectedObjectCount()
    {
        // 4 source placeholder records ("<= UNINITIALIZED =>", Stanton) were filtered out of the
        // extractor's map artifact; ids 419/437/473/515 are gaps, not renumbered.
        // 961 = 963 in the artifact minus the 2 objects for systems that are not in the game,
        // excluded at load (MapCatalog.ExcludedObjects).
        Assert.Equal(961, Catalog.Count);
        Assert.Equal(961, Catalog.Objects.Count);
    }

    // ── Unreachable-system exclusions (the owner, 2026-08-01) ──

    [Theory]
    [InlineData("Stanton", "Stanton - Magnus Jump Point")]
    [InlineData("Nyx", "Nyx - Castra Jump Point")]
    public void UnreachableSystemObjects_AreNotInTheCatalog(string system, string name)
    {
        // Magnus and Castra are not in the game. These rows exist in the artifact only because it is
        // derived from the game's object catalog, which carries unreleased content. The Castra case
        // shows the rule is about the DESTINATION system, not the system the object sits in: that
        // jump point is physically in Nyx, which is live.
        Assert.Null(Catalog.ByName(system, name));
        Assert.DoesNotContain(Catalog.Objects, o => o.Name == name);
    }

    [Theory]
    [InlineData("Stanton", "Terra Gateway")]
    [InlineData("Stanton", "Stanton - Terra Jump Point")]
    public void TerraObjects_AreInTheCatalog(string system, string name)
    {
        // Excluded on 2026-08-01 and restored the same day - the owner: "terra gateway does exist in the
        // game, magnus does not". Pinned in the positive so the mistake cannot be repeated silently.
        Assert.NotNull(Catalog.ByName(system, name));
    }

    [Fact]
    public void ExcludedObjects_CarryNoUexAliases()
    {
        // The guard that replaced a whole carve-out. While Terra was wrongly excluded, its UEX
        // aliases had to be kept resolvable for distance or 21 real terminals would have silently
        // lost the figure the planner had always shown. Every current exclusion is alias-free, so
        // that second resolve path was deleted - and this fails the moment someone excludes an
        // alias-carrying object again, which is exactly when the problem would return.
        foreach (var name in new[] { "Stanton - Magnus Jump Point", "Nyx - Castra Jump Point" })
        {
            var terminal = new MarketTerminal(1, "probe", "commodity", false,
                                              name.StartsWith("Nyx") ? "Nyx" : "Stanton", name);
            Assert.Null(Catalog.ResolveTerminal(terminal));
        }
    }

    [Fact]
    public void StantonGatewayInNyx_Survives_AndIsRerootedOffTheCastraJumpPoint()
    {
        // The Nyx-side repeat of the Nyx-Gateway-under-Magnus case: a gateway to a REACHABLE system
        // was parented to an excluded jump point. Two independent instances is why the re-rooting
        // pass is a general rule rather than a one-off patch.
        var obj = Catalog.ByName("Nyx", "Stanton Gateway");
        Assert.NotNull(obj);
        Assert.Null(obj!.Parent);
    }

    [Fact]
    public void TerraMillsHydroFarm_Survives_ItSharesOnlyTheWord()
    {
        // A real Hurston outpost. The exclusion matches full (system, name) keys, never substrings,
        // specifically so an unrelated name cannot be swept up by the word "Terra".
        var obj = Catalog.ByName("Stanton", "Terra Mills HydroFarm");
        Assert.NotNull(obj);
        Assert.Equal("Outpost", obj!.Type);
    }

    [Fact]
    public void NyxGatewayInStanton_Survives_AndIsRerootedOffItsExcludedParent()
    {
        // The case that made the second load pass necessary: Stanton's "Nyx Gateway" is parented to
        // the Stanton-Magnus Jump Point in the source data, and Nyx IS reachable. Excluding the
        // parent must not orphan the child onto an id that resolves to nothing.
        var obj = Catalog.ByName("Stanton", "Nyx Gateway");
        Assert.NotNull(obj);
        Assert.Null(obj!.Parent);
    }

    [Fact]
    public void EveryParentId_Resolves()
    {
        // The invariant the re-rooting pass exists to hold, asserted over the whole catalog rather
        // than just the one known case - any future exclusion gets this check for free.
        foreach (var obj in Catalog.Objects)
            if (obj.Parent is { } parentId)
                Assert.True(Catalog.ById(parentId) != null,
                    $"{obj.System}/{obj.Name} has parent id {parentId}, which resolves to nothing");
    }

    [Fact]
    public void TerraJumpPoint_KeepsItsUexAliases()
    {
        // "Stanton - Terra Jump Point" carries the UEX aliases "Terra Gateway (Stanton)" (location
        // and orbit), covering 21 real UEX terminals. Losing this resolution was the concrete cost
        // of the brief wrong exclusion, and it stays coherent with location_aliases.json, where the
        // matching entry was restored in the same correction.
        var terminal = new MarketTerminal(1, "Some Terminal", "commodity", false, "Stanton", "Terra Gateway (Stanton)");
        Assert.NotNull(Catalog.ResolveTerminal(terminal));
    }

    [Fact]
    public void ByName_Hurston_HasVerbatimPositionAndParentsToStanton()
    {
        // Values copied verbatim from Data/starmap_map.json (object id 1): x=12850457093,
        // parent=0 (the id of the "Stanton" star object itself).
        var hurston = Catalog.ByName("Stanton", "Hurston");
        Assert.NotNull(hurston);
        Assert.Equal(12850457093, hurston!.X);
        Assert.NotNull(hurston.Parent);

        var parent = Catalog.ById(hurston.Parent!.Value);
        Assert.NotNull(parent);
        Assert.Equal("Stanton", parent!.Name);
    }

    [Fact]
    public void ByName_PyroExhangSite_IsFound()
    {
        var obj = Catalog.ByName("Pyro", "PYAM-EXHANG-0-1");
        Assert.NotNull(obj);
    }

    [Fact]
    public void ByName_Magda_NameIsClean()
    {
        // "Clean" here means the plain catalog Name carries no UEX alias decoration - it is just
        // the moon's own name as stored on the object.
        Assert.Equal("Magda", Catalog.ByName("Stanton", "Magda")!.Name);
    }

    [Fact]
    public void ResolveTerminal_ByLocation_ReturnsMatchingObject()
    {
        var terminal = new MarketTerminal(1, "x", "", false, "Stanton", "Everus Harbor");
        var resolved = Catalog.ResolveTerminal(terminal);
        Assert.NotNull(resolved);
        Assert.Equal("Everus Harbor", resolved!.Name);
    }

    [Fact]
    public void ResolveTerminal_FallsThroughToPlanetOrMoon_WhenLocationEmpty()
    {
        var terminal = new MarketTerminal(1, "x", "", false, "Stanton", "", PlanetOrMoon: "ArcCorp");
        var resolved = Catalog.ResolveTerminal(terminal);
        Assert.NotNull(resolved);
        Assert.Equal("ArcCorp", resolved!.Name);
    }

    [Fact]
    public void ResolveTerminal_FallsThroughToOrbit_WhenLocationAndPlanetOrMoonEmpty()
    {
        var terminal = new MarketTerminal(1, "x", "", false, "Stanton", "", Orbit: "ArcCorp");
        var resolved = Catalog.ResolveTerminal(terminal);
        Assert.NotNull(resolved);
        Assert.Equal("ArcCorp", resolved!.Name);
    }

    [Fact]
    public void ResolveTerminal_NonMatchingLocation_FallsThroughToPlanetOrMoon()
    {
        var terminal = new MarketTerminal(1, "x", "", false, "Stanton", "Nowhere Station", PlanetOrMoon: "ArcCorp");
        var resolved = Catalog.ResolveTerminal(terminal);
        Assert.NotNull(resolved);
        Assert.Equal("ArcCorp", resolved!.Name);
    }

    [Fact]
    public void ResolveTerminal_NothingMatches_ReturnsNull()
    {
        var terminal = new MarketTerminal(1, "x", "", false, "Stanton", "Nowhere", Orbit: "Nowhere Either", PlanetOrMoon: "Still Nowhere");
        Assert.Null(Catalog.ResolveTerminal(terminal));
    }

    [Fact]
    public void ResolveTerminal_NullTerminal_ReturnsNull()
    {
        Assert.Null(Catalog.ResolveTerminal(null));
    }

    [Fact]
    public void ResolveTerminal_IsOrdinalIgnoreCase()
    {
        var terminal = new MarketTerminal(1, "x", "", false, "STANTON", "everus harbor");
        var resolved = Catalog.ResolveTerminal(terminal);
        Assert.NotNull(resolved);
        Assert.Equal("Everus Harbor", resolved!.Name);
    }

    [Fact]
    public void DistanceMeters_HurstonToArcCorp_WithinOnePercentOfPinnedFigure()
    {
        var hurston = Catalog.ByName("Stanton", "Hurston");
        var arcCorp = Catalog.ByName("Stanton", "ArcCorp");
        var meters = Catalog.DistanceMeters(hurston, arcCorp);
        Assert.NotNull(meters);

        const double expected = 2.2882e10;
        double pctError = Math.Abs(meters!.Value - expected) / expected;
        Assert.True(pctError <= 0.01, $"Expected ~{expected} within 1%, got {meters.Value}");
    }

    [Fact]
    public void DistanceMeters_CrossSystem_ReturnsNull()
    {
        var hurston = Catalog.ByName("Stanton", "Hurston");
        var pyroExhang = Catalog.ByName("Pyro", "PYAM-EXHANG-0-1");
        Assert.Null(Catalog.DistanceMeters(hurston, pyroExhang));
    }

    [Fact]
    public void DistanceMeters_EitherSideNull_ReturnsNull()
    {
        var hurston = Catalog.ByName("Stanton", "Hurston");
        Assert.Null(Catalog.DistanceMeters(hurston, null));
        Assert.Null(Catalog.DistanceMeters(null, hurston));
        // Explicitly typed: DistanceMeters is now overloaded for MapObject and MarketTerminal (the
        // latter is the seam Trade uses since it retired StarmapCatalog), so a bare (null, null)
        // is ambiguous. This case belongs to the MapObject overload.
        Assert.Null(Catalog.DistanceMeters((MapObject?)null, (MapObject?)null));
    }

    [Fact]
    public void Load_MalformedStream_ReturnsEmptyCatalog_NoThrow()
    {
        using var bad = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("{ not json"));
        var catalog = MapCatalog.Load(bad);
        Assert.Equal(0, catalog.Count);
        Assert.Empty(catalog.Objects);
        Assert.Null(catalog.ById(1));
        Assert.Null(catalog.ByName("Stanton", "Hurston"));
        Assert.Null(catalog.ResolveTerminal(new MarketTerminal(1, "x", "", false, "Stanton", "Everus Harbor")));
    }

    [Fact]
    public void ById_UnknownId_ReturnsNull()
    {
        Assert.Null(Catalog.ById(-1));
    }

    [Fact]
    public void ByName_UnknownName_ReturnsNull()
    {
        Assert.Null(Catalog.ByName("Stanton", "Nowhere At All"));
    }

    [Theory]
    [InlineData(22_900_000_000.0, "22.9 Gm")]     // >= 10 Gm: one decimal
    [InlineData(10_000_000_000.0, "10.0 Gm")]     // exact boundary counts as the >= 10 branch
    [InlineData(1_850_000_000.0, "1.85 Gm")]      // >= 1 Gm: two decimals
    [InlineData(1_000_000_000.0, "1.00 Gm")]      // exact boundary counts as the >= 1 branch
    [InlineData(460_000_000.0, "0.46 Gm")]        // >= 0.1 Gm: two decimals
    [InlineData(100_000_000.0, "0.10 Gm")]        // exact boundary counts as the >= 0.1 branch
    [InlineData(99_000_000.0, "<0.1 Gm")]         // just under 0.1 Gm: floor literal
    [InlineData(1_000.0, "<0.1 Gm")]              // tiny: floor literal
    [InlineData(0.0, "<0.1 Gm")]                  // zero: floor literal
    public void FormatGm_MatchesSpecBuckets(double meters, string expected)
        => Assert.Equal(expected, MapCatalog.FormatGm(meters));

    // ── Search (location search box, MAP tab side panel) ──

    [Fact]
    public void Search_PrefixMatch_RanksAboveMidStringMatch()
    {
        // "Everus Harbor" (Stanton) starts with "Ever"; "Nevermind" (Stanton) only contains "ever"
        // mid-string (N-ever-mind). The prefix hit must rank strictly above the substring hit.
        var results = Catalog.Search("Ever", 10);
        Assert.Contains(results, o => o.Name == "Everus Harbor");
        Assert.Contains(results, o => o.Name == "Nevermind");

        int everusIdx = IndexOfName(results, "Everus Harbor");
        int nevermindIdx = IndexOfName(results, "Nevermind");
        Assert.True(everusIdx < nevermindIdx, "prefix match (Everus Harbor) must rank above the mid-string match (Nevermind)");
    }

    [Fact]
    public void Search_IsCaseInsensitive()
    {
        var results = Catalog.Search("everus harbor", 10);
        Assert.Contains(results, o => o.Name == "Everus Harbor");
    }

    [Fact]
    public void Search_RespectsLimit()
    {
        // "a" matches a large fraction of the 963-object catalog - limit must still cap the result set.
        var results = Catalog.Search("a", 5);
        Assert.True(results.Count <= 5);
    }

    [Fact]
    public void Search_EmptyQuery_ReturnsEmpty()
    {
        Assert.Empty(Catalog.Search("", 10));
    }

    [Fact]
    public void Search_WhitespaceQuery_ReturnsEmpty()
    {
        Assert.Empty(Catalog.Search("   ", 10));
    }

    [Fact]
    public void Search_CrossSystemHit_IsReturned()
    {
        // "Ruin Station" lives in Pyro. The catalog's own Search has no notion of an "active system" -
        // that gating belongs to the caller (MapPage), not this pure seam - so a Pyro-only query must
        // resolve regardless of whatever system a caller happens to be looking at.
        var results = Catalog.Search("Ruin", 10);
        var hit = Assert.Single(results);
        Assert.Equal("Ruin Station", hit.Name);
        Assert.Equal("Pyro", hit.System);
    }

    [Fact]
    public void Search_NoMatch_ReturnsEmpty()
    {
        Assert.Empty(Catalog.Search("zzznomatchzzz", 10));
    }

    [Fact]
    public void Search_NeverThrows_OnNullQuery()
    {
        Assert.Empty(Catalog.Search(null!, 10));
    }

    private static int IndexOfName(IReadOnlyList<MapObject> results, string name)
    {
        for (int i = 0; i < results.Count; i++)
            if (results[i].Name == name) return i;
        return -1;
    }

    // ── ResolvePlayerLocation (MAP tab player marker: LocationTracker.LastKnownLocation + its raw
    // Game.log token -> the MapObject it names). Resolution order: raw-token gateway mapping first
    // (the only source that can tell the Pyro-side "Stanton Gateway Station" from the Nyx-side one,
    // since the display name repeats), then exact case-insensitive name match across every system,
    // then the 3 non-ambiguous display-name aliases (Area 18, Checkmate Station, Terra Gateway
    // Station), else null. Never throws. ──

    [Fact]
    public void ResolvePlayerLocation_PlainDirectNameHit_ReturnsMatchingObject()
    {
        // "Everus Harbor" is already an exact MapObject.Name (35-of-41 direct hits) - no raw token
        // needed, no alias hop needed.
        var obj = Catalog.ResolvePlayerLocation("Everus Harbor", rawToken: null);
        Assert.NotNull(obj);
        Assert.Equal("Everus Harbor", obj!.Name);
        Assert.Equal("Stanton", obj.System);
    }

    [Fact]
    public void ResolvePlayerLocation_DirectNameHit_IsCaseInsensitive()
    {
        var obj = Catalog.ResolvePlayerLocation("everus harbor", rawToken: null);
        Assert.NotNull(obj);
        Assert.Equal("Everus Harbor", obj!.Name);
    }

    [Fact]
    public void ResolvePlayerLocation_AreaEighteenAlias_ResolvesToArea18LandingZone()
    {
        var obj = Catalog.ResolvePlayerLocation("Area 18", rawToken: null);
        Assert.NotNull(obj);
        Assert.Equal("Area18", obj!.Name);
        Assert.Equal("Stanton", obj.System);
        Assert.Equal("LandingZone", obj.Type);
    }

    [Fact]
    public void ResolvePlayerLocation_CheckmateStationAlias_ResolvesToPyroCheckmate()
    {
        var obj = Catalog.ResolvePlayerLocation("Checkmate Station", rawToken: null);
        Assert.NotNull(obj);
        Assert.Equal("Checkmate", obj!.Name);
        Assert.Equal("Pyro", obj.System);
    }

    [Fact]
    public void ResolvePlayerLocation_TerraGatewayStationAlias_ResolvesToStantonTerraGateway()
    {
        // Terra Gateway exists in game (the owner, 2026-08-01) and "Terra Gateway" names an object in
        // Stanton ONLY, so a plain display-name alias is correct here - unlike the gateways whose
        // object names repeat across systems and need the raw token to be told apart.
        var obj = Catalog.ResolvePlayerLocation("Terra Gateway Station", rawToken: null);
        Assert.NotNull(obj);
        Assert.Equal("Terra Gateway", obj!.Name);
        Assert.Equal("Stanton", obj.System);
    }

    [Fact]
    public void ResolvePlayerLocation_NyxGatewayRawToken_ResolvesToThePyroSideGate()
    {
        // Captured live 2026-08-01 at the Pyro-side Nyx gateway. "Nyx Gateway" names an object in
        // BOTH Stanton and Pyro, so the display name alone cannot place it - this is exactly the
        // ambiguity the raw-token tier exists for.
        var obj = Catalog.ResolvePlayerLocation("Nyx Gateway Station", rawToken: "RR_JP_PyroNyx");
        Assert.NotNull(obj);
        Assert.Equal("Nyx Gateway", obj!.Name);
        Assert.Equal("Pyro", obj.System);
    }

    [Fact]
    public void ResolvePlayerLocation_NyxGatewayDisplayNameAlone_DoesNotGuessASystem()
    {
        // Without the raw token there is no honest answer: the exact-name tier cannot match
        // "Nyx Gateway Station" (the objects are named "Nyx Gateway"), and the display-name alias
        // table deliberately omits it because two systems carry that object. Null beats a coin flip.
        Assert.Null(Catalog.ResolvePlayerLocation("Nyx Gateway Station", rawToken: null));
    }

    [Fact]
    public void ResolvePlayerLocation_MagnusGatewayStation_ReturnsNull_MagnusNotLive()
    {
        // No "Magnus" system and no "Magnus Gateway" object exist anywhere in starmap_map.json -
        // must resolve to nothing gracefully, never guess.
        Assert.Null(Catalog.ResolvePlayerLocation("Magnus Gateway Station", rawToken: null));
        Assert.Null(Catalog.ResolvePlayerLocation("Magnus Gateway Station", "RR_JP_StantonMagnus"));
    }

    [Fact]
    public void ResolvePlayerLocation_GatewayRawTokens_ResolveToDifferentSystems()
    {
        // The two truly ambiguous gateway display names ("Pyro Gateway Station" / "Stanton Gateway
        // Station" both exist as MapObject names in more than one system: Pyro Gateway lives in
        // Stanton AND Nyx; Stanton Gateway lives in Pyro AND Nyx). Only the raw Game.log token
        // (unique per physical gateway) can tell them apart - this is the core disambiguation proof.
        var stantonSide = Catalog.ResolvePlayerLocation("Pyro Gateway Station", "RR_JP_StantonPyro");
        var pyroSide = Catalog.ResolvePlayerLocation("Stanton Gateway Station", "RR_JP_PyroStanton");

        Assert.NotNull(stantonSide);
        Assert.NotNull(pyroSide);
        Assert.Equal("Pyro Gateway", stantonSide!.Name);
        Assert.Equal("Stanton", stantonSide.System);
        Assert.Equal("Stanton Gateway", pyroSide!.Name);
        Assert.Equal("Pyro", pyroSide.System);

        // The whole point of the raw-token tier: these two resolve to DIFFERENT systems even though
        // a naive display-name-only lookup could not tell them apart.
        Assert.NotEqual(stantonSide.System, pyroSide.System);
        Assert.NotEqual(stantonSide.Id, pyroSide.Id);
    }

    [Fact]
    public void ResolvePlayerLocation_AmbiguousGatewayDisplayName_WithoutRawToken_ReturnsNull()
    {
        // No raw token available (or an unrecognized one) for a display name that repeats across
        // systems: never guess a system for the player - null, not a coin flip.
        Assert.Null(Catalog.ResolvePlayerLocation("Pyro Gateway Station", rawToken: null));
        Assert.Null(Catalog.ResolvePlayerLocation("Stanton Gateway Station", rawToken: null));
    }

    [Fact]
    public void ResolvePlayerLocation_UngroundedGatewayToken_SharingAmbiguousDisplayName_ReturnsNull()
    {
        // RR_JP_TerraStanton normalizes to "Stanton Gateway Station" too (same display text as the
        // Pyro-side token), but it is physically in Terra - not a live system, no map object - so it
        // must not be added to the raw-token map and must not fall back to guessing Pyro or Nyx.
        Assert.Null(Catalog.ResolvePlayerLocation("Stanton Gateway Station", "RR_JP_TerraStanton"));
    }

    [Fact]
    public void ResolvePlayerLocation_NullDisplayNameAndRawToken_ReturnsNull()
    {
        Assert.Null(Catalog.ResolvePlayerLocation(null, null));
    }

    [Fact]
    public void ResolvePlayerLocation_EmptyDisplayName_ReturnsNull()
    {
        Assert.Null(Catalog.ResolvePlayerLocation("", null));
    }

    [Fact]
    public void ResolvePlayerLocation_UnknownName_ReturnsNull()
    {
        Assert.Null(Catalog.ResolvePlayerLocation("Nowhere At All", "RR_NOT_A_REAL_TOKEN"));
    }

    [Fact]
    public void ResolvePlayerLocation_UnrecognizedRawToken_FallsThroughToNameMatch()
    {
        // An unrecognized raw token must not short-circuit resolution - the display name still gets
        // its normal exact-match/alias chance.
        var obj = Catalog.ResolvePlayerLocation("Everus Harbor", "RR_NOT_A_REAL_TOKEN");
        Assert.NotNull(obj);
        Assert.Equal("Everus Harbor", obj!.Name);
    }

    [Fact]
    public void ResolvePlayerLocation_OnMalformedCatalog_NeverThrows()
    {
        using var bad = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("{ not json"));
        var catalog = MapCatalog.Load(bad);
        Assert.Null(catalog.ResolvePlayerLocation("Everus Harbor", "RR_JP_StantonPyro"));
    }
}
