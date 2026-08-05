using System.Globalization;
using NexusApp.Services;

namespace NexusApp.Models;

// One observed game session's profit footprint - the only per-session state the history keeps.
// Never a transaction list, so the trend can span months at a few bytes per session.
public sealed record SessionSummary
{
    public string SessionKey { get; init; } = "";   // MakeKey below; stable under replay
    public GameChannel Channel { get; init; }
    public DateTime StartUtc { get; init; }
    public DateTime LastSeenUtc { get; init; }
    public long Bought { get; init; }               // unvoided settlements only, like the rest
    public long Sold { get; init; }
    public long Net { get; init; }
    public int TxCount { get; init; }

    // Channel plus the log's own first-line stamp: a later replay of the same log lands on the
    // same key and overwrites rather than duplicates.
    public static string MakeKey(GameChannel channel, DateTime logFirstLineUtc) =>
        string.Create(CultureInfo.InvariantCulture, $"{channel}|{logFirstLineUtc:O}");
}

// One channel's retained history: capped entries plus the base that pruned sessions fold into.
// A PTU session writes a PTU entry and never moves the LIVE trend (PTU wipes make this hard).
public sealed class ChannelHistory
{
    public GameChannel Channel { get; set; }
    public DateTime FirstSessionUtc { get; set; }   // "since Jun 12"; outlives every prune
    public long PrunedNet { get; set; }             // nets the cap folded out of Entries
    public int PrunedCount { get; set; }
    public DateTime PrunedThroughUtc { get; set; }  // newest StartUtc the cap ever folded; Upsert's watermark
    public List<SessionSummary> Entries { get; set; } = new();
}

// The state profit_history.json round-trips. Pure data; ProfitHistory owns the rules.
public sealed class ProfitHistoryState
{
    public int Schema { get; set; } = 1;
    public List<ChannelHistory> Channels { get; set; } = new();
}
