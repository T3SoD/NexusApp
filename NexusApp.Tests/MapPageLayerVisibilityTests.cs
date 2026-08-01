using NexusApp.Views;
using Xunit;

namespace NexusApp.Tests;

// MapPage.LayerRowVisible (internal, InternalsVisibleTo NexusApp.Tests via NexusApp.csproj:47) is
// the pure decision behind the MAP tab's LAYERS zone row-hiding fix: a row with nothing in the
// active system (e.g. EXEC HANGAR showing 0 in Stanton/Nyx - the game has exactly one Executive
// Hangar site, in Pyro) should be hidden rather than shown as a dead 0-count toggle. TRADE is the
// one exception - a 0 there can mean "consent off" or "no snapshot yet" (TradeGated), a state the
// SELECTION zone's hint text exists to explain, so TRADE must stay reachable to turn on. This is
// WPF-tree-adjacent (feeds a Border.Visibility set in RefreshLayerCounts) but the decision itself
// has no WPF dependency, so it is extracted and tested here rather than only traced by hand.
public class MapPageLayerVisibilityTests
{
    [Theory]
    [InlineData("guides", 0, false)]
    [InlineData("mining", 0, true)]   // gated is irrelevant to non-trade rows
    [InlineData("hangar", 0, false)]
    [InlineData("asteroids", 0, false)]
    public void NonTradeRow_ZeroCount_Hidden(string key, int count, bool gated)
    {
        Assert.False(MapPage.LayerRowVisible(key, count, gated));
    }

    [Theory]
    [InlineData("guides", 1, false)]
    [InlineData("mining", 96, false)]
    [InlineData("hangar", 1, false)]
    [InlineData("asteroids", 158, false)]
    public void NonTradeRow_PositiveCount_Visible(string key, int count, bool gated)
    {
        Assert.True(MapPage.LayerRowVisible(key, count, gated));
    }

    // EXEC HANGAR: the reported live-use bug. Zero in Stanton/Nyx, one in Pyro.
    [Fact]
    public void Hangar_ZeroInStantonOrNyx_Hidden()
    {
        Assert.False(MapPage.LayerRowVisible("hangar", 0, tradeGated: false));
    }

    [Fact]
    public void Hangar_OneInPyro_Visible()
    {
        Assert.True(MapPage.LayerRowVisible("hangar", 1, tradeGated: false));
    }

    // TRADE: the critical exception. A 0 count while gated (consent off / no snapshot) must stay
    // reachable so the SELECTION zone's "Trade layer needs market data (Settings)." hint is not
    // orphaned behind a hidden row.
    [Fact]
    public void Trade_ZeroCount_Gated_StaysVisible()
    {
        Assert.True(MapPage.LayerRowVisible("trade", 0, tradeGated: true));
    }

    // TRADE only hides once data is actually available (ungated) and the system genuinely has none.
    [Fact]
    public void Trade_ZeroCount_Ungated_Hidden()
    {
        Assert.False(MapPage.LayerRowVisible("trade", 0, tradeGated: false));
    }

    [Fact]
    public void Trade_PositiveCount_UngatedOrGated_Visible()
    {
        Assert.True(MapPage.LayerRowVisible("trade", 5, tradeGated: false));
        Assert.True(MapPage.LayerRowVisible("trade", 5, tradeGated: true));   // stale count, gate flipped mid-refresh: still visible
    }

    [Fact]
    public void Trade_KeyMatch_IsCaseInsensitive()
    {
        Assert.True(MapPage.LayerRowVisible("TRADE", 0, tradeGated: true));
    }
}
