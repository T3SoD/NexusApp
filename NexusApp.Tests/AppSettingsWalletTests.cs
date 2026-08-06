using System.Text.Json;
using NexusApp.Models;
using Xunit;

namespace NexusApp.Tests;

public class AppSettingsWalletTests
{
    [Fact]
    public void WalletFieldsRoundTrip()
    {
        var settings = new AppSettings
        {
            WalletRegion = new ScanRegion { X = 1720, Y = 460, Width = 300, Height = 64 },
            WalletOcrEnabled = false,
        };

        var json = JsonSerializer.Serialize(settings);
        var loaded = JsonSerializer.Deserialize<AppSettings>(json)!;

        Assert.NotNull(loaded.WalletRegion);
        Assert.Equal(1720, loaded.WalletRegion!.X);
        Assert.Equal(300, loaded.WalletRegion.Width);
        Assert.False(loaded.WalletOcrEnabled);
    }

    [Fact]
    public void AbsentWalletFieldsDefaultToEnabledWithNoRegion()
    {
        var loaded = JsonSerializer.Deserialize<AppSettings>("{}")!;

        Assert.Null(loaded.WalletRegion);
        Assert.True(loaded.WalletOcrEnabled); // enabled by default; inert until a region exists
    }
}
