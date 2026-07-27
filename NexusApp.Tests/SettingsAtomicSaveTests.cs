using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Pins the write-temp-then-rename contract of SettingsService.Save against a temp
// settings file, so it runs headless and never touches the real user profile. A
// leftover .tmp would mean the rename never completed and a reader could see a
// half-written settings.json.
public class SettingsAtomicSaveTests : IDisposable
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

    private string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexus-settings-test-" + Path.GetRandomFileName());
        _tempDirs.Add(dir);
        return dir;
    }

    [Fact]
    public void Save_LeavesNoTempFileAndRoundTrips()
    {
        var path = Path.Combine(TempDir(), "settings.json");
        var s = new SettingsService(path);
        s.Current.LastSeenVersion = "6.9.0";
        s.Save();
        Assert.False(File.Exists(path + ".tmp"));   // the write-temp-then-rename must finish the rename
        Assert.Equal("6.9.0", new SettingsService(path).Current.LastSeenVersion);
    }

    [Fact]
    public void Save_ReplacesExistingFileAndStaleTemp()
    {
        var path = Path.Combine(TempDir(), "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", "{ stale garbage from a crashed save");
        var s = new SettingsService(path);
        s.Current.LastSeenVersion = "1.0.0";
        s.Save();
        s.Current.LastSeenVersion = "2.0.0";
        s.Save();
        Assert.False(File.Exists(path + ".tmp"));
        Assert.Equal("2.0.0", new SettingsService(path).Current.LastSeenVersion);
    }
}
