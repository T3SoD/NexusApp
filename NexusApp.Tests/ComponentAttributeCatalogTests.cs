using System.IO;
using System.Text;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

public class ComponentAttributeCatalogTests
{
    // ---- Layer B: parsing and lookup against synthetic JSON ----

    private static ComponentAttributeCatalog From(string json) =>
        ComponentAttributeCatalog.Load(new MemoryStream(Encoding.UTF8.GetBytes(json)));

    private const string Sample = """
        {"schema":1,"tokenFamilies":["COOL","SHLD"],"names":{
            "Mirage":{"size":"1","grade":"A","class":"Stealth"},
            "Bare":{"size":"2","grade":"","class":"Military"}
        }}
        """;

    [Fact]
    public void Resolve_KnownName_ReturnsAttributes()
    {
        var a = From(Sample).Resolve("Mirage");
        Assert.NotNull(a);
        Assert.Equal("1", a!.Size);
        Assert.Equal("A", a.Grade);
        Assert.Equal("Stealth", a.Class);
    }

    [Fact]
    public void Resolve_IsCaseInsensitive()
    {
        Assert.NotNull(From(Sample).Resolve("mirage"));
        Assert.NotNull(From(Sample).Resolve("MIRAGE"));
    }

    [Fact]
    public void Resolve_UnknownEmptyOrNull_ReturnsNull()
    {
        var cat = From(Sample);
        Assert.Null(cat.Resolve("Quasi Grazer"));
        Assert.Null(cat.Resolve(""));
        Assert.Null(cat.Resolve(null));
    }

    [Fact]
    public void Decorate_KnownComponent_AppendsSizeClassGrade()
    {
        Assert.Equal("Mirage - S1 - Stealth - A", From(Sample).Decorate("Mirage"));
    }

    [Fact]
    public void Decorate_SkipsBlankParts()
    {
        // No grade in the data: the label must not end with a dangling separator.
        Assert.Equal("Bare - S2 - Military", From(Sample).Decorate("Bare"));
    }

    [Fact]
    public void Decorate_NonComponent_ReturnsNameUnchanged()
    {
        Assert.Equal("Quantainium", From(Sample).Decorate("Quantainium"));
    }

    [Fact]
    public void Decorate_PreservesTheCallersCasing()
    {
        // Lookup is case-insensitive, but the rendered name is the caller's string.
        Assert.Equal("MIRAGE - S1 - Stealth - A", From(Sample).Decorate("MIRAGE"));
    }

    // The wallet's cross-domain guard: purchase tokens gate decoration, because ships,
    // weapons, and vehicles share display names with components (Eclipse, Predator, Nova).
    [Theory]
    [InlineData("COOL_ACOM_S02_AbsoluteZero_SCItem", true)]
    [InlineData("shld_asas_s01_mirage", true)]
    [InlineData("COOL", true)]
    [InlineData("AEGS_Eclipse_short", false)]
    [InlineData("TMBL_Nova", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsComponentToken_MatchesTheFamilyPrefixOnly(string? token, bool expected)
        => Assert.Equal(expected, From(Sample).IsComponentToken(token));

    // ---- Layer A: the real embedded table stays pinned ----

    private static readonly ComponentAttributeCatalog Embedded = ComponentAttributeCatalog.LoadEmbedded();

    [Theory]
    [InlineData("Mirage", "1", "A", "Stealth")]
    [InlineData("FR-76", "2", "A", "Military")]
    [InlineData("Eco-Flow", "1", "B", "Industrial")]
    public void Embedded_ResolvesKnownComponents(string name, string size, string grade, string cls)
    {
        var a = Embedded.Resolve(name);
        Assert.NotNull(a);
        Assert.Equal(size, a!.Size);
        Assert.Equal(grade, a.Grade);
        Assert.Equal(cls, a.Class);
    }

    [Fact]
    public void Embedded_DecoratesTheRequestExample()
    {
        Assert.Equal("Mirage - S1 - Stealth - A", Embedded.Decorate("Mirage"));
    }

    [Fact]
    public void Embedded_CarriesTheGradedComponentSet()
    {
        Assert.True(Embedded.Count >= 300, $"only {Embedded.Count} components in the embedded table");
    }

    [Theory]
    [InlineData("COOL_ACOM_S02_AbsoluteZero_SCItem", true)]
    [InlineData("QDRV_RSI_S02_Mirage", true)]
    [InlineData("SHLD_GODI_S02_FR76_SCItem", true)]
    [InlineData("AEGS_Eclipse_short", false)]
    public void Embedded_CarriesTheComponentTokenFamilies(string token, bool expected)
        => Assert.Equal(expected, Embedded.IsComponentToken(token));
}
