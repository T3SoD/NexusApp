using System.Collections.Generic;
using System.IO;
using System.Text;

namespace NexusApp.Services;

// Reads the user's Star Citizen localization file (Data/Localization/english/global.ini) and joins
// it to the bundled key -> official-name reference to produce a customDisplay -> official lookup.
//
// Community localization mods (any of them) edit only the VALUE side of global.ini; the KEY is
// constant, so reading the user's own file translates whatever custom display string the game
// wrote to Game.log back to a name the seed knows — regardless of the format the user chose, even
// formats with no separators. Strictly read-only and CIG-sanctioned community localization.
public static class GlobalIniReader
{
    private const string KeyPrefix = "item_Name";
    private const char Bom = '\uFEFF';

    /// <summary>customDisplay value -> official name, joining the user's global.ini lines to the
    /// bundled key -> official reference. Lines that aren't item-name entries (item_Desc, UI strings,
    /// etc.) and keys absent from the reference are ignored.</summary>
    public static IReadOnlyDictionary<string, string> BuildCustomToOfficial(
        IEnumerable<string> globalIniLines, IReadOnlyDictionary<string, string> keyToOfficial)
    {
        var map = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var raw in globalIniLines)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            int eq = raw.IndexOf('=');
            if (eq <= 0) continue;

            var key = raw[..eq].Trim().TrimStart(Bom).Trim();
            if (!key.StartsWith(KeyPrefix, System.StringComparison.OrdinalIgnoreCase)) continue;

            if (!keyToOfficial.TryGetValue(ComponentStringReference.NormalizeKey(key), out var official)) continue;

            var value = raw[(eq + 1)..].Trim();
            if (value.Length == 0) continue;

            map[value] = official;   // the game wrote this exact custom string to Game.log
        }
        return map;
    }

    /// <summary>Where global.ini lives relative to the configured Game.log path: both sit under the
    /// install's LIVE/PTU folder, so deriving it means zero extra setup for most users. Null when no
    /// log path is known.</summary>
    public static string? DeriveGlobalIniPath(string gameLogPath)
    {
        if (string.IsNullOrWhiteSpace(gameLogPath)) return null;
        var dir = Path.GetDirectoryName(gameLogPath);
        if (string.IsNullOrEmpty(dir)) return null;
        return Path.Combine(dir, "Data", "Localization", "english", "global.ini");
    }

    /// <summary>Read the user's global.ini (shared, read-only) and build the customDisplay -> official
    /// map. Null when the file is absent or unreadable — callers fall back to today's behavior. The
    /// file can be several MB, so it streams line by line.</summary>
    public static IReadOnlyDictionary<string, string>? TryBuildFromFile(
        string globalIniPath, IReadOnlyDictionary<string, string> keyToOfficial)
    {
        try
        {
            if (string.IsNullOrEmpty(globalIniPath) || !File.Exists(globalIniPath)) return null;
            using var fs = new FileStream(globalIniPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            return BuildCustomToOfficial(ReadLines(sr), keyToOfficial);
        }
        catch { return null; }
    }

    private static IEnumerable<string> ReadLines(StreamReader sr)
    {
        string? line;
        while ((line = sr.ReadLine()) != null) yield return line;
    }
}
