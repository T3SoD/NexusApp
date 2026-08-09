using NexusApp.Models;

namespace NexusApp.Services;

/// <summary>One commodity still held this session, and what it cost to buy.</summary>
public sealed record HeldCommodity(string ResourceGuid, decimal Scu, long Cost);

// What the player is still carrying, valued at what they paid for it. Pure: transactions in,
// figures out, no display strings and no WPF.
//
// This is the fold that fixes SESSION PROFIT reading as a loss mid-run. Buying does not lose money,
// it converts it, and this is the converted half. It needs NO route attribution and no terminal
// matching: the resource GUID is its own key, and the log carries real decimal SCU and the real
// aUEC paid. Only the EXPECTED figure needs a route's sell price, which is why it is not here.
//
// Cost basis is the session's weighted average per commodity, not FIFO lots. Weighted average is
// the honest choice while the session is the only window there is: the log carries no lot identity,
// so matching a specific sell against a specific buy would be invented precision.
public static class CargoValue
{
    /// <summary>Commodities with SCU still on board, ordered by cost descending. A commodity sold
    /// down to zero (or below, which happens whenever cargo bought in an earlier session is sold in
    /// this one) is ABSENT rather than zero or negative: the same absent-beats-guessed rule every
    /// other unresolved surface follows.</summary>
    public static IReadOnlyList<HeldCommodity> Held(IReadOnlyList<CommodityTransaction> txs)
    {
        var boughtScu = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var boughtCost = new Dictionary<string, long>(StringComparer.Ordinal);
        var soldScu = new Dictionary<string, decimal>(StringComparer.Ordinal);

        foreach (var tx in txs)
        {
            if (tx.Voided is not null) continue;   // refused settlements never count, anywhere
            if (tx.Scu <= 0) continue;             // a zero-SCU settlement carries no cargo to value
            if (tx.Kind == TransactionKind.Buy)
            {
                boughtScu[tx.ResourceGuid] = boughtScu.GetValueOrDefault(tx.ResourceGuid) + tx.Scu;
                boughtCost[tx.ResourceGuid] = boughtCost.GetValueOrDefault(tx.ResourceGuid) + tx.Amount;
            }
            else
            {
                soldScu[tx.ResourceGuid] = soldScu.GetValueOrDefault(tx.ResourceGuid) + tx.Scu;
            }
        }

        var held = new List<HeldCommodity>();
        foreach (var (guid, bought) in boughtScu)
        {
            var remaining = bought - soldScu.GetValueOrDefault(guid);
            if (remaining <= 0) continue;
            var perScu = boughtCost[guid] / bought;   // decimal division, session weighted average
            held.Add(new HeldCommodity(guid, remaining, (long)Math.Round(remaining * perScu)));
        }
        return held.OrderByDescending(h => h.Cost).ToList();
    }

    /// <summary>The aUEC currently sitting in unsold cargo. Zero when nothing is held.</summary>
    public static long TotalCost(IReadOnlyList<CommodityTransaction> txs)
    {
        long total = 0;
        foreach (var h in Held(txs)) total += h.Cost;
        return total;
    }
}
