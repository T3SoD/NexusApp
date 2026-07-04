using NexusApp.Services.Cargo;

namespace NexusApp.Models.Cargo;

// A single ship's cargo-grid layout shared as a .nexusgrid file: the contributor's RSI handle,
// a one-line change summary, free-form notes, and the full grid set (same shape as the override
// store, so it round-trips losslessly). One ship per file.
public sealed record GridSharePackage
{
    public string Format { get; init; } = GridShareService.FormatV1;
    public string ShipId { get; init; } = "";
    public string ShipName { get; init; } = "";
    public string RsiHandle { get; init; } = "";
    public string Summary { get; init; } = "";
    public string Notes { get; init; } = "";
    public string CreatedUtc { get; init; } = "";
    public string AppVersion { get; init; } = "";
    public List<GridOverride> Grids { get; init; } = new();
}
