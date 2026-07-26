using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace NexusApp.Services;

// Write-ahead journal for the portable self-swap. Every destructive rename is recorded and
// FLUSHED to disk before it happens, so a crash at any instant leaves enough truth for the
// next start to finish cleanup (status Complete) or put the previous version back (InProgress).
//
// Lives OUTSIDE the updates\ dir (PurgeStaleDownloads deletes that wholesale) and is treated
// as UNTRUSTED INPUT on read-back: it sits in user-writable space, so schema, status, and
// every relative path are re-validated before any file is touched.
public sealed class SwapJournal
{
    public const int CurrentSchema = 1;
    public const string FileName = "update_journal.json";
    public const string StatusInProgress = "InProgress";
    public const string StatusComplete = "Complete";

    public static string DefaultPath => Path.Combine(AppPaths.Root, FileName);

    public int Schema { get; set; } = CurrentSchema;
    public string Status { get; set; } = StatusInProgress;
    public string AttemptedVersion { get; set; } = "";
    public string PreviousVersion { get; set; } = "";
    public string InstallDir { get; set; } = "";
    public List<SwapOp> Ops { get; set; } = new();

    private static readonly JsonSerializerOptions _opts = new() { WriteIndented = true };

    // Rewrite-in-full, atomically: the journal IS the crash contract, so a crash during the
    // journal write itself must never destroy the previous generation (a truncated journal
    // would make recovery discard it and strand the .old set). Write a temp, hard-flush it,
    // rename over the real file; the stale generation a crash can leave behind is safe
    // because recovery re-checks file existence per op. Throws on failure; the caller aborts
    // the swap before any mutation if the journal cannot be written.
    public void Save(string path)
    {
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(fs, this, _opts);
            fs.Flush(true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    // Untrusted read: any parse problem, unknown schema or status, or a non-rooted install
    // dir returns null and the caller treats the file as garbage. Never throws.
    public static SwapJournal? TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var j = JsonSerializer.Deserialize<SwapJournal>(File.ReadAllText(path));
            if (j is null || j.Schema != CurrentSchema) return null;
            if (j.Status is not (StatusInProgress or StatusComplete)) return null;
            if (string.IsNullOrWhiteSpace(j.InstallDir) || !Path.IsPathRooted(j.InstallDir)) return null;
            // A JSON null survives deserialization as a null element (the UpdateManifest.Parse
            // lesson); one would NRE recovery mid-restore, so the whole journal is refused.
            if (j.Ops is null || j.Ops.Any(o => o is null)) return null;
            return j;
        }
        catch { return null; }
    }

    // A journal entry may only name a plain relative file strictly inside the install dir:
    // no roots, no traversal, no "." segments, no alternate data streams. Recovery acts on
    // nothing that fails this, so a tampered journal can never become a delete primitive.
    public static bool IsSafeRel(string installDir, string rel)
    {
        if (string.IsNullOrWhiteSpace(rel)) return false;
        if (rel.Contains(':') || Path.IsPathRooted(rel)) return false;
        foreach (var seg in rel.Split('\\', '/'))
            if (seg.Length == 0 || seg == "." || seg == "..") return false;
        try
        {
            var root = Path.GetFullPath(installDir).TrimEnd('\\') + "\\";
            var full = Path.GetFullPath(Path.Combine(root, rel));
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}

// One file's flip progress. OldMoved: current renamed to .old. NewPlaced: staged file renamed
// into the vacated name. NonCritical files (README.txt) may end Skipped instead of failing.
public sealed class SwapOp
{
    public string Rel { get; set; } = "";
    public bool OldMoved { get; set; }
    public bool NewPlaced { get; set; }
    public bool NonCritical { get; set; }
    public bool Skipped { get; set; }
}
