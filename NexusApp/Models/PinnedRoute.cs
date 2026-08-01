namespace NexusApp.Models;

/// <summary>
/// A trade route the player pinned, as it is PERSISTED (the owner, 2026-08-01: "lets have trade routes
/// pinned persist the same as refinery orders"). Pins shipped session-only that morning on the
/// reasoning that a pin is a "what I'm running right now" marker; a real run outlives a session, so
/// that was wrong.
///
/// <para>WHAT IS STORED AND WHY. The first three fields are the haul's IDENTITY - the same (buy
/// terminal, sell terminal, commodity) triple RoutePlanner.SameHaul has always used - and they are
/// what re-attaches a pin to a live TradeRoute after a restart. The rest are DISPLAY FACTS captured
/// at pin time so the overlay can draw the card before, or without, a matching live route.</para>
///
/// <para>Prices are deliberately NOT stored. A price is the one thing here that rots, and a card
/// quoting yesterday's margin as though it were today's is the exact failure the whole market layer
/// is built to avoid. PerScuMargin is stored because it is what the card shows, and it carries
/// PinnedUtc beside it so its age is always available to say out loud.</para>
///
/// <para>Consequence worth stating plainly: a pin no longer disappears because its route fell out of
/// the current top-25 ranking. Falling out of a ranking means "not among the best 25 for the ship,
/// budget and scope you have selected right now", which is not the same as ceasing to exist - and a
/// pin that silently vanished because the user switched ships would be indistinguishable from a
/// bug. Pins now go away when the user removes them, exactly like a work order.</para>
/// </summary>
public sealed class PinnedRoute
{
    // ── identity: what re-attaches this to a live route ──
    public int BuyTerminalId { get; set; }
    public int SellTerminalId { get; set; }
    public int CommodityId { get; set; }

    // ── display facts, refreshed whenever the route appears in a fresh ranking ──
    public string CommodityName { get; set; } = "";
    public string BuyTerminalName { get; set; } = "";
    public string SellTerminalName { get; set; } = "";
    public int TripQty { get; set; }
    public double PerScuMargin { get; set; }

    /// <summary>When these display facts were last refreshed from a live ranking. Set at pin time
    /// and updated on every rebuild that finds the route, so "how old is this card" is answerable
    /// without guessing.</summary>
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>When the user pinned it. Never updated - it is the answer to "how long have I been
    /// meaning to run this".</summary>
    public DateTime PinnedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Two pins name the same haul when their identity triples match. The same rule
    /// RoutePlanner.SameHaul applies to live routes, kept here so the persisted form can be
    /// compared without materialising a TradeRoute.</summary>
    public bool SameHaulAs(PinnedRoute other) =>
        BuyTerminalId == other.BuyTerminalId
        && SellTerminalId == other.SellTerminalId
        && CommodityId == other.CommodityId;
}
