using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NexusApp.Services;

public sealed record GameLogExportResult(string Text, int LinesKept, int LinesTotal, bool Scrubbed,
                                         int FilesScanned = 1, int FilesWithContent = 1);

/// <summary>
/// Builds the Game.log slice a user attaches to a bug report (issue #48). Pure formatting and
/// filtering so it is unit-testable headless, the same shape as DiagnosticSnapshot.
///
/// <para>TIME. Game.log stamps every line <c>&lt;yyyy-MM-ddTHH:mm:ss.fffZ&gt;</c> and that Z is real:
/// the game writes UTC, not local time (the same format every parser in this app already reads,
/// CommodityLogParser.ParseStamp). The issue asks to "confirm time in game.log compared to system
/// time" - this is that answer. The UI takes LOCAL times from the user, because that is what a clock
/// on the wall says, and converts to UTC here. A user in UTC-5 asking for "14:00 to 15:00" gets
/// 19:00-20:00Z, which is what they meant.</para>
///
/// <para>PII. Scrubbing is on by default and can be turned off, because the maintainer sometimes
/// needs the handle to correlate a report. What it removes is listed on <see cref="Scrub"/>.</para>
/// </summary>
public static class GameLogExport
{
    // The stamp every Game.log line opens with. Anchored: a timestamp-shaped string later in a line
    // is data, not the line's own time.
    private static readonly Regex StampRx =
        new(@"^<(?<ts>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z)>", RegexOptions.Compiled);

