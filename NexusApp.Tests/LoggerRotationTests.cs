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
