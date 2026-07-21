using System;
using System.IO;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Regression guard for Issue #9 ("App log monitor only shows 1 line"). Root cause: the 72h
// age-based rotation in Logger deleted + recreated nexus.log, but NTFS file-system tunneling
// re-applies a deleted file's ORIGINAL creation time when a same-named file is recreated within
// ~15s - so once the log first aged past 72h, every write rotated again, pinning the file to a
// single, ever-overwritten line. The fix stamps the recreated file's creation time so the window
// genuinely restarts. (The test project targets a Windows TFM, so these run on the OS where the
// bug actually occurs and where SetCreationTimeUtc takes effect.)
public class LoggerRotationTests
{
    [Fact]
    public void AgeRotation_RestartsCreationTimeWindow_SoLogKeepsAccumulating()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NexusLoggerTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "nexus.log");
        try
        {
            var start = DateTime.UtcNow;

            // First write creates the file.
            Logger.WriteTo(path, "INFO", "first", null, start);
            Assert.True(File.Exists(path));

            // Pretend the log has aged past the 72h window: this write must rotate (delete + recreate).
            var aged = start.AddHours(80);
            Logger.WriteTo(path, "INFO", "second", null, aged);

            // The fix stamps the recreated file's creation time to the rotation clock, so the age
            // window actually restarts. Without it, tunneling keeps creation time stuck in the past.
            var creation = File.GetCreationTimeUtc(path);
            Assert.True(Math.Abs((creation - aged).TotalMinutes) < 5,
                $"expected creation ~{aged:o} after rotation, got {creation:o}");

            // A write just after the rotation is within the window → must NOT rotate; it accumulates.
            Logger.WriteTo(path, "INFO", "third", null, aged.AddSeconds(1));

            var lines = File.ReadAllLines(path);
            Assert.Equal(2, lines.Length);            // "second" + "third" - not pinned to a single line
            Assert.Contains("second", lines[0]);
            Assert.Contains("third", lines[1]);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void AgeRotation_KeepsPreviousGeneration()
    {
        // Crash evidence must survive one rotation: a user who launches a day after a crash
        // would otherwise send an empty log (the 72h window lapped the whole file).
        var dir = Path.Combine(Path.GetTempPath(), "NexusLoggerTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "nexus.log");
        try
        {
            var start = DateTime.UtcNow;
            Logger.WriteTo(path, "ERROR", "crash evidence", null, start);
            Logger.WriteTo(path, "INFO", "fresh session", null, start.AddHours(80));

            Assert.Contains("fresh session", File.ReadAllText(path));
            Assert.DoesNotContain("crash evidence", File.ReadAllText(path));
            Assert.True(File.Exists(path + ".1"), "previous generation nexus.log.1 must exist");
            Assert.Contains("crash evidence", File.ReadAllText(path + ".1"));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void SecondRotation_ReplacesPreviousGeneration()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NexusLoggerTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "nexus.log");
        try
        {
            var start = DateTime.UtcNow;
            Logger.WriteTo(path, "INFO", "gen one", null, start);
            Logger.WriteTo(path, "INFO", "gen two", null, start.AddHours(80));
            Logger.WriteTo(path, "INFO", "gen three", null, start.AddHours(160));

            Assert.Contains("gen three", File.ReadAllText(path));
            Assert.Contains("gen two", File.ReadAllText(path + ".1"));
            Assert.DoesNotContain("gen one", File.ReadAllText(path + ".1"));
            Assert.False(File.Exists(path + ".2"), "only ONE previous generation is kept");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void BlockedRotation_NeverDeletesCurrentLog_AndRetriesNextWrite()
    {
        // A snapshot read holds nexus.log.1 open (FileShare.ReadWrite, no Delete) at the
        // instant rotation fires. Rotation must NOT fall back to deleting the CURRENT log
        // (that destroys the fresh evidence and keeps the stale generation); it skips this
        // rotation and succeeds on a later write once the reader is gone.
        var dir = Path.Combine(Path.GetTempPath(), "NexusLoggerTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "nexus.log");
        try
        {
            var start = DateTime.UtcNow;
            Logger.WriteTo(path, "ERROR", "fresh evidence", null, start);
            File.WriteAllText(path + ".1", "stale generation");

            using (File.Open(path + ".1", FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                Logger.WriteTo(path, "INFO", "during block", null, start.AddHours(80));
                Assert.Contains("fresh evidence", File.ReadAllText(path));
                Assert.Contains("during block", File.ReadAllText(path));
            }

            // Reader gone: the next over-age write rotates normally.
            Logger.WriteTo(path, "INFO", "after block", null, start.AddHours(81));
            Assert.Contains("after block", File.ReadAllText(path));
            Assert.DoesNotContain("fresh evidence", File.ReadAllText(path));
            Assert.Contains("fresh evidence", File.ReadAllText(path + ".1"));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void NormalWrites_BelowThresholds_AccumulateWithoutRotation()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NexusLoggerTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "nexus.log");
        try
        {
            var t = DateTime.UtcNow;
            Logger.WriteTo(path, "INFO", "a", null, t);
            Logger.WriteTo(path, "INFO", "b", null, t.AddSeconds(1));
            Logger.WriteTo(path, "INFO", "c", null, t.AddSeconds(2));

            Assert.Equal(3, File.ReadAllLines(path).Length);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
