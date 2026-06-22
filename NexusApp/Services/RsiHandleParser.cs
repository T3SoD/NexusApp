using System.IO;

namespace NexusApp.Services;

/// <summary>
/// Extracts the local player's RSI handle from Star Citizen Game.log lines (read-only). Two
/// authoritative login-time lines carry it; both are matched here. Confirmed against a real LIVE
/// Game.log. Only the handle is taken — never the accountId/geid that sit on the same lines.
/// </summary>
public static class RsiHandleParser
{
    /// <summary>Try to read the handle from a single line.</summary>
    public static bool TryExtract(string line, out string handle)
    {
        handle = "";
        if (string.IsNullOrEmpty(line)) return false;

        // Primary — the authenticated login response:
        //   <Legacy login response> [CIG-net] User Login Success - Handle[<HANDLE>] - Time[...]
        if (line.Contains("User Login Success", StringComparison.Ordinal))
        {
            var h = Between(line, "Handle[", "]");
            if (!string.IsNullOrWhiteSpace(h)) { handle = h!.Trim(); return true; }
        }

        // Fallback — the current character status line:
        //   <AccountLoginCharacterStatus_Character> Character: ... - name <HANDLE> - state STATE_CURRENT
        if (line.Contains("AccountLoginCharacterStatus_Character", StringComparison.Ordinal))
        {
            var h = Between(line, " - name ", " - state");
            if (!string.IsNullOrWhiteSpace(h)) { handle = h!.Trim(); return true; }
        }

        return false;
    }

    /// <summary>Scan many lines, returning the most recent handle (newest login wins). Null if none.</summary>
    public static string? ScanForLatest(IEnumerable<string> lines)
    {
        string? latest = null;
        foreach (var line in lines)
            if (TryExtract(line, out var h)) latest = h;
        return latest;
    }

    /// <summary>Scan a Game.log file for the latest handle, opened with shared access (the game keeps
    /// the file open for writing — a plain read would hit a sharing violation). Null on any error.</summary>
    public static string? ScanFile(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            return ScanForLatest(ReadLines(sr));
        }
        catch { return null; }
    }

    private static IEnumerable<string> ReadLines(StreamReader sr)
    {
        string? line;
        while ((line = sr.ReadLine()) != null) yield return line;
    }

    private static string? Between(string s, string startToken, string endToken)
    {
        var i = s.IndexOf(startToken, StringComparison.Ordinal);
        if (i < 0) return null;
        i += startToken.Length;
        var j = s.IndexOf(endToken, i, StringComparison.Ordinal);
        return j < 0 ? null : s.Substring(i, j - i);
    }
}
