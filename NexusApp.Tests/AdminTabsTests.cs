using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Headless coverage for the Admin page tab restore guard, mirroring SettingsTabsTests. All
// three admin tabs are restorable (none is destructive: demo mode never touches the live
// profile), unknown and legacy ids fall back to the default.
public class AdminTabsTests : IDisposable
{
    private readonly string _tempPath =
        Path.Combine(Path.GetTempPath(), $"nexus_admin_tabs_{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_tempPath)) File.Delete(_tempPath);
    }

    [Theory]
    [InlineData("roster", "roster")]
    [InlineData("diagnostics", "diagnostics")]
    [InlineData("tools", "tools")]
    [InlineData("", "roster")]
    [InlineData(null, "roster")]
    [InlineData("legacy-junk", "roster")]
    [InlineData("ROSTER", "roster")]    // ids are case-sensitive keys; unknown casing falls back
    public void NormalizeForRestore_GuardsUnknown(string? saved, string expected)
        => Assert.Equal(expected, AdminTabs.NormalizeForRestore(saved));

    [Fact]
    public void Ids_AreTheThreeTabs()
        => Assert.Equal(new[] { "roster", "diagnostics", "tools" }, AdminTabs.Ids);

    [Fact]
    public void AdminActiveTab_PersistsAcrossReload()
    {
        var s1 = new SettingsService(_tempPath);
        s1.Current.AdminActiveTab = "tools";
        s1.Save();

        var s2 = new SettingsService(_tempPath);     // fresh instance reads the saved file
        Assert.Equal("tools", s2.Current.AdminActiveTab);
    }

    [Fact]
    public void AdminActiveTab_DefaultsToRoster()
        => Assert.Equal("roster", new SettingsService(_tempPath).Current.AdminActiveTab);
}
