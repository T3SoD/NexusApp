using System.Globalization;
using System.Text;

namespace NexusApp.Services;

// Shared sanitizer for untrusted third-party text (fields from imported .nexuslib / .nexusgrid files)
// before it is written to nexus.log or stored on a record. Strips Unicode Control (Cc: CR, LF, tab,
// C0/C1) and Format (Cf: zero-width, bidi overrides, BOM) characters and truncates, so a value like
// "pilot\r\n[SCAN] forged" cannot forge log lines and an oversized field cannot bloat a record.
//
// Promoted out of GridShareService.Clean so the log sinks and the .nexusgrid importer share one
// implementation (GridShareService still calls Clean for its stored fields).
internal static class TextSanitizer
{
    // Cap for a single interpolated log token: long enough for any real handle / ship id / blueprint
    // name, short enough that one hostile field cannot flood a log line.
    private const int LogMax = 512;

    // Strip control and bidi/format characters (keeping newlines only where allowed) and truncate.
    public static string Clean(string? s, int max, bool allowNewlines)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(Math.Min(s.Length, max));
        foreach (var ch in s)
        {
            if (sb.Length >= max) break;
            if (allowNewlines && ch == '\n') { sb.Append('\n'); continue; }
            // Category-based so every invisible/injecting code point is covered, not just the common ones.
            var cat = char.GetUnicodeCategory(ch);
            if (cat is UnicodeCategory.Control or UnicodeCategory.Format) continue;
            sb.Append(ch);
        }
        return sb.ToString();
    }

    // Single-line, length-capped form for interpolating an untrusted field into a nexus.log line.
    public static string ForLog(string? s) => Clean(s, LogMax, allowNewlines: false);
}
