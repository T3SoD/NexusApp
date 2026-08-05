using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NexusApp.Models;
using NexusApp.Services;

namespace NexusApp.Views;

// The profit surfaces' pure folds (spec 2026-08-05 sections 5, 6 and the section 9 rulings; mock
// nexus-design-lab/profit-tracker). Everything the chip, the panel and the overlay line render is
// derived here so the WPF partials do no arithmetic, the same split TradeFinancials keeps for the
// financial rail. Formatting is pinned invariant like the parser's numeric fields: the mock reads
// every number as en-US N0. Internal statics, unit-tested via InternalsVisibleTo.
internal static class ProfitDisplay
{
    // Section 9 ruling 1: the chip label. The panel eyebrow wears the same words.
    internal const string ChipLabel = "SESSION PROFIT";
    internal const string NoneValue = "- - -";

    internal const string WaitingLine = "Waiting on the first kiosk transaction.";
    internal const string OfflinePrefix = "Game offline. Last session: ";
    // Offline with an empty ledger: "waiting" and "lands here within a second" are live claims,
    // so the offline variants state only what is on screen (review fix, 2026-08-05).
    internal const string WaitingOfflineLine = "Game offline. No kiosk transactions to show.";

    internal const string EmptyLedger =
        "No kiosk transactions this session yet. Buy or sell at any commodity kiosk and the row "
        + "lands here within a second, with the price the game wrote to its own log. Refused "
        + "transactions land too, voided, as proof they were seen and did not count.";
    internal const string EmptyLedgerOffline =
        "No kiosk transactions to show. Once a session is live, every buy or sell at a commodity "
        + "kiosk lands here within a second, with the price the game wrote to its own log. Refused "
        + "transactions land too, voided, as proof they were seen and did not count.";

    // Section 9 ruling 4 folds the chart by local calendar day, so this copy names days where the
    // mock (which predates the ruling) said sessions.
    internal const string HistoryEmptyNone =
        "No profit history yet. The chart draws once two observed days have kiosk transactions; "
        + "the all-time line starts with the first session.";
    internal const string HistoryEmptyOne =
        "One observed day so far. The chart draws at two; the all-time line above already counts it.";

    internal const string ChartWindowNote = "chart: last 500 sessions";

    // Footer caveat (S5). The unsold-cargo sentence trails in the normal state and LEADS, bolded
    // by the view, in NEGATIVE (S6): in the red mid-run is a normal state, said in words. The
    // second sentence claims the OUTCOME only (review fix, 2026-08-05): recon proved no money
    // field for some of those and never checked ship purchases, so "never reach the Game.log"
    // overclaimed.
    internal const string CaveatBody =
        "Commodity kiosk transactions only. Ship purchases, rentals, fines, repairs and mission "
        + "payouts never move this number. Auto-load fees are paid in game but never logged, so "
        + "auto-loaded buys understate spend by the fee.";
    internal const string CaveatUnsoldTail = " Unsold cargo reads as spend until it sells.";
    internal const string CaveatNegativeLead =
        "In the red mid-run is normal: unsold cargo reads as spend until it sells.";

    internal static string Format(long v) => v.ToString("N0", CultureInfo.InvariantCulture);

    internal static string Signed(long v) => (v < 0 ? "-" : "+") + Format(Math.Abs(v));

    /// <summary>The chip and overlay value: dashes until something settles, signed net after.</summary>
    internal static string ChipValue(int settled, long net) => settled == 0 ? NoneValue : Signed(net);

    // Compact M form for the ALL TIME figure only, from 10M up so young histories stay exact.
    // One decimal, ".0" dropped (12,000,000 reads +12M, 12,400,000 reads +12.4M).
    internal static string Compact(long v)
    {
        var a = Math.Abs(v);
        if (a < 10_000_000) return Signed(v);
        var m = (a / 1_000_000.0).ToString("0.0", CultureInfo.InvariantCulture);
        if (m.EndsWith(".0", StringComparison.Ordinal)) m = m[..^2];
        return (v < 0 ? "-" : "+") + m + "M";
    }

