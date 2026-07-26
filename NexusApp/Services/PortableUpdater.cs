using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace NexusApp.Services;

public sealed record PortablePreflight(bool Ok, string Reason);

public enum PortableApplyOutcome { Completed, FailedNothingChanged, FailedRolledBack, FailedRollbackIncomplete }

public sealed record PortableApplyResult(PortableApplyOutcome Outcome, string Reason);

// Seam so UpdateService's state machine is testable without a filesystem or a real swap.
internal interface IPortableSwapper
{
    PortablePreflight Preflight(long zipBytes);
    PortableApplyResult Apply(string zipPath, string expectedSha256, Version version, string currentVersion);
    bool UnpackForManual(string zipPath, string expectedSha256, Version version);
}

// The Windows special folders the precondition rules compare against, injectable so the
// rules are testable on a throwaway tree (the AppPaths.ResolveRoot pattern).
internal sealed record PortableEnv(
    string TempPath, string ProgramFiles, string ProgramFilesX86, string WindowsDir,
    string LocalAppData, string AppDataRoot)
{
    public static PortableEnv Real() => new(
        Path.GetTempPath(),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppPaths.Root);
}

// What one verified extraction produced: every payload file's SHA-256 keyed by its backslash
// path relative to the payload root, the actual decompressed byte count, and that root.
internal sealed record ExtractResult(Dictionary<string, string> FileHashes, long TotalBytes, string PayloadRoot);

// The portable self-swap engine: the running, already-verified app replaces its own files
// with journaled same-volume renames (the Syncthing/yt-dlp pattern) and the new exe is
// spawned by App.OnExit as the process's last action. Windows permits RENAMING a running
// exe and its loaded DLLs; it forbids deleting or overwriting them, which is why there is
// no overwrite branch anywhere in this file.
public sealed class PortableUpdater : IPortableSwapper
{
    public const string StagingDirName = "update-staging";
    public const string LockFileName = "update.lock";
    public const string OldSuffix = ".old";
    internal const int MaxZipEntries = 4096;
    internal const long MaxExtractedBytes = 600L * 1024 * 1024;
    internal const long FreeSpaceSlackBytes = 50L * 1024 * 1024;

    private readonly string _installDir;
    private readonly string? _processPath;
    private readonly string _updatesDir;
    private readonly string _journalPath;
    private readonly Func<string> _distribution;
    private readonly PortableEnv _env;
    private readonly int[] _retryDelaysMs;
    private readonly Func<int> _nexusProcessCount;
    private readonly Action<string> _openFolder;

    // Test hook: fires per file after the staging copy, before that file's re-hash, so the
    // staged-tamper defense is provable. Never set in production.
    internal Action<string, string>? BeforeFlipHook;

    public PortableUpdater() : this(
        AppContext.BaseDirectory, Environment.ProcessPath,
        Path.Combine(AppPaths.Root, "updates"), SwapJournal.DefaultPath,
        () => NexusApp.AppInfo.Distribution, PortableEnv.Real())
    { }

    internal PortableUpdater(string installDir, string? processPath, string updatesDir, string journalPath,
                             Func<string> distribution, PortableEnv env,
                             int[]? retryDelaysMs = null, Func<int>? nexusProcessCount = null,
                             Action<string>? openFolder = null)
    {
        _installDir = Path.GetFullPath(installDir).TrimEnd('\\');
        _processPath = processPath;
        _updatesDir = updatesDir;
        _journalPath = journalPath;
        _distribution = distribution;
        _env = env;
        _retryDelaysMs = retryDelaysMs ?? new[] { 250, 500, 1000, 2000, 4000, 8000 };
        _nexusProcessCount = nexusProcessCount ?? CountOwnProcesses;
        _openFolder = openFolder ?? OpenInExplorer;
    }

