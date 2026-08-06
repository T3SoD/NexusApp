using System.IO;
using System.Text;
using System.Text.Json;
using NexusApp.Models;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// The demo dataset ships INSIDE the public app so screenshots can show a believable fake
// profile (StarlightHauler). This guard makes the hygiene promise permanent: the dataset must
// exist, parse, identify as the fake operator, and never contain the real operator handle or any
// absolute user-profile path. (The private PII sweep beyond these public markers happens
// before files are ever added; this test keeps the publicly checkable part enforced forever.)
public class DemoSeedHygieneTests
{
    private static readonly string[] Required = ["settings.json", "nexus.db", "network.db", "Game.log", "wallet.json"];

    private static byte[] LoadDemo(string name)
    {
        using var s = typeof(DataService).Assembly
            .GetManifestResourceStream($"NexusApp.Data.demo.{name}");
        Assert.True(s != null, $"embedded demo resource missing: {name}");
        using var ms = new MemoryStream();
        s!.CopyTo(ms);
        return ms.ToArray();
    }

    [Fact]
    public void AllDemoResources_ShipEmbedded()
    {
        foreach (var n in Required) Assert.NotEmpty(LoadDemo(n));
    }

    [Fact]
    public void DemoSettings_IdentifyAsStarlightHauler_WithNoMachinePaths()
    {
        var s = JsonSerializer.Deserialize<AppSettings>(
            Encoding.UTF8.GetString(LoadDemo("settings.json")));
        Assert.NotNull(s);
        Assert.Equal("StarlightHauler", s!.DetectedRsiHandle);
        Assert.Equal("", s.GameLogPath);       // patched at seed time; never ships absolute
        Assert.True(s.FirstRunComplete);       // the demo must not open on the welcome wizard
    }

    [Fact]
    public void DemoGameLog_CarriesTheDemoHandle()
        => Assert.Contains("StarlightHauler", Encoding.UTF8.GetString(LoadDemo("Game.log")));

    [Fact]
    public void NoDemoResource_ContainsOwnerHandleOrUserPaths()
    {
        string[] banned = [OwnerGate.OwnerHandle, @"C:\Users\", "C:/Users/"];
        foreach (var n in Required)
        {
            var text = Encoding.ASCII.GetString(LoadDemo(n));
            foreach (var b in banned)
                Assert.DoesNotContain(b, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    // The csproj glob embeds everything under Data/demo. This pins the resource set to exactly
    // the five swept files, so a stray addition cannot ship unswept.
    [Fact]
    public void DemoResourceSet_IsExactlyTheFiveSweptFiles()
        => Assert.Equal(
            Required.Select(n => $"NexusApp.Data.demo.{n}").OrderBy(n => n, StringComparer.Ordinal),
            typeof(DataService).Assembly.GetManifestResourceNames()
                .Where(n => n.StartsWith("NexusApp.Data.demo.", StringComparison.Ordinal))
                .OrderBy(n => n, StringComparer.Ordinal));
}
