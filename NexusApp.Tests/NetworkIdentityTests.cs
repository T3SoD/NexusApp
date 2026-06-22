using NexusApp.Models;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// The local user's Blueprint Network identity (GUID + handle/nickname label) lives in settings.
// Exercised against a temp settings file so it runs headless.
public class NetworkIdentityTests : IDisposable
{
    private readonly string _tempPath =
        Path.Combine(Path.GetTempPath(), $"nexus_id_{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_tempPath)) File.Delete(_tempPath);
    }

    [Fact]
    public void EnsureLocalNetworkId_GeneratesAValidGuid_AndPersists()
    {
        var s1 = new SettingsService(_tempPath);
        var id = s1.EnsureLocalNetworkId();
        Assert.True(Guid.TryParse(id, out _));

        var s2 = new SettingsService(_tempPath);     // fresh instance reads the saved file
        Assert.Equal(id, s2.EnsureLocalNetworkId());  // stable, not regenerated
    }

    [Fact]
    public void EnsureLocalNetworkId_IsStableOnRepeatedCalls()
    {
        var s = new SettingsService(_tempPath);
        Assert.Equal(s.EnsureLocalNetworkId(), s.EnsureLocalNetworkId());
    }

    [Fact]
    public void SetLocalIdentity_NormalizesKind()
    {
        var s = new SettingsService(_tempPath);
        s.SetLocalIdentity("PlayerName", NetworkIdentityKind.Nickname);
        Assert.Equal(NetworkIdentityKind.Nickname, s.Current.LocalIdentityKind);
        Assert.Equal("PlayerName", s.Current.LocalDisplayName);

        s.SetLocalIdentity("PlayerName", "garbage");   // anything unknown falls back to handle
        Assert.Equal(NetworkIdentityKind.Handle, s.Current.LocalIdentityKind);
    }

    [Fact]
    public void SetDetectedRsiHandle_PersistsAcrossReload()
    {
        var s1 = new SettingsService(_tempPath);
        s1.SetDetectedRsiHandle("PlayerName");
        Assert.Equal("PlayerName", s1.Current.DetectedRsiHandle);

        var s2 = new SettingsService(_tempPath);
        Assert.Equal("PlayerName", s2.Current.DetectedRsiHandle);
    }
}
