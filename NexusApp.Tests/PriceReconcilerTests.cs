using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

public class PriceReconcilerTests
{
    private static readonly DateTime NowUtc = new(2026, 7, 29, 12, 0, 0, DateTimeKind.Utc);

    private static TradePriceRow Row(double buy, double sell, DateTime modifiedUtc, string commodityName = "Laranite") =>
        new(TerminalId: 778, CommodityId: 44, Buy: buy, Sell: sell, BuyStockScu: 100, SellDemandScu: 0,
            StatusBuy: 1, StatusSell: 1, ContainerSizes: "1,2,4,8,16,24,32", ModifiedUtc: modifiedUtc,
            TerminalName: "Nyx > Levski", CommodityName: commodityName);

    private static SctListing Listing(double price, DateTime timestampUtc, string commodity = "laranite") =>
        new("nyx > levski", "BUYS", commodity, price, 0, 0.0, timestampUtc);

    [Fact]
    public void Reconcile_BothNull_ReturnsNull()
        => Assert.Null(PriceReconciler.Reconcile(null, "buy", null, NowUtc));

    [Fact]
    public void Reconcile_UexOnly_ReturnsUexOnlyState()
    {
        var row = Row(buy: 7800, sell: 0, modifiedUtc: NowUtc.AddHours(-2));
        var r = PriceReconciler.Reconcile(row, "buy", null, NowUtc);
        Assert.NotNull(r);
        Assert.Equal(7800, r!.Value);
        Assert.Equal(PriceSourceState.UexOnly, r.State);
        Assert.Null(r.SctTimestampUtc);
    }

    [Fact]
    public void Reconcile_SctOnly_ReturnsSctOnlyState_ValueFromSct()
    {
        var sct = Listing(price: 7800, timestampUtc: NowUtc.AddHours(-1));
        var r = PriceReconciler.Reconcile(null, "buy", sct, NowUtc);
        Assert.NotNull(r);
        Assert.Equal(7800, r!.Value);
        Assert.Equal(PriceSourceState.SctOnly, r.State);
        Assert.Equal(default, r.UexModifiedUtc);
    }

    // Real fixture: sc-trade-tools-api-recon.md section 7 - "Levski 7,800 == 7,800 (0.0%)".
    [Fact]
    public void Reconcile_RealLaranite_Levski_ExactAgreement_Corroborated()
    {
        var row = Row(buy: 7800, sell: 0, modifiedUtc: NowUtc.AddHours(-2));
        var sct = Listing(price: 7800, timestampUtc: NowUtc.AddHours(-3));
        var r = PriceReconciler.Reconcile(row, "buy", sct, NowUtc);
        Assert.NotNull(r);
        Assert.Equal(7800, r!.Value);           // UEX's own number, even in agreement
        Assert.Equal(PriceSourceState.Corroborated, r.State);
        Assert.Equal(0.0, r.DisagreePct, precision: 5);
    }

    // Real fixture: same doc - "Stanton Gateway 8,700 vs 8,800 (+1.1%, UEX row modified ~10h
    // before the SCT observation)". +1.1% is within the 3% threshold: still Corroborated.
    [Fact]
    public void Reconcile_RealLaranite_StantonGateway_SmallDisagreement_StillCorroborated()
    {
        var row = Row(buy: 8700, sell: 0, modifiedUtc: NowUtc.AddHours(-10));
        var sct = Listing(price: 8800, timestampUtc: NowUtc);
        var r = PriceReconciler.Reconcile(row, "buy", sct, NowUtc);
        Assert.NotNull(r);
        Assert.Equal(8700, r!.Value);            // value stays UEX's, per the mock's tooltip
        Assert.Equal(PriceSourceState.Corroborated, r.State);
        Assert.True(r.DisagreePct is > 1.0 and < 1.2, $"expected ~1.1%, got {r.DisagreePct}");
    }

    // Real-magnitude fixture (divergence-benchmark's own "crowdsourcing input error" example):
    // quantum fuel 8,978 (UEX) vs 978 (SCT, prepended-digit typo) - both assumed fresh here so the
    // Disagree branch itself is what's under test, not the freshness gate.
    [Fact]
    public void Reconcile_RealDivergenceExample_QuantumFuel_Disagree()
    {
        var row = Row(buy: 8978, sell: 0, modifiedUtc: NowUtc.AddHours(-1));
        var sct = Listing(price: 978, timestampUtc: NowUtc);
        var r = PriceReconciler.Reconcile(row, "buy", sct, NowUtc);
        Assert.NotNull(r);
        Assert.Equal(8978, r!.Value);
        Assert.Equal(PriceSourceState.Disagree, r.State);
        Assert.True(r.DisagreePct > 25, $"expected a large divergence, got {r.DisagreePct}");
    }

