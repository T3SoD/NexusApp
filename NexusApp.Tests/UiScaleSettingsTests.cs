using System;
using System.IO;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Round-trip persistence for the two UI scale settings against a temp settings file,
// mirroring BlueprintOwnershipTests: headless, never touches the real user profile.
public class UiScaleSettingsTests : IDisposable
{
    private readonly string _tempPath =
        Path.Combine(Path.GetTempPath(), $"nexus_test_{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_tempPath)) File.Delete(_tempPath);
    }

    [Fact]
    public void Defaults_AreOneHundredPercent()
    {
        var s = new SettingsService(_tempPath);
        Assert.Equal(1.0, s.Current.AppUiScale);
        Assert.Equal(1.0, s.Current.OverlayUiScale);
    }

    [Fact]
    public void Scales_PersistAcrossReload()
    {
        var s1 = new SettingsService(_tempPath);
        s1.Current.AppUiScale = 1.25;
        s1.Current.OverlayUiScale = 1.4;
        s1.Save();

        var s2 = new SettingsService(_tempPath);
        Assert.Equal(1.25, s2.Current.AppUiScale);
        Assert.Equal(1.4, s2.Current.OverlayUiScale);
    }

    [Fact]
    public void OldSettingsFile_WithoutScaleKeys_LoadsDefaults()
    {
        // A pre-6.6 settings.json has no scale keys; missing keys must deserialize to 1.0.
        File.WriteAllText(_tempPath, "{ \"SettingsSchemaVersion\": 5, \"OverlayOpacity\": 0.7 }");
        var s = new SettingsService(_tempPath);
        Assert.Equal(1.0, s.Current.AppUiScale);
        Assert.Equal(1.0, s.Current.OverlayUiScale);
    }
}
