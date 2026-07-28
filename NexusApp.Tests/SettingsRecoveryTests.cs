using System.Text.Json;
using NexusApp.Models;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Pins SettingsService's corrupt-file recovery ladder (spec decision 9, 2026-07-28 app-wide
// review): a settings.json that fails to parse is quarantined as settings.json.corrupt-<stamp>,
// settings.json.bak is then tried, and only when that is also unavailable/unreadable does Load
// fall back to fresh defaults. Mirrors the quarantine-and-.bak shape already proven for Cargo
// grid overrides (Services/Cargo/JsonFile.LoadOrRecover), applied here to the higher-value
// settings.json (every user preference lives there).
public class SettingsRecoveryTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    public void Dispose()
    {
        foreach (var d in _tempDirs)
        {
            try { if (Directory.Exists(d)) Directory.Delete(d, recursive: true); }
            catch { /* best effort */ }
        }
    }

    private string TempSettingsPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexus-settings-recovery-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return Path.Combine(dir, "settings.json");
    }

    private static string[] QuarantineFiles(string path) =>
        Directory.GetFiles(Path.GetDirectoryName(path)!, "settings.json.corrupt-*");

    [Fact]
    public void Load_CorruptPrimary_WithBackup_RecoversFromBackup()
    {
        var path = TempSettingsPath();

        // Save twice through the real service so the second Save() rotates the first good write
        // into settings.json.bak (File.Replace's backup-file behavior).
        var seed = new SettingsService(path);
        seed.Current.LastSeenVersion = "6.10.0";
        seed.Save();
        seed.Current.LastSeenVersion = "6.10.1";
        seed.Save();
        Assert.True(File.Exists(path + ".bak"));

        File.WriteAllText(path, "{ this is not valid json");   // corrupt the primary in place

        var recovered = new SettingsService(path);

        Assert.Equal("6.10.0", recovered.Current.LastSeenVersion);   // recovered from .bak
        Assert.Single(QuarantineFiles(path));                        // corrupt primary set aside
        Assert.True(File.Exists(path + ".bak"));                     // .bak itself untouched
    }

    [Fact]
    public void Load_CorruptPrimary_NoBackup_FallsBackToDefaults()
    {
        var path = TempSettingsPath();
        File.WriteAllText(path, "{ this is not valid json");   // no .bak has ever been written

        var recovered = new SettingsService(path);

        Assert.Equal(new AppSettings().LastSeenVersion, recovered.Current.LastSeenVersion);
        Assert.False(File.Exists(path + ".bak"));
        Assert.Single(QuarantineFiles(path));   // the unreadable file is still set aside for diagnosis
    }

    [Fact]
    public void Save_LeavesBackupIndependentlyReadable()
    {
        var path = TempSettingsPath();
        var s = new SettingsService(path);
        s.Current.LastSeenVersion = "1.0.0";
        s.Save();
        s.Current.LastSeenVersion = "2.0.0";
        s.Save();

        var bakPath = path + ".bak";
        Assert.True(File.Exists(bakPath));
        var bak = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(bakPath));
        Assert.NotNull(bak);
        Assert.Equal("1.0.0", bak!.LastSeenVersion);   // .bak holds the PRIOR good write, not the latest
    }

    // Pins the CRITICAL fix (review round 2): recovering from a .bak that is ALREADY at the
    // current schema version - the realistic case for any already-migrated user - must still
    // re-persist to settings.json, even though none of the migration ladder's `< N` checks fire
    // (so `migrated` alone would never trigger a save). Before the fix, RecoverFromCorrupt quarantined
    // the primary (removing it from disk) and the recovered values only lived in memory; the next
    // launch's File.Exists fast path would see no file at all and silently hand back full defaults,
    // discarding the .bak's real values. The seed below is produced by letting the REAL migration
    // ladder run to its own top (not a hand-constructed AppSettings at a hardcoded version), per the
    // reviewer's own repro - the two other tests above seed via the "no file yet" fast path, which
    // hardcodes SettingsSchemaVersion=1 and therefore still trips the ladder on recovery, masking
    // this exact bug.
    [Fact]
    public void Load_CorruptPrimary_AtCurrentSchemaVersion_StillPersistsRecoveredSettingsToDisk()
    {
        var path = TempSettingsPath();

        // A bare "{}" deserializes to AppSettings with SettingsSchemaVersion defaulting to 0, so the
        // very first Load() drives the ladder naturally from 0 up to its current top (5) and saves
        // the result - this is how a real settings.json reaches "current schema version", not a
        // hand-set field.
        File.WriteAllText(path, "{}");
        var seed = new SettingsService(path);
        Assert.Equal(5, seed.Current.SettingsSchemaVersion);

        // Two explicit saves at (already) schema version 5 so settings.json.bak itself ends up at
        // version 5 too (the first real save's own .bak rotation would otherwise just be the
        // pre-ladder "{}" dump, which is not what we're testing).
        seed.Current.LastSeenVersion = "v5-marker-A";
        seed.Save();
        seed.Current.LastSeenVersion = "v5-marker-B";
        seed.Save();

        var bak = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path + ".bak"));
        Assert.Equal(5, bak!.SettingsSchemaVersion);
        Assert.Equal("v5-marker-A", bak.LastSeenVersion);

        File.WriteAllText(path, "{ this is not valid json");   // corrupt the current (marker-B) primary

        var recovered = new SettingsService(path);
        Assert.Equal("v5-marker-A", recovered.Current.LastSeenVersion);   // recovered from .bak
        Assert.Equal(5, recovered.Current.SettingsSchemaVersion);         // ladder was a no-op (already at top)
        Assert.True(File.Exists(path));                                  // THE FIX: re-persisted despite migrated == false

        // No explicit Save() call between the recovery above and this reload - if the fix only
        // wrote Current in memory (the pre-fix bug), this constructor would hit the quarantined-away
        // file's absence and silently return fresh defaults instead of the recovered marker.
        var reloadedAgain = new SettingsService(path);
        Assert.Equal("v5-marker-A", reloadedAgain.Current.LastSeenVersion);
        Assert.Equal(5, reloadedAgain.Current.SettingsSchemaVersion);
    }
}
