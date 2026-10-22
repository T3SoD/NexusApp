using System.Text.Json;
using NexusApp.Models;
using Xunit;

namespace NexusApp.Tests;

public class AppSettingsHeldQualityTests
{
    private static AppSettings RoundTrip(AppSettings s) =>
        JsonSerializer.Deserialize<AppSettings>(
            JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }))!;

    [Fact]
    public void HeldQualityOf_Absent_Is500()
    {
        Assert.Equal(500, new AppSettings().HeldQualityOf("Iron"));
    }

    [Fact]
    public void SetHeldQuality_RoundTripsThroughJson()
    {
        var s = new AppSettings();
        s.SetHeldQuality("Iron", 780);
        Assert.Equal(780, RoundTrip(s).HeldQualityOf("Iron"));
    }

    [Fact]
    public void HeldQuality_ClampsOnLoad()
    {
        var s = JsonSerializer.Deserialize<AppSettings>("{\"HeldQuality\": {\"Iron\": 5000, \"Copper\": -3}}")!;
        Assert.Equal(1000, s.HeldQualityOf("Iron"));
        Assert.Equal(0, s.HeldQualityOf("Copper"));
    }

    [Fact]
    public void HeldQuality_NullInJson_IsEmpty()
    {
        var s = JsonSerializer.Deserialize<AppSettings>("{\"HeldQuality\": null}")!;
        Assert.Equal(500, s.HeldQualityOf("Iron"));
        Assert.Equal(500, s.HeldQualityOf(null!));
    }

    [Fact]
    public void HeldQuality_DuplicateKeysByCase_LoadWithoutThrowing()
    {
        var s = JsonSerializer.Deserialize<AppSettings>("{\"HeldQuality\": {\"Iron\": 700, \"iron\": 800}}")!;
        Assert.Contains(s.HeldQualityOf("IRON"), new[] { 700, 800 });
    }

    [Fact]
    public void HeldQuality_KeyIsCaseInsensitive()
    {
        var s = new AppSettings();
        s.SetHeldQuality("Iron", 640);
        Assert.Equal(640, s.HeldQualityOf("IRON"));
        var loaded = RoundTrip(s);
        Assert.Equal(640, loaded.HeldQualityOf("iron"));
        Assert.Equal(640, loaded.HeldQualityOf("IRON"));
    }
}
