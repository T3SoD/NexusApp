using System.Globalization;
using System.IO;
using System.Text.Json;
using NexusApp.Models;

namespace NexusApp.Services;

// Embedded auto-load time coefficients (Data\autoload_times.json, DataCore
// GlobalCargoLoadingParams values). Calibrated stays false until real samples validate the
// table; every prediction surface goes dark while it is false. A malformed embed folds to an
// empty table, which keeps Calibrated false and every lookup null.
public sealed class AutoLoadTimeTable
{
    private static AutoLoadTimeTable? _instance;
    public static AutoLoadTimeTable Instance => _instance ??= LoadEmbedded();

    private readonly Dictionary<decimal, double> _load;
    private readonly Dictionary<decimal, double> _unload;
    private readonly double? _baseLoad;
    private readonly double? _baseUnload;

    public bool Calibrated { get; }
    public string GameBuild { get; }
    public IReadOnlyList<int> IntSizes { get; }

    private AutoLoadTimeTable(bool calibrated, string gameBuild, double? baseLoad, double? baseUnload,
                              Dictionary<decimal, double> load, Dictionary<decimal, double> unload)
    {
        Calibrated = calibrated; GameBuild = gameBuild;
        _baseLoad = baseLoad; _baseUnload = baseUnload; _load = load; _unload = unload;
        IntSizes = load.Keys.Where(k => k >= 1m && k == decimal.Truncate(k))
                            .Select(k => (int)k).OrderBy(k => k).ToArray();
    }

    public double? BaseSeconds(TransactionKind kind) => kind == TransactionKind.Buy ? _baseLoad : _baseUnload;

    public double? SecondsPerBox(TransactionKind kind, decimal size)
    {
        var map = kind == TransactionKind.Buy ? _load : _unload;
        return map.TryGetValue(size, out var v) ? v : null;
    }

    public static AutoLoadTimeTable LoadEmbedded()
    {
        using var stream = typeof(AutoLoadTimeTable).Assembly
            .GetManifestResourceStream("NexusApp.Data.autoload_times.json");
        return stream is null ? Empty() : Load(stream);
    }

    public static AutoLoadTimeTable Load(Stream stream)
    {
        try
        {
            var raw = JsonSerializer.Deserialize<RawFile>(stream, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
            if (raw is null) return Empty();
            return new AutoLoadTimeTable(raw.Calibrated, raw.GameBuild ?? "",
                raw.BaseLoadingSeconds, raw.BaseUnloadingSeconds,
                Sizes(raw.BoxLoadingSeconds), Sizes(raw.BoxUnloadingSeconds));
        }
        catch
        {
            return Empty();
        }
    }

    private static AutoLoadTimeTable Empty() =>
        new(false, "", null, null, new Dictionary<decimal, double>(), new Dictionary<decimal, double>());

    private static Dictionary<decimal, double> Sizes(Dictionary<string, double>? raw)
    {
        var result = new Dictionary<decimal, double>();
        if (raw is null) return result;
        foreach (var (key, value) in raw)
            if (decimal.TryParse(key, NumberStyles.Number, CultureInfo.InvariantCulture, out var size))
                result[size] = value;
        return result;
    }

    private sealed class RawFile
    {
        public bool Calibrated { get; set; }
        public string? GameBuild { get; set; }
        public double? BaseLoadingSeconds { get; set; }
        public double? BaseUnloadingSeconds { get; set; }
        public Dictionary<string, double>? BoxLoadingSeconds { get; set; }
        public Dictionary<string, double>? BoxUnloadingSeconds { get; set; }
    }
}
