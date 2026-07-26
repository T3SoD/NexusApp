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

public sealed record SwapRecoveryResult(bool ShowSwapFailedNotice, string? AttemptedVersion);

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

    // Every reason returned here routes the UI to the guided manual flow, never to an error:
    // a precondition failure is a different intentional path, and the reason line lands in
    // nexus.log as "[UPDATE] portable swap unavailable: <reason>, offering manual handoff".
    public PortablePreflight Preflight(long zipBytes)
    {
        var issue = PreflightPathIssue(_processPath, _installDir, _env);
        if (issue != null) return new(false, issue);
        if (_distribution() != "Portable") return new(false, "this is not the portable distribution");
        // Layout sanity: a real portable install has the native loaders beside the exe.
        if (!File.Exists(Path.Combine(_installDir, "e_sqlite3.dll")) ||
            !File.Exists(Path.Combine(_installDir, "wpfgfx_cor3.dll")))
            return new(false, "this folder does not look like a portable Nexus install");
        try
        {
            if ((File.GetAttributes(_installDir) & FileAttributes.ReparsePoint) != 0)
                return new(false, "the install folder is a link, not a real folder");
        }
        catch (Exception ex) { return new(false, $"the install folder could not be inspected: {ex.Message}"); }
        if (_nexusProcessCount() > 1) return new(false, "another Nexus window is open");
        // Writability probe: create + rename + delete catches Controlled Folder Access,
        // read-only media, and ACL denials in one shot. Explorer stays allowed under CFA,
        // so the manual fallback this reason routes to still works.
        var probe = Path.Combine(_installDir, "nexus-update-probe-" + Path.GetRandomFileName());
        try
        {
            File.WriteAllText(probe, "probe");
            File.Move(probe, probe + ".r");
            File.Delete(probe + ".r");
        }
        catch
        {
            // Both probe generations: the failure can hit before or after the rename step,
            // and a leftover probe file would violate the portable-cleanliness rule.
            try { if (File.Exists(probe)) File.Delete(probe); } catch { }
            try { if (File.Exists(probe + ".r")) File.Delete(probe + ".r"); } catch { }
            return new(false, "Nexus cannot write to its own folder here (folder protection, permissions, or read-only media)");
        }
        // Free space: the staging copy coexists with the originals, so the install volume
        // transiently needs roughly twice the payload; the data volume needs zip plus
        // extraction. A query failure (SUBST, quotas) is attempt-anyway, not a refusal.
        try
        {
            var installDrive = new DriveInfo(Path.GetPathRoot(_installDir)!);
            if (installDrive.DriveType == DriveType.Network)
                return new(false, "the install folder is on a network drive");
            if (installDrive.DriveType == DriveType.Removable)
                Logger.Info($"{UpdateService.Tag} install folder is on removable media; a power cut mid-swap can corrupt it (the .old set is the recovery)");
            if (installDrive.AvailableFreeSpace < zipBytes * 2 + FreeSpaceSlackBytes)
                return new(false, "not enough free space in the install folder's drive");
            if (new DriveInfo(Path.GetPathRoot(_updatesDir)!).AvailableFreeSpace < zipBytes * 2 + FreeSpaceSlackBytes)
                return new(false, "not enough free space on the Windows drive to unpack the update");
        }
        catch (Exception ex)
        {
            Logger.Info($"{UpdateService.Tag} free-space probe inconclusive ({ex.Message}); attempting anyway");
        }
        return new(true, "");
    }

    public PortableApplyResult Apply(string zipPath, string expectedSha256, Version version, string currentVersion)
    {
        long zipBytes = 0;
        try { zipBytes = new FileInfo(zipPath).Length; } catch { }
        var pre = Preflight(zipBytes);
        if (!pre.Ok) return new(PortableApplyOutcome.FailedNothingChanged, pre.Reason);

        // Upgrade re-asserted against the ON-DISK exe, not just this process: a stale
        // instance must never re-apply its download over an already-updated folder.
        var baseline = OnDiskVersionOrFallback(Path.Combine(_installDir, "NexusApp.exe"), currentVersion);
        if (!UpdateVerifier.IsUpgrade(baseline, version.ToString(3)))
            return new(PortableApplyOutcome.FailedNothingChanged, "the update is not newer than the installed version");

        FileStream? lockFs = null;
        try
        {
            // Exclusive for the whole swap; DeleteOnClose means a crash can never leave a
            // stale lock (the handle's death releases and removes it).
            try
            {
                lockFs = new FileStream(Path.Combine(_installDir, LockFileName),
                    FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch { return new(PortableApplyOutcome.FailedNothingChanged, "another update is already in progress"); }

            Logger.Info($"{UpdateService.Tag} unpack started");
            ExtractResult extracted;
            var stagedRoot = Path.Combine(_updatesDir, version.ToString(3), "staged", "NexusApp");
            try { extracted = VerifyAndExtract(zipPath, expectedSha256, stagedRoot); }
            catch (Exception ex) { return new(PortableApplyOutcome.FailedNothingChanged, ex.Message); }
            Logger.Info($"{UpdateService.Tag} unpack complete, hash re-verified ({extracted.FileHashes.Count} files)");

            // Exact free-space check now that the true payload size is known.
            try
            {
                if (new DriveInfo(Path.GetPathRoot(_installDir)!).AvailableFreeSpace < extracted.TotalBytes + FreeSpaceSlackBytes)
                    return new(PortableApplyOutcome.FailedNothingChanged, "not enough free space in the install folder's drive");
            }
            catch { /* attempt anyway */ }

            var stagingDir = Path.Combine(_installDir, StagingDirName);
            try
            {
                if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, recursive: true);
                CopyTree(extracted.PayloadRoot, stagingDir);
            }
            catch (Exception ex)
            {
                TryDeleteDir(stagingDir);
                return new(PortableApplyOutcome.FailedNothingChanged, $"couldn't stage the update inside the install folder: {ex.Message}");
            }
            Logger.Info($"{UpdateService.Tag} staged copy placed inside the install folder");

            return Flip(extracted, stagingDir, version, currentVersion);
        }
        finally { lockFs?.Dispose(); }
    }

    // Two-phase core. Phase order per file: journal the intent (write-ahead), clear any stale
    // .old, rename current -> .old, rename staged -> current. NexusApp.exe goes strictly
    // LAST so every earlier failure rolls back without ever touching the running image.
    private PortableApplyResult Flip(ExtractResult extracted, string stagingDir, Version version, string currentVersion)
    {
        var rels = extracted.FileHashes.Keys
            .Where(r => !string.Equals(r, "NexusApp.exe", StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
            .Append("NexusApp.exe")
            .ToList();

        // Reparse sweep over every directory a flip touches: a planted junction must never
        // redirect a rename outside the install folder.
        var installRoot = _installDir.TrimEnd('\\');
        foreach (var dir in rels.Select(r => Path.GetDirectoryName(Path.Combine(_installDir, r))!)
                                .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            for (var d = dir; d.Length > installRoot.Length; d = Path.GetDirectoryName(d)!)
                if (Directory.Exists(d) && (File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0)
                {
                    TryDeleteDir(stagingDir);
                    return new(PortableApplyOutcome.FailedNothingChanged, "a folder inside the install folder is a link");
                }
            Directory.CreateDirectory(dir);
        }

        var oneDrive = IsOneDriveManaged(_installDir);
        var journal = new SwapJournal
        {
            AttemptedVersion = version.ToString(3),
            PreviousVersion = currentVersion,
            InstallDir = _installDir,
        };
        try { journal.Save(_journalPath); }
        catch (Exception ex)
        {
            // Abort BEFORE any mutation if the write-ahead contract cannot be established,
            // and take the staging folder back out of the install dir.
            TryDeleteDir(stagingDir);
            return new(PortableApplyOutcome.FailedNothingChanged, $"couldn't write the update journal: {ex.Message}");
        }

        foreach (var rel in rels)
        {
            var staged = Path.Combine(stagingDir, rel);
            var target = Path.Combine(_installDir, rel);
            var old = target + OldSuffix;
            var nonCritical = string.Equals(rel, "README.txt", StringComparison.OrdinalIgnoreCase);
            BeforeFlipHook?.Invoke(staged, rel);

            // Re-verify the staged bytes against the hash captured during same-handle
            // extraction, immediately before the rename: the staged tree sat in
            // user-writable space between extraction and now.
            string stagedHash;
            try
            {
                using var sfs = File.OpenRead(staged);
                stagedHash = Convert.ToHexString(SHA256.HashData(sfs)).ToLowerInvariant();
            }
            catch (Exception ex) { return Abort(journal, stagingDir, $"couldn't re-verify {rel}: {ex.Message}"); }
            if (!string.Equals(stagedHash, extracted.FileHashes[rel], StringComparison.Ordinal))
                return Abort(journal, stagingDir, $"{rel} changed between unpack and install");

            var op = new SwapOp { Rel = rel, NonCritical = nonCritical };
            journal.Ops.Add(op);
            if (!SaveJournal(journal)) return Abort(journal, stagingDir, "couldn't update the journal");

            if (File.Exists(old) && !RetryDelete(old, oneDrive))
            {
                if (nonCritical) { op.Skipped = true; SaveJournal(journal); Logger.Info($"{UpdateService.Tag} skipped {rel}: a leftover {rel}{OldSuffix} is in use"); continue; }
                return Abort(journal, stagingDir, $"a leftover {rel}{OldSuffix} could not be removed");
            }
            if (File.Exists(target))
            {
                if (!RetryRename(target, old, oneDrive))
                {
                    if (nonCritical) { op.Skipped = true; SaveJournal(journal); Logger.Info($"{UpdateService.Tag} skipped {rel}: the current file is in use"); continue; }
                    return Abort(journal, stagingDir, $"{rel} is held open by another program");
                }
                op.OldMoved = true;
                if (!SaveJournal(journal)) return Abort(journal, stagingDir, "couldn't update the journal");
            }
            if (!RetryRename(staged, target, oneDrive))
            {
                if (nonCritical)
                {
                    if (op.OldMoved) { RetryRename(old, target, oneDrive); op.OldMoved = false; }
                    op.Skipped = true; SaveJournal(journal);
                    Logger.Info($"{UpdateService.Tag} skipped {rel}: couldn't place the new file");
                    continue;
                }
                return Abort(journal, stagingDir, $"couldn't place the new {rel}");
            }
            op.NewPlaced = true;
            if (!SaveJournal(journal)) return Abort(journal, stagingDir, "couldn't update the journal");
        }

        journal.Status = SwapJournal.StatusComplete;
        if (!SaveJournal(journal)) return Abort(journal, stagingDir, "couldn't mark the journal complete");
        TryDeleteDir(stagingDir);   // empty shells only: every file was renamed out of it
        Logger.Info($"{UpdateService.Tag} flips complete, new version in place");
        return new(PortableApplyOutcome.Completed, "");
    }

    // Roll back every completed pair in reverse. The running app cannot be file-locked out
    // of its own renames, so this normally restores the folder exactly. If a rename still
    // fails (AV holding a file), STOP: keep the complete .old set and the verified zip, keep
    // the journal InProgress, and say exactly where it stuck; the next start finishes the
    // restore. The folder is never left without a stated recovery path.
    private PortableApplyResult Abort(SwapJournal journal, string stagingDir, string reason)
    {
        // The in-memory status may already read Complete (the final stamp's save is one of
        // the failure paths that land here). It must never reach disk that way: a stuck
        // rollback saves the journal below, and a Complete status over a half-rolled-back
        // folder would send startup recovery down the cleanup branch, deleting the .old set
        // instead of restoring it.
        journal.Status = SwapJournal.StatusInProgress;
        Logger.Error($"{UpdateService.Tag} portable swap failed: {reason}; rolling back");
        var oneDrive = IsOneDriveManaged(_installDir);
        for (int i = journal.Ops.Count - 1; i >= 0; i--)
        {
            var op = journal.Ops[i];
            if (op.Skipped) continue;
            var target = Path.Combine(_installDir, op.Rel);
            var old = target + OldSuffix;
            if (op.NewPlaced)
            {
                if (!RetryRename(target, Path.Combine(stagingDir, op.Rel), oneDrive))
                    return RollbackStuck(journal, op.Rel, reason);
                op.NewPlaced = false;
                SaveJournal(journal);
            }
            if (op.OldMoved)
            {
                if (!RetryRename(old, target, oneDrive))
                    return RollbackStuck(journal, op.Rel, reason);
                op.OldMoved = false;
                SaveJournal(journal);
            }
        }
        TryDelete(_journalPath);
        TryDeleteDir(stagingDir);
        Logger.Info($"{UpdateService.Tag} rolled back, nothing changed");
        return new(PortableApplyOutcome.FailedRolledBack, reason);
    }

    private PortableApplyResult RollbackStuck(SwapJournal journal, string rel, string reason)
    {
        SaveJournal(journal);
        Logger.Error($"{UpdateService.Tag} rollback incomplete at {rel}; previous files are kept as {OldSuffix} and the verified download is retained; the next start finishes the restore");
        return new(PortableApplyOutcome.FailedRollbackIncomplete, reason);
    }

    // ---- Startup recovery ----

    // Startup recovery: MUST run before UpdateService.PurgeStaleDownloads (which would delete
    // the verified zip, the one artifact that can rebuild a broken folder). Reads the journal
    // as untrusted input and either finishes a completed swap's cleanup or puts the previous
    // version back after a crashed one.
    public static SwapRecoveryResult RecoverAtStartup() =>
        RecoverAtStartup(SwapJournal.DefaultPath, AppContext.BaseDirectory, NexusApp.AppInfo.Version);

    internal static SwapRecoveryResult RecoverAtStartup(string journalPath, string baseDirectory,
                                                        string runningVersion, int[]? retryDelaysMs = null)
    {
        var none = new SwapRecoveryResult(false, null);
        if (!File.Exists(journalPath))
        {
            // A crash during the staging copy predates the journal, so a partial
            // update-staging folder can sit in the install dir with nothing claiming it; the
            // next Apply would wipe it, but the user may never run another update. The sweep
            // must not race a LIVE Apply mid-copy (its journal does not exist yet either):
            // Apply holds the exclusive lock for the whole swap, so take the same lock and
            // treat failure as "an update is running or the folder is protected, not ours".
            var orphan = Path.Combine(baseDirectory, StagingDirName);
            if (Directory.Exists(orphan))
            {
                try
                {
                    using var sweepLock = new FileStream(Path.Combine(baseDirectory, LockFileName),
                        FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
                    Logger.Info($"{UpdateService.Tag} swap recovery: removing an unclaimed staging folder from an interrupted update");
                    TryDeleteDir(orphan);
                }
                catch { /* a live update owns it, or the folder is protected; leave it */ }
            }
            return none;
        }
        var journal = SwapJournal.TryLoad(journalPath);
        if (journal is null)
        {
            // Garbage or future-schema content is not ours to act on; discard it so the
            // purge guard cannot wedge updates forever on an unreadable file.
            Logger.Info($"{UpdateService.Tag} swap recovery: unreadable journal discarded");
            TryDelete(journalPath);
            return none;
        }
        string ourDir, journalDir;
        try
        {
            ourDir = Path.GetFullPath(baseDirectory).TrimEnd('\\');
            journalDir = Path.GetFullPath(journal.InstallDir).TrimEnd('\\');
        }
        catch { TryDelete(journalPath); return none; }
        if (!string.Equals(ourDir, journalDir, StringComparison.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(journalDir))
            {
                Logger.Info($"{UpdateService.Tag} swap recovery: journal points at a folder that no longer exists; discarded");
                TryDelete(journalPath);
                return none;
            }
            // This profile also serves the installer flavor and other portable copies; the
            // journal belongs to a different install and only ITS instance may touch it.
            Logger.Info($"{UpdateService.Tag} swap recovery: journal belongs to another install, leaving it alone");
            return none;
        }
        var delays = retryDelaysMs ?? new[] { 500, 1000, 2000 };
        // Serialize against a concurrently starting second instance recovering the same dir.
        FileStream? lockFs = null;
        try
        {
            lockFs = new FileStream(Path.Combine(journalDir, LockFileName),
                FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch
        {
            Logger.Info($"{UpdateService.Tag} swap recovery: another instance holds the update lock; skipping this start");
            return none;
        }
        try
        {
            // InProgress but the running exe IS the attempted version: the crash fell between
            // the final flip and the Complete stamp, so the swap in fact finished.
            if (journal.Status == SwapJournal.StatusComplete ||
                string.Equals(runningVersion, journal.AttemptedVersion, StringComparison.Ordinal))
                return CleanupCompleted(journal, journalPath, journalDir, delays);
            return RestorePrevious(journal, journalPath, journalDir, delays);
        }
        finally { lockFs?.Dispose(); }
    }

    private static SwapRecoveryResult CleanupCompleted(SwapJournal journal, string journalPath,
                                                       string installDir, int[] delays)
    {
        var allClean = true;
        foreach (var op in journal.Ops)
        {
            if (!SwapJournal.IsSafeRel(installDir, op.Rel)) continue;   // tampered entry: touch nothing
            var old = Path.Combine(installDir, op.Rel) + OldSuffix;
            if (File.Exists(old) && !RetryDeleteWith(old, delays)) allClean = false;
        }
        if (!TryDeleteDir(Path.Combine(installDir, StagingDirName))) allClean = false;
        if (allClean)
        {
            TryDelete(journalPath);
            Logger.Info($"{UpdateService.Tag} portable swap complete");
        }
        else
        {
            // The old exe stays delete-locked for a few seconds after exit, and Controlled
            // Folder Access can block the NEW exe's first purge. Housekeeping that will
            // succeed on a later start never deserves an ERROR line.
            journal.Status = SwapJournal.StatusComplete;
            try { journal.Save(journalPath); } catch { }
            Logger.Info($"{UpdateService.Tag} portable swap complete; some previous-version files are still in use and will be cleaned on a later start");
        }
        return new SwapRecoveryResult(false, null);
    }

    private static SwapRecoveryResult RestorePrevious(SwapJournal journal, string journalPath,
                                                      string installDir, int[] delays)
    {
        var allRestored = true;
        for (int i = journal.Ops.Count - 1; i >= 0; i--)
        {
            var op = journal.Ops[i];
            if (op.Skipped || !SwapJournal.IsSafeRel(installDir, op.Rel)) continue;
            var target = Path.Combine(installDir, op.Rel);
            var old = target + OldSuffix;
            if (File.Exists(old))
            {
                // The new file at the live name is reproducible from the kept zip; the .old
                // set is not. Delete new, restore old.
                if (File.Exists(target) && !RetryDeleteWith(target, delays)) { allRestored = false; continue; }
                if (!RetryMoveWith(old, target, delays)) { allRestored = false; continue; }
            }
            // A new-only file (no .old pair) is deleted; but existence alone cannot tell a
            // genuinely-added file from one this very method already restored on an earlier
            // pass (previous bytes back at the live name, .old consumed, flags stale because
            // the journal is only re-saved wholesale). OldMoved is the disambiguator: it was
            // saved only after a real rename-out, so OldMoved=true in this branch means the
            // .old was already moved back and the file on disk IS the previous version.
            else if (!op.OldMoved && op.NewPlaced && File.Exists(target) && !RetryDeleteWith(target, delays))
            {
                allRestored = false;
            }
        }
        TryDeleteDir(Path.Combine(installDir, StagingDirName));
        if (allRestored)
        {
            TryDelete(journalPath);
            Logger.Info($"{UpdateService.Tag} portable swap failed, previous version restored");
        }
        else
        {
            try { journal.Save(journalPath); } catch { }
            Logger.Info($"{UpdateService.Tag} portable swap failed; some files are still in use, the restore finishes on a later start");
        }
        return new SwapRecoveryResult(true,
            string.IsNullOrEmpty(journal.AttemptedVersion) ? null : journal.AttemptedVersion);
    }

    // ---- Small shared helpers ----

    private bool SaveJournal(SwapJournal journal)
    {
        try { journal.Save(_journalPath); return true; }
        catch (Exception ex) { Logger.Error($"{UpdateService.Tag} couldn't write the update journal", ex); return false; }
    }

    private bool RetryRename(string from, string to, bool oneDrive) =>
        RetryMoveWith(from, to, EffectiveDelays(oneDrive));

    private bool RetryDelete(string path, bool oneDrive) =>
        RetryDeleteWith(path, EffectiveDelays(oneDrive));

    // OneDrive-managed folders (Known Folder Move) hold transient sync locks longer than AV
    // scans do; the backoff doubles rather than switching to a different strategy.
    private int[] EffectiveDelays(bool oneDrive) =>
        oneDrive ? _retryDelaysMs.Select(d => d * 2).ToArray() : _retryDelaysMs;

    internal static bool RetryMoveWith(string from, string to, int[] delays)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { File.Move(from, to); return true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= delays.Length) return false;
                Thread.Sleep(delays[attempt]);
            }
        }
    }

    internal static bool RetryDeleteWith(string path, int[] delays)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                // Zip extractors and users set ReadOnly on README/Web content; it blocks
                // delete (not rename), so clear it first.
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= delays.Length) return false;
                Thread.Sleep(delays[attempt]);
            }
        }
    }

    private static bool IsOneDriveManaged(string dir)
    {
        const FileAttributes RecallOnDataAccess = (FileAttributes)0x400000;
        try { return (File.GetAttributes(dir) & RecallOnDataAccess) != 0; }
        catch { return false; }
    }

    private static string OnDiskVersionOrFallback(string exePath, string fallback)
    {
        try
        {
            var fvi = FileVersionInfo.GetVersionInfo(exePath);
            if (Version.TryParse(fvi.FileVersion, out var v) && v.Build >= 0) return v.ToString(3);
        }
        catch { }
        // No readable version resource (dev builds, test fixtures): the caller's process
        // version already gated this upgrade once.
        return fallback;
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); } }
        catch { /* best effort; a later start retries */ }
    }

    private static bool TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); return true; }
        catch { return false; }
    }

    // Completed by a later task; throws until then so the class compiles without lying
    // about what works.
    public bool UnpackForManual(string zipPath, string expectedSha256, Version version) => throw new NotImplementedException();
}
