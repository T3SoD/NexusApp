using System.IO;
using System.Text.Json;
using NexusApp.Models;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Seeding must extract the full embedded dataset into an arbitrary root, patch GameLogPath to
// the seeded Game.log (absolute paths cannot ship embedded), be idempotent (demo-session state
// survives relaunches), and Reset must remove the root entirely. All against a temp dir; the
// real demo root is never touched here.
public class DemoProfileTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"nexus_demo_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void EnsureSeeded_ExtractsEveryFile()
    {
        DemoProfile.EnsureSeeded(_root);
        foreach (var f in DemoProfile.Files)
        {
            var fi = new FileInfo(Path.Combine(_root, f));
            Assert.True(fi.Exists, $"missing after seed: {f}");
            Assert.True(fi.Length > 0, $"zero-byte after seed: {f}");
        }
    }

    [Fact]
    public void IsSeeded_FlipsAfterSeeding()
    {
        Assert.False(DemoProfile.IsSeeded(_root));
        DemoProfile.EnsureSeeded(_root);
        Assert.True(DemoProfile.IsSeeded(_root));
    }

    [Fact]
    public void EnsureSeeded_PatchesGameLogPathIntoTheRoot()
    {
        DemoProfile.EnsureSeeded(_root);
        var s = JsonSerializer.Deserialize<AppSettings>(
            File.ReadAllText(Path.Combine(_root, "settings.json")));
        Assert.Equal(Path.Combine(_root, "LIVE", "Game.log"), s!.GameLogPath);
        // The patch round-trips the whole object; these pin that it cannot silently reset the
        // rest of the demo profile to defaults (a casing or model drift would do exactly that).
        Assert.Equal("StarlightHauler", s.LocalDisplayName);
        Assert.Equal(4, s.PinnedResources.Count);
    }

    [Fact]
    public void EnsureSeeded_IsIdempotent()
    {
        DemoProfile.EnsureSeeded(_root);
        var marker = Path.Combine(_root, "settings.json");
        File.AppendAllText(marker, "\n// session state");
        DemoProfile.EnsureSeeded(_root);   // must not re-extract over the live demo session
        Assert.Contains("// session state", File.ReadAllText(marker));
    }

    [Fact]
    public void Reset_RemovesTheRoot()
    {
        DemoProfile.EnsureSeeded(_root);
        DemoProfile.Reset(_root);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void Reset_OnMissingRoot_IsANoOp()
        => DemoProfile.Reset(_root);   // must not throw

    [Fact]
    public void PinGameLogPath_PinsEmptyToTheRoot_KeepsExisting()
    {
        Assert.Equal(Path.Combine(_root, "LIVE", "Game.log"), DemoProfile.PinGameLogPath("", _root));
        Assert.Equal(Path.Combine(_root, "LIVE", "Game.log"), DemoProfile.PinGameLogPath(null, _root));
        Assert.Equal(@"D:\somewhere\Game.log", DemoProfile.PinGameLogPath(@"D:\somewhere\Game.log", _root));
    }

    // The demo Game.log seeds under LIVE so channel inference (issue #28) reads the demo profile as
    // an ordinary LIVE install: a root-level file would infer Custom and put CUSTOM chips, shard
    // badges and the custom-folder notice into every public screenshot.
    [Fact]
    public void SeededGameLog_LivesUnderTheLiveChannelFolder()
    {
        DemoProfile.EnsureSeeded(_root);
        var log = Path.Combine(_root, "LIVE", "Game.log");
        Assert.True(File.Exists(log));
        Assert.Equal(GameChannel.Live, GameChannels.FromLogPath(log));
    }
}
