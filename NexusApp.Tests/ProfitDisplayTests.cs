using System;
using System.Collections.Generic;
using System.Globalization;
using NexusApp.Models;
using NexusApp.Services;
using NexusApp.Views;
using Xunit;

namespace NexusApp.Tests;

// ProfitDisplay: the profit panel's pure folds (spec 2026-08-05 sections 5, 6 and the section 9
// rulings; mock nexus-design-lab/profit-tracker). The headline fixtures are the mock's own: the
// captured bioplastic round trip (sold 1,067,200, bought 836,854, net +230,346) and the spec's
// ALL TIME example (+12.4M, 612 sessions, since Jun 12).
public class ProfitDisplayTests
{
    // ── Signed value (chip, big net, ledger amounts) ─────────────────────────

    [Theory]
    [InlineData(230_346, "+230,346")]
    [InlineData(-836_854, "-836,854")]
    [InlineData(0, "+0")]
    public void Signed_AlwaysCarriesTheSign(long v, string expected)
        => Assert.Equal(expected, ProfitDisplay.Signed(v));

    // Display formatting is pinned invariant, like the parser's numeric fields: under a
    // comma-decimal culture "N0" would regroup with dots and the mock's en-US shape breaks.
    [Fact]
    public void Signed_UnderCommaDecimalCulture_IsInvariant()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("+1,234,567", ProfitDisplay.Signed(1_234_567));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void ChipValue_NoSettledTransactions_IsDashes()
        => Assert.Equal("- - -", ProfitDisplay.ChipValue(0, 0));

    [Fact]
    public void ChipValue_Settled_IsSignedNet()
        => Assert.Equal("+230,346", ProfitDisplay.ChipValue(2, 230_346));

    // ── Compact M form (ALL TIME only, from 10M up; below that exact N0) ─────

    [Theory]
    [InlineData(9_999_999, "+9,999,999")]
    [InlineData(10_000_000, "+10M")]
    [InlineData(12_400_000, "+12.4M")]
    [InlineData(12_000_000, "+12M")]
    [InlineData(-12_345_678, "-12.3M")]
    [InlineData(230_346, "+230,346")]
    public void Compact_TenMillionThreshold(long v, string expected)
        => Assert.Equal(expected, ProfitDisplay.Compact(v));

    // ── State fold (spec section 6; OFFLINE from GameLogFeed.IsSessionLive) ──

    [Theory]
    [InlineData(0, 0, true, ProfitState.None)]
    [InlineData(2, 230_346, true, ProfitState.Positive)]
    [InlineData(1, -182_560, true, ProfitState.Negative)]
    [InlineData(2, 0, true, ProfitState.None)]         // settled but flat reads dim, not green
    [InlineData(2, 230_346, false, ProfitState.Offline)]
    [InlineData(0, 0, false, ProfitState.Offline)]
    public void State_Fold(int settled, long net, bool live, ProfitState expected)
        => Assert.Equal(expected, ProfitDisplay.State(settled, net, live));

    // ── Derivation line (spec section 5 sample copy) ─────────────────────────

    [Fact]
    public void DerivationLine_RoundTrip()
        => Assert.Equal("1 sell in 1,067,200 aUEC, 1 buy out 836,854 aUEC, this session.",
            ProfitDisplay.DerivationLine(1, 1_067_200, 1, 836_854, 0, live: true));

    [Fact]
    public void DerivationLine_PluralsAndVoided()
        => Assert.Equal("2 sells in 2,000,000 aUEC, 3 buys out 1,500,000 aUEC, 2 voided, this session.",
            ProfitDisplay.DerivationLine(2, 2_000_000, 3, 1_500_000, 2, live: true));

    [Fact]
    public void DerivationLine_BuysOnly()
        => Assert.Equal("1 buy out 182,560 aUEC, this session.",
            ProfitDisplay.DerivationLine(0, 0, 1, 182_560, 0, live: true));

    [Fact]
    public void DerivationLine_Offline_PrefixesLastSession()
        => Assert.Equal("Game offline. Last session: 1 sell in 1,067,200 aUEC, 1 buy out 836,854 aUEC.",
            ProfitDisplay.DerivationLine(1, 1_067_200, 1, 836_854, 0, live: false));

    [Fact]
    public void DerivationLine_NothingYet_IsTheWaitingCopy()
        => Assert.Equal("Waiting on the first kiosk transaction.",
            ProfitDisplay.DerivationLine(0, 0, 0, 0, 0, live: true));

