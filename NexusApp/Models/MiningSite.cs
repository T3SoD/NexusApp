namespace NexusApp.Models;

/// <summary>How a resource spawns at a body: a primary ship rock, a byproduct of other ship
/// rocks, a ROC (ground vehicle) deposit, or a hand-mined (FPS) deposit.</summary>
public enum SpawnKind { Primary, Byproduct, Roc, Hand }

/// <summary>One body where a resource spawns. Share is the rock's share of its group in
/// percent, null for Byproduct rows. MinPct/MaxPct/Probability/Hosts apply to Byproduct
/// rows only; Hosts lists the host rocks in descending rock-weight order.</summary>
public record OreSpawn(string Body, string System, SpawnKind Kind, double? Share, double MinPct, double MaxPct,
    double Probability, IReadOnlyList<string> Hosts, int PackMin, int PackMax);

public record SiteByproduct(string Ore, double MinPct, double MaxPct, double Probability);

public record SiteRock(string Ore, double Weight, double Share, int PackMin, int PackMax, IReadOnlyList<SiteByproduct> Byproducts);

/// <summary>A spawn group at a body. Kind is ship, roc, fps or plants. Count is the plant
/// count for plants groups and 0 otherwise.</summary>
public record SiteGroup(string Kind, double Weight, IReadOnlyList<SiteRock> Rocks, int Count);

public record MiningSite(string Body, string System, IReadOnlyList<SiteGroup> Groups);
