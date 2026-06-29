using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace NexusApp.Services;

// BETA. Parses "Received Blueprint: <name>" events out of SC's Game.log and maps them
// to Nexus blueprint names so ownership can auto-fill. Handles BOTH vanilla names and the
// community StarStrings text mod, which (in its default format) prefixes SHIP COMPONENTS with
// Type/Size/Grade (e.g. "Tundra" -> "Mil/1/D Tundra") and leaves armor/FPS-weapons/ammo
// alone. Other custom localization formats are resolved via the user's own global.ini - see
// GlobalIniReader / ComponentStringReference. Reads a game-authored file - see the EAC note
// in [[nexus-gamelog-ownership]].
public sealed class GameLogBlueprintImporter
{
    private const string Marker = "Received Blueprint:";
    // Leading StarStrings "Type/Size/Grade " prefix, e.g. "Mil/2/B " or "Ind/3/A ".
    private static readonly Regex StarStringsPrefix = new(@"^[A-Za-z]+/\d+/[A-Za-z]+\s+", RegexOptions.Compiled);

    // StarStrings renames ship components to "<Class>/<Size>/<Grade> Name" (e.g. "Mil/1/D Tundra").
    // Spotting that token anywhere in the log reliably signals the mod is active - even when the
    // player received no component blueprints. The class set is fixed: Civ/Cmp/Ind/Mil/Sth.
    private static readonly Regex StarStringsSignature = new(@"\b(Civ|Cmp|Ind|Mil|Sth)/\d{1,2}/[A-Za-z]\b", RegexOptions.Compiled);

    // Leading "<2026-05-19T11:04:00.765Z>" timestamp every Game.log line opens with. Used to report
    // how far back a history scan could actually see, so the user understands that blueprints received
    // before then aren't recoverable (SC overwrites older logs). Null for the rare line without one.
    private static readonly Regex LineTimestamp = new(@"^<(?<ts>[0-9T:.\-+Z]+)>", RegexOptions.Compiled);

    /// <summary>Parses the leading "&lt;...Z&gt;" timestamp of a Game.log line as UTC, or null if the line
    /// has none. Matches the ISO-8601 Zulu format SC writes (same shape ShardLogParser reads).</summary>
    public static System.DateTime? TryParseLineTimestampUtc(string line)
    {
        var m = LineTimestamp.Match(line);
        if (!m.Success) return null;
        return System.DateTimeOffset.TryParse(m.Groups["ts"].Value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var dto)
            ? dto.UtcDateTime : (System.DateTime?)null;
    }

    private readonly Dictionary<string, string> _known;   // case-insensitive lookup -> canonical seed name

    public GameLogBlueprintImporter(IEnumerable<string> seedBlueprintNames)
    {
        _known = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var n in seedBlueprintNames)
            if (!string.IsNullOrWhiteSpace(n)) _known[n] = n;
    }

    // Pulls the blueprint name from a log line, or null if it isn't a receipt. The game wraps it:
    //   Added notification "Received Blueprint: <NAME>: " [n] to queue. ...   (older logs omit the ":")
    // The name itself can contain quotes for skinned variants (e.g. Atzkav "Igniter" Sniper Rifle),
    // so end at the notification's CLOSING quote - the one right before " [" - not the first quote.
    public static string? ExtractRawName(string line)
    {
        int i = line.IndexOf(Marker, System.StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        i += Marker.Length;

        int end = line.IndexOf("\" [", i, System.StringComparison.Ordinal);   // closing quote before " [n]"
        if (end < 0) end = line.IndexOf('"', i);                              // older/odd lines: first quote
        string seg = (end >= 0 ? line[i..end] : line[i..]).Trim();
        if (seg.EndsWith(":")) seg = seg[..^1].TrimEnd();
        return seg.Length == 0 ? null : seg;
    }

    // Raw log name -> canonical Nexus name, or null if unknown.
    //   1) exact (vanilla everything + modded armor/weapons/ammo StarStrings doesn't touch)
    //   2) strip the default StarStrings ship-component prefix, retry (modded ship components)
    //   3) translate via the user's localization file (any custom mod / format), then match
    // A vanilla name has no prefix, so step 2 is a harmless no-op for unmodded users; step 3 is a
    // no-op when no localizationMap is supplied.
    public string? Resolve(string rawName, IReadOnlyDictionary<string, string>? localizationMap = null)
    {
        if (_known.TryGetValue(rawName, out var canon)) return canon;
        var stripped = StarStringsPrefix.Replace(rawName, "");
        if (stripped.Length != rawName.Length && _known.TryGetValue(stripped, out canon)) return canon;
        // The user's global.ini (built into localizationMap as customDisplay -> official) handles any
        // mod or custom format - including no-separator strings the prefix strip can't touch.
        if (localizationMap is not null
            && localizationMap.TryGetValue(rawName, out var official)
            && _known.TryGetValue(official, out canon)) return canon;
        return null;
    }

    public string? ResolveLine(string line, IReadOnlyDictionary<string, string>? localizationMap = null)
    {
        var raw = ExtractRawName(line);
        return raw is null ? null : Resolve(raw, localizationMap);
    }

    /// <summary>True if a log line contains a StarStrings component token (e.g. "Mil/1/D") - a
    /// reliable signal the mod is active, checked across all log lines during a history scan.</summary>
    public static bool HasStarStringsComponentSignature(string line) =>
        line.IndexOf('/') >= 0 && StarStringsSignature.IsMatch(line);

    public sealed record HistoryScan(List<string> Matched, List<string> Unmatched, List<string> UnmatchedLines, int FilesScanned, bool StarStringsDetected, System.DateTime? EarliestUtc);

    // Scans the current Game.log + sibling logbackups/*.log for every blueprint receipt.
    // Returns DISTINCT canonical matched names + distinct unmatched raw names. Read-only,
    // but logbackups can be hundreds of MB - call from a background thread.
    public HistoryScan ScanHistory(string liveLogPath, System.Action<int>? progress = null,
        IReadOnlyDictionary<string, string>? localizationMap = null)
    {
        var matched = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        var unmatched = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        var unmatchedLines = new List<string>();   // one full example log line per distinct unmatched name
        bool starStrings = false;
        System.DateTime? earliest = null;           // oldest log timestamp seen = how far back the scan could see

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
                bool fileStartCaptured = false;   // SC logs are chronological, so the first stamped line is this file's oldest
                while ((line = sr.ReadLine()) != null)
                {
                    if (!fileStartCaptured)
                    {
                        var ts = TryParseLineTimestampUtc(line);
                        if (ts.HasValue) { fileStartCaptured = true; if (earliest is null || ts < earliest) earliest = ts; }
                    }
                    if (!starStrings && HasStarStringsComponentSignature(line)) starStrings = true;
                    if (line.IndexOf(Marker, System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                    var raw = ExtractRawName(line);
                    if (raw is null) continue;
                    var canon = Resolve(raw, localizationMap);
                    if (canon is not null) matched.Add(canon);
                    else if (unmatched.Add(raw)) unmatchedLines.Add(line.Trim());   // keep the raw line as a sample
                }
            }
            catch { /* skip unreadable / locked file */ }
            progress?.Invoke(++done);
        }

        return new HistoryScan(matched.OrderBy(s => s).ToList(), unmatched.OrderBy(s => s).ToList(), unmatchedLines, files.Count, starStrings, earliest);
    }
}
