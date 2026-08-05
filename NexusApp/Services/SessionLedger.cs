using NexusApp.Models;

namespace NexusApp.Services;

// Pure math for the current log session's commodity settlements: apply with replay dedupe by Key,
// the error-voiding rule, totals over unvoided rows. No file I/O, no WPF; ProfitTracker feeds it
// from the shared Game.log tail and rolls it on log reset. Feeding the same log twice changes
// nothing: transactions dedupe by Key, error responses by their own stamp.
public sealed class SessionLedger
{
    // Observed request-to-error pairings are all under a quarter second; 5 seconds is generous
    // and still unambiguous because a second same-kind request never lands inside the window in
    // any observed log.
    public static readonly TimeSpan VoidWindow = TimeSpan.FromSeconds(5);

    private readonly List<CommodityTransaction> _txs = new();
    private readonly HashSet<string> _txKeys = new();
    private readonly HashSet<string> _errorKeys = new();

    public IReadOnlyList<CommodityTransaction> Transactions => _txs;   // time-ordered (log order)

    public long Bought { get; private set; }
    public long Sold { get; private set; }
    public long Net => Sold - Bought;   // negative is a normal mid-run state, never clamped
    public int UnvoidedCount { get; private set; }

    public bool Apply(CommodityTransaction tx)
    {
        if (!_txKeys.Add(tx.Key)) return false;   // replay dedupe
        _txs.Add(tx);
        if (tx.Voided is null)
        {
            if (tx.Kind == TransactionKind.Buy) Bought += tx.Amount; else Sold += tx.Amount;
            UnvoidedCount++;
        }
        Logger.Info($"[LEDGER] applied {tx.Kind} {tx.Amount} net {Net}");
        return true;
    }

    // An error response voids the nearest preceding unvoided transaction of the same kind within
    // the window. Voided rows stay in the ledger (rendered dim with the result code) and never
    // count toward the totals. An orphan error (no candidate in window) logs and is dropped.
    public bool ApplyError(CommodityErrorInfo err)
    {
        if (!_errorKeys.Add(ErrorKey(err))) return false;   // replayed line, already settled

        for (var i = _txs.Count - 1; i >= 0; i--)
        {
            var tx = _txs[i];
            if (tx.Kind != err.Kind || tx.Voided is not null) continue;
            var age = err.TimestampUtc - tx.TimestampUtc;
            if (age < TimeSpan.Zero || age > VoidWindow) continue;
            tx.Voided = err.Result;
            if (tx.Kind == TransactionKind.Buy) Bought -= tx.Amount; else Sold -= tx.Amount;
            UnvoidedCount--;
            Logger.Info($"[LEDGER] voided {tx.Kind} result {err.Result}");
            return true;
        }

        Logger.Info($"[LEDGER] orphan error dropped result {err.Result}");
        return false;
    }

    public void Reset()
    {
        if (_txs.Count == 0 && _errorKeys.Count == 0) return;
        _txs.Clear();
        _txKeys.Clear();
        _errorKeys.Clear();
        Bought = 0;
        Sold = 0;
        UnvoidedCount = 0;
        Logger.Info("[LEDGER] session reset");
    }

    // Snapshot for the history store: unvoided settlements only, per the history contract.
    public SessionSummary BuildSummary(string sessionKey, GameChannel channel, DateTime startUtc)
    {
        var last = startUtc;
        foreach (var tx in _txs)
            if (tx.TimestampUtc > last) last = tx.TimestampUtc;
        return new SessionSummary
        {
            SessionKey = sessionKey, Channel = channel, StartUtc = startUtc, LastSeenUtc = last,
            Bought = Bought, Sold = Sold, Net = Net, TxCount = UnvoidedCount,
        };
    }

    private static string ErrorKey(CommodityErrorInfo err) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{err.TimestampUtc:O}|{err.Kind}|{err.Result}");
}

// The pure history rules over ProfitHistoryState (profit_history.json). Entries upsert by
// SessionKey (channel + the log's own first-line stamp) so replaying a log overwrites rather than
// duplicates; the cap folds the oldest entry (never the one being upserted) into the per-channel
// base in the same mutation, so the derived all-time figure never moves when a prune folds. The
// PrunedThroughUtc watermark refuses re-entry of anything the base already holds.
public static class ProfitHistory
{
    public const int MaxSessionsPerChannel = 500;

    public static void Upsert(ProfitHistoryState state, SessionSummary summary, int cap = MaxSessionsPerChannel)
    {
        var ch = state.Channels.Find(c => c.Channel == summary.Channel);

        // A zero-transaction session is noise, not history: write nothing, and retract the entry
        // a mid-session rewrite may have left behind (applied, persisted, then voided to zero).
        if (summary.TxCount == 0)
        {
            ch?.Entries.RemoveAll(e => e.SessionKey == summary.SessionKey);
            return;
        }

        if (ch is null)
        {
            ch = new ChannelHistory { Channel = summary.Channel, FirstSessionUtc = summary.StartUtc };
            state.Channels.Add(ch);
        }

        var idx = ch.Entries.FindIndex(e => e.SessionKey == summary.SessionKey);

        // Watermark refusal: a key not retained whose StartUtc is at or before PrunedThroughUtc is
        // a replay of a session the base already folded; re-adding it would compound the base on
        // the next prune, once per settlement at the cap. A pre-watermark session never observed
        // is refused the same way, deliberately: correctness over completeness, and the surface
        // already states the boundary as sessions Nexus observed.
        if (idx < 0 && summary.StartUtc <= ch.PrunedThroughUtc) return;

        if (summary.StartUtc < ch.FirstSessionUtc) ch.FirstSessionUtc = summary.StartUtc;

        if (idx >= 0) ch.Entries[idx] = summary;
        else ch.Entries.Add(summary);

        while (ch.Entries.Count > cap)
        {
            // Never evict the entry being upserted (fold the oldest OTHER entry): folding the live
            // session would put it under the watermark and refuse its own next rewrite.
            var oldest = -1;
            for (var i = 0; i < ch.Entries.Count; i++)
            {
                if (ch.Entries[i].SessionKey == summary.SessionKey) continue;
                if (oldest < 0 || ch.Entries[i].StartUtc < ch.Entries[oldest].StartUtc) oldest = i;
            }
            if (oldest < 0) break;   // only the upserted entry remains; nothing else to fold

            var folded = ch.Entries[oldest];
            ch.PrunedNet += folded.Net;   // fold, so the all-time figure does not move
            ch.PrunedCount++;
            if (folded.StartUtc > ch.PrunedThroughUtc) ch.PrunedThroughUtc = folded.StartUtc;
            ch.Entries.RemoveAt(oldest);
        }
    }

    // Derived, never stored: the base survives the cap, the entries survive replay.
    public static long AllTimeNet(ProfitHistoryState state, GameChannel channel)
    {
        var ch = state.Channels.Find(c => c.Channel == channel);
        if (ch is null) return 0;
        var net = ch.PrunedNet;
        foreach (var e in ch.Entries) net += e.Net;
        return net;
    }

    public static int AllTimeSessionCount(ProfitHistoryState state, GameChannel channel)
    {
        var ch = state.Channels.Find(c => c.Channel == channel);
        return ch is null ? 0 : ch.PrunedCount + ch.Entries.Count;
    }
}