    // Name-based enumeration with immediate Dispose (the ForegroundMonitor pattern): never a
    // handle into anything. On doubt report two: an unknown process landscape fails closed
    // to the manual flow, never into a concurrent swap.
    private static int CountOwnProcesses()
    {
        try
        {
            var procs = Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName);
            foreach (var p in procs) p.Dispose();
            return procs.Length;
        }
        catch { return 2; }
    }

    private static void OpenInExplorer(string dir) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });

    // ---- Pure precondition rules (no filesystem access) ----

    // Path-shape reasons the self-swap must not run here. Every string doubles as the logged
    // fallback reason, so it is plain English, lowercase start, no trailing period.
    internal static string? PreflightPathIssue(string? processPath, string baseDirectory, PortableEnv env)
    {
        if (string.IsNullOrEmpty(processPath)) return "the running process path is unknown";
        if (!string.Equals(Path.GetFileName(processPath), "NexusApp.exe", StringComparison.OrdinalIgnoreCase))
            return "the app file has been renamed";
        string exeDir, baseDir;
        try
        {
            exeDir = Path.GetFullPath(Path.GetDirectoryName(processPath)!).TrimEnd('\\');
            baseDir = Path.GetFullPath(baseDirectory).TrimEnd('\\');
        }
        catch { return "the install folder path could not be resolved"; }
        // Environment.ProcessPath is the anchor (single-file publish can point BaseDirectory
        // at an extraction cache under DOTNET_BUNDLE_EXTRACT_BASE_DIR); disagreement means
        // the swap would target the wrong directory.
        if (!string.Equals(exeDir, baseDir, StringComparison.OrdinalIgnoreCase))
            return "the app is not running from its own folder";
        if (exeDir.Length > 200) return "the install folder path is too long";
        if (exeDir.StartsWith(@"\\", StringComparison.Ordinal)) return "the install folder is on a network share";
        if (IsUnder(exeDir, env.TempPath)) return "the app is running from a temporary folder";
        if (IsUnder(exeDir, env.AppDataRoot)) return "the app is running from the Nexus data folder";
        if (IsUnder(exeDir, env.ProgramFiles) || IsUnder(exeDir, env.ProgramFilesX86))
            return "the install folder is under Program Files";
        if (IsUnder(exeDir, env.WindowsDir)) return "the install folder is under Windows";
        var installerDir = Path.Combine(env.LocalAppData, "Nexus").TrimEnd('\\');
        if (string.Equals(exeDir, installerDir, StringComparison.OrdinalIgnoreCase))
            return "this is the installed copy of Nexus";
        return null;
    }

    private static bool IsUnder(string dir, string root)
    {
        if (string.IsNullOrEmpty(root)) return false;
        string r;
        try { r = Path.GetFullPath(root).TrimEnd('\\') + "\\"; }
        catch { return false; }
        return (dir + "\\").StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Zip entry hardening ----

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    // Validates one zip entry name and yields its path relative to the mandatory top-level
    // NexusApp folder ("" for the folder entry itself). Inputs are already whole-zip
    // hash-verified, so anything unexpected is a packaging accident and gets refused loudly,
    // the UpdateManifest.Parse philosophy. Returns the rejection reason or null.
    internal static string? NormalizeEntry(string entryName, out string rel)
    {
        rel = "";
        var name = entryName.Replace('/', '\\');
        if (name.Length == 0) return "empty entry name";
        if (name.Contains(':')) return "drive or stream separator in entry name";
        if (name.StartsWith('\\')) return "rooted entry name";
        var segs = name.Split('\\');
        for (int i = 0; i < segs.Length; i++)
        {
            var seg = segs[i];
            if (seg.Length == 0 && i == segs.Length - 1) continue;   // trailing slash: directory entry
            if (seg.Length == 0 || seg == "." || seg == "..") return "path traversal in entry name";
            if (seg.EndsWith('.') || seg.EndsWith(' ')) return "entry segment ends with a dot or space";
            if (ReservedDeviceNames.Contains(seg.Split('.')[0])) return "reserved device name in entry";
        }
        if (!string.Equals(segs[0], "NexusApp", StringComparison.OrdinalIgnoreCase))
            return "entry outside the NexusApp top-level folder";
        var relSegs = segs.Skip(1).Where(s => s.Length > 0).ToArray();
        rel = string.Join('\\', relSegs);
        if (rel.Length == 0) return null;
        foreach (var s in relSegs)
        {
            if (s.EndsWith(OldSuffix, StringComparison.OrdinalIgnoreCase)) return "entry name ends in .old";
            if (s.EndsWith(".new", StringComparison.OrdinalIgnoreCase)) return "entry name ends in .new";
            if (string.Equals(s, StagingDirName, StringComparison.OrdinalIgnoreCase))
                return "entry collides with the staging folder";
        }
        var file = relSegs[^1];
        if (string.Equals(file, "install.marker", StringComparison.OrdinalIgnoreCase))
            return "install.marker is not allowed in the portable payload";
        if (string.Equals(file, SwapJournal.FileName, StringComparison.OrdinalIgnoreCase))
            return "journal filename is not allowed in the payload";
        if (string.Equals(file, LockFileName, StringComparison.OrdinalIgnoreCase))
            return "lock filename is not allowed in the payload";
        return null;
    }

    // ---- Same-handle verify and hardened extraction ----

    // Same-handle verify and extract. The zip is opened denying writers AND deleters
    // (FileShare.Read), hashed against the SIGNED manifest hash on that open stream, rewound,
    // and extracted from the SAME handle: the bytes hashed and the bytes extracted are
    // identical, so there is no zip-level TOCTOU window in the user-writable staging dir.
    // Per-entry SHA-256 is computed DURING extraction and held in memory so the flip phase
    // can re-verify each staged file immediately before renaming it into place, which chains
    // every placed byte to the signed manifest without a manifest schema change.
    internal static ExtractResult VerifyAndExtract(string zipPath, string expectedSha256, string destDir,
                                                   int maxEntries = MaxZipEntries, long maxBytes = MaxExtractedBytes)
    {
        using var fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var zipHash = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        if (!string.Equals(zipHash, expectedSha256, StringComparison.Ordinal))
            throw new InvalidOperationException("the downloaded file failed verification at install time");
        fs.Position = 0;

        // A stale tree from a crashed run must never contribute files to a swap.
        if (Directory.Exists(destDir)) Directory.Delete(destDir, recursive: true);
        Directory.CreateDirectory(destDir);
        var destRoot = Path.GetFullPath(destDir).TrimEnd('\\') + "\\";

        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        var exeSeen = false;
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: true);
        if (zip.Entries.Count > maxEntries)
            throw new InvalidOperationException("the update archive lists too many files");
        foreach (var entry in zip.Entries)
        {
            var issue = NormalizeEntry(entry.FullName, out var rel);
            if (issue != null)
                throw new InvalidOperationException($"refused archive entry \"{entry.FullName}\": {issue}");
            if (rel.Length == 0) continue;   // the top-level folder entry itself
            var target = Path.GetFullPath(Path.Combine(destRoot, rel));
            // Defense in depth beside NormalizeEntry (and .NET 8's own refusal): nothing
            // canonicalizing outside the destination is ever created.
            if (!target.StartsWith(destRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"refused archive entry \"{entry.FullName}\": escapes the destination");
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (string.Equals(rel, "NexusApp.exe", StringComparison.OrdinalIgnoreCase)) exeSeen = true;
            using var src = entry.Open();
            using var dst = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            int read;
            while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                // Enforced on ACTUAL decompressed bytes: a zip header's declared sizes are
                // metadata an accident (or a bomb) can misstate.
                if (total > maxBytes)
                    throw new InvalidOperationException("the update archive expands past the size cap");
                hasher.AppendData(buffer, 0, read);
                dst.Write(buffer, 0, read);
            }
            hashes[rel] = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
        }
        if (!exeSeen)
            throw new InvalidOperationException("the update archive does not contain NexusApp\\NexusApp.exe");
        return new ExtractResult(hashes, total, destDir);
    }

    // Interface members are completed by later tasks; these throw until then so the class
    // compiles without lying about what works.
    public PortablePreflight Preflight(long zipBytes) => throw new NotImplementedException();
    public PortableApplyResult Apply(string zipPath, string expectedSha256, Version version, string currentVersion) => throw new NotImplementedException();
    public bool UnpackForManual(string zipPath, string expectedSha256, Version version) => throw new NotImplementedException();
}
