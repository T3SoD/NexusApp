using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NexusApp.Models;

namespace NexusApp.Services;

/// <summary>One shard the player was on, folded for display.
/// <para>The shard's identity is carried as its three raw parts rather than one preformatted
/// string. Naming a shard is a DISPLAY convention that the overlay's STATS panel already owns
/// ("US East . Shard 142", with the raw id beneath), and a second copy of that rule here is how the
/// two surfaces drift apart. The view composes; this only measures.</para>
/// <para><paramref name="Duration"/> is null when it cannot be known rather than 0: the game logs a
/// join and never a leave, so the newest shard has no measurable length once the player is off it.
/// A zero would read as "was there for no time", which is a different and false claim.</para></summary>
public sealed record ShardRow(string Region, string Instance, string ShardId,
    string? Duration, string When, bool Live);

/// <summary>One session's profit, folded for the history bars. A losing session is still a bar.</summary>
public sealed record ProfitBar(string Label, long Net, bool Current);

/// <summary>Why a row of money exists. <see cref="Unattributed"/> is not an error state: the wallet
/// watched the balance move and no log line explained it, which the rail states rather than hides.</summary>
public enum TxKind { Sell, Buy, Unattributed }

/// <summary>One money movement, folded for display. <paramref name="Amount"/> carries its own sign
/// so no caller has to remember that a buy is money leaving.</summary>
public sealed record TxRow(string What, string Where, string Ago, long Amount, TxKind Kind);

/// <summary>
/// The folds behind the Operations right rail (system-view redesign, 2026-08-10): shard history,
/// session profit history, and the wallet's money movements.
///
/// <para>Every method here is pure and takes its clock, so the whole rail is provable without a
/// window, a WebView2, or Star Citizen running. The view does nothing but render what comes back.</para>
/// </summary>
public static class OperationsPanels
{
    // ── shard history ───────────────────────────────────────────────────────────────────────────

    /// <summary>Folds shard history into display rows, newest first.</summary>
    /// <param name="history">Newest first, as <see cref="ShardTracker.All"/> keeps it.</param>
    /// <param name="onShard">Whether the player is on the newest entry right now.</param>
    public static IReadOnlyList<ShardRow> ShardRows(IReadOnlyList<ShardSession> history,
        bool onShard, DateTime nowUtc, int max = 4)
    {
        if (history is null || history.Count == 0) return Array.Empty<ShardRow>();

        var rows = new List<ShardRow>(Math.Min(max, history.Count));
        for (var i = 0; i < history.Count && rows.Count < max; i++)
        {
            var s = history[i];
            var live = i == 0 && onShard;

            // A past shard ended when the player joined the NEXT one. History is newest first, so
            // the next join is the PREVIOUS index. The newest entry has no successor, so it is
            // measured to now only while the player is still on it.
            DateTime? endedAt = i == 0 ? (onShard ? nowUtc : null) : history[i - 1].JoinedAt;

            string? duration = null;
            if (endedAt is { } end)
            {
                var span = end - s.JoinedAt;
                // Replayed logs can arrive out of order. A negative span is bad data, not a
                // negative duration, so it reads as unknown.
                if (span >= TimeSpan.Zero) duration = FormatSpan(span);
            }

            rows.Add(new ShardRow(s.Region ?? "", s.Instance ?? "", s.ShardId ?? "",
                                  duration, live ? "now" : FormatWhen(nowUtc - s.JoinedAt), live));
        }
        return rows;
    }

    // "1h 12m" / "46m". Minutes stay two digits beside an hour so a column of these does not jitter.
    private static string FormatSpan(TimeSpan t)
    {
        var total = (int)t.TotalMinutes;
        if (total < 60) return total + "m";
        return string.Create(CultureInfo.InvariantCulture, $"{total / 60}h {total % 60:00}m");
    }

    // Coarser than a duration on purpose: this answers "when", not "how long".
    private static string FormatWhen(TimeSpan ago)
    {
        if (ago < TimeSpan.Zero) return "now";
        if (ago.TotalMinutes < 60) return (int)ago.TotalMinutes + "m ago";
        if (ago.TotalHours < 24) return (int)ago.TotalHours + "h ago";
        if (ago.TotalHours < 48) return "yesterday";
        return (int)ago.TotalDays + "d ago";
    }

    // ── session profit history ──────────────────────────────────────────────────────────────────