    [Theory]
    [InlineData(10299, PriceSourceState.Corroborated)]   // +2.99%
    [InlineData(10300, PriceSourceState.Corroborated)]   // +3.00% exactly: inclusive boundary
    [InlineData(10301, PriceSourceState.Disagree)]        // +3.01%
    public void Reconcile_ThresholdEdges_2_99_3_00_3_01(double sctPrice, PriceSourceState expected)
    {
        var row = Row(buy: 10000, sell: 0, modifiedUtc: NowUtc.AddHours(-1));
        var sct = Listing(price: sctPrice, timestampUtc: NowUtc);
        var r = PriceReconciler.Reconcile(row, "buy", sct, NowUtc);
        Assert.Equal(expected, r!.State);
    }

    [Fact]
    public void Reconcile_BothFresh_47h59m_StillCorroborated()
    {
        var row = Row(buy: 7800, sell: 0, modifiedUtc: NowUtc - TimeSpan.FromHours(47).Add(TimeSpan.FromMinutes(59)));
        var sct = Listing(price: 7800, timestampUtc: NowUtc - TimeSpan.FromHours(47).Add(TimeSpan.FromMinutes(59)));
        var r = PriceReconciler.Reconcile(row, "buy", sct, NowUtc);
        Assert.Equal(PriceSourceState.Corroborated, r!.State);
    }

    [Fact]
    public void Reconcile_UexStale_48h01m_DegradesToUexOnly()
    {
        // Even though the two prices agree exactly (0%), a stale second source corroborates
        // nothing - this must NOT read as Corroborated.
        var row = Row(buy: 7800, sell: 0, modifiedUtc: NowUtc - TimeSpan.FromHours(48).Add(TimeSpan.FromMinutes(1)));
        var sct = Listing(price: 7800, timestampUtc: NowUtc.AddHours(-1));
        var r = PriceReconciler.Reconcile(row, "buy", sct, NowUtc);
        Assert.Equal(PriceSourceState.UexOnly, r!.State);
    }

    [Fact]
    public void Reconcile_SctStale_48h01m_DegradesToUexOnly()
    {
        var row = Row(buy: 7800, sell: 0, modifiedUtc: NowUtc.AddHours(-1));
        var sct = Listing(price: 7800, timestampUtc: NowUtc - TimeSpan.FromHours(48).Add(TimeSpan.FromMinutes(1)));
        var r = PriceReconciler.Reconcile(row, "buy", sct, NowUtc);
        Assert.Equal(PriceSourceState.UexOnly, r!.State);
    }

    [Fact]
    public void Reconcile_SellSide_ReadsSellNotBuy()
    {
        var row = Row(buy: 100, sell: 250, modifiedUtc: NowUtc.AddHours(-1));
        var r = PriceReconciler.Reconcile(row, "sell", null, NowUtc);
        Assert.Equal(250, r!.Value);
    }

    // Architect resolution (2026-07-29, binding): Ship Ammunition commodities never receive
    // corroboration. UEX titles these "Ship Ammunition - Size N"; SCT lower-cases them "ship
    // ammunition - size N" - the size-tier split never lines up cleanly enough across sources for
    // an agree/disagree reading to mean anything, so Reconcile forces UexOnly whenever the UEX
    // row exists, no matter what SCT says.
    [Fact]
    public void Reconcile_ShipAmmunition_UexPresent_ExactAgreement_StaysUexOnly()
    {
        var row = Row(buy: 7800, sell: 0, modifiedUtc: NowUtc.AddHours(-1), commodityName: "Ship Ammunition - Size 3");
        var sct = Listing(price: 7800, timestampUtc: NowUtc, commodity: "ship ammunition - size 3");
        var r = PriceReconciler.Reconcile(row, "buy", sct, NowUtc);
        Assert.NotNull(r);
        Assert.Equal(7800, r!.Value);
        Assert.Equal(PriceSourceState.UexOnly, r.State);
    }

    [Fact]
    public void Reconcile_ShipAmmunition_UexPresent_WildDisagreement_StaysUexOnly()
    {
        var row = Row(buy: 7800, sell: 0, modifiedUtc: NowUtc.AddHours(-1), commodityName: "Ship Ammunition - Size 3");
        var sct = Listing(price: 100, timestampUtc: NowUtc, commodity: "ship ammunition - size 3");
        var r = PriceReconciler.Reconcile(row, "buy", sct, NowUtc);
        Assert.NotNull(r);
        Assert.Equal(7800, r!.Value);
        Assert.Equal(PriceSourceState.UexOnly, r.State);
    }

