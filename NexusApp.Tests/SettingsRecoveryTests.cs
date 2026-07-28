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
}
