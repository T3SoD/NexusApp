namespace NexusApp.Models;

// One in-flight auto-load or auto-unload, opened from a flagged kiosk transaction. StartUtc is
// the LOG line's own stamp, so elapsed time is immune to tail-poll delivery delay.
public sealed record AutoLoadEntry(DateTime StartUtc, TransactionKind Kind, string ShopName,
    string? CommodityName, decimal Scu, IReadOnlyList<CargoBoxGroup> Boxes, int? PredictedSeconds);

// One finished timing observation, the calibration unit for the auto-load time table.
public sealed record AutoLoadSample(int Schema, DateTime TimestampUtc, string Kind, string Shop,
    decimal Scu, Dictionary<string, int> Boxes, int ElapsedSeconds, int? PredictedSeconds,
    bool Abandoned, string GameBuild);