    // Section 6. OFFLINE wins (the reading is last session's, however it ended); a flat net with
    // settlements reads dim like empty, never green.
    internal static ProfitState State(int settled, long net, bool sessionLive) =>
        !sessionLive ? ProfitState.Offline
        : settled == 0 || net == 0 ? ProfitState.None
        : net > 0 ? ProfitState.Positive
        : ProfitState.Negative;

    // "1 sell in 1,067,200, 1 buy out 836,854, this session." Clauses only for what happened;
    // offline swaps the live suffix for the last-session prefix (or, empty, the offline waiting
    // copy: nothing to prefix and no live promise to make).
    internal static string DerivationLine(int sells, long sold, int buys, long bought, int voided, bool live)
    {
        if (sells == 0 && buys == 0 && voided == 0) return live ? WaitingLine : WaitingOfflineLine;

        var clauses = new List<string>(3);
        if (sells > 0) clauses.Add($"{sells} sell{(sells == 1 ? "" : "s")} in {Format(sold)}");
        if (buys > 0) clauses.Add($"{buys} buy{(buys == 1 ? "" : "s")} out {Format(bought)}");
        if (voided > 0) clauses.Add($"{voided} voided");
        var joined = string.Join(", ", clauses);
        return live ? joined + ", this session." : OfflinePrefix + joined + ".";
    }

    /// <summary>Section 9 ruling 4: sessions folded by the LOCAL calendar day they started, in day
    /// order. Storage stays per-session; this is presentation only. The converter parameter exists
    /// for the tests; live callers take the machine-local default.</summary>
    internal static List<ProfitDay> FoldByLocalDay(
        IEnumerable<SessionSummary> entries, Func<DateTime, DateTime>? toLocal = null)
    {
        var local = toLocal ?? (utc => utc.ToLocalTime());
        return entries
            .GroupBy(e => local(e.StartUtc).Date)
            .OrderBy(g => g.Key)
            .Select(g => new ProfitDay(g.Key, g.Sum(e => e.Net), g.Count(), g.Sum(e => e.TxCount)))
            .ToList();
    }

    // Ledger render cap (S5 review fix, 2026-08-05): the panel renders the latest rows only, in
    // their own scroll region, and a dim note names the fold. 50 is legibility, not screen
    // height; the region's MaxHeight carries that.
    internal const int LedgerRowCap = 50;

    internal static int LedgerHiddenCount(int total) => Math.Max(0, total - LedgerRowCap);

    internal static string LedgerEarlierNote(int hidden) =>
        $"and {Format(hidden)} earlier this session";

    // Mechanical shop-token cleanup only (S3): strip the SCShop_ prefix and the _Int_X interior
    // suffix, unscore the rest. The raw token stays in the row tooltip.
    internal static string ShopLabel(string token)
    {
        var t = token;
        if (t.StartsWith("SCShop_", StringComparison.Ordinal)) t = t["SCShop_".Length..];
        if (t.Length > 6 && t[^6..^1] == "_Int_" && char.IsUpper(t[^1])) t = t[..^6];
        return t.Replace('_', ' ');
    }

    /// <summary>"612 sessions, since Jun 12": the count outlives the chart cap (S3b).</summary>
    internal static string AllTimeLine(int sessions, DateTime firstSessionUtc, Func<DateTime, DateTime>? toLocal = null)
    {
        var local = toLocal ?? (utc => utc.ToLocalTime());
        var since = local(firstSessionUtc).ToString("MMM d", CultureInfo.InvariantCulture);
        return $"{sessions} session{(sessions == 1 ? "" : "s")}, since {since}";
    }

    /// <summary>The strip's stated boundary (S3b): what Nexus saw, active channel only.</summary>
    internal static string Boundary(GameChannel channel) =>
        $"SESSIONS NEXUS OBSERVED, {GameChannels.FolderName(channel)} CHANNEL ONLY";
}

/// <summary>One chart bar: sessions folded onto a local calendar day (section 9 ruling 4).</summary>
internal sealed record ProfitDay(DateTime LocalDate, long Net, int Sessions, int TxCount);

// Chip/value state fold (spec section 6). Public, not internal: xunit test methods are public and
// CS0051 forbids an internal type in their signatures, the same reason RoiTier is public.
public enum ProfitState { None, Positive, Negative, Offline }