    // PII shapes, all confirmed against the parsers that already read these lines.
    private static readonly Regex HandleField = new(@"Handle\[[^\]]*\]", RegexOptions.Compiled);
    private static readonly Regex NameField   = new(@"(?<= - name )\S+(?= - state)", RegexOptions.Compiled);
    private static readonly Regex PlayerId    = new(@"playerId\[[^\]]*\]", RegexOptions.Compiled);
    private static readonly Regex AccountId   = new(@"(?<key>accountId|account_id|geid)\[[^\]]*\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Ipv4        = new(@"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b", RegexOptions.Compiled);

    /// <summary>The line's own UTC stamp, or null when it has none (a continuation line: a stack
    /// trace, a wrapped message). Callers must not treat null as "out of range" - see Build.</summary>
    public static DateTime? StampOf(string? line)
    {
        if (string.IsNullOrEmpty(line)) return null;
        var m = StampRx.Match(line);
        if (!m.Success) return null;
        return DateTime.ParseExact(m.Groups["ts"].Value, "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    }

    /// <summary>Removes what identifies the player, leaving the shape of every line intact so the
    /// log stays readable and every parser still matches it:
    /// <list type="bullet">
    /// <item>the RSI handle wherever it appears, including inside other words' boundaries</item>
    /// <item><c>Handle[...]</c> and the <c>- name X - state</c> character line</item>
    /// <item><c>playerId[...]</c>, <c>accountId[...]</c>, <c>geid[...]</c></item>
    /// <item>IPv4 addresses (shard and server connection lines)</item>
    /// <item>the Windows user-profile path, which can be a real name</item>
    /// </list>
    /// The handle replacement runs LAST on purpose: the field replacements above already removed the
    /// structured copies, so what is left is the loose mentions (party lines, kill feed, chat).</summary>
    public static string Scrub(string line, string? handle, string? home)
    {
        if (string.IsNullOrEmpty(line)) return line ?? "";
        var s = line;
        s = HandleField.Replace(s, "Handle[<REDACTED>]");
        s = NameField.Replace(s, "<REDACTED>");
        s = PlayerId.Replace(s, "playerId[<REDACTED>]");
        s = AccountId.Replace(s, m => $"{m.Groups["key"].Value}[<REDACTED>]");
        s = Ipv4.Replace(s, "<IP>");
        s = DiagnosticSnapshot.RedactUserProfile(s, home);
        if (!string.IsNullOrWhiteSpace(handle))
            s = Regex.Replace(s, Regex.Escape(handle), "<REDACTED>", RegexOptions.IgnoreCase);
        return s;
    }

    /// <summary>
    /// Filters to the inclusive UTC window and optionally scrubs, returning the finished file text
    /// plus the counts the dialog reports back.
    ///
    /// <para>A line with no stamp INHERITS the last stamp seen, so a stack trace or a wrapped
    /// message is kept or dropped with the entry it belongs to instead of being orphaned. Lines
    /// before the first stamp in the file (a header the game wrote before its clock line) are kept
    /// only when there is no lower bound, since nothing can place them in time.</para>
    ///
    /// <para>Null bounds mean unbounded on that side, so "everything" is the natural default.</para>
    /// </summary>
    public static GameLogExportResult Build(
        IEnumerable<string> lines, DateTime? fromUtc, DateTime? toUtc, bool scrub,
        string? handle, string? home, string appVersion, DateTime nowUtc)
        => Build(new[] { new GameLogSource("Game.log", lines.ToList()) },
                 fromUtc, toUtc, scrub, handle, home, appVersion, nowUtc);

    /// <summary>
    /// The same filter across MANY session files (issue #48 follow-up). Star Citizen keeps only the
    /// current session in Game.log and moves finished ones into logbackups, so a range wider than
    /// one evening spans several files. Each source is filtered independently, and one that
    /// contributes nothing is named in the header rather than silently omitted, so a user who
    /// exports four days and gets one session can see that the other files were read and had
    /// nothing in range, instead of guessing.
    ///
    /// <para>Sources must arrive oldest first (GameLogSources.Discover orders them), so the export
    /// reads forwards in time.</para>
    /// </summary>
    public static GameLogExportResult Build(
        IReadOnlyList<GameLogSource> sources, DateTime? fromUtc, DateTime? toUtc, bool scrub,
        string? handle, string? home, string appVersion, DateTime nowUtc)
    {
        var body = new StringBuilder();
        var total = 0;
        var keptCount = 0;
        var withContent = 0;
        var empties = new List<string>();

        foreach (var source in sources)
        {
            var kept = new List<string>();
            DateTime? current = null;   // per file: a session's stamps never carry into the next

            foreach (var line in source.Lines)
            {
                total++;
                var stamp = StampOf(line);
                if (stamp is not null) current = stamp;

                bool inRange = current is { } c
                    ? (fromUtc is null || c >= fromUtc) && (toUtc is null || c <= toUtc)
                    : fromUtc is null;   // nothing to place it by: keep it only when nothing is excluded
                if (!inRange) continue;

                kept.Add(scrub ? Scrub(line, handle, home) : line);
            }

            if (kept.Count == 0) { empties.Add(source.Name); continue; }

            withContent++;
            keptCount += kept.Count;
            // Session boundaries are marked so the reader can tell one launch from the next; the
            // file NAME only, never its path, which runs through the Windows user folder.
            body.AppendLine($"--- {source.Name} ({kept.Count} lines) ---");
            foreach (var line in kept) body.AppendLine(line);
            body.AppendLine();
        }

        var sb = new StringBuilder();
        sb.AppendLine("=== Star Citizen Game.log export ===");
        sb.AppendLine($"Exported: {nowUtc:yyyy-MM-dd HH:mm:ss} UTC by Nexus {appVersion}");
        sb.AppendLine($"Range: {Describe(fromUtc)} to {Describe(toUtc)} (UTC, the timezone Game.log itself writes)");
        sb.AppendLine(scrub
            ? "Personal details: REMOVED (handle, account and player ids, IP addresses, Windows user folder)"
            : "Personal details: KEPT - this file identifies you. Share it only with the developer.");
        sb.AppendLine($"Session files: {withContent} of {sources.Count} had lines in range");
        if (empties.Count > 0) sb.AppendLine($"Nothing in range from: {string.Join(", ", empties)}");
        sb.AppendLine($"Lines: {keptCount} of {total}");
        sb.AppendLine();
        sb.Append(body);
        return new GameLogExportResult(sb.ToString(), keptCount, total, scrub, sources.Count, withContent);
    }

    private static string Describe(DateTime? utc) =>
        utc is { } d ? d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "the beginning";

    /// <summary>The default file name. Carries the date so a user attaching two exports to one issue
    /// can tell them apart, and says "scrubbed" when it is, so the maintainer knows what they have.</summary>
    public static string SuggestedFileName(DateTime nowLocal, bool scrubbed) =>
        $"game_log_{nowLocal:yyyyMMdd_HHmmss}{(scrubbed ? "_scrubbed" : "")}.txt";
}
