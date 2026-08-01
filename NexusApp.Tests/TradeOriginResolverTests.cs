using System.Collections.Generic;
using System.Linq;
using NexusApp.Services;
using NexusApp.Views;
using Xunit;

namespace NexusApp.Tests;

public class TradeOriginResolverTests
{
    private static readonly List<MarketTerminal> Terminals = new()
    {
        new(1, "CRU-L4 Shallow Fields", "trading", false, "Stanton", "Crusader"),
        new(2, "CRU-L1 Ambitious Dream", "trading", false, "Stanton", "Crusader"),
        new(3, "Everus Harbor", "trading", true,  "Stanton", "Hurston"),
        new(4, "Baijini Point", "trading", false, "Stanton", "microTech"),
    };

    [Fact]
    public void TerminalIdForName_ExactCaseInsensitiveMatch()
        => Assert.Equal(3, TradeOriginResolver.TerminalIdForName("everus harbor", Terminals));

    [Fact]
    public void TerminalIdForName_NoMatch_ReturnsNull()
        => Assert.Null(TradeOriginResolver.TerminalIdForName("Nowhere Station", Terminals));

    [Fact]
    public void TerminalIdForName_NullOrEmpty_ReturnsNull()
    {
        Assert.Null(TradeOriginResolver.TerminalIdForName(null, Terminals));
        Assert.Null(TradeOriginResolver.TerminalIdForName("", Terminals));
    }

    [Fact]
    public void TerminalIdsForLocation_MatchesEveryTerminalAtThatLocation()
    {
        var ids = TradeOriginResolver.TerminalIdsForLocation("Crusader", Terminals);
        Assert.Equal(new HashSet<int> { 1, 2 }, ids);
    }

    [Fact]
    public void TerminalIdsForLocation_FallsBackToNameContains_WhenNoExactLocationMatch()
    {
        // LocationTracker's raw key can be looser than a Location string (e.g. a station slug);
        // a substring match against Name/Location is the honest best-effort fallback.
        var ids = TradeOriginResolver.TerminalIdsForLocation("CRU-L4", Terminals);
        Assert.Equal(new HashSet<int> { 1 }, ids);
    }

    [Fact]
    public void TerminalIdsForLocation_NoMatchAnywhere_ReturnsEmpty_NotAGuess()
        => Assert.Empty(TradeOriginResolver.TerminalIdsForLocation("Nyx Outpost", Terminals));

