using NexusApp.Models;

namespace NexusApp.Services;

// Pure auto-load time math over AutoLoadTimeTable. Callers gate on Calibrated before SHOWING a
// prediction; the math itself stays available so the tracker can record predicted-vs-actual
// samples while the table is still dark.
public static class AutoLoadEstimator
{
    public static int? PredictSeconds(AutoLoadTimeTable table, TransactionKind kind,
                                      IReadOnlyList<CargoBoxGroup> boxes)
    {
        if (table.BaseSeconds(kind) is not { } baseS || boxes.Count == 0) return null;
        double total = baseS;
        foreach (var b in boxes)
        {
            if (table.SecondsPerBox(kind, b.BoxSize) is not { } per) return null;
            total += b.UnitAmount * per;
        }
        return (int)Math.Round(total);
    }

    public static (int Min, int Max)? RangeSeconds(AutoLoadTimeTable table, int scu, string containerSizes)
    {
        if (scu <= 0 || table.BaseSeconds(TransactionKind.Buy) is not { } baseS) return null;
        var sizes = ParseSizes(containerSizes, table);
        if (sizes.Count == 0) return null;
        int? min = null, max = null;
        foreach (var size in sizes)
        {
            if (table.SecondsPerBox(TransactionKind.Buy, size) is not { } per) continue;
            var boxes = (scu + size - 1) / size;
            var t = (int)Math.Round(baseS + boxes * per);
            if (min is null || t < min) min = t;
            if (max is null || t > max) max = t;
        }
        return min is null ? null : (min.Value, max!.Value);
    }

    public static string FormatDuration(int seconds)
    {
        seconds = Math.Max(0, seconds);
        var h = seconds / 3600; var m = seconds % 3600 / 60; var s = seconds % 60;
        return h >= 1 ? $"{h}h {m:00}m {s:00}s" : $"{m}m {s:00}s";
    }

    public static string? FormatRange(AutoLoadTimeTable table, int scu, string containerSizes)
        => RangeSeconds(table, scu, containerSizes) is { } r
            ? $"{FormatDuration(r.Min)} - {FormatDuration(r.Max)}"
            : null;

    // Same split discipline as TradeMath.MaxContainerScu ("1,2,4,8" CSV). Sizes the table does
    // not carry are skipped; an empty or garbage list falls back to the full table.
    private static List<int> ParseSizes(string containerSizes, AutoLoadTimeTable table)
    {
        var sizes = new List<int>();
        if (!string.IsNullOrWhiteSpace(containerSizes))
            foreach (var token in containerSizes.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                if (int.TryParse(token, out var size) && size > 0 && table.IntSizes.Contains(size))
                    sizes.Add(size);
        if (sizes.Count == 0) sizes.AddRange(table.IntSizes);
        return sizes;
    }
}
