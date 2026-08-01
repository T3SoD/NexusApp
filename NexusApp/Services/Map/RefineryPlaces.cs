using NexusApp.Models;

namespace NexusApp.Services.Map;

/// <summary>
/// Puts the mining seed's refinery stations on the map, so "best refinery" can account for where the
/// player actually is (app review G10: the line picked purely on yield modifier and could recommend a
/// station in another system without ever saying so).
///
/// <para>The seed writes gateway refineries in UEX vocabulary - "Stanton Gateway (Nyx)" is the
/// Stanton gateway that sits IN Nyx - while the catalog names the object plainly, "Stanton Gateway",
/// and separates the two same-named gateways by system. The parenthetical is therefore dropped and
/// the yield's own System field does the disambiguating. That is the opposite of what
/// MapCatalog.ResolveSeedLocation does with a parenthetical (it keeps the INNER name, because the
/// mining seed writes planets as "Pyro II (Monox)"), and using that here would resolve "Stanton
/// Gateway (Nyx)" to the star Nyx - a real object, wrong by about a solar system. Hence a separate
/// resolver rather than a shared one.</para>
///
/// <para>18 of the 19 refinery stations in the seed resolve this way. Levski does not, because
/// Delamar is not in the object catalog; that is a silent null, and every caller must render an
/// unplaceable refinery exactly as it renders one today.</para>
/// </summary>
public static class RefineryPlaces
{
    /// <summary>"Stanton Gateway (Nyx)" -> "Stanton Gateway". Names with no parenthetical are
    /// returned unchanged, which is most of them.</summary>
    public static string BaseName(string? station)
    {
        if (string.IsNullOrWhiteSpace(station)) return "";
        var cut = station.IndexOf(" (", StringComparison.Ordinal);
        return (cut > 0 ? station[..cut] : station).Trim();
    }

    /// <summary>The map object a refinery station sits at, or null when it does not resolve.</summary>
    public static MapObject? Resolve(MapCatalog map, RefineryYield? yield)
        => yield is null ? null : map.ByName(yield.System, BaseName(yield.Station));

    /// <summary>
    /// The best refinery for an ore. Yield modifier decides it, exactly as before - this never
    /// promotes a worse refinery for being closer, because "best" has to keep meaning one thing or
    /// the recommendation becomes unreadable. Distance only breaks TIES, which the seed has plenty
    /// of (ten stations share the same modifier for some ores) and which used to be settled by
    /// whatever order the seed happened to list them in.
    /// </summary>
    /// <param name="playerAt">Where the player is, or null when unknown - then this is the old
    /// pure-modifier pick with its original stable ordering.</param>
    public static RefineryYield? Best(IReadOnlyList<RefineryYield>? yields, MapCatalog map, MapObject? playerAt)
    {
        if (yields is null || yields.Count == 0) return null;

        var top = yields.Max(y => y.ModifierPct);
        var tied = yields.Where(y => y.ModifierPct == top).ToList();
        if (tied.Count == 1 || playerAt is null) return tied[0];

        // Unmeasurable ties (another system, or a station the catalog cannot place) sort last but
        // are never dropped: one of them may be the only refinery there is.
        return tied
            .OrderBy(y => map.DistanceMeters(playerAt, Resolve(map, y)) ?? double.PositiveInfinity)
            .First();
    }

    /// <summary>The "where is this" label for a refinery, in the same vocabulary every price
    /// surface already uses: "Stanton", "Stanton, 12.4 Gm", "Stanton, another system", or null.</summary>
    public static string? Describe(RefineryYield? yield, MapCatalog map, MapObject? playerAt)
        => yield is null ? null : PlaceLabel.Describe(yield.System, Resolve(map, yield), map, playerAt);
}
