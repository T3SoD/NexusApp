using System.IO;
using System.Text.Json;

namespace NexusApp.Services;

// The 28 Loading Dock amenity sites (Data\loading_docks.json), keyed by starmap object id, the
// same namespace starmap_locations.json carries. Membership is the auto-load availability gate
// for planner surfaces. Malformed embed folds to empty: no chip, no estimate, no throw.
public sealed class LoadingDockCatalog
{
    private static LoadingDockCatalog? _instance;
    public static LoadingDockCatalog Instance => _instance ??= LoadEmbedded();

    private readonly HashSet<string> _ids;
    private LoadingDockCatalog(HashSet<string> ids) => _ids = ids;

    public int Count => _ids.Count;
    public bool Contains(string? starmapId) => starmapId is not null && _ids.Contains(starmapId);

    public static LoadingDockCatalog LoadEmbedded()
    {
        using var stream = typeof(LoadingDockCatalog).Assembly
            .GetManifestResourceStream("NexusApp.Data.loading_docks.json");
        return stream is null ? new LoadingDockCatalog(new(StringComparer.OrdinalIgnoreCase)) : Load(stream);
    }

    public static LoadingDockCatalog Load(Stream stream)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var raw = JsonSerializer.Deserialize<RawFile>(stream, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
            foreach (var d in raw?.Docks ?? new())
                if (!string.IsNullOrWhiteSpace(d.Starmap))
                    ids.Add(d.Starmap);
        }
        catch
        {
            ids.Clear();
        }
        return new LoadingDockCatalog(ids);
    }

    private sealed class RawFile { public List<RawDock>? Docks { get; set; } }
    private sealed class RawDock { public string? Starmap { get; set; } public string? Name { get; set; } }
}
