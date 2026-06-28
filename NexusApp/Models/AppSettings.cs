namespace NexusApp.Models;

public class AppSettings
{
    public int SettingsSchemaVersion { get; set; } = 0;
    public string Theme { get; set; } = "luxury";   // luxury (v5 gold) | classic (v4 slate/teal)
    public double WindowLeft { get; set; } = 100;
    public double WindowTop { get; set; } = 100;
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 820;
    public double OverlayLeft { get; set; } = 20;
    public double OverlayTop { get; set; } = 20;
    public double OverlayWidth { get; set; } = 320;
    public double OverlayHeight { get; set; } = 480;
    public ScanRegion? ScanRegion { get; set; }
    public double OverlayOpacity { get; set; } = 0.7;
    public List<string> PinnedResources { get; set; } = [];
    public List<string> OwnedBlueprints { get; set; } = [];
    public bool FirstRunComplete { get; set; }

    // BETA Session Tracking — remember whether the watch / auto-collect were on, so they
    // resume on next launch, plus the Game.log path the user picked (so a custom location
    // isn't lost on restart — it was reverting to the default C: path).
    public bool GameLogTrackSession { get; set; }
    public bool GameLogAutoTrack { get; set; }
    public string GameLogPath { get; set; } = "";

    // Optional path to Star Citizen's localization file (Data/Localization/english/global.ini).
    // Read-only; lets the importer translate blueprint names renamed by community localization mods
    // (any custom format) back to seed names. "" = auto-detect next to the Game.log path.
    public string GlobalIniPath { get; set; } = "";

    // Blueprint Network — local identity only. The shared roster (other people's libraries)
    // lives in network.db; these few fields are just "who you are" when you export/share.
    public string LocalNetworkId { get; set; } = "";          // stable GUID, generated once
    public string LocalDisplayName { get; set; } = "";        // the label other users see
    public string LocalIdentityKind { get; set; } = "handle"; // "handle" | "nickname"
    public string DetectedRsiHandle { get; set; } = "";       // auto-detected from Game.log (export default)

    // Server/shard display: rolling current + last 3 shards (most recent first), persisted so the
    // RECENT list survives app/SC relaunches. Populated from Game.log <Join PU> lines.
    public List<ShardSession> RecentShards { get; set; } = [];

    // Cargo contract OCR: screen region the ContractScanner reads and whether it starts automatically.
    // ContractRegion mirrors ScanRegion (same pixel-coordinate struct); null = not yet set by user.
    public ScanRegion? ContractRegion { get; set; }
    public bool AutoScanContracts { get; set; }
}

public class ScanRegion
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}
