using NexusApp.Views;
using Xunit;

namespace NexusApp.Tests;

// MapPage.ParseLayers/FormatLayers back the MAP tab's persisted view state. Before this, the system
// and every layer toggle were plain constructor fields, so a Pyro player re-picked Pyro and
// re-flipped every toggle on each launch while every neighbouring surface (TradeStartManual,
// TradeDestManual, the Codex sell column) persisted. Pure statics, so the round-trip and the
// first-run rule are testable without a WPF tree.
//
// Seven layers since app review G11 added MY HAULS and MY ORDERS.
public class MapPageViewStateTests
{
    [Fact]
    public void NeverSaved_TurnsOnMiningAndAsteroids_ButNotTradeWithoutConsent()
    {
        // First run with market data off. TRADE stays off because its row hides entirely under the
        // consent gate, and defaulting a layer whose row is invisible to ON is not something the
        // user could make sense of.
        var (trade, guides, mining, hangar, asteroids, _, _) = MapPage.ParseLayers(null, marketConsent: false);
        Assert.False(trade);
        Assert.False(guides);
        Assert.True(mining);
        Assert.False(hangar);
        Assert.True(asteroids);
    }

    [Fact]
    public void NeverSaved_TurnsOnBothLiveStateLayers()
    {
        // They need no consent and no network, and their rows hide themselves when the player has
        // nothing running - so defaulting them off would mean a layer that is invisible when empty
        // AND off when populated, which is a feature nobody would ever find.
        var (_, _, _, _, _, hauls, orders) = MapPage.ParseLayers(null, marketConsent: false);
        Assert.True(hauls);
        Assert.True(orders);
    }

    [Fact]
    public void NeverSaved_WithConsentAlreadyGranted_TurnsTradeOn()
    {
        var (trade, _, mining, _, asteroids, _, _) = MapPage.ParseLayers(null, marketConsent: true);
        Assert.True(trade);
        Assert.True(mining);
        Assert.True(asteroids);
    }

    [Fact]
    public void EmptyStringIsARealSavedState_NotAReSeed()
    {
        // The distinction that makes null meaningful: a user who switched everything off must find
        // everything still off next launch, not the first-run defaults handed back.
        var (trade, guides, mining, hangar, asteroids, hauls, orders) = MapPage.ParseLayers("", marketConsent: true);
        Assert.False(trade);
        Assert.False(guides);
        Assert.False(mining);
        Assert.False(hangar);
        Assert.False(asteroids);
        Assert.False(hauls);
        Assert.False(orders);
    }

    [Theory]
    [InlineData(true, false, false, false, false, false, false)]
    [InlineData(false, true, false, false, false, false, false)]
    [InlineData(false, false, true, false, false, false, false)]
    [InlineData(false, false, false, true, false, false, false)]
    [InlineData(false, false, false, false, true, false, false)]
    [InlineData(false, false, false, false, false, true, false)]
    [InlineData(false, false, false, false, false, false, true)]
    [InlineData(true, true, true, true, true, true, true)]
    [InlineData(false, false, false, false, false, false, false)]
    [InlineData(true, false, true, false, true, false, true)]
    public void EveryCombinationRoundTrips(bool t, bool g, bool m, bool h, bool a, bool hl, bool o)
    {
        var saved = MapPage.FormatLayers(t, g, m, h, a, hl, o);
        var back = MapPage.ParseLayers(saved, marketConsent: false);
        Assert.Equal((t, g, m, h, a, hl, o), back);
    }

    [Fact]
    public void FormatNeverReturnsNull_SoAllOffCannotBeMistakenForNeverSaved()
    {
        Assert.Equal("", MapPage.FormatLayers(false, false, false, false, false, false, false));
    }

    // A settings.json written before G11 has no hauls/orders keys. It must read as a real saved
    // state with those two OFF, NOT fall through to the first-run defaults - otherwise upgrading
    // would silently switch layers back on for a user who had deliberately turned things off.
    [Fact]
    public void SettingsWrittenBeforeTheLiveLayersExisted_LeaveThemOff()
    {
        var (trade, _, mining, _, asteroids, hauls, orders) =
            MapPage.ParseLayers("trade,mining,asteroids", marketConsent: true);
        Assert.True(trade);
        Assert.True(mining);
        Assert.True(asteroids);
        Assert.False(hauls);
        Assert.False(orders);
    }

    [Fact]
    public void UnknownKeysAreIgnored_AndOrderDoesNotMatter()
    {
        // Defensive against a hand-edited settings.json or a layer key retired in a later release.
        var (trade, guides, mining, hangar, asteroids, _, _) =
            MapPage.ParseLayers("asteroids, nonsense ,trade", marketConsent: false);
        Assert.True(trade);
        Assert.True(asteroids);
        Assert.False(guides);
        Assert.False(mining);
        Assert.False(hangar);
    }

    [Fact]
    public void ParsingIsCaseInsensitiveAndToleratesStrayWhitespace()
    {
        var (trade, _, mining, _, _, hauls, _) = MapPage.ParseLayers("  TRADE , Mining , HAULS ", marketConsent: false);
        Assert.True(trade);
        Assert.True(mining);
        Assert.True(hauls);
    }
}