    /// <summary>Folds retained sessions into the most recent <paramref name="count"/> bars, oldest
    /// first so the chart reads left to right.</summary>
    /// <param name="currentKey">The live session's key, or null when no session is running.</param>
    public static IReadOnlyList<ProfitBar> ProfitBars(IReadOnlyList<SessionSummary> entries,
        string? currentKey, int count = 7)
    {
        if (entries is null || entries.Count == 0) return Array.Empty<ProfitBar>();

        // Entries are append-ordered and the cap evicts from the middle, so storage order is not
        // time order. Sorting here is not a nicety: without it the bars are meaningless.
        var recent = entries.OrderBy(e => e.StartUtc)
                            .Skip(Math.Max(0, entries.Count - count))
                            .ToList();

        var bars = new List<ProfitBar>(recent.Count);
        foreach (var e in recent)
        {
            var live = currentKey != null && e.SessionKey == currentKey;
            // A zero or negative session still gets a bar. Dropping it would quietly under-report
            // how many times the player actually played.
            bars.Add(new ProfitBar(live ? "NOW" : e.StartUtc.ToLocalTime().ToString("ddd", CultureInfo.InvariantCulture).ToUpperInvariant(),
                                   e.Net, live));
        }
        return bars;
    }

    // ── wallet money movements ──────────────────────────────────────────────────────────────────

    /// <summary>Folds settled transactions and unattributed wallet movements into one list,
    /// newest first, capped at <paramref name="max"/>.</summary>
    /// <param name="resolveName">GUID to commodity name. Returns empty when the name has not
    /// arrived yet, which is normal and must still produce a readable row.</param>
    /// <param name="resolveWhere">Transaction to place label. Injected rather than called directly
    /// so this fold stays in Services and the view supplies the shipped
    /// <c>ProfitDisplay.WhereText</c>, keeping one place-rendering rule across every surface.
    /// Null falls back to the raw shop token.</param>
    public static IReadOnlyList<TxRow> TransactionRows(IReadOnlyList<CommodityTransaction> txs,
        IReadOnlyList<UntrackedEntry> untracked, Func<string, string> resolveName,
        DateTime nowUtc, int max = 6, Func<CommodityTransaction, string>? resolveWhere = null)
    {
        var rows = new List<(DateTime At, TxRow Row)>();

        foreach (var t in txs ?? Array.Empty<CommodityTransaction>())
        {
            // A refused transaction never moved money. It has no place in a list of money moved.
            if (t.Voided != null) continue;

            var name = resolveName?.Invoke(t.ResourceGuid ?? "") ?? "";
            var scu = t.Scu == decimal.Truncate(t.Scu)
                ? ((long)t.Scu).ToString("N0", CultureInfo.InvariantCulture)
                : t.Scu.ToString("0.##", CultureInfo.InvariantCulture);
            var what = string.IsNullOrWhiteSpace(name)
                ? $"Unnamed commodity, {scu} SCU"   // the GUID is never shown: it means nothing to a player
                : $"{name}, {scu} SCU";

            var where = resolveWhere?.Invoke(t);
            if (string.IsNullOrWhiteSpace(where))
                where = !string.IsNullOrWhiteSpace(t.PlaceLabel) ? t.PlaceLabel!
                      : !string.IsNullOrWhiteSpace(t.ShopName) ? t.ShopName
                      : "unknown";

            var amount = t.Kind == TransactionKind.Buy ? -t.Amount : t.Amount;
            rows.Add((t.TimestampUtc, new TxRow(what, where, FormatAgo(nowUtc - t.TimestampUtc),
                                                amount, t.Kind == TransactionKind.Buy ? TxKind.Buy : TxKind.Sell)));
        }

        foreach (var u in untracked ?? Array.Empty<UntrackedEntry>())
        {
            var what = string.IsNullOrWhiteSpace(u.Label) ? "Movement not attributed" : u.Label!;
            rows.Add((u.Utc, new TxRow(what, "unknown", FormatAgo(nowUtc - u.Utc), u.Amount, TxKind.Unattributed)));
        }

        return rows.OrderByDescending(r => r.At).Take(max).Select(r => r.Row).ToList();
    }

    // "12m ago" / "1h 04m ago". Finer than the shard "when" because these land minutes apart.
    private static string FormatAgo(TimeSpan ago)
    {
        if (ago < TimeSpan.FromMinutes(1)) return "just now";
        if (ago.TotalMinutes < 60) return (int)ago.TotalMinutes + "m ago";
        var total = (int)ago.TotalMinutes;
        return string.Create(CultureInfo.InvariantCulture, $"{total / 60}h {total % 60:00}m ago");
    }
}
