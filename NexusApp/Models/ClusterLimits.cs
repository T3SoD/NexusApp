namespace NexusApp.Models;

// Max rocks per spawned cluster, by rarity tier. Datamined from the DataCore
// HarvestableClusterPreset records (harvestable/clusteringpresets/shipmineables, one file per
// tier); a sweep of every HarvestableProviderPreset shows each ship ore pairs with exactly its
// rarity-tier preset, and the tiers agree with the community numbers cited in issue #34. Unknown
// rarity returns null: no cap data must never hide a result.
public static class ClusterLimits
{
    public static int? MaxNodes(string rarity) => rarity switch
    {
        "common"    => 6,
        "uncommon"  => 5,
        "rare"      => 4,
        "epic"      => 3,
        "legendary" => 2,
        _           => null,
    };
}
