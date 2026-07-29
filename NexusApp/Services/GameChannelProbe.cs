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

    /// <summary>The candidate to watch (spec section 2). Last-write time is the signal, never
    /// creation time on its own: copying a LIVE Game.log into a PTU folder (a documented user
    /// habit) keeps the source's last write but stamps a brand new creation time, so ranking by
    /// creation would abandon the log the player is actually playing on.
    ///
    /// - No candidates: null.
    /// - Star Citizen running and the current file written inside <see cref="ActiveWriteWindow"/>:
    ///   the current file, always. One log is written at a time, so this is never wrong.
    /// - Star Citizen running, current file among the candidates: another candidate takes over only
    ///   with a STRICTLY newer last write (the copied-log case ties, and a tie stays put). Newest
    ///   last write wins among several, newer creation breaks that tie.
    /// - Star Citizen running, current file NOT among the candidates (its channel was uninstalled
    ///   or the file was deleted): the recovery path - newest last write wins, creation breaks ties.
    /// - Star Citizen closed: newest last write wins; the current file wins a last-write tie so
    ///   unchanged stats never cause a switch, and newer creation breaks ties between the rest.</summary>
    public static string? SelectActive(IReadOnlyList<LogStat> stats, string currentPath, DateTime nowUtc, bool sessionLive)
    {
        if (stats.Count == 0) return null;

        LogStat? current = null;
        foreach (var s in stats)
            if (PathsEqual(s.Path, currentPath)) { current = s; break; }

        if (sessionLive && current is { } active)
        {
            if (nowUtc - active.LastWriteUtc < ActiveWriteWindow) return active.Path;

            LogStat? rival = null;
            foreach (var s in stats)
            {
                if (PathsEqual(s.Path, currentPath)) continue;
                if (rival is null || Beats(s, rival.Value, currentPath)) rival = s;
            }
            return rival is { } r && r.LastWriteUtc > active.LastWriteUtc ? r.Path : active.Path;
        }

        var best = stats[0];
        foreach (var s in stats.Skip(1))
            if (Beats(s, best, currentPath)) best = s;
        return best.Path;
    }

    /// <summary>Ranking: newer last write wins; the current path wins a last-write tie (equal stats
    /// must never cause a switch); newer creation breaks what is left.</summary>
    private static bool Beats(LogStat a, LogStat b, string currentPath)
    {
        int byWrite = a.LastWriteUtc.CompareTo(b.LastWriteUtc);
        if (byWrite != 0) return byWrite > 0;
        bool aIsCurrent = PathsEqual(a.Path, currentPath), bIsCurrent = PathsEqual(b.Path, currentPath);
        if (aIsCurrent != bIsCurrent) return aIsCurrent;
        return a.CreationUtc > b.CreationUtc;
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
