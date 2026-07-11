namespace NexusApp.Services;

using NexusApp.Models;

/// <summary>Per-resource composition lookup that hits the database once per name
/// (case-insensitive) so overlay cards can query lazily on expand. Empty results
/// cache too - hand/vehicle ores without composition data stay cheap.</summary>
public sealed class CompositionCache
{
    private readonly Func<string, List<CompositionPart>> _loader;
    private readonly Dictionary<string, List<CompositionPart>> _byName =
        new(StringComparer.OrdinalIgnoreCase);

    public CompositionCache(Func<string, List<CompositionPart>> loader) => _loader = loader;

    public IReadOnlyList<CompositionPart> Get(string resourceName)
    {
        if (!_byName.TryGetValue(resourceName, out var parts))
        {
            parts = _loader(resourceName) ?? new List<CompositionPart>();
            _byName[resourceName] = parts;
        }
        return parts;
    }
}
