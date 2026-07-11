namespace NexusApp.Services;

/// <summary>Friendly display text for the contract scanner's last pipeline stage.
/// Stage tokens only - raw OCR text never reaches the UI (PII posture).</summary>
public static class ContractStageText
{
    public static string For(string? stage) => stage switch
    {
        "parsed" => "last scan: contract parsed",
        "notext" => "last scan: no text found",
        "noregion" => "last scan: no region set",
        "unavail" => "last scan: scanner unavailable",
        "noanchor" => "last scan: no contract found",
        _ => "last scan: waiting",
    };
}