    // Offline with nothing recorded must not promise a live feed ("waiting on", "lands here
    // within a second" are live claims): the offline variants state only what is on screen.
    [Fact]
    public void DerivationLine_OfflineAndEmpty_MakesNoLiveClaim()
        => Assert.Equal("Game offline. No kiosk transactions to show.",
            ProfitDisplay.DerivationLine(0, 0, 0, 0, 0, live: false));

    [Fact]
    public void EmptyLedgerOfflineCopy_MakesNoLiveClaim()
    {
        Assert.StartsWith("No kiosk transactions to show.", ProfitDisplay.EmptyLedgerOffline);
        Assert.DoesNotContain("within a second", ProfitDisplay.EmptyLedgerOffline.Split('.')[0]);
    }

    // ── Day fold (section 9 ruling 4: local calendar day, presentation only) ─

    private static SessionSummary Session(string key, DateTime startUtc, long net, int tx) => new()
    {
        SessionKey = key, Channel = GameChannel.Live, StartUtc = startUtc,
        LastSeenUtc = startUtc, Net = net, TxCount = tx,
    };

    [Fact]
    public void FoldByLocalDay_SameDaySessionsSum()
    {
        var days = ProfitDisplay.FoldByLocalDay(new[]
        {
            Session("a", new DateTime(2026, 7, 31, 9, 0, 0, DateTimeKind.Utc), 100_000, 3),
            Session("b", new DateTime(2026, 7, 31, 21, 0, 0, DateTimeKind.Utc), -25_000, 2),
            Session("c", new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc), 40_000, 1),
        }, toLocal: utc => utc);

