using NexusApp.Models;
using NexusApp.Services;
using NexusApp.Views;
using Xunit;

namespace NexusApp.Tests;

public class WalletDisplayTests
{
    private static readonly DateTime Now = new(2026, 8, 6, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void NoAnchorIsNotSetEvenOffline()
    {
        Assert.Equal(WalletUiState.NotSet,
            WalletDisplay.State(hasAnchor: false, estimate: null, anchorUtc: null, Now, sessionLive: false));
    }

    [Fact]
    public void NegativeEstimateIsImpossibleEvenOffline()
    {
        // The mock's derivation order: a provably wrong number outranks the offline dimming.
        Assert.Equal(WalletUiState.Impossible,
            WalletDisplay.State(true, -5, Now.AddMinutes(-2), Now, sessionLive: false));
    }

    [Fact]
    public void OfflineBeatsAging()
    {
        Assert.Equal(WalletUiState.Offline,
            WalletDisplay.State(true, 100, Now.AddHours(-5), Now, sessionLive: false));
    }

    [Fact]
    public void AgingStartsExactlyAtTheThreshold()
    {
        var atThreshold = Now - WalletTracker.AgingThreshold;
        Assert.Equal(WalletUiState.Aging, WalletDisplay.State(true, 100, atThreshold, Now, true));
        Assert.Equal(WalletUiState.Current,
            WalletDisplay.State(true, 100, atThreshold.AddSeconds(1), Now, true));
    }

    [Fact]
    public void ProvenanceNamesTheSourceAndAge()
    {
        Assert.Equal("captured from mobiGlas 4m ago",
            WalletDisplay.Provenance("Ocr", Now.AddMinutes(-4), Now));
        Assert.Equal("manual entry, 2h 5m ago",
            WalletDisplay.Provenance("Manual", Now.AddMinutes(-125), Now));
        Assert.Equal("captured from mobiGlas moments ago",
            WalletDisplay.Provenance("Ocr", Now.AddSeconds(-10), Now));
        Assert.Equal(WalletDisplay.SetupHint, WalletDisplay.Provenance(null, null, Now));
    }

    [Fact]
    public void UntrackedTitleFollowsTheSign()
    {
        Assert.Equal("Untracked income", WalletDisplay.UntrackedTitle(80_000));
        Assert.Equal("Untracked purchase", WalletDisplay.UntrackedTitle(-80_000));
    }

    [Fact]
    public void SnapshotLineCarriesPresenceOnlyNeverAmounts()
    {
        var line = WalletDisplay.SnapshotLine(true, "Ocr", Now.AddMinutes(-4), Now, untrackedCount: 3);
        Assert.Equal("anchored (Ocr, 4m ago), 3 untracked this session", line);
        Assert.Equal("not anchored", WalletDisplay.SnapshotLine(false, null, null, Now, 0));
        // A balance could only surface as a long digit run; ages and counts never reach four digits.
        Assert.DoesNotMatch("[0-9]{4,}", line);
    }

    [Fact]
    public void MergeRowsInterleavesNewestFirstAndCaps()
    {
        var t1 = new CommodityTransaction { TimestampUtc = Now.AddMinutes(-40), Kind = TransactionKind.Buy, Amount = 1 };
        var t2 = new CommodityTransaction { TimestampUtc = Now.AddMinutes(-10), Kind = TransactionKind.Sell, Amount = 2 };
        var u1 = new UntrackedEntry { Utc = Now.AddMinutes(-20), Amount = 3 };
        var u2 = new UntrackedEntry { Utc = Now.AddMinutes(-5), Amount = 4 };

        var merged = WalletDisplay.MergeRows(new[] { t1, t2 }, new[] { u1, u2 }, cap: 3);

        Assert.Equal(3, merged.Count);
        Assert.Same(u2, merged[0]); // -5 min
        Assert.Same(t2, merged[1]); // -10 min
        Assert.Same(u1, merged[2]); // -20 min; t1 fell to the cap
    }
}
