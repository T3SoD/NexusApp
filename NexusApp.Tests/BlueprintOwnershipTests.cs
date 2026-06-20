using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Exercises the non-WPF ownership logic in SettingsService against a temp settings
// file, so it runs headless (no window) and never touches the real user profile.
public class BlueprintOwnershipTests : IDisposable
{
    private readonly string _tempPath =
        Path.Combine(Path.GetTempPath(), $"nexus_test_{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_tempPath)) File.Delete(_tempPath);
    }

    [Fact]
    public void NewStore_HasNoOwnedBlueprints()
    {
        var s = new SettingsService(_tempPath);
        Assert.Equal(0, s.OwnedBlueprintCount);
        Assert.False(s.IsBlueprintOwned("Bracket Cooler"));
    }

    [Fact]
    public void MarkOwned_IsReportedAndCounted()
    {
        var s = new SettingsService(_tempPath);
        s.SetBlueprintOwned("Bracket Cooler", true);
        Assert.True(s.IsBlueprintOwned("Bracket Cooler"));
        Assert.Equal(1, s.OwnedBlueprintCount);
    }

    [Fact]
    public void Ownership_IsCaseInsensitive()
    {
        var s = new SettingsService(_tempPath);
        s.SetBlueprintOwned("Bracket Cooler", true);
        Assert.True(s.IsBlueprintOwned("bracket cooler"));
        Assert.True(s.IsBlueprintOwned("BRACKET COOLER"));
    }

    [Fact]
    public void MarkingTwice_DoesNotDuplicate()
    {
        var s = new SettingsService(_tempPath);
        s.SetBlueprintOwned("Bracket Cooler", true);
        s.SetBlueprintOwned("bracket cooler", true);
        Assert.Equal(1, s.OwnedBlueprintCount);
    }

    [Fact]
    public void Unmark_RemovesOwnership_CaseInsensitively()
    {
        var s = new SettingsService(_tempPath);
        s.SetBlueprintOwned("Bracket Cooler", true);
        s.SetBlueprintOwned("BRACKET COOLER", false);
        Assert.False(s.IsBlueprintOwned("Bracket Cooler"));
        Assert.Equal(0, s.OwnedBlueprintCount);
    }

    [Fact]
    public void Ownership_PersistsAcrossReload()
    {
        var s1 = new SettingsService(_tempPath);
        s1.SetBlueprintOwned("Bracket Cooler", true);
        s1.SetBlueprintOwned("Hellion Cannon", true);

        var s2 = new SettingsService(_tempPath);   // fresh instance reads the saved file
        Assert.True(s2.IsBlueprintOwned("Bracket Cooler"));
        Assert.True(s2.IsBlueprintOwned("Hellion Cannon"));
        Assert.Equal(2, s2.OwnedBlueprintCount);
    }

    [Fact]
    public void ClearOwnedBlueprints_RemovesAll_ResetsLookup_AndPersists()
    {
        var s = new SettingsService(_tempPath);
        s.SetBlueprintOwned("Bracket Cooler", true);
        s.SetBlueprintOwned("Hellion Cannon", true);

        s.ClearOwnedBlueprints();

        Assert.Equal(0, s.OwnedBlueprintCount);
        Assert.False(s.IsBlueprintOwned("Bracket Cooler"));   // lookup set reset, not stale
        Assert.False(s.IsBlueprintOwned("Hellion Cannon"));

        var reloaded = new SettingsService(_tempPath);        // empty state was saved
        Assert.Equal(0, reloaded.OwnedBlueprintCount);
    }
}
