using NexusApp.Services;

namespace NexusApp.Services.Map;

/// <summary>
/// The trailing "where is this price" label shared by every non-Trade price surface: the Mining
/// Codex dossier and cards, the work order sell block, the decoder scan card, and the overlay SCAN
/// line. All four previously showed a bare terminal name, because PriceHit carried no terminal id
/// and so none of them could resolve a MarketTerminal at all.
///
/// <para>ONE helper rather than four, because the interesting part is the rules, and four copies of
/// a rule is four chances to disagree about it.</para>
///
/// <para>The silence rule is load-bearing and matches what the trade surfaces already do: a terminal
/// that does not resolve returns NULL, and callers render exactly what they render today. Never a
/// placeholder, never "unknown", never a zero. A price whose location we cannot establish is still a
/// perfectly good price.</para>
/// </summary>
internal static class PriceLocationLabel
{
    // Comma rather than a middle dot or bullet: this text is appended inline after a terminal name,
    // sometimes at 10px in the overlay, and a comma renders identically in every font the app ships.
    private const string Sep = ", ";

    /// <summary>Builds the label for a terminal, given where the player currently is.</summary>
    /// <param name="terminal">The resolved market terminal, or null when the id did not resolve.</param>
    /// <param name="map">The app-wide geometry catalog.</param>
    /// <param name="playerAt">Where the player is, or null when unknown - the normal state with the
    /// game closed, and the reason the distance half is always optional.</param>
    /// <returns>"Stanton", "Stanton, 12.4 Gm", "Stanton, another system", or null for silence.</returns>
    public static string? Describe(MarketTerminal? terminal, MapCatalog map, MapObject? playerAt)
    {
        if (terminal is null || string.IsNullOrWhiteSpace(terminal.System)) return null;

        // The system alone is worth showing even with no live session, and it is the half that
        // needed no geometry at all - which is why it works the moment the terminal id exists.
        if (playerAt is null) return terminal.System;

        // Deliberately WORDS, not a number. DistanceMeters returns null across systems because jump
        // travel is not Euclidean, and a blank there would read as "we failed to measure" rather
        // than "this is somewhere else entirely".
        if (!string.Equals(playerAt.System, terminal.System, StringComparison.OrdinalIgnoreCase))
            return terminal.System + Sep + "another system";

        var target = map.ResolveTerminal(terminal);
        return map.DistanceMeters(playerAt, target) is { } meters
            ? terminal.System + Sep + MapCatalog.FormatGm(meters)
            : terminal.System;
    }

    /// <summary>Convenience over the app singletons for the WPF call sites, which all have a
    /// PriceHit and a snapshot rather than a resolved terminal. Returns null on every miss, so a
    /// caller can append it unconditionally.</summary>
    public static string? Describe(int terminalId, IReadOnlyList<MarketTerminal>? terminals,
                                   MapCatalog map, MapObject? playerAt)
    {
        if (terminalId <= 0 || terminals is null) return null;
        var terminal = terminals.FirstOrDefault(t => t.Id == terminalId);
        return Describe(terminal, map, playerAt);
    }
}
