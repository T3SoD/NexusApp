using System.IO;
using NexusApp.Models;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

public class WalletStoreTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    private string TempPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexus-wallet-store-test-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return Path.Combine(dir, "wallet.json");
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void RoundTripsAnchorAndUntrackedPerChannel()
    {
        var path = TempPath();
        var state = new WalletState();
        state.Channels.Add(new WalletChannelState
        {
            Channel = GameChannel.Live,
            Anchor = 5230346,
            AnchorUtc = new DateTime(2026, 8, 6, 0, 26, 39, DateTimeKind.Utc),
            Source = "Ocr",
            Untracked = { new UntrackedEntry { Utc = new DateTime(2026, 8, 6, 0, 30, 0, DateTimeKind.Utc), Amount = -80000 } },
        });
        state.Channels.Add(new WalletChannelState { Channel = GameChannel.Ptu, Anchor = 12, AnchorUtc = DateTime.UtcNow, Source = "Manual" });

        Assert.True(WalletStore.Save(path, state));
        var loaded = WalletStore.Load(path, out var reason);

        Assert.Null(reason);
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.Channels.Count);
        var live = loaded.Channels.Single(c => c.Channel == GameChannel.Live);
        Assert.Equal(5230346, live.Anchor);
        Assert.Equal("Ocr", live.Source);
        var entry = Assert.Single(live.Untracked);
        Assert.Equal(-80000, entry.Amount);
        Assert.Equal("Manual", loaded.Channels.Single(c => c.Channel == GameChannel.Ptu).Source);
    }

    [Fact]
    public void MissingFileLoadsNullWithoutPreserving()
    {
        var path = TempPath();
        var loaded = WalletStore.Load(path, out var reason);
        Assert.Null(loaded);
        Assert.NotNull(reason);
        Assert.False(File.Exists(Path.ChangeExtension(path, ".bad.json")));
    }

    [Fact]
    public void CorruptFileIsPreservedAsideAndReasonCarriesNoPath()
    {
        var path = TempPath();
        File.WriteAllText(path, "{ not json");
        var loaded = WalletStore.Load(path, out var reason);
        Assert.Null(loaded);
        Assert.True(File.Exists(Path.ChangeExtension(path, ".bad.json")));
        Assert.NotNull(reason);
        Assert.DoesNotContain(Path.GetDirectoryName(path)!, reason!);
    }

    [Fact]
    public void WrongSchemaIsRefusedAndPreserved()
    {
        var path = TempPath();
        File.WriteAllText(path, "{\"Schema\":9,\"Channels\":[]}");
        var loaded = WalletStore.Load(path, out var reason);
        Assert.Null(loaded);
        Assert.True(File.Exists(Path.ChangeExtension(path, ".bad.json")));
    }

    [Fact]
    public void OversizedFileIsRefusedAndPreserved()
    {
        var path = TempPath();
        File.WriteAllText(path, new string(' ', (int)WalletStore.MaxLoadBytes + 1));
        var loaded = WalletStore.Load(path, out var reason);
        Assert.Null(loaded);
        Assert.True(File.Exists(Path.ChangeExtension(path, ".bad.json")));
    }

    [Fact]
    public void SaveReturnsFalseInsteadOfThrowing()
    {
        var blocker = TempPath();
        File.WriteAllText(blocker, "a file, not a directory");
        var impossible = Path.Combine(blocker, "wallet.json");
        Assert.False(WalletStore.Save(impossible, new WalletState()));
    }

    [Fact]
    public void LabelRoundTripsAndPreLabelFilesLoadNull()
    {
        var path = TempPath();
        var state = new WalletState();
        state.Channels.Add(new WalletChannelState
        {
            Channel = GameChannel.Live,
            Anchor = 1_051_250,
            AnchorUtc = new DateTime(2026, 8, 6, 0, 28, 11, DateTimeKind.Utc),
            Source = "Ocr",
            Untracked = { new UntrackedEntry
            {
                Utc = new DateTime(2026, 8, 6, 0, 28, 11, DateTimeKind.Utc),
                Amount = 51_250, Label = "Security Patrol",
            } },
        });

        Assert.True(WalletStore.Save(path, state));
        var loaded = WalletStore.Load(path, out _);
        Assert.Equal("Security Patrol", Assert.Single(loaded!.Channels.Single().Untracked).Label);

        // Files written before the label existed carry no Label property: they load with null.
        File.WriteAllText(path, "{\"Schema\":1,\"Channels\":[{\"Channel\":0,\"Anchor\":1," +
            "\"AnchorUtc\":\"2026-08-06T00:00:00Z\",\"Source\":\"Ocr\"," +
            "\"Untracked\":[{\"Utc\":\"2026-08-06T00:30:00Z\",\"Amount\":-7}]}]}");
        var legacy = WalletStore.Load(path, out var reason);
        Assert.Null(reason);
        Assert.Null(Assert.Single(legacy!.Channels.Single().Untracked).Label);
    }
}
