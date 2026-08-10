using System.IO;

namespace NexusApp.Services;

/// <summary>One log file the export can draw from: its display name and its lines.</summary>
public sealed record GameLogSource(string Name, IReadOnlyList<string> Lines);

/// <summary>
/// Finds every Game.log the export can reach (issue #48 follow-up).
///
/// <para>THE DEFECT THIS FIXES. Star Citizen writes only the CURRENT session to
/// <c>LIVE\Game.log</c> and moves each finished session into <c>LIVE\logbackups\</c> as its own
/// file, named like <c>Game Build(12344265) 07 Aug 26 (20 54 39).log</c>. The first version of the
/// export read the single active file, so asking for a four-day range returned only whatever
/// session happened to be sitting in Game.log - typically one evening. A user reporting a bug from
/// two days ago got a file that could not contain it, with nothing saying why.</para>
///
/// <para>Ordering is by last-write time, oldest first, so the exported file reads forwards in time
/// across sessions. The name is the FILE NAME only, never the full path: the path runs through the
/// Windows user folder, which can be a real name.</para>
/// </summary>
public static class GameLogSources
{
    public const string BackupFolder = "logbackups";

    /// <summary>
    /// The candidate files for a range, oldest first: the active Game.log plus every backup.
    ///
    /// <para>A file whose last write is older than <paramref name="fromUtc"/> cannot hold a line in
    /// range - the game only ever appends, so its newest line is no later than that stamp - and is
    /// skipped without being opened. That keeps a wide range from reading a hundred megabytes of
    /// sessions it will discard line by line anyway.</para>
    /// </summary>
    public static IReadOnlyList<string> Discover(string? activeLogPath, DateTime? fromUtc)
    {
        var found = new List<(string Path, DateTime Written)>();
        if (string.IsNullOrWhiteSpace(activeLogPath)) return Array.Empty<string>();

        void Consider(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return;
                var written = info.LastWriteTimeUtc;
                if (fromUtc is { } from && written < from) return;   // every line in it predates the range
                found.Add((path, written));
            }
            catch { /* a locked or vanished file is simply not a source */ }
        }

        Consider(activeLogPath);

        try
        {
            var dir = Path.GetDirectoryName(activeLogPath);
            if (!string.IsNullOrEmpty(dir))
            {
                var backups = Path.Combine(dir, BackupFolder);
                if (Directory.Exists(backups))
                    foreach (var f in Directory.EnumerateFiles(backups, "*.log")) Consider(f);
            }
        }
        catch (Exception ex)
        {
            Logger.Info($"[UI] Game.log backups unreadable: {ex.Message}");
        }

        return found.OrderBy(f => f.Written).Select(f => f.Path).ToList();
    }

    /// <summary>Reads one file without denying the game its write handle. Star Citizen holds the
    /// active Game.log open, so a plain read throws exactly when a live session is what the user
    /// wants to report on.</summary>
    public static GameLogSource Read(string path)
    {
        var lines = new List<string>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        while (sr.ReadLine() is { } line) lines.Add(line);
        return new GameLogSource(Path.GetFileName(path), lines);
    }
}
