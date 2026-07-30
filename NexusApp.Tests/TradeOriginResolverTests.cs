using System.Collections.Generic;
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
}
