using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace NexusApp.Services;

// BETA. Parses "Received Blueprint: <name>" events out of SC's Game.log and maps them
// to Nexus blueprint names so ownership can auto-fill. Handles BOTH vanilla names and
// the StarStrings text mod (MrKraken/StarStrings), which prefixes SHIP COMPONENTS with
// Type/Size/Grade (e.g. "Tundra" -> "Mil/1/D Tundra") and leaves armor/FPS-weapons/ammo
// alone. Reads a game-authored file — see the EAC note in [[nexus-gamelog-ownership]].
public sealed class GameLogBlueprintImporter
{
    private const string Marker = "Received Blueprint:";
    // Leading StarStrings "Type/Size/Grade " prefix, e.g. "Mil/2/B " or "Ind/3/A ".
    private static readonly Regex StarStringsPrefix = new(@"^[A-Za-z]+/\d+/[A-Za-z]+\s+", RegexOptions.Compiled);

    private readonly Dictionary<string, string> _known;   // case-insensitive lookup -> canonical seed name

    public GameLogBlueprintImporter(IEnumerable<string> seedBlueprintNames)
    {
        _known = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var n in seedBlueprintNames)
            if (!string.IsNullOrWhiteSpace(n)) _known[n] = n;
    }

    // Pulls the blueprint name from a log line, or null if it isn't a receipt. Lines:
    //   ... "Received Blueprint: A03 Sniper Rifle: " [122] ...   (older logs omit the ":")
    public static string? ExtractRawName(string line)
    {
        int i = line.IndexOf(Marker, System.StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        i += Marker.Length;
        int q = line.IndexOf('"', i);
        string seg = (q >= 0 ? line[i..q] : line[i..]).Trim();
        if (seg.EndsWith(":")) seg = seg[..^1].TrimEnd();
        return seg.Length == 0 ? null : seg;
    }

    // Raw log name -> canonical Nexus name, or null if unknown.
    //   1) exact (vanilla everything + modded armor/weapons/ammo StarStrings doesn't touch)
    //   2) strip the StarStrings ship-component prefix, retry (modded ship components)
    // A vanilla name has no prefix, so step 2 is a harmless no-op for unmodded users.
    public string? Resolve(string rawName)
    {
        if (_known.TryGetValue(rawName, out var canon)) return canon;
        var stripped = StarStringsPrefix.Replace(rawName, "");
        if (stripped.Length != rawName.Length && _known.TryGetValue(stripped, out canon)) return canon;
        return null;
    }

    public string? ResolveLine(string line)
    {
        var raw = ExtractRawName(line);
        return raw is null ? null : Resolve(raw);
    }

    /// <summary>True if a raw name carries a StarStrings "Type/Size/Grade " prefix (modded ship
    /// components). Used to flag likely-modded names in the unrecognized-import report.</summary>
    public static bool HasStarStringsPrefix(string name) => StarStringsPrefix.IsMatch(name);

    public sealed record HistoryScan(List<string> Matched, List<string> Unmatched, int FilesScanned);

    // Scans the current Game.log + sibling logbackups/*.log for every blueprint receipt.
    // Returns DISTINCT canonical matched names + distinct unmatched raw names. Read-only,
    // but logbackups can be hundreds of MB — call from a background thread.
    public HistoryScan ScanHistory(string liveLogPath, System.Action<int>? progress = null)
    {
        var matched = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        var unmatched = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

        var files = new List<string>();
        try
        {
            if (File.Exists(liveLogPath)) files.Add(liveLogPath);
            var dir = Path.GetDirectoryName(liveLogPath);
            if (!string.IsNullOrEmpty(dir))
            {
                var backups = Path.Combine(dir, "logbackups");
                if (Directory.Exists(backups)) files.AddRange(Directory.EnumerateFiles(backups, "*.log"));
            }
        }
        catch { /* ignore enumeration errors */ }

        int done = 0;
        foreach (var f in files)
        {
            try
            {
                using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs, Encoding.UTF8);
                string? line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (line.IndexOf(Marker, System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var raw = ExtractRawName(line);
                    if (raw is null) continue;
                    var canon = Resolve(raw);
                    if (canon is not null) matched.Add(canon); else unmatched.Add(raw);
                }
            }
            catch { /* skip unreadable / locked file */ }
            progress?.Invoke(++done);
        }

        return new HistoryScan(matched.OrderBy(s => s).ToList(), unmatched.OrderBy(s => s).ToList(), files.Count);
    }
}
