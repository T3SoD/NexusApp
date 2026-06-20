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
}

public class ScanRegion
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}
