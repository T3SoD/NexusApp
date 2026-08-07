using System.IO;
using System.Text;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// GUID first, token second. Both maps exist because CIG's entity tokens and localization keys
// disagree for some items, and because either identifier can drift between patches.
public class ItemNameCatalogTests
{
    private static ItemNameCatalog From(string json) =>
        ItemNameCatalog.Load(new MemoryStream(Encoding.UTF8.GetBytes(json)));

    private const string Sample = """
        {"guids":{
          "d4408421-e939-4e34-9902-0644fa6934be":"'Chaos' III Missile"
        },
        "names":{
          "crlf_consumable_healing_01":"MedPen (Hemozal)",
          "Carryable_1H_CY_medical_canister_healing_1":"C-71 Medical Case",
          "at_symbol":"@mp_ePistol",
          "placeholder_item":"PLACEHOLDER - placeholder_item"
        }}
        """;

    [Fact]
    public void Resolve_PrefersTheGuid()
    {
        Assert.Equal("'Chaos' III Missile",
            From(Sample).Resolve("d4408421-e939-4e34-9902-0644fa6934be", "unknown_token"));
    }

    [Fact]
    public void Resolve_FallsBackToTheTokenWhenTheGuidMisses()
    {
        Assert.Equal("MedPen (Hemozal)",
            From(Sample).Resolve("00000000-0000-0000-0000-000000000000", "crlf_consumable_healing_01"));
        Assert.Equal("C-71 Medical Case",
            From(Sample).Resolve(null, "Carryable_1H_CY_medical_canister_healing_1"));
    }

    [Fact]
    public void Resolve_IsCaseInsensitiveOnBothKeys()
    {
        var c = From(Sample);
        Assert.Equal("'Chaos' III Missile", c.Resolve("D4408421-E939-4E34-9902-0644FA6934BE", null));
        Assert.Equal("MedPen (Hemozal)", c.Resolve(null, "CRLF_CONSUMABLE_HEALING_01"));
    }

    [Fact]
    public void Resolve_ReturnsNullWhenBothMiss()
    {
        var c = From(Sample);
        Assert.Null(c.Resolve("nope", "also_nope"));
        Assert.Null(c.Resolve(null, null));
        Assert.Null(c.Resolve("", ""));
    }

    [Fact]
    public void Resolve_RejectsNonNames()
    {
        var c = From(Sample);
        Assert.Null(c.Resolve(null, "at_symbol"));          // "@mp_ePistol" is a loc pointer
        Assert.Null(c.Resolve(null, "placeholder_item"));   // "PLACEHOLDER - ..." is not a name
    }

    [Fact]
    public void Load_ReportsCount()
    {
        Assert.Equal(4, From(Sample).Count);
        Assert.Equal(0, From("""{"guids":{},"names":{}}""").Count);
    }

    // The shipped table, loaded the way the app loads it. Guards the csproj registration and the
    // resource name, and pins the four GUIDs captured live on 2026-08-07.
    [Fact]
    public void LoadEmbedded_ResolvesTheLiveCapturedItems()
    {
        var c = ItemNameCatalog.LoadEmbedded();
        Assert.True(c.Count > 8000);
        Assert.Equal("'Chaos' III Missile", c.Resolve("d4408421-e939-4e34-9902-0644fa6934be", null));
        Assert.Equal("APX Fire Extinguisher", c.Resolve("1b6a6b76-f3fc-402c-a24a-204f2eeae6f7", null));
        Assert.Equal("Agure", c.Resolve("94e97499-375e-4c63-a2ea-c605a4d3f461", null));
        Assert.Equal("CorticoPen (Sterogen)", c.Resolve("354ec8a6-32eb-4747-8e75-03d2703edfd6", null));
    }
}
