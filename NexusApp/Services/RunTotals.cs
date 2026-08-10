using NexusApp.Models;

namespace NexusApp.Services;

/// <summary>What this run pays, with the two halves kept apart (trade/cargo fusion spec
/// 2026-08-09, section 3.6). They are NOT summed, and that is the whole point of the section: a
/// contract reward is a durable promise the game already made, while a route margin is recomputed
/// from live market prices every hour and can rot between now and the kiosk. Fusing them into one
/// number would give a durable figure the volatility of the weaker half and hide which is which.
///
/// <para><paramref name="ContractsWithUnknownReward"/> and <paramref name="UnpricedRoutes"/> exist
/// so the panel can say what it could not count. A silent omission reads as a smaller payout
/// rather than an unread one.</para></summary>
public sealed record RunPays(long ContractRewards, int ContractsWithUnknownReward,
                             long RouteMargin, int UnpricedRoutes);

/// <summary>SCU the accepted work commits you to, against the ship selected in the planner
/// (spec section 3.7). This is the honest substitute for "what is in your hold", which the app
/// cannot know: there is no ship detection and no running cargo total, so the only truthful
/// question is what you signed up for versus the hull you told the planner you fly.</summary>
public sealed record CommittedLoad(int RouteScu, int ContractScu, int? ShipScu)
{
    public int TotalScu => RouteScu + ContractScu;

    /// <summary>Runs the selected ship needs to move all of it, or null when no ship is selected
    /// or nothing is committed. Never 0: with cargo and a hull, the answer is at least one run.</summary>
    public int? Runs => ShipScu is > 0 && TotalScu > 0
        ? (int)Math.Ceiling((double)TotalScu / ShipScu.Value)
        : null;

    public bool ExceedsOneRun => Runs is > 1;
}

public static class RunTotals
{
    /// <summary>Contract rewards and accepted-route margin, counted separately.
    ///
    /// <para>A reward of 0 is ABSENCE, not free work: Haul.Reward is OCR-sourced and its own model
    /// comment says "0 = unknown". Counting it as zero pay would understate the run and look like
    /// a bug to anyone holding a contract they know pays.</para>
    ///
    /// <para>Sell-only routes are excluded from the margin and counted instead, for the same
    /// reason AcceptedRouteMoney.ExpectedMargin excludes them: with no buy leg, PerScuMargin holds
    /// a raw sell PRICE, and adding a price to a pile of margins inflates the total by an entire
    /// cargo's gross revenue.</para></summary>
    public static RunPays Pays(IEnumerable<int> contractRewards, IReadOnlyList<AcceptedRoute> routes)
    {
        long rewards = 0;
        var unknown = 0;
        foreach (var reward in contractRewards)
        {
            if (reward <= 0) { unknown++; continue; }
            rewards += reward;
        }

        long margin = 0;
        var unpriced = 0;
        foreach (var r in routes)
        {
            if (r.Stage == AcceptedStage.Sold) continue;   // already realised, and already gone
            if (r.BuyTerminalId is null) { unpriced++; continue; }
            margin += (long)Math.Round(r.PerScuMargin * (r.ActualQty ?? r.TripQty));
        }

        return new RunPays(rewards, unknown, margin, unpriced);
    }

    /// <summary>Route SCU plus contract SCU against the planner's ship. Route quantity is the
    /// corrected figure once a buy has landed (ActualQty), the accepted plan otherwise, so the
    /// commitment tracks what is actually being carried rather than what was projected.
    ///
    /// <para>Contract SCU must come from the PICKUP side only. Every leg of a contract appears
    /// twice in the consolidation, once to collect and once to deliver, so counting both would
    /// double every contract's load.</para></summary>
    public static CommittedLoad Committed(IReadOnlyList<AcceptedRoute> routes, int contractScu, int? shipScu)
    {
        var routeScu = 0;
        foreach (var r in routes)
        {
            if (r.Stage == AcceptedStage.Sold) continue;
            routeScu += r.ActualQty ?? r.TripQty;
        }
        return new CommittedLoad(routeScu, Math.Max(0, contractScu), shipScu);
    }
}
