using NexusApp.Models;

namespace NexusApp.Services;

// What happens at a stop. Ordered Collect -> Deliver -> Sell, which is both the order a visit
// naturally runs in and the order entries sort within a stop.
public enum StopAction { Collect, Deliver, Sell }

public sealed record StopEntry(StopAction Action, string Commodity, int Scu);

public sealed record BoardStop(string Location, IReadOnlyList<StopEntry> Entries)
{
    public int TotalScu
    {
        get { var t = 0; foreach (var e in Entries) t += e.Scu; return t; }
    }
}

/// <summary>
/// One merged stop list for the Cargo Hauling page (trade/cargo fusion spec 2026-08-09, section
/// 3.5). It replaces the contracts-only consolidation table, which grouped COLLECT and DELIVER
/// into two separate blocks and knew nothing about accepted trade routes at all.
///
/// <para>The spec's reason for merging is a layout argument, not a tidiness one: "it is the only
/// layout in which an empty leg is visible without going looking for it". A hauler standing at
/// Everus Harbor wants one answer to "what do I do here", not three lists to cross-reference. Two
/// contract pickups and a route sale at the same place are one stop.</para>
///
/// <para>SELL is the only action a route contributes, per the spec. A route's BUY leg is
/// deliberately absent: the buy is what the auto-load panel and the route card already narrate,
/// and a route that has not bought yet is a plan rather than a stop on this run.</para>
/// </summary>
public static class StopBoard
{
    /// <summary>Merges contract legs and accepted routes into one stop per location, first-seen
    /// order preserved (the caller sorts, since distance needs the map and the player). Locations
    /// are compared case-insensitively and trimmed, because the two sources are different
    /// vocabularies: contract stops are free text from OCR or Game.log legs, while a route's sell
    /// leg is a UEX terminal name.</summary>
    public static IReadOnlyList<BoardStop> Merge(Consolidation con, IReadOnlyList<AcceptedRoute> routes)
    {
        var order = new List<string>();
        var byLocation = new Dictionary<string, List<StopEntry>>(StringComparer.OrdinalIgnoreCase);

        void Add(string? location, StopAction action, string commodity, int scu)
        {
            var key = (location ?? "").Trim();
            if (!byLocation.TryGetValue(key, out var list))
            {
                byLocation[key] = list = new List<StopEntry>();
                order.Add(key);   // the first spelling seen wins; the comparer keeps later ones out
            }
            list.Add(new StopEntry(action, commodity, scu));
        }

        foreach (var s in con.Pickups)
            foreach (var item in s.Items) Add(s.Location, StopAction.Collect, item.Commodity, item.Scu);
        foreach (var s in con.Dropoffs)
            foreach (var item in s.Items) Add(s.Location, StopAction.Deliver, item.Commodity, item.Scu);

        foreach (var r in routes)
        {
            // A Sold route is finished work and has already left the active list in the real app;
            // skipped here too so the fold cannot render a stop for cargo that is gone.
            if (r.Stage == AcceptedStage.Sold) continue;
            Add(r.SellTerminalName, StopAction.Sell, r.CommodityName, r.ActualQty ?? r.TripQty);
        }

        var result = new List<BoardStop>(order.Count);
        foreach (var key in order)
        {
            // OrderBy is stable, so entries keep their source order within an action.
            var entries = byLocation[key].OrderBy(e => (int)e.Action).ToList();
            result.Add(new BoardStop(key, entries));
        }
        return result;
    }
}
