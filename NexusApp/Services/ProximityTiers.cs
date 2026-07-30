namespace NexusApp.Services;

// How close two terminals are, for the route planner's proximity badge. Coarse tiers only - no
// distances, no ETAs (spec: "no invented ETAs. Proximity as hierarchy tiers.").
public enum ProximityTier { SameOrbit, SamePlanet, SameSystem, CrossSystem }

// Pure derivation over MarketTerminal's location hierarchy fields (System, Orbit, PlanetOrMoon,
// Location). Each check requires BOTH terminals to carry a non-empty value for that field: an
// empty/unknown field can never assert a tighter tier, it only falls through to the next wider
// one, down to CrossSystem when nothing is confirmable. Conservative by design - a wrong "closer"
// badge is worse than a wide one.
public static class ProximityTiers
{
    public static ProximityTier Derive(MarketTerminal a, MarketTerminal b)
    {
        if (SameNonEmpty(a.Orbit, b.Orbit) || SameNonEmpty(a.Location, b.Location))
            return ProximityTier.SameOrbit;
        if (SameNonEmpty(a.PlanetOrMoon, b.PlanetOrMoon))
            return ProximityTier.SamePlanet;
        if (SameNonEmpty(a.System, b.System))
            return ProximityTier.SameSystem;
        return ProximityTier.CrossSystem;
    }

    public static string Label(ProximityTier t) => t switch
    {
        ProximityTier.SameOrbit   => "SAME ORBIT",
        ProximityTier.SamePlanet  => "SAME PLANET",
        ProximityTier.SameSystem  => "SAME SYSTEM",
        ProximityTier.CrossSystem => "CROSS-SYSTEM",
        _                         => "",
    };

    private static bool SameNonEmpty(string x, string y) =>
        !string.IsNullOrEmpty(x) && !string.IsNullOrEmpty(y)
        && string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
}
