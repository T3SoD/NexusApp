namespace NexusApp.Models.Cargo;

// Where a ship's current cargo-grid override came from: the contributor whose submission was applied,
// plus the summary / notes / dates and which aspects were merged. Stored ALONGSIDE (not inside) the
// override so nothing that reads overrides has to change. Cleared when the ship reverts to datamined or
// the owner hand-edits it (owner-authored, no contributor provenance).
public sealed record OverrideProvenance
{
    public string ShipId { get; init; } = "";
    public string Handle { get; init; } = "";
    public string Summary { get; init; } = "";
    public string Notes { get; init; } = "";
    public string CreatedUtc { get; init; } = "";   // when the contributor made the submission
    public string AppliedUtc { get; init; } = "";    // when the owner applied it
    public string Aspects { get; init; } = "";       // the merged GridMergeAspect flags, e.g. "Positions, Sizes"
}
