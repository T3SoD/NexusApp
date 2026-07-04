namespace NexusApp.Models.Cargo;

// Where a container-size cap came from. Default means it was never known and resolves to 16 SCU.
public enum CapSource { Ocr, User, Default }

// Whether a manifest line was typed by the user or imported from an active in-game haul.
public enum ManifestSource { Manual, Imported }

// One line of the cargo to pack: a quantity of SCU of one commodity, with a container-size cap.
// The cap affects only how the SCU is split into physical boxes, never the total SCU.
public sealed class ManifestItem
{
    public const int DefaultCap = 16;

    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Label { get; set; } = "";          // commodity or free label, cosmetic only
    public int Scu { get; set; }
    public int? Cap { get; set; }                    // null = unknown, resolves to DefaultCap
    public CapSource CapSource { get; set; } = CapSource.Default;
    public ManifestSource Source { get; set; } = ManifestSource.Manual;
    public string? GameLogMissionId { get; init; }
    public int ColorId { get; set; }

    // The cap actually used at pack time: never larger than the ship's biggest grid can hold.
    public int EffectiveCap(int shipMaxCap) => Math.Min(Cap ?? DefaultCap, shipMaxCap);
}
