using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Headless coverage for the settings tab restore guard. The persisted tab must survive a
// round-trip, unknown or legacy ids must fall back to the default, and the destructive DATA
// tab must never be the tab a user lands on at open.
public class SettingsTabsTests : IDisposable
{
    private readonly string _tempPath =
        Path.Combine(Path.GetTempPath(), $"nexus_tabs_{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_tempPath)) File.Delete(_tempPath);
    }

    [Theory]
    [InlineData("game", "game")]
    [InlineData("diagnostics", "diagnostics")]
    [InlineData("interface", "interface")]
    [InlineData("data", "game")]        // destructive tab is never auto-restored
    [InlineData("", "game")]
    [InlineData(null, "game")]
    [InlineData("legacy-junk", "game")]
    [InlineData("GAME", "game")]        // ids are case-sensitive keys; unknown casing falls back
    public void NormalizeForRestore_GuardsUnknownAndDanger(string? saved, string expected)
        => Assert.Equal(expected, SettingsTabs.NormalizeForRestore(saved));

    [Fact]
    public void Ids_AreTheFourTabs()
        => Assert.Equal(new[] { "game", "diagnostics", "interface", "data" }, SettingsTabs.Ids);

    [Fact]
    public void SettingsActiveTab_PersistsAcrossReload()
    {
        var s1 = new SettingsService(_tempPath);
        s1.Current.SettingsActiveTab = "interface";
        s1.Save();

        var s2 = new SettingsService(_tempPath);     // fresh instance reads the saved file
        Assert.Equal("interface", s2.Current.SettingsActiveTab);
    }

    [Fact]
    public void SettingsActiveTab_DefaultsToGame()
    {
        var s = new SettingsService(_tempPath);
        Assert.Equal("game", s.Current.SettingsActiveTab);
    }
}
