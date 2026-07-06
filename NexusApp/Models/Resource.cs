namespace NexusApp.Models;

public class Resource
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int BaseRs { get; set; }
    public string Tier { get; set; } = "";      // S A B C
    public string Rarity { get; set; } = "";    // legendary epic rare uncommon common
    public string Method { get; set; } = "";    // ship vehicle fps fps+vehicle
    public List<string> Locations { get; set; } = [];
    public List<RefineryYield> Refineries { get; set; } = [];
    public bool IsPinned { get; set; }

    // Python: ratio = rs / base_rs; nearest = round(ratio)
    // exact if rs % base_rs == 0; fuzzy if abs(ratio - nearest)/nearest <= 0.5%
    // Ship ores only: hand/vehicle deposits share one flat family signature (3000 fps / 4000
    // vehicle), so they cannot be discriminated by RS and would only pollute ship-scan matching.
    public (bool Matches, int Nodes, bool IsExact, double ErrorPct) CheckRs(int rs)
    {
        if (BaseRs <= 0 || Method != "ship") return (false, 0, false, 0);
        double ratio = (double)rs / BaseRs;
        int nearest = (int)Math.Round(ratio);
        if (nearest < 1) return (false, 0, false, 0);

        if (rs % BaseRs == 0)
            return (true, nearest, true, 0.0);

        double errorPct = Math.Abs(ratio - nearest) / nearest * 100;
        if (errorPct <= 0.5)
            return (true, nearest, false, errorPct);

        return (false, 0, false, 0);
    }
}

public record RefineryYield(string Station, string System, int ModifierPct);

// A deposit belonging to another ore that also yields this resource as a byproduct.
// Ore = the host deposit's primary ore; MinPct/MaxPct = the % band this resource occupies
// in that rock; Probability = best spawn chance across the host's rock-type variants (0-1);
// Variants = how many rock-type variants of that host carry it. Datamined from SC
// MineableComposition; reference data only.
public record FoundInSource(string Ore, double MinPct, double MaxPct, double Probability, int Variants);

// Ship-mining minigame parameters for a resource, datamined from its MineableElement record.
// Class = Metal / Mineral / Gem (from the ResourceType tag). Instability 0-1000 (volatility),
// Resistance 0-1 (fracture hardness), WindowMid 0-1 (optimal charge centre), WindowThin
// (optimal-window tightness), Explosion (blast multiplier), Cluster 0-1 (cluster density).
// Reference data only. Gems read 0 for the physics fields (no ship charge mechanic).
public record MiningProfile(
    string Class, double Instability, double Resistance,
    double WindowMid, double WindowThin, double Explosion, double Cluster);

// One element in a resource's own dedicated rock: the resource itself (IsPrimary) plus the
// byproducts that come with it. MinPct/MaxPct = that element's composition band. Datamined.
public record CompositionPart(string Ore, double MinPct, double MaxPct, bool IsPrimary);
