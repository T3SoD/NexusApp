using System;
using System.Collections.Generic;
using System.Linq;
using NexusApp.Services;

namespace NexusApp.Views;

// Resolves the TRADE page's "origin" (ORIGIN chip / FROM HERE anchor) to the MarketTerminal ids
// RoutePlanner/SellLookup need. Two entry points because the two origin states in the mock
// (index.html:691-716) resolve differently: manual mode names one exact terminal; live mode's
// LocationTracker key is looser than one terminal (a station can host several).
internal static class TradeOriginResolver
{
    // Manual mode: the ORIGIN dropdown names one terminal by its exact display name.
    public static int? TerminalIdForName(string? terminalName, IReadOnlyList<MarketTerminal> terminals)
    {
        if (string.IsNullOrWhiteSpace(terminalName)) return null;
        return terminals.FirstOrDefault(t =>
            string.Equals(t.Name, terminalName, StringComparison.OrdinalIgnoreCase))?.Id;
    }

    // Live mode: every terminal whose Location or Name matches the tracked location counts as
    // "at the origin". Exact case-insensitive match first; a substring fallback covers a raw
    // Game.log location key that is more specific than the terminal's own Location field (e.g. a
    // station slug that only appears inside a terminal's Name). Empty when nothing matches at all -
    // an honest empty FROM HERE list beats a guessed one (spec: "never a bare number, never null
    // ambiguity" - the same honesty rule applied to zero matches here).
    public static IReadOnlySet<int> TerminalIdsForLocation(string? locationLabel, IReadOnlyList<MarketTerminal> terminals)
    {
        if (string.IsNullOrWhiteSpace(locationLabel)) return new HashSet<int>();
        var exact = terminals.Where(t =>
                string.Equals(t.Location, locationLabel, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t.Name, locationLabel, StringComparison.OrdinalIgnoreCase))
            .Select(t => t.Id).ToHashSet();
        if (exact.Count > 0) return exact;
        return terminals.Where(t =>
                t.Location.Contains(locationLabel, StringComparison.OrdinalIgnoreCase) ||
                t.Name.Contains(locationLabel, StringComparison.OrdinalIgnoreCase))
            .Select(t => t.Id).ToHashSet();
    }

    // Reverse of TerminalIdForName: the MAP tab's "set planner start here" action names a
    // terminal by id (a starmap pin), and TradePage.PrefillPlannerOriginFromMap needs that
    // terminal's display Name to seed the STARTING LOCATION field with - that field persists a
    // name, never an id (AppSettings.TradeStartManual). Null when the id resolves to nothing
    // (a stale pin from a snapshot that has since changed).
    internal static string? OriginNameForTerminal(int terminalId, IReadOnlyList<MarketTerminal> terminals)
        => terminals.FirstOrDefault(t => t.Id == terminalId)?.Name;

    // Route planner STARTING LOCATION resolution (task 10): the one pure seam between the combo's
    // persisted kind (AppSettings.TradeStartManual - "ANY", "LIVE", or a terminal name) and the
    // terminal id set RoutePlanner needs, so RebuildPlanner's derivation is unit testable without a
    // live TradePage/App context. "ANY" (also null/empty, fail-open for legacy/corrupt values)
    // means no constraint - null, mirroring DestTerminalIds' contract for the destination side.
    // "LIVE" resolves through TerminalIdsForLocation above (the same lookup the old FROM HERE
    // anchor's live half used), honestly empty when no live location is known - never a guess.
    // Any other value is a terminal name, resolved via TerminalIdForName - honestly empty when it
    // fails to resolve, same contract as DestTerminalIds' unresolved-name case.
    internal static IReadOnlySet<int>? StartTerminalIds(string? startManual, string? liveLocation, IReadOnlyList<MarketTerminal> terminals)
    {
        if (string.IsNullOrEmpty(startManual) || startManual == "ANY") return null;
        if (startManual == "LIVE")
            return string.IsNullOrEmpty(liveLocation) ? new HashSet<int>() : TerminalIdsForLocation(liveLocation, terminals);
        return TerminalIdForName(startManual, terminals) is { } id ? new HashSet<int> { id } : new HashSet<int>();
    }
}
