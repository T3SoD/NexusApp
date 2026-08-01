using NexusApp.Services;
using NexusApp.Services.Map;
using Xunit;

namespace NexusApp.Tests;

// App review G7b: the MAP tab has drawn guide sites as its GUIDES layer since it shipped, but the
// guide cards never said where they were - the two features referenced each other in one direction
// only. This reads MapLayers.GuideSites the other way round, guide id first.
public class GuidePlacesTests
{
    private static readonly MapCatalog Map = MapCatalog.LoadEmbedded();

    [Fact]
    public void SitesFor_GuideWithOneSite_ReturnsIt()
    {
        var sites = GuidePlaces.SitesFor("checkmate");
        Assert.Single(sites);
        Assert.Equal(("Pyro", "Checkmate"), sites[0]);
    }

    [Fact]
    public void SitesFor_GuideCoveringTwoFacilities_ReturnsBoth()
    {
        // The Supervisor guide is one map of two adjacent facilities, and its own title says so
        // ("PYAM-SUPVISR-3-4/5"). Collapsing it to one site would make the card contradict it.
        Assert.Equal(2, GuidePlaces.SitesFor("supervisor").Count);
    }

    [Theory]
    [InlineData("tsg-overview")]
    [InlineData("tsg-trench")]
    public void SitesFor_GuideWithNoLocation_IsEmpty(string guideId)
    {
        // The Tactical Strike Groups pair document a formation, not a place, and never will have a
        // site. Their cards must keep exactly the shape they have today rather than gaining a row.
        Assert.Empty(GuidePlaces.SitesFor(guideId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guide")]
    public void SitesFor_UnknownOrBlankId_IsEmpty_NeverThrows(string? guideId)
        => Assert.Empty(GuidePlaces.SitesFor(guideId));

    [Fact]
    public void Resolve_TakesTheFirstSiteInTableOrder_NotTheNearest()
    {
        // Deliberately not distance-ordered: the table's order is the order the guide itself
        // presents them, and a card that reordered its own subject by where the player happened to
        // be standing would be disorienting.
        var obj = GuidePlaces.Resolve(Map, "supervisor");
        Assert.NotNull(obj);
        Assert.Equal("PYAM-SUPVISR-3-4", obj!.Name);
    }

    [Fact]
    public void Resolve_GuideWithNoSite_IsNull() => Assert.Null(GuidePlaces.Resolve(Map, "tsg-overview"));

    // Every guide that HAS a site must place: if one silently stopped resolving, its card would
    // quietly lose the strip and the MAP tab would quietly lose the pin, with nothing failing.
    [Fact]
    public void EveryGuideWithASite_Resolves()
    {
        foreach (var guide in GuideCatalog.All)
        {
            if (GuidePlaces.SitesFor(guide.Id).Count == 0) continue;
            Assert.True(GuidePlaces.Resolve(Map, guide.Id) is not null, $"guide '{guide.Id}' no longer resolves");
        }
    }

    [Fact]
    public void EveryGuideSiteId_IsARealGuideInTheCatalog()
    {
        // The other direction: a table entry naming a guide that does not exist would put a pin on
        // the map that no card could ever match.
        foreach (var (guideId, _, _) in MapLayers.GuideSites)
            Assert.Contains(GuideCatalog.All, g => g.Id == guideId);
    }

    // ---- Describe -----------------------------------------------------------------------------

    [Fact]
    public void Describe_NoKnownPosition_NamesThePlaceAndItsSystem()
        => Assert.Equal("Checkmate - Pyro", GuidePlaces.Describe(Map, "checkmate", playerAt: null));

    [Fact]
    public void Describe_SameSystem_CarriesARealDistance()
    {
        var label = GuidePlaces.Describe(Map, "checkmate", Map.ByName("Pyro", "Ruin Station"));
        Assert.StartsWith("Checkmate - Pyro, ", label);
        Assert.Contains("Gm", label);
    }

    [Fact]
    public void Describe_AcrossASystemBoundary_SaysSoInWords()
        => Assert.Equal("Checkmate - Pyro, another system",
                        GuidePlaces.Describe(Map, "checkmate", Map.ByName("Stanton", "Everus Harbor")));

    [Fact]
    public void Describe_MultiSiteGuide_MarksTheSitesItIsNotNaming()
    {
        var label = GuidePlaces.Describe(Map, "supervisor", playerAt: null);
        Assert.StartsWith("PYAM-SUPVISR-3-4 +1", label);
    }

    [Fact]
    public void Describe_GuideWithNoSite_IsNull_SoNoRowIsBuilt()
        => Assert.Null(GuidePlaces.Describe(Map, "tsg-overview", playerAt: null));
}
