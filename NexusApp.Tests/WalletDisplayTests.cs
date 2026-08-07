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
    public void HeaderValueIsTheEstimateOrNotSet()
    {
        Assert.Equal("5,105,256 aUEC", WalletDisplay.HeaderValue(5_105_256));
        Assert.Equal("-2,425 aUEC", WalletDisplay.HeaderValue(-2_425)); // impossible renders, never clamps
        Assert.Equal("not set", WalletDisplay.HeaderValue(null));
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
    public void UntrackedTitlePrefersTheAttributionLabel()
    {
        Assert.Equal("Security Patrol", WalletDisplay.UntrackedTitle(51_250, "Security Patrol"));
        Assert.Equal("2 contracts completed", WalletDisplay.UntrackedTitle(90_000, "2 contracts completed"));
        Assert.Equal("Untracked income", WalletDisplay.UntrackedTitle(51_250, null));
        Assert.Equal("Untracked purchase", WalletDisplay.UntrackedTitle(-7, null));
    }

    [Fact]
    public void MergeRowsInterleavesNewestFirstAndCaps()
    {
        var t1 = new CommodityTransaction { TimestampUtc = Now.AddMinutes(-40), Kind = TransactionKind.Buy, Amount = 1 };
        var t2 = new CommodityTransaction { TimestampUtc = Now.AddMinutes(-10), Kind = TransactionKind.Sell, Amount = 2 };
        var u1 = new UntrackedEntry { Utc = Now.AddMinutes(-20), Amount = 3 };
        var u2 = new UntrackedEntry { Utc = Now.AddMinutes(-5), Amount = 4 };

        var merged = WalletDisplay.MergeRows(new[] { t1, t2 }, new[] { u1, u2 }, Array.Empty<ShopPurchase>(), cap: 3);

        Assert.Equal(3, merged.Count);
        Assert.Same(u2, merged[0]); // -5 min
        Assert.Same(t2, merged[1]); // -10 min
        Assert.Same(u1, merged[2]); // -20 min; t1 fell to the cap
    }

    private static ShopPurchase P(DateTime utc, long price, int qty, string token, string shop) =>
        new()
        {
            TimestampUtc = utc, Kind = ShopTransactionKind.Buy, Price = price, Quantity = qty,
            ItemToken = token, ItemGuid = "", ShopName = shop, ShopId = "S1", KioskId = "K1",
        };

    [Fact]
    public void MergeRows_InterleavesPurchasesByTime()
    {
        var utc = new DateTime(2026, 8, 7, 13, 0, 0, DateTimeKind.Utc);
        var purchases = new[]
        {
            P(utc.AddMinutes(2), 1_470, 2, "MISL_S03_IR_VNCL_Chaos", "SCShop_Centermass_NewBabbage"),
            P(utc.AddMinutes(4), 1_150, 1, "kegr_fire_extinguisher_01", "SCShop_Shubin-001"),
        };
        var merged = WalletDisplay.MergeRows(
            Array.Empty<CommodityTransaction>(), Array.Empty<UntrackedEntry>(), purchases, 50);

        Assert.Equal(2, merged.Count);
        Assert.Equal(1_150, ((ShopPurchase)merged[0]).Price);   // newest first
        Assert.Equal(1_470, ((ShopPurchase)merged[1]).Price);
    }

    [Fact]
    public void MergeRows_RefusedPurchasesAreStillShown()
    {
        var utc = new DateTime(2026, 8, 7, 13, 0, 0, DateTimeKind.Utc);
        var p = P(utc, 500, 1, "tok", "SCShop_Test-001");
        p.Refused = "InsufficientFunds";
        var merged = WalletDisplay.MergeRows(
            Array.Empty<CommodityTransaction>(), Array.Empty<UntrackedEntry>(), new[] { p }, 50);
        Assert.Single(merged);
    }

    [Fact]
    public void ShopDisplayName_StripsThePrefixAndInstanceSuffix()
    {
        Assert.Equal("RestStop Pharmacy", WalletDisplay.ShopDisplayName("SCShop_RestStop_Pharmacy-001"));
        Assert.Equal("Centermass NewBabbage", WalletDisplay.ShopDisplayName("SCShop_Centermass_NewBabbage"));
        Assert.Equal("Admin lt base g", WalletDisplay.ShopDisplayName("SCShop_Admin_lt_base_g"));
        Assert.Equal("Odd-Name", WalletDisplay.ShopDisplayName("Odd-Name"));
    }

    // Finding 5: ShopDisplayName used to strip only the -NNN instance suffix, and
    // ProfitDisplay.ShopLabel used to strip only the _Int_X interior suffix, so the same token
    // rendered two different ways depending on which surface drew the row. Both patterns are real
    // shop-token vocabulary, so the merged cleaner applies both, and ShopDisplayName is now a thin
    // alias for ProfitDisplay.ShopLabel rather than its own rules.
    [Fact]
    public void ShopDisplayName_AlsoStripsTheInteriorSuffix()
        => Assert.Equal("Trdpst Warehouse OTLW", WalletDisplay.ShopDisplayName("SCShop_Trdpst_Warehouse_OTLW_Int_B"));

    [Theory]
    [InlineData("SCShop_RestStop_Pharmacy-001")]
    [InlineData("SCShop_Trdpst_Warehouse_OTLW_Int_B")]
    [InlineData("SCShop_Admin_lt_base_g")]
    [InlineData("Odd-Name")]
    public void ShopDisplayName_IsTheSameCleanerAsProfitDisplayShopLabel(string token)
        => Assert.Equal(ProfitDisplay.ShopLabel(token), WalletDisplay.ShopDisplayName(token));

    [Fact]
    public void PurchaseTitle_FallsBackToTheShopNameWhenUnresolved()
    {
        var p = P(new DateTime(2026, 8, 7, 13, 0, 0, DateTimeKind.Utc),
                  11_000, 1, "RADR_UNKNOWN_TOKEN", "SCShop_OmegaPro_NewBabbage");
        Assert.Equal("OmegaPro NewBabbage", WalletDisplay.PurchaseTitle(p));
    }

    // Finding 3(a): PurchaseTitle reads the name resolved once at apply time rather than resolving
    // it itself. A populated DisplayName wins outright, even over a token/shop pair that would
    // resolve or clean up differently, proving no catalog lookup happens here.
    [Fact]
    public void PurchaseTitle_PrefersAPopulatedDisplayNameOverResolvingAgain()
    {
        var p = P(new DateTime(2026, 8, 7, 13, 0, 0, DateTimeKind.Utc),
                  500, 1, "RADR_UNKNOWN_TOKEN", "SCShop_OmegaPro_NewBabbage");
        p.DisplayName = "Pre-Resolved Item Name";
        Assert.Equal("Pre-Resolved Item Name", WalletDisplay.PurchaseTitle(p));
    }

    // Trades plus untracked alone sit exactly at the cap (3 rows) and would not overflow on
    // their own. Purchases are what push the combined total to 5, proving MergeRows caps all
    // three sequences together rather than the two it merged before purchase rows existed.
    [Fact]
    public void MergeRows_PurchasesCanPushTheCombinedTotalPastTheCap()
    {
        var utc = new DateTime(2026, 8, 7, 13, 0, 0, DateTimeKind.Utc);
        var tx = new CommodityTransaction { TimestampUtc = utc.AddMinutes(-50), Kind = TransactionKind.Buy, Amount = 1 };
        var u1 = new UntrackedEntry { Utc = utc.AddMinutes(-40), Amount = 2 };
        var u2 = new UntrackedEntry { Utc = utc.AddMinutes(-30), Amount = 3 };
        var p1 = P(utc.AddMinutes(-20), 100, 1, "tokA", "SCShop_A-001");
        var p2 = P(utc.AddMinutes(-10), 200, 1, "tokB", "SCShop_B-001");

        var merged = WalletDisplay.MergeRows(new[] { tx }, new[] { u1, u2 }, new[] { p1, p2 }, cap: 3);

        Assert.Equal(3, merged.Count);
        Assert.Same(p2, merged[0]);   // -10 min, newest
        Assert.Same(p1, merged[1]);   // -20 min
        Assert.Same(u2, merged[2]);   // -30 min; tx and u1 fell to the cap
    }

    // ProfitDisplay.LedgerHiddenCount only ever sees the int its caller hands it; it cannot tell
    // whether that total included purchases. This pins the arithmetic against a purchase-inclusive
    // total (the exact regression shape: 30 trades, 5 untracked, 40 purchases), but it cannot
    // reach TradePage.Profit.cs's own call site directly, since that WPF view code does not
    // compile in this test project. The merge-side test above covers the row-dropping behaviour;
    // this one covers only the fold-note arithmetic in isolation.
    [Fact]
    public void LedgerHiddenCount_AccountsForAPurchaseInclusiveTotal()
    {
        const int txs = 30, untracked = 5, purchases = 40;
        Assert.Equal(25, ProfitDisplay.LedgerHiddenCount(txs + untracked + purchases));
    }
}
