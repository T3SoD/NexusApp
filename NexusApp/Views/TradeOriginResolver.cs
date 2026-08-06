using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
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
    // "at the origin". uexLocation (LocationTracker.LastKnownUexLocation) is an optional first
    // pass, tried before the exact/substring passes below: some in-game display names (e.g. "Pyro
    // Gateway Station", from LocationAliases' RR_JP_StantonPyro) simply do not appear anywhere in
    // UEX's own Location/Name vocabulary ("Pyro Gateway (Stanton)"), so no amount of exact or
    // substring matching against locationLabel can ever connect them - the live planner silently
    // produced zero routes at a real, correctly-identified gateway station until this was added.
    // uexLocation is resolved from the raw Game.log token, never derived from locationLabel here,
    // because in-game display names are not unique (three raw gateway tokens all display as
    // "Stanton Gateway Station") and matching on the display name would risk resolving to a
    // terminal in the wrong system. Falls through to exact, then substring, matching on
    // locationLabel exactly as before whenever uexLocation is null or matches nothing - every
    // existing non-gateway location (the other ~38 aliases, all of which already equal a real UEX
    // Location string) is completely unaffected. Empty when nothing matches at all - an honest
    // empty FROM HERE list beats a guessed one (spec: "never a bare number, never null ambiguity" -
    // the same honesty rule applied to zero matches here).
    public static IReadOnlySet<int> TerminalIdsForLocation(string? locationLabel, IReadOnlyList<MarketTerminal> terminals, string? uexLocation = null)
    {
        if (!string.IsNullOrWhiteSpace(uexLocation))
        {
            var fromUex = terminals.Where(t =>
                    string.Equals(t.Location, uexLocation, StringComparison.OrdinalIgnoreCase))
                .Select(t => t.Id).ToHashSet();
            if (fromUex.Count > 0) return fromUex;
        }
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
    // liveUexLocation (LocationTracker.LastKnownUexLocation) is passed straight through as
    // TerminalIdsForLocation's first-pass hint; see that method's doc comment for why it exists
    // and why it is safe. Any other value is a terminal name, resolved via TerminalIdForName -
    // honestly empty when it fails to resolve, same contract as DestTerminalIds' unresolved-name
    // case.
    internal static IReadOnlySet<int>? StartTerminalIds(string? startManual, string? liveLocation, IReadOnlyList<MarketTerminal> terminals, string? liveUexLocation = null)
    {
        if (string.IsNullOrEmpty(startManual) || startManual == "ANY") return null;
        if (startManual == "LIVE")
            return string.IsNullOrEmpty(liveLocation) ? new HashSet<int>() : TerminalIdsForLocation(liveLocation, terminals, liveUexLocation);
        return TerminalIdForName(startManual, terminals) is { } id ? new HashSet<int> { id } : new HashSet<int>();
    }

    // The move-rebuild gate (2026-08-04): whether the persisted start kind above actually
    // reads the live location, so TradePage's Locations.Changed handler re-ranks the planner only
    // when a move can change its inputs. Mirrors StartTerminalIds' branching: ANY (also
    // null/empty, the same fail-open) and named-terminal starts ignore liveLocation entirely, and
    // re-ranking them on a move tears down and re-fades an identical results list.
    internal static bool StartDependsOnLiveLocation(string? startManual) => startManual == "LIVE";

    // Location-first DISPLAY flip (2026-07-31): UEX terminal names are Shop-first
    // ("Admin - ARC-L1", "Platinum Bay - Everus Harbor"), which clumps every station's admin
    // office/trade hub under the same first letter in an alphabetical picker instead of grouping by
    // where it actually is. Verified against the live snapshot: zero collisions across all
    // 135 listed terminals (see LocationFirst_CollisionFreeOverFixture in
    // TradeOriginResolverTests.cs for a fixture-backed regression check of that property).
    //
    // Pure DISPLAY transform - callers (TradePage.Planner.cs's RefreshStartCombo/RefreshDestCombo)
    // must keep the REAL UEX name as the persisted/resolved value (AppSettings.TradeStartManual/
    // TradeDestManual, TerminalIdForName, StartTerminalIds) and only show this method's return
    // value on screen. Nothing in this method or its callers may feed a flipped string back into
    // TerminalIdForName - that lookup only ever sees genuine UEX names, so a leaked flipped label
    // would silently resolve to nothing (an empty terminal id set, not an exception).
    //
    // Rule: split on " - ". One segment (58 of the 135, e.g. "Ashland", "ArcCorp Mining Area 045")
    // has no shop-vs-location split to make and is returned EXACTLY as given (not even trimmed -
    // the rule only touches segments it actually rearranges). Two or more segments become
    // "{last segment} - {first segment}", trimmed, dropping any middle segments - those are
    // sub-location detail the flip does not attempt to preserve (e.g. "Admin - L19 Residences -
    // Metro Center - Lorville" -> "Lorville - Admin"). Null/empty passes through unchanged; this
    // never throws.
    [return: NotNullIfNotNull(nameof(name))]
    internal static string? LocationFirst(string? name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        var parts = name.Split(" - ", StringSplitOptions.None);
        if (parts.Length <= 1) return name;
        return $"{parts[^1].Trim()} - {parts[0].Trim()}";
    }
}
