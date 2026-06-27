namespace NexusApp.Models;

// One Star Citizen shard the player joined, parsed from a Game.log <Join PU> line.
// ServerIp is the game server address (matchmaking-assigned), not the player - no PII.
public sealed class ShardSession
{
    public string ShardId { get; init; } = "";      // pub_use1b_12030094_140
    public string Region { get; init; } = "";        // decoded, e.g. "US East"
    public string RegionCode { get; init; } = "";    // raw, e.g. "use1b"
    public string Instance { get; init; } = "";      // "140"
    public string ServerIp { get; init; } = "";      // 34.181.129.126
    public DateTime JoinedAt { get; init; } = DateTime.UtcNow;
}
