namespace Nexus_v4.Models;

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
    public (bool Matches, int Nodes, bool IsExact, double ErrorPct) CheckRs(int rs)
    {
        if (BaseRs <= 0) return (false, 0, false, 0);
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
