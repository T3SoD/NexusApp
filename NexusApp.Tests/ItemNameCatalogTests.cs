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

    [Fact]
    public void Load_ReportsCount()
    {
        Assert.Equal(2, From(Sample).Count);
        Assert.Equal(0, From("""{"names":{}}""").Count);
    }
}
