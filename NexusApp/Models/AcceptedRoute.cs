namespace NexusApp.Models;

/// <summary>
/// A trade route the player pinned, as it is PERSISTED (ruling 2026-08-01: pinned trade routes
/// persist the same way refinery orders do). Pins shipped session-only that morning on the
/// reasoning that a pin is a "what I'm running right now" marker; a real run outlives a session, so
/// that was wrong.
///
/// <para>RENAMED 2026-08-09 (trade/cargo fusion spec, section 1): PinnedRoute became AcceptedRoute.
/// A pin is a bookmark with no end state, which is why pinned routes could never be totalled with
/// contracts; an accepted route is work the player took on, with a lifecycle (Stage moves
/// Accepted -> Loaded -> Sold). This is a type rename only - System.Text.Json serializes property
/// names, not type names, so every pin already in a settings.json file deserializes unchanged.
/// AppSettings.PinnedRoutes deliberately KEEPS its name: see the comment on that property for why.
/// </para>
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
public sealed class AcceptedRoute
{
    // ── identity: what re-attaches this to a live route ──
    /// <summary>Null means a SELL-ONLY pin (2026-08-01, the Sell tab's own PIN TO OVERLAY):
    /// the player already holds the cargo, so there is no buy leg - the pin is a destination plus
    /// a commodity. Sell-only pins draw no Starmap leg (a leg needs two ends) and their card shows
    /// SELL AT with a live distance instead of FROM/band/TO. Planner pins always carry a value, and
    /// pre-existing persisted pins deserialize into the value unchanged.</summary>
    public int? BuyTerminalId { get; set; }
    public int SellTerminalId { get; set; }
    public int CommodityId { get; set; }

    // ── display facts, refreshed whenever the route appears in a fresh ranking ──
    public string CommodityName { get; set; } = "";
    public string BuyTerminalName { get; set; } = "";
    public string SellTerminalName { get; set; } = "";
    public int TripQty { get; set; }
    /// <summary>Planner pins: margin per SCU (sell minus buy). Sell-only pins: the SELL PRICE per
    /// SCU - there is no buy side to subtract. The overlay's tooltip words each accordingly.</summary>
    public double PerScuMargin { get; set; }

    /// <summary>When these display facts were last refreshed from a live ranking. Set at pin time
    /// and updated on every rebuild that finds the route, so "how old is this card" is answerable
    /// without guessing.</summary>
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>When the user pinned it. Never updated - it is the answer to "how long have I been
    /// meaning to run this".</summary>
    public DateTime PinnedUtc { get; set; } = DateTime.UtcNow;

    // ── lifecycle (2026-08-09, trade/cargo fusion spec section 1.2) ──────────────────────────
    // ACCEPTED --(matched buy)--> LOADED --(matched sell)--> SOLD. A route ends only by selling
    // its cargo or by an explicit Delete route - no timeout, no inactivity expiry, no shard-change
    // wipe. That is a deliberate divergence from HaulTracker's contracts, which DO clear on a shard
    // change or PU exit because a contract is a game-side object owned by the mission system; an
    // accepted route is a player-side plan living in settings.json, so it survives shard changes,
    // app restarts, and the game being closed. Do not "fix" this to match contracts later.

    /// <summary>Defaults to Accepted (0) so every route persisted before this field existed
    /// deserializes into the right starting stage - old JSON has no buy or sell matched to it yet,
    /// which is exactly what Accepted means.</summary>
    public AcceptedStage Stage { get; set; }

    /// <summary>What was really bought, from a logged transaction matched to this route - null
    /// until a buy is matched (spec 1.4, "the route corrects itself"). May differ from TripQty, the
    /// plan's figure: accept 750 SCU, buy 680, and this holds 680 - a partial fill is not an error,
    /// it is the honest number the card has to show instead of the plan.</summary>
    public int? ActualQty { get; set; }

    /// <summary>The price actually paid per SCU, derived Amount/Scu from the matched buy - null
    /// until matched. Distinct from PerScuMargin, which is the RANKED margin captured at accept
    /// time and is never overwritten by a realised figure.</summary>
    public decimal? ActualBuyPer { get; set; }

    /// <summary>When a buy was matched to this route (Stage moved to Loaded). Null until then.</summary>
    public DateTime? LoadedUtc { get; set; }

    /// <summary>When a sell was matched to this route (Stage moved to Sold). Null until then.</summary>
    public DateTime? SoldUtc { get; set; }

    /// <summary>Two pins name the same haul when their identity triples match. The same rule
    /// RoutePlanner.SameHaul applies to live routes, kept here so the persisted form can be
    /// compared without materialising a TradeRoute.</summary>
    public bool SameHaulAs(AcceptedRoute other) =>
        BuyTerminalId == other.BuyTerminalId
        && SellTerminalId == other.SellTerminalId
        && CommodityId == other.CommodityId;
}

/// <summary>An accepted route's lifecycle stage (trade/cargo fusion spec, section 1.2):
/// Accepted --(matched buy)--> Loaded --(matched sell)--> Sold. Accepted is 0 on purpose - it is
/// the correct default for JSON written before this field existed, since every such route has no
/// buy or sell matched to it and is exactly as "just accepted" as one accepted today.</summary>
public enum AcceptedStage { Accepted, Loaded, Sold }
