using NexusApp.Models;

namespace NexusApp.Services;

// The EXPECTED segment's arithmetic (trade/cargo fusion spec 2026-08-09, sections 1.4/4.3/4.4,
// task D4). Deliberately its OWN small fold and NOT folded into CargoValue: CargoValue's own
// pinned test (CargoValueTests.CargoValue_NeedsNoRouteOrTerminalContext) keeps that fold free of
// AcceptedRoute knowledge on purpose, because spec 4.3 draws the line there - "IN CARGO needs no
// route attribution at all... Only EXPECTED needs a route binding, since it needs the accepted
// route's sell price." So the ONE fold that does need a route's price gets a home of its own
// instead of teaching CargoValue about routes.
public static class AcceptedRouteMoney
{
    /// <summary>What every LOADED accepted route is expected to return once its cargo sells,
    /// summed across all of them: PerScuMargin x (ActualQty ?? TripQty) per route - the real
    /// quantity once a buy has corrected it, the planned figure otherwise. Null when no route
    /// contributes, so the conversion bar's EXPECTED segment is simply absent rather than a
    /// guessed zero (the honesty rule, spec section 0): an Accepted route has no cargo yet to
    /// price, and a Sold route has already realised its margin and left the active list entirely.
    ///
    /// <para>Sell-only routes (null BuyTerminalId) are EXCLUDED even though RoutePlanner.ToSellPin
    /// now creates them Loaded (code review fix, 2026-08-09): AcceptedRoute.PerScuMargin's own doc
    /// comment says a sell-only route stores the raw SELL PRICE there, not a margin, because there
    /// is no buy side to subtract. Summing that price as though it were a margin would inflate
    /// EXPECTED by the route's full gross revenue instead of its (unknown, since there is no
    /// buy leg to net against) profit.</para></summary>
    public static long? ExpectedMargin(IReadOnlyList<AcceptedRoute> routes)
    {
        double total = 0;
        bool any = false;
        foreach (var r in routes)
        {
            if (r.Stage != AcceptedStage.Loaded) continue;
            if (r.BuyTerminalId is null) continue;   // sell-only: PerScuMargin is a price, not a margin
            any = true;
            total += r.PerScuMargin * (r.ActualQty ?? r.TripQty);
        }
        return any ? (long)Math.Round(total) : null;
    }
}
