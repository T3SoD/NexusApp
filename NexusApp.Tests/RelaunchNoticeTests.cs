using System;
using NexusApp.Models;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// The auto-relaunch notice (render-crash recovery banner) appears on two surfaces: the Operations
// HUB strip and a Settings > Diagnostics row. Its two decision seams are pure and WPF-free, so they
// are pinned here: (1) was this process started by CrashGuard's auto-relaunch, and (2) how the
// last-relaunch timestamp renders, including the empty state. The persisted field that backs the
// Settings row round-trips through the settings file like its neighbours (NetworkIdentityTests).
public class RelaunchNoticeTests
{
    // ── Relaunch-arg detection: string[] args -> bool ───────────────────────────

    [Fact]
    public void WasAutoRelaunched_WithRelaunchArg_IsTrue()
    {
        Assert.True(RelaunchNotice.WasAutoRelaunched(new[] { CrashGuard.RelaunchArg }));
    }

    [Fact]
    public void WasAutoRelaunched_ArgAmongOtherArgs_IsTrue()
    {
        Assert.True(RelaunchNotice.WasAutoRelaunched(new[] { "--other", CrashGuard.RelaunchArg, "tail" }));
    }

    [Fact]
    public void WasAutoRelaunched_WithoutRelaunchArg_IsFalse()
    {
        Assert.False(RelaunchNotice.WasAutoRelaunched(new[] { "--something", "else" }));
    }

    [Fact]
    public void WasAutoRelaunched_EmptyArgs_IsFalse()
    {
        Assert.False(RelaunchNotice.WasAutoRelaunched(Array.Empty<string>()));
    }

    [Fact]
    public void WasAutoRelaunched_NullArgs_IsFalse()
    {
        Assert.False(RelaunchNotice.WasAutoRelaunched(null));
    }

    // ── Timestamp formatting: DateTime? -> display string, incl. empty state ─────

    [Fact]
    public void FormatTimestamp_Null_IsNoneRecorded()
    {
        Assert.Equal("None recorded", RelaunchNotice.FormatTimestamp(null));
    }

    [Fact]
    public void FormatTimestamp_UtcValue_RendersLocalTimeInFrozenFormat()
    {
        var utc = new DateTime(2026, 7, 21, 21, 47, 0, DateTimeKind.Utc);
        var expected = utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        Assert.Equal(expected, RelaunchNotice.FormatTimestamp(utc));
    }

    [Fact]
    public void FormatTimestamp_UnspecifiedKind_IsTreatedAsUtc()
    {
        var utc = new DateTime(2026, 7, 21, 21, 47, 0, DateTimeKind.Utc);
        var unspecified = new DateTime(2026, 7, 21, 21, 47, 0, DateTimeKind.Unspecified);
        Assert.Equal(RelaunchNotice.FormatTimestamp(utc), RelaunchNotice.FormatTimestamp(unspecified));
    }

    // ── Persisted field: LastAutoRelaunchUtc round-trips through the settings file ─

    [Fact]
    public void LastAutoRelaunchUtc_DefaultsToNull()
    {
        Assert.Null(new AppSettings().LastAutoRelaunchUtc);
    }

    [Fact]
    public void LastAutoRelaunchUtc_RoundTripsThroughSettingsFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nexus_relaunch_{Guid.NewGuid():N}.json");
        try
        {
            var when = new DateTime(2026, 7, 21, 21, 47, 0, DateTimeKind.Utc);
            var s1 = new SettingsService(path);
            s1.Current.LastAutoRelaunchUtc = when;
            s1.Save();

            var s2 = new SettingsService(path);
            Assert.NotNull(s2.Current.LastAutoRelaunchUtc);
            Assert.Equal(when, s2.Current.LastAutoRelaunchUtc!.Value.ToUniversalTime());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
