using NexusApp.Models;

namespace NexusApp.Services;

// Pure math for the current log session's shop kiosk transactions: apply with replay dedupe by
// Key, the result-refusal rule, and a signed delta over settled rows. Sibling of SessionLedger
// and deliberately NOT part of it: SessionLedger owns Bought, Sold, and Net, which are the
// trading figures, and a shop purchase is not a trade. No file I/O, no WPF.
public sealed class PurchaseLedger
{
    // Observed request-to-response pairings run about 0.7 to 1.2 seconds; 5 seconds matches
    // SessionLedger.VoidWindow and stays unambiguous because responses arrive in request order.
    public static readonly TimeSpan SettleWindow = TimeSpan.FromSeconds(5);

    private readonly List<ShopPurchase> _purchases = new();
    private readonly HashSet<string> _keys = new();
    private readonly HashSet<string> _resultKeys = new();
    private readonly HashSet<string> _answered = new();

    public IReadOnlyList<ShopPurchase> Purchases => _purchases;   // time-ordered (log order)

    public bool Apply(ShopPurchase p)
    {
        if (!_keys.Add(p.Key)) return false;   // replay dedupe
        _purchases.Add(p);
        Logger.Info($"[LEDGER] shop {p.Kind} {p.Price} x{p.Quantity} at {p.ShopName}");
        return true;
    }

    // A result routes by shopId plus kioskId (the response carries no item or price) to the
    // OLDEST still-unrefused, still-UNANSWERED request of the same direction inside the window.
    // Success confirms and changes nothing; anything else refuses the row, which then stops
    // counting. A request that never receives a result stays SETTLED: 754 of 755 requests in the
    // corpus do receive a Success, so a missing one is a response we did not see, not a failed
    // purchase.
    //
    // _answered exists because Refused == null alone cannot tell "never answered" apart from
    // "answered Success": both leave Refused null. Without a separate answered marker, a later
    // result at the same kiosk re-scans, finds the already-Success'd row as the oldest still-
    // unrefused candidate, and wrongly refuses it instead of the row it actually belongs to.
    public bool ApplyResult(ShopFlowResult r)
    {
        if (!_resultKeys.Add(ResultKey(r))) return false;   // replayed line, already handled

        for (var i = 0; i < _purchases.Count; i++)
        {
            var p = _purchases[i];
            if (p.Kind != r.Kind || p.Refused is not null || _answered.Contains(p.Key)) continue;
            if (!string.Equals(p.ShopId, r.ShopId, StringComparison.Ordinal)) continue;
            if (!string.Equals(p.KioskId, r.KioskId, StringComparison.Ordinal)) continue;
            var age = r.TimestampUtc - p.TimestampUtc;
            if (age < TimeSpan.Zero || age > SettleWindow) continue;

            // WaitingForPendingResult is neither success nor failure: consume it, but do NOT mark
            // the row answered, so a later real result can still resolve this same row and an
            // expiry falls to the settled default. Success and an explicit failure both settle
            // the question of THIS row and must never be matched again.
            if (r.Result == "WaitingForPendingResult") return true;
            _answered.Add(p.Key);
            if (r.Result != "Success")
            {
                p.Refused = r.Result;
                Logger.Info($"[LEDGER] shop {p.Kind} refused result {r.Result}");
            }
            return true;
        }

        Logger.Info($"[LEDGER] orphan shop result dropped {r.Result}");
        return false;
    }

    /// <summary>Signed aUEC for settled rows with afterUtc &lt; t &lt;= endUtc: buys out, sells in.
    /// Mirrors the wallet's own window, which opens strictly after the anchor.</summary>
    public long SettledDeltaBetween(DateTime afterUtc, DateTime endUtc)
    {
        long sum = 0;
        foreach (var p in _purchases)
        {
            if (p.Refused is not null) continue;
            if (p.TimestampUtc <= afterUtc || p.TimestampUtc > endUtc) continue;
            sum += p.Kind == ShopTransactionKind.Sell ? p.Price : -p.Price;
        }
        return sum;
    }

    public void Reset()
    {
        if (_purchases.Count == 0 && _resultKeys.Count == 0) return;
        _purchases.Clear();
        _keys.Clear();
        _resultKeys.Clear();
        _answered.Clear();
        Logger.Info("[LEDGER] shop session reset");
    }

    private static string ResultKey(ShopFlowResult r) =>
        $"{r.TimestampUtc:O}|{r.Kind}|{r.Result}|{r.ShopId}|{r.KioskId}";
}