        Assert.Equal(2, days.Count);
        Assert.Equal(new DateTime(2026, 7, 31), days[0].LocalDate);
        Assert.Equal(75_000, days[0].Net);
        Assert.Equal(2, days[0].Sessions);
        Assert.Equal(5, days[0].TxCount);
        Assert.Equal(40_000, days[1].Net);
    }

    // The boundary is the machine's LOCAL midnight: the same two stamps land on one day or two
    // depending on the offset, so the fold must honor the converter it is given.
    [Fact]
    public void FoldByLocalDay_BoundaryHonorsLocalTime()
    {
        var late = Session("a", new DateTime(2026, 7, 31, 23, 30, 0, DateTimeKind.Utc), 10, 1);
        var early = Session("b", new DateTime(2026, 8, 1, 1, 0, 0, DateTimeKind.Utc), 20, 1);

        Assert.Equal(2, ProfitDisplay.FoldByLocalDay(new[] { late, early }, utc => utc).Count);
        Assert.Single(ProfitDisplay.FoldByLocalDay(new[] { late, early }, utc => utc.AddHours(2)));
    }

    [Fact]
    public void FoldByLocalDay_OrderedByDayRegardlessOfInput()
    {
        var days = ProfitDisplay.FoldByLocalDay(new[]
        {
            Session("b", new DateTime(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc), 2, 1),
            Session("a", new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc), 1, 1),
        }, utc => utc);

        Assert.Equal(new DateTime(2026, 7, 20), days[0].LocalDate);
        Assert.Equal(new DateTime(2026, 8, 2), days[1].LocalDate);
    }

    // ── Shop token cleanup (S3: mechanical only; raw stays in the tooltip) ───

    [Theory]
    [InlineData("SCShop_Trdpst_Warehouse_OTLW_Int_B", "Trdpst Warehouse OTLW")]
    [InlineData("SCShop_AdminOffice_GrimHex", "AdminOffice GrimHex")]
    [InlineData("SCShop_Pyro_RestStop_Rund_Admin", "Pyro RestStop Rund Admin")]
    public void ShopLabel_MechanicalCleanupOnly(string token, string expected)
        => Assert.Equal(expected, ProfitDisplay.ShopLabel(token));

    // ── ALL TIME header line (S3b/S5) ────────────────────────────────────────

    [Fact]
    public void AllTimeLine_SpecExample()
        => Assert.Equal("612 sessions, since Jun 12",
            ProfitDisplay.AllTimeLine(612, new DateTime(2026, 6, 12, 14, 0, 0, DateTimeKind.Utc), utc => utc));

    [Fact]
    public void AllTimeLine_SingleSessionSingular()
        => Assert.Equal("1 session, since Aug 5",
            ProfitDisplay.AllTimeLine(1, new DateTime(2026, 8, 5, 14, 0, 0, DateTimeKind.Utc), utc => utc));

    // ── History strip copy and boundary ──────────────────────────────────────

    [Theory]
    [InlineData(GameChannel.Live, "SESSIONS NEXUS OBSERVED, LIVE CHANNEL ONLY")]
    [InlineData(GameChannel.Ptu, "SESSIONS NEXUS OBSERVED, PTU CHANNEL ONLY")]
    public void Boundary_NamesTheActiveChannel(GameChannel channel, string expected)
        => Assert.Equal(expected, ProfitDisplay.Boundary(channel));

    [Fact]
    public void ChartWindowNote_MatchesTheSpec()
        => Assert.Equal("chart: last 500 sessions", ProfitDisplay.ChartWindowNote);

    // Section 9 ruling 4 folds the chart by day, so the empty-ladder copy names days, not the
    // mock's per-session wording (the mock predates the ruling).
    [Fact]
    public void HistoryEmptyCopy_NamesDays()
    {
        Assert.Equal("No profit history yet. The chart draws once two observed days have kiosk "
            + "transactions; the all-time line starts with the first session.",
            ProfitDisplay.HistoryEmptyNone);
        Assert.Equal("One observed day so far. The chart draws at two; the all-time line above "
            + "already counts it.", ProfitDisplay.HistoryEmptyOne);
    }

    // ── Footer caveat (S5: must name the auto-load fee gap) ──────────────────

    [Fact]
    public void Caveat_CarriesTheAutoLoadFeeSentence()
    {
        Assert.Contains("Auto-load fees are paid in game but never logged, so auto-loaded buys "
            + "understate spend by the fee.", ProfitDisplay.CaveatBody);
        Assert.Contains("Commodity kiosk transactions only.", ProfitDisplay.CaveatBody);
        Assert.Equal(" Unsold cargo reads as spend until it sells.", ProfitDisplay.CaveatUnsoldTail);
        Assert.Equal("In the red mid-run is normal: unsold cargo reads as spend until it sells.",
            ProfitDisplay.CaveatNegativeLead);
    }

    // Review fix 2026-08-05: recon proved these never MOVE the number; it never checked what the
    // log carries for all of them (ship purchases were not in the sweep at all). The copy claims
    // the outcome only.
    [Fact]
    public void Caveat_ClaimsTheOutcomeNotTheLogContents()
    {
        Assert.Contains("Ship purchases, rentals, fines, repairs and mission payouts never move "
            + "this number.", ProfitDisplay.CaveatBody);
        Assert.DoesNotContain("never reach the Game.log", ProfitDisplay.CaveatBody);
    }

    // ── Ledger render cap (S5 review fix: the panel renders the latest 50 rows) ──

    [Theory]
    [InlineData(0, 0)]
    [InlineData(50, 0)]
    [InlineData(51, 1)]
    [InlineData(200, 150)]
    public void LedgerHiddenCount_CountsBeyondTheCap(int total, int hidden)
        => Assert.Equal(hidden, ProfitDisplay.LedgerHiddenCount(total));

    [Fact]
    public void WhereText_PrefersTheStampedPlace()
        => Assert.Equal("MIC-L5", ProfitDisplay.WhereText("MIC-L5", false, "SCShop_Admin_lt_base_g"));

    [Fact]
    public void WhereText_AreaReading_SaysSpace()
        => Assert.Equal("Crusader space", ProfitDisplay.WhereText("Crusader", true, "SCShop_Admin_lt_base_g"));

    [Fact]
    public void WhereText_NoPlace_FallsBackToTheCleanedShopToken()
        => Assert.Equal(ProfitDisplay.ShopLabel("SCShop_Admin_lt_base_g"),
            ProfitDisplay.WhereText(null, false, "SCShop_Admin_lt_base_g"));

    [Fact]
    public void LedgerCapAndEarlierNote_MatchTheSpec()
    {
        Assert.Equal(50, ProfitDisplay.LedgerRowCap);
        Assert.Equal("and 150 earlier this session", ProfitDisplay.LedgerEarlierNote(150));
        Assert.Equal("and 1,024 earlier this session", ProfitDisplay.LedgerEarlierNote(1024));
    }

    [Fact]
    public void EmptyLedgerCopy_MatchesTheMock()
        => Assert.StartsWith("No kiosk transactions this session yet.", ProfitDisplay.EmptyLedger);

    [Fact]
    public void ChipLabel_IsTheRuledName()
        => Assert.Equal("SESSION PROFIT", ProfitDisplay.ChipLabel);
}
