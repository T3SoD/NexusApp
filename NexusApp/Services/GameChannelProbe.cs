using System.Linq;

namespace NexusApp.Services;

/// <summary>File-stat probing for environment auto-follow (issue #28). The pure selection rules
/// (SelectActive) are split from the IO (Stat / Candidates) so the switching behavior is headless
/// testable. EAC rule: file stats only - never a handle to the game process.</summary>
public static class GameChannelProbe
{
    public readonly record struct LogStat(string Path, DateTime CreationUtc, DateTime LastWriteUtc);

    /// <summary>An actively-written log is never abandoned: while the current file's last write is
    /// this fresh, no switch happens regardless of the other candidates' stats. SC writes one log
    /// at a time, so this both prevents flapping and is always correct mid-session.</summary>
    public static readonly TimeSpan ActiveWriteWindow = TimeSpan.FromSeconds(10);

    /// <summary>The StarCitizen root above a KNOWN channel folder, or null for custom layouts
    /// (unrecognized parent folder = single-file semantics, no auto-follow - the spec's escape
    /// hatch for non-standard installs).</summary>
    public static string? RootFrom(string gameLogPath)
    {
        if (GameChannels.FromLogPath(gameLogPath) == GameChannel.Custom) return null;
        try { return System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(gameLogPath)); }
        catch { return null; }
    }

    /// <summary>Sibling channel Game.logs that exist on disk right now, the anchor's own included.
    /// Empty for custom layouts. Probe errors are swallowed, same as FindGameLog's.</summary>
    public static List<string> Candidates(string anchorLogPath)
    {
        var found = new List<string>();
        var root = RootFrom(anchorLogPath);
        if (root is null) return found;
        try
        {
            foreach (var ch in GameChannels.KnownFolders)
            {
                var p = System.IO.Path.Combine(root, ch, "Game.log");
                if (System.IO.File.Exists(p)) found.Add(p);
            }
        }
        catch { /* ignore probe errors */ }
        return found;
    }

    public static LogStat Stat(string path) => new(
        path,
        System.IO.File.GetCreationTimeUtc(path),
        System.IO.File.GetLastWriteTimeUtc(path));

    /// <summary>The candidate to watch (spec section 2): keep an actively-written current file;
    /// else newest creation time wins (SC recreates Game.log on every launch), last-write breaks
    /// creation ties, and the current path wins full ties so equal stats never cause a switch.</summary>
    public static string? SelectActive(IReadOnlyList<LogStat> stats, string currentPath, DateTime nowUtc)
    {
        if (stats.Count == 0) return null;
        foreach (var s in stats)
            if (PathsEqual(s.Path, currentPath) && nowUtc - s.LastWriteUtc < ActiveWriteWindow)
                return s.Path;

        var best = stats[0];
        foreach (var s in stats.Skip(1))
        {
            int byCreation = s.CreationUtc.CompareTo(best.CreationUtc);
            if (byCreation > 0
                || (byCreation == 0 && s.LastWriteUtc > best.LastWriteUtc)
                || (byCreation == 0 && s.LastWriteUtc == best.LastWriteUtc && PathsEqual(s.Path, currentPath)))
                best = s;
        }
        return best.Path;
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
