using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace NexusApp.Services;

// Bundled, build-time reference: internal component key -> clean official name.
//
// Community localization mods rewrite only the VALUE side of the game's localization file; the
// KEY (e.g. item_Name_SHLD_GODI_S02_FR76) is constant across every mod and custom format, so it
// is the mod-independent anchor used to translate any custom blueprint string back to a name the
// seed knows. This reference supplies the second hop, key -> official name, derived from the
// default-format component string list embedded in the build (strip the "Class/Size/Grade "
// prefix from each value). The default list is refreshed per game patch by dropping in an updated
// Data/components.ini; the data only reaches users through a new build (no OTA).
public static class ComponentStringReference
{
    // Leading default-format "Class/Size/Grade " prefix, e.g. "Mil/2/A " or "Civ/0/C ".
    private static readonly Regex DefaultPrefix = new(@"^[A-Za-z]+/\d+/[A-Za-z]+\s+", RegexOptions.Compiled);
    private const string KeyPrefix = "item_Name";
    private const char Bom = '\uFEFF';

    private static readonly System.Lazy<IReadOnlyDictionary<string, string>> _embedded = new(LoadEmbedded);

    /// <summary>internal key (normalized) -> official name, from the embedded default component list.</summary>
    public static IReadOnlyDictionary<string, string> KeyToOfficialName => _embedded.Value;

    /// <summary>Builds the key -> official-name map from default-format "key=Class/Size/Grade Name" lines.
    /// Keys are normalized so the game's two spellings (item_Name… / item_Name_…) collapse together.</summary>
    public static IReadOnlyDictionary<string, string> BuildMap(IEnumerable<string> lines)
    {
        var map = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            int eq = raw.IndexOf('=');
            if (eq <= 0) continue;

            var key = raw[..eq].Trim().TrimStart(Bom).Trim();
            if (!key.StartsWith(KeyPrefix, System.StringComparison.OrdinalIgnoreCase)) continue;

            var value = raw[(eq + 1)..].Trim();
            if (value.Length == 0) continue;

            map[NormalizeKey(key)] = StripDefaultPrefix(value);
        }
        return map;
    }

    /// <summary>The clean official name = value with any leading default "Class/Size/Grade " prefix removed.</summary>
    public static string StripDefaultPrefix(string value) => DefaultPrefix.Replace(value, "");

    /// <summary>Collapse the game's two key spellings to one form by dropping an optional underscore
    /// directly after "item_Name" (item_Name_SHLD… -> item_NameSHLD…). Comparison is case-insensitive,
    /// so a leading capital is harmless.</summary>
    public static string NormalizeKey(string key) =>
        key.Length > KeyPrefix.Length
        && key.StartsWith(KeyPrefix, System.StringComparison.OrdinalIgnoreCase)
        && key[KeyPrefix.Length] == '_'
            ? KeyPrefix + key[(KeyPrefix.Length + 1)..]
            : key;

    private static IReadOnlyDictionary<string, string> LoadEmbedded()
    {
        using var stream = typeof(ComponentStringReference).Assembly
            .GetManifestResourceStream("NexusApp.Data.components.ini");
        if (stream == null) return new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
        using var sr = new StreamReader(stream);
        return BuildMap(ReadLines(sr));
    }

    private static IEnumerable<string> ReadLines(StreamReader sr)
    {
        string? line;
        while ((line = sr.ReadLine()) != null) yield return line;
    }
}
