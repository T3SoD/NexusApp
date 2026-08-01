using NexusApp.Views;
using Xunit;

namespace NexusApp.Tests;

// MapPage.TradeHint (internal, InternalsVisibleTo NexusApp.Tests via NexusApp.csproj) is the pure
// copy decision behind the SELECTION zone's gated-TRADE-layer hint. B7 put the one-click market
// consent strip on the MAP tab, which made the previous single static string ("...(Settings).")
// wrong in two of its three reachable states: with the question unanswered the strip is directly
// above the map, and with consent already granted there is nothing in Settings left to change.
// Keyed off MarketNotice.ShouldShowConsent rather than a second copy of that gate, so the wording
// cannot outlive the strip - including in the demo profile, where the strip is suppressed.
public class MapPageTradeHintTests
{
    [Fact]
    public void Unanswered_PointsAtTheStripAbove()
    {
        // The strip is showing (consent null, not the demo profile), so the one-click answer is
        // right there - sending the user to Settings would be the long way round.
        var hint = MapPage.TradeHint(consent: null, isDemoProfile: false);
        Assert.Contains("strip above", hint);
        Assert.DoesNotContain("Settings", hint);
    }

    [Fact]
    public void Declined_PointsAtSettings()
    {
        // The strip never returns once answered, so Settings is genuinely the only way back.
        var hint = MapPage.TradeHint(consent: false, isDemoProfile: false);
        Assert.Contains("Settings", hint);
        Assert.DoesNotContain("strip above", hint);
    }

    [Fact]
    public void EnabledButNoSnapshotYet_SaysItIsWaiting_NotThatSomethingNeedsTurningOn()
    {
        // The only way this text shows with consent granted is TradeGated's other half: no
        // snapshot yet. Telling the user to enable an already-enabled setting is the bug.
        var hint = MapPage.TradeHint(consent: true, isDemoProfile: false);
        Assert.Contains("waiting", hint);
        Assert.DoesNotContain("Settings", hint);
        Assert.DoesNotContain("strip above", hint);
    }

    [Fact]
    public void DemoProfileUnanswered_PointsAtSettings_BecauseTheStripIsSuppressedThere()
    {
        // MarketNotice.ShouldShowConsent suppresses the strip in the demo profile, so "turn it on
        // above" would point at nothing. This is the case a hand-rolled null check would miss.
        var hint = MapPage.TradeHint(consent: null, isDemoProfile: true);
        Assert.Contains("Settings", hint);
        Assert.DoesNotContain("strip above", hint);
    }
}