    // Case-insensitive match: UEX's real capture casing ("Ship Ammunition - Size 3") is the common
    // case, but the rule must not silently stop applying if a row ever arrives in a different case.
    [Fact]
    public void Reconcile_ShipAmmunition_UexPresent_CaseInsensitiveMatch_StaysUexOnly()
    {
        var row = Row(buy: 7800, sell: 0, modifiedUtc: NowUtc.AddHours(-1), commodityName: "SHIP AMMUNITION - SIZE 3");
        var sct = Listing(price: 7800, timestampUtc: NowUtc, commodity: "ship ammunition - size 3");
        var r = PriceReconciler.Reconcile(row, "buy", sct, NowUtc);
        Assert.Equal(PriceSourceState.UexOnly, r!.State);
    }

    // Other direction: only an SCT observation exists (no UEX row at all) and it names an
    // ammunition commodity - there is nothing to corroborate and no UEX row to fall back on, so
    // the result is null, not SctOnly.
    [Fact]
    public void Reconcile_ShipAmmunition_SctOnly_ReturnsNull()
    {
        var sct = Listing(price: 7800, timestampUtc: NowUtc.AddHours(-1), commodity: "ship ammunition - size 3");
        var r = PriceReconciler.Reconcile(null, "buy", sct, NowUtc);
        Assert.Null(r);
    }

    // Fix (post-review, 2026-07-29): a UEX side price of 0 is kept BY DESIGN - "terminal that
    // neither buys nor sells today" (MarketParse.ParseTradePriceRows) - and must not be treated as
    // a real number to corroborate. Without this fold, the divide-by-zero guard used to force
    // pct=0 and silently report a fabricated Corroborated at Value=0. It must instead behave as if
    // uexRow were absent: SctOnly when SCT is present and not ammunition.
    [Fact]
    public void Reconcile_ZeroPriceUex_SctPresent_ReturnsSctOnly()
    {
        var row = Row(buy: 0, sell: 0, modifiedUtc: NowUtc.AddHours(-1));
        var sct = Listing(price: 500, timestampUtc: NowUtc.AddHours(-1));
        var r = PriceReconciler.Reconcile(row, "buy", sct, NowUtc);
        Assert.NotNull(r);
        Assert.Equal(500, r!.Value);
        Assert.Equal(PriceSourceState.SctOnly, r.State);
        Assert.Equal(default, r.UexModifiedUtc);
        Assert.Equal(sct.TimestampUtc, r.SctTimestampUtc);
    }

    // Same fold, ammunition branch: with the UEX side absent (price 0), the ammo check that
    // applies is the SCT-side one (sct.Commodity), not uexRow.CommodityName - proven here by
    // giving the UEX row an ordinary, non-ammo CommodityName while the SCT row is ammunition.
    [Fact]
    public void Reconcile_ZeroPriceUex_SctPresentAmmunition_ReturnsNull()
    {
        var row = Row(buy: 0, sell: 0, modifiedUtc: NowUtc.AddHours(-1), commodityName: "Laranite");
        var sct = Listing(price: 7384, timestampUtc: NowUtc.AddHours(-1), commodity: "ship ammunition - size 5");
        var r = PriceReconciler.Reconcile(row, "buy", sct, NowUtc);
        Assert.Null(r);
    }

    [Fact]
    public void Reconcile_ZeroPriceUex_NoSct_ReturnsNull()
    {
        var row = Row(buy: 0, sell: 0, modifiedUtc: NowUtc.AddHours(-1));
        var r = PriceReconciler.Reconcile(row, "buy", null, NowUtc);
        Assert.Null(r);
    }

    // Bonus robustness check: the decision reads "<= 0", not "== 0" - a negative UEX price (should
    // never occur in real data, but the guard is written as an inequality) folds the same way.
    [Fact]
    public void Reconcile_NegativePriceUex_SctPresent_ReturnsSctOnly()
    {
        var row = Row(buy: -1, sell: 0, modifiedUtc: NowUtc.AddHours(-1));
        var sct = Listing(price: 500, timestampUtc: NowUtc.AddHours(-1));
        var r = PriceReconciler.Reconcile(row, "buy", sct, NowUtc);
        Assert.NotNull(r);
        Assert.Equal(PriceSourceState.SctOnly, r!.State);
    }
}
