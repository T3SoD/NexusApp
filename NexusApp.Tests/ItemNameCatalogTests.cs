using System.IO;
using System.Text;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Token to display name lookup. A miss returns null so callers fall back rather than guess.
public class ItemNameCatalogTests
{
    private static ItemNameCatalog From(string json) =>
        ItemNameCatalog.Load(new MemoryStream(Encoding.UTF8.GetBytes(json)));

    private const string Sample = """
        {"names":{
          "crlf_consumable_healing_01":"MedPen (Hemozal)",
          "Carryable_1H_CY_medical_canister_healing_1":"C-71 Medical Case"
        }}
        """;

    [Fact]
    public void Resolve_ReturnsDisplayName()
    {
        var catalog = From(Sample);
        Assert.Equal("MedPen (Hemozal)", catalog.Resolve("crlf_consumable_healing_01"));
        Assert.Equal("C-71 Medical Case",
            catalog.Resolve("Carryable_1H_CY_medical_canister_healing_1"));
    }

    [Fact]
    public void Resolve_IsCaseInsensitive()
    {
        Assert.Equal("MedPen (Hemozal)", From(Sample).Resolve("CRLF_CONSUMABLE_HEALING_01"));
    }

    [Fact]
    public void Resolve_ReturnsNullOnMiss()
    {
        var catalog = From(Sample);
        Assert.Null(catalog.Resolve("crlf_medgun_vial_01"));
        Assert.Null(catalog.Resolve(""));
        Assert.Null(catalog.Resolve(null));
    }

    // The shipped table carries two non-name shapes left over from extraction: an unresolved
    // localization pointer ("@mp_ePistol") and a placeholder string, either case
    // ("Placeholder - ..." or "PLACEHOLDER - ..."). Both must miss like an unknown token so the
    // caller falls back to the shop name instead of rendering either one to the player.
    [Fact]
    public void Resolve_TreatsUnresolvedPointersAndPlaceholdersAsMisses()
    {
        var catalog = From("""
            {"names":{
              "mp_ePistol":"@mp_ePistol",
              "cbd_boots_01_01_01":"Placeholder - CBD Boots",
              "cbd_hat_03_01_CFP_var2":"PLACEHOLDER - cbd_hat_03_01_CFP_var2"
            }}
            """);
        Assert.Null(catalog.Resolve("mp_ePistol"));
        Assert.Null(catalog.Resolve("cbd_boots_01_01_01"));
        Assert.Null(catalog.Resolve("cbd_hat_03_01_CFP_var2"));
    }

    [Fact]
    public void Load_ReportsCount()
    {
        Assert.Equal(2, From(Sample).Count);
        Assert.Equal(0, From("""{"names":{}}""").Count);
    }

    // The shipped table, loaded the way the app loads it. Guards the csproj registration and
    // the resource name, which a rename would silently break.
    [Fact]
    public void LoadEmbedded_ResolvesKnownItems()
    {
        var catalog = ItemNameCatalog.LoadEmbedded();
        Assert.True(catalog.Count > 8000);
        Assert.Equal("MedPen (Hemozal)", catalog.Resolve("crlf_consumable_healing_01"));
        Assert.Equal("ParaMed Medical Device", catalog.Resolve("crlf_medgun_01"));
    }
}
