namespace Nexus_v4;

/// <summary>
/// The Star Citizen patch this app's reference data targets. This is ISOLATED from the
/// mining-data version on purpose: it is bumped manually, only when the game itself
/// patches, and is never touched by the auto-update flow. The mining-data version
/// (the seed's <c>miningDataVersion</c>) updates independently as seed content changes —
/// see <see cref="Services.DataService.MiningDataVersion"/> and DataUpdateService.
/// </summary>
public static class GameData
{
    /// <summary>SC live patch, e.g. "4.8.1". Edit this (and only this) on a game patch.</summary>
    public const string Version = "4.8.1";
}