    [Fact]
    public void TerminalIdsForLocation_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Empty(TradeOriginResolver.TerminalIdsForLocation(null, Terminals));
        Assert.Empty(TradeOriginResolver.TerminalIdsForLocation("", Terminals));
    }

    // Route planner STARTING LOCATION seam (task 10): TradeOriginResolver.StartTerminalIds is what
    // RebuildPlanner calls to turn the combo's persisted kind into RoutePlanner's terminal id set.
    [Fact]
    public void StartTerminalIds_Any_ReturnsNull()
        => Assert.Null(TradeOriginResolver.StartTerminalIds("ANY", "Crusader", Terminals));

    [Fact]
    public void StartTerminalIds_NullOrEmpty_FailsOpenToNull_SameAsAny()
    {
        Assert.Null(TradeOriginResolver.StartTerminalIds(null, "Crusader", Terminals));
        Assert.Null(TradeOriginResolver.StartTerminalIds("", "Crusader", Terminals));
    }

    [Fact]
    public void StartTerminalIds_LiveWithResolvableLocation_ReturnsTheSet()
    {
        var ids = TradeOriginResolver.StartTerminalIds("LIVE", "Crusader", Terminals);
        Assert.Equal(new HashSet<int> { 1, 2 }, ids);
    }

    [Fact]
    public void StartTerminalIds_LiveWithNullLocation_ReturnsEmpty()
        => Assert.Empty(TradeOriginResolver.StartTerminalIds("LIVE", null, Terminals)!);

    [Fact]
    public void StartTerminalIds_TerminalName_ReturnsSingleIdSet()
        => Assert.Equal(new HashSet<int> { 3 }, TradeOriginResolver.StartTerminalIds("Everus Harbor", null, Terminals));

    [Fact]
    public void StartTerminalIds_UnknownName_ReturnsEmpty()
        => Assert.Empty(TradeOriginResolver.StartTerminalIds("Nowhere Station", null, Terminals)!);

    // ---- Live gateway origin fix (owner's live bug, 2026-07-31). Root cause: LocationTracker
    // stores the in-game DISPLAY name ("Pyro Gateway Station"), but UEX's own Location/Name
    // vocabulary for the same station is "Pyro Gateway (Stanton)" - a string that appears
    // nowhere inside the display name, so neither the exact nor substring pass above could ever
    // connect them. TerminalIdsForLocation/StartTerminalIds now take an optional uexLocation
    // hint (LocationTracker.LastKnownUexLocation, resolved from the raw Game.log token via
    // LocationAliases.UexLocationForToken) and try it first.
    //
    // A slice of the real UEX terminal vocabulary for the Stanton<->Pyro gateway (matches the
    // Locations verified live against NexusApp/Data/location_aliases.json's uexLocations block).
    // Separate ids/list from Terminals above so the two fixtures never collide.
    private static readonly List<MarketTerminal> GatewayTerminals = new()
    {
        new(101, "Admin - Pyro Gateway (Stanton)", "commodity", false, "Stanton", "Pyro Gateway (Stanton)"),
        new(102, "Platinum Bay - Pyro Gateway (Stanton)", "commodity", false, "Stanton", "Pyro Gateway (Stanton)"),
        new(103, "Admin - Stanton Gateway (Pyro)", "commodity", false, "Pyro", "Stanton Gateway (Pyro)"),
        new(104, "Platinum Bay - Stanton Gateway (Pyro)", "commodity", false, "Pyro", "Stanton Gateway (Pyro)"),
    };

    [Fact]
    public void TerminalIdsForLocation_UexLocationHint_ResolvesWhenDisplayNameMatchesNothing()
    {
        // "Pyro Gateway Station" (the display name) does not equal, and is not a substring of
        // (nor contains), "Pyro Gateway (Stanton)" (the real UEX Location) - without the hint
        // this is exactly the owner's bug: a real, correctly-identified station resolving to zero
        // terminals.
        var ids = TradeOriginResolver.TerminalIdsForLocation("Pyro Gateway Station", GatewayTerminals, "Pyro Gateway (Stanton)");
        Assert.Equal(new HashSet<int> { 101, 102 }, ids);
    }

    [Fact]
    public void TerminalIdsForLocation_UexLocationHint_TakesPriorityOverADecoyDisplayMatch()
    {
        // A decoy terminal whose Location happens to equal the raw display name text - if the
        // hint pass did not run first (or ran but wasn't checked first), the old exact-match pass
        // would find this decoy instead of the real gateway terminals.
        var withDecoy = new List<MarketTerminal>(GatewayTerminals)
        {
            new(999, "Some Other Desk", "commodity", false, "Nowhere", "Pyro Gateway Station"),
        };
        var ids = TradeOriginResolver.TerminalIdsForLocation("Pyro Gateway Station", withDecoy, "Pyro Gateway (Stanton)");
        Assert.Equal(new HashSet<int> { 101, 102 }, ids);
    }

    // The ambiguity this whole fix has to respect: RR_JP_PyroStanton and RR_JP_TerraStanton both
    // Normalize to the SAME display name ("Stanton Gateway Station" - LocationAliasesTests proves
    // LocationAliases itself never conflates their UEX Locations). This proves the terminal
    // resolution built on top doesn't conflate them either - two different tokens sharing a
    // display name resolve to two DIFFERENT terminal sets, never the same (wrong-system) one.
    [Fact]
    public void TerminalIdsForLocation_AmbiguousDisplayName_DifferentTokensResolveToDifferentSets()
    {
        var pyroSideUex = LocationAliases.UexLocationForToken("RR_JP_PyroStanton");    // grounded
        var terraSideUex = LocationAliases.UexLocationForToken("RR_JP_TerraStanton");  // not live: null

        var pyroSideIds = TradeOriginResolver.TerminalIdsForLocation("Stanton Gateway Station", GatewayTerminals, pyroSideUex);
        var terraSideIds = TradeOriginResolver.TerminalIdsForLocation("Stanton Gateway Station", GatewayTerminals, terraSideUex);

        Assert.Equal(new HashSet<int> { 103, 104 }, pyroSideIds);
        Assert.Empty(terraSideIds);   // no live Terra terminal exists - honestly empty, never a guess
        Assert.NotEqual(pyroSideIds, terraSideIds);
    }

    [Fact]
    public void TerminalIdsForLocation_UexLocationHint_FallsBackToDisplayMatch_WhenHintMatchesNothing()
    {
        // A hint that resolves to zero terminals must not black-hole the whole lookup - falls
        // through to the pre-existing exact/substring passes on locationLabel, unchanged.
        var ids = TradeOriginResolver.TerminalIdsForLocation("Crusader", Terminals, "Some UEX Location That Does Not Exist");
        Assert.Equal(new HashSet<int> { 1, 2 }, ids);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TerminalIdsForLocation_NoUexLocationHint_FallsThroughToTodaysExactSubstringBehavior(string? hint)
    {
        // Regression guard for the other ~38 aliases, which already exact-match a real UEX
        // Location and never carry a hint at all: unchanged behavior, exact match wins.
        var ids = TradeOriginResolver.TerminalIdsForLocation("Crusader", Terminals, hint);
        Assert.Equal(new HashSet<int> { 1, 2 }, ids);
    }

    [Fact]
    public void StartTerminalIds_Live_PassesUexLocationHintThrough_ResolvesTheGatewayBug()
    {
        var ids = TradeOriginResolver.StartTerminalIds("LIVE", "Pyro Gateway Station", GatewayTerminals, "Pyro Gateway (Stanton)");
        Assert.Equal(new HashSet<int> { 101, 102 }, ids);
    }

    [Fact]
    public void StartTerminalIds_Live_NoUexLocationHint_UnchangedForNonGatewayLocations()
        => Assert.Equal(new HashSet<int> { 1, 2 }, TradeOriginResolver.StartTerminalIds("LIVE", "Crusader", Terminals));

    // LocationFirst (owner's ask, 2026-07-31): flips UEX's Shop-first terminal names to
    // location-first for DISPLAY only, so the STARTING LOCATION / DESTINATION pickers group by
    // where a terminal actually is instead of clumping every station's "Admin" office together.
    // Never touches persistence or resolution - this is a pure string transform, tested in
    // isolation from TradePage.Planner.cs's combo wiring.

    [Theory]
    [InlineData("Ashland", "Ashland")]
    [InlineData("ArcCorp Mining Area 045", "ArcCorp Mining Area 045")]
    public void LocationFirst_OneSegment_LeftExactlyAsIs(string input, string expected)
        => Assert.Equal(expected, TradeOriginResolver.LocationFirst(input));

    [Theory]
    [InlineData("Admin - ARC-L1", "ARC-L1 - Admin")]
    [InlineData("Platinum Bay - Everus Harbor", "Everus Harbor - Platinum Bay")]
    public void LocationFirst_TwoSegments_FlipsShopAndLocation(string input, string expected)
        => Assert.Equal(expected, TradeOriginResolver.LocationFirst(input));

    [Fact]
    public void LocationFirst_ThreeSegments_CollapsesToFirstAndLast()
        => Assert.Equal("Orison - Orison Municipal Services",
            TradeOriginResolver.LocationFirst("Orison Municipal Services - Providence Platform - Orison"));

    [Theory]
    [InlineData("Admin - L19 Residences - Metro Center - Lorville", "Lorville - Admin")]
    [InlineData("TDD - Trade and Development Division - Commons - New Babbage", "New Babbage - TDD")]
    public void LocationFirst_FourSegments_CollapsesToFirstAndLast_DroppingMiddle(string input, string expected)
        => Assert.Equal(expected, TradeOriginResolver.LocationFirst(input));

    [Fact]
    public void LocationFirst_TrimsWhitespaceOnEachSegment()
        => Assert.Equal("ARC-L1 - Admin", TradeOriginResolver.LocationFirst("  Admin - ARC-L1  "));

    [Fact]
    public void LocationFirst_NullOrEmpty_ReturnsInputUnchanged_NeverThrows()
    {
        Assert.Null(TradeOriginResolver.LocationFirst(null));
        Assert.Equal("", TradeOriginResolver.LocationFirst(""));
    }

    // Collision fixture (owner verified zero collisions across all 135 live-snapshot terminals):
    // a smaller fixture covering the deep-name examples plus a spread of 1/2/3/4-segment shapes,
    // asserting the flip stays injective - no two distinct raw names ever land on the same display.
    private static readonly string[] CollisionFixture =
    {
        "Ashland",
        "ArcCorp Mining Area 045",
        "Admin - ARC-L1",
        "Admin - ARC-L2",
        "Platinum Bay - Everus Harbor",
        "Admin - L19 Residences - Metro Center - Lorville",
        "TDD - Trade and Development Division - Commons - New Babbage",
        "Orison Municipal Services - Providence Platform - Orison",
        "Admin - Baijini Point",
        "Admin - CRU-L1 Ambitious Dream",
        "Cargo Center - CRU-L1 Ambitious Dream",
    };

    [Fact]
    public void LocationFirst_CollisionFreeOverFixture()
    {
        var displays = CollisionFixture.Select(n => TradeOriginResolver.LocationFirst(n)!).ToList();
        Assert.Equal(CollisionFixture.Length, displays.Distinct(System.StringComparer.Ordinal).Count());
    }
}
