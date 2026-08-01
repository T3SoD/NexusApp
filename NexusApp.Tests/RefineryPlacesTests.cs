using System.Collections.Generic;
using NexusApp.Models;
using NexusApp.Services.Map;
using Xunit;

namespace NexusApp.Tests;

// App review G10: the "best refinery" line picked purely on yield modifier, broke ties by whatever
// order the seed happened to list them in, and never said where the winner was - so a +8% refinery
// a jump away read exactly like a +8% one next door.
public class RefineryPlacesTests
{
    private static readonly MapCatalog Map = MapCatalog.LoadEmbedded();

    private static RefineryYield Y(string station, string system, int pct) => new(station, system, pct);

    // ---- BaseName / Resolve -------------------------------------------------------------------

    [Fact]
    public void BaseName_DropsTheUexParenthetical()
        => Assert.Equal("Stanton Gateway", RefineryPlaces.BaseName("Stanton Gateway (Nyx)"));

    [Fact]
    public void BaseName_LeavesPlainNamesAlone()
        => Assert.Equal("Ruin Station", RefineryPlaces.BaseName("Ruin Station"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BaseName_NullOrBlank_ReturnsEmpty_NeverThrows(string? station)
        => Assert.Equal("", RefineryPlaces.BaseName(station));

    [Fact]
    public void Resolve_PlainStationName_FindsTheObject()
    {
        var obj = RefineryPlaces.Resolve(Map, Y("Ruin Station", "Pyro", 5));
        Assert.NotNull(obj);
        Assert.Equal("Pyro", obj!.System);
    }

    // The trap this resolver exists to avoid. MapCatalog.ResolveSeedLocation keeps the INNER name
    // of a parenthetical, because the MINING seed writes planets as "Pyro II (Monox)". Applied to a
    // refinery it would turn "Stanton Gateway (Nyx)" into the star Nyx - a real object, and wrong
    // by roughly a solar system. The system field is what disambiguates here, not the parenthetical.
    [Fact]
    public void Resolve_GatewayStation_LandsOnTheGateway_NotOnTheStarNamedInTheParenthetical()
    {
        var obj = RefineryPlaces.Resolve(Map, Y("Stanton Gateway (Nyx)", "Nyx", 5));
        Assert.NotNull(obj);
        Assert.Equal("Stanton Gateway", obj!.Name);
        Assert.Equal("Nyx", obj.System);
        Assert.NotEqual("Star", obj.Type);
    }

    [Fact]
    public void Resolve_TwoGatewaysSharingAName_AreSeparatedByTheirSystem()
    {
        var inNyx = RefineryPlaces.Resolve(Map, Y("Pyro Gateway (Nyx)", "Nyx", 5));
        var inStanton = RefineryPlaces.Resolve(Map, Y("Pyro Gateway (Stanton)", "Stanton", 5));

        Assert.Equal("Nyx", inNyx!.System);
        Assert.Equal("Stanton", inStanton!.System);
        Assert.NotEqual(inNyx.Id, inStanton.Id);
    }

    [Fact]
    public void Resolve_StationTheCatalogDoesNotCarry_ReturnsNull()
    {
        // Levski, the one refinery in the seed that does not resolve: Delamar is not an object in
        // the catalog. Null is the answer, and callers render it exactly as they do today.
        Assert.Null(RefineryPlaces.Resolve(Map, Y("Levski", "Nyx", 5)));
    }

    [Fact]
    public void Resolve_Null_ReturnsNull_NeverThrows() => Assert.Null(RefineryPlaces.Resolve(Map, null));

    // ---- Best ---------------------------------------------------------------------------------

    [Fact]
    public void Best_HighestModifierWins_EvenWhenItIsFartherAway()
    {
        // The rule that must NOT change: "best" keeps meaning "best yield". A recommendation that
        // silently reorders itself by where you happen to be standing is unreadable.
        var yields = new List<RefineryYield> { Y("Ruin Station", "Pyro", 4), Y("Orbituary", "Pyro", 9) };
        var playerAt = Map.ByName("Pyro", "Ruin Station");

        Assert.Equal("Orbituary", RefineryPlaces.Best(yields, Map, playerAt)!.Station);
    }

    [Fact]
    public void Best_TieGoesToTheNearest()
    {
        var yields = new List<RefineryYield> { Y("Orbituary", "Pyro", 6), Y("Ruin Station", "Pyro", 6) };
        var playerAt = Map.ByName("Pyro", "Ruin Station");   // standing at the second one

        Assert.Equal("Ruin Station", RefineryPlaces.Best(yields, Map, playerAt)!.Station);
    }

    [Fact]
    public void Best_TieWithNoKnownPosition_KeepsTheOriginalOrder()
    {
        // Star Citizen closed is the normal state, and it must behave exactly as it did before G10.
        var yields = new List<RefineryYield> { Y("Orbituary", "Pyro", 6), Y("Ruin Station", "Pyro", 6) };

        Assert.Equal("Orbituary", RefineryPlaces.Best(yields, Map, playerAt: null)!.Station);
    }

    [Fact]
    public void Best_TieWhereOneIsUnplaceable_PrefersTheOneItCanMeasure_ButKeepsBoth()
    {
        var yields = new List<RefineryYield> { Y("Levski", "Nyx", 6), Y("Ruin Station", "Pyro", 6) };
        var playerAt = Map.ByName("Pyro", "Orbituary");

        Assert.Equal("Ruin Station", RefineryPlaces.Best(yields, Map, playerAt)!.Station);
    }

    [Fact]
    public void Best_SingleEntry_IsReturnedWhateverItsModifier()
    {
        var yields = new List<RefineryYield> { Y("Levski", "Nyx", -3) };
        Assert.Equal("Levski", RefineryPlaces.Best(yields, Map, playerAt: null)!.Station);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Best_NoRefineries_ReturnsNull(bool empty)
        => Assert.Null(RefineryPlaces.Best(empty ? new List<RefineryYield>() : null, Map, playerAt: null));

    // ---- Describe -----------------------------------------------------------------------------

    [Fact]
    public void Describe_NoKnownPosition_NamesTheSystemOnly()
        => Assert.Equal("Pyro", RefineryPlaces.Describe(Y("Ruin Station", "Pyro", 5), Map, playerAt: null));

    [Fact]
    public void Describe_SameSystem_CarriesARealDistance()
    {
        var label = RefineryPlaces.Describe(Y("Ruin Station", "Pyro", 5), Map, Map.ByName("Pyro", "Orbituary"));
        Assert.StartsWith("Pyro, ", label);
        Assert.Contains("Gm", label);
    }

    [Fact]
    public void Describe_StandingAtTheRefinery_ReadsZero_NotSilence()
    {
        var here = Map.ByName("Pyro", "Ruin Station");
        Assert.Equal("Pyro, " + MapCatalog.FormatGm(0), RefineryPlaces.Describe(Y("Ruin Station", "Pyro", 5), Map, here));
    }

    [Fact]
    public void Describe_AcrossASystemBoundary_SaysSo_InWordsRatherThanANumber()
    {
        // Jump travel is not Euclidean, so there is no honest number here - and a blank would read
        // as "we failed to measure" rather than "this is somewhere else entirely".
        var label = RefineryPlaces.Describe(Y("Ruin Station", "Pyro", 5), Map, Map.ByName("Stanton", "Everus Harbor"));
        Assert.Equal("Pyro, another system", label);
    }

    [Fact]
    public void Describe_UnplaceableStation_StillNamesItsSystem()
    {
        // Levski does not resolve, but the seed still knows what system it is in, and that half
        // needs no geometry at all.
        Assert.Equal("Nyx", RefineryPlaces.Describe(Y("Levski", "Nyx", 5), Map, Map.ByName("Nyx", "Nyx I")));
    }

    [Fact]
    public void Describe_Null_ReturnsNull_NeverThrows()
        => Assert.Null(RefineryPlaces.Describe(null, Map, playerAt: null));

    // ---- The seed itself: coverage, asserted rather than assumed -------------------------------

    [Fact]
    public void EverySeededRefineryStation_ResolvesExceptTheOneKnownGap()
    {
        // Straight from the embedded seed, so this measures the real data rather than a fixture
        // that could drift away from it.
        using var seed = SeedTestFixture.LoadSeed();
        var stations = new SortedSet<string>();
        var unresolved = new SortedSet<string>();
        foreach (var resource in seed.RootElement.GetProperty("resources").EnumerateArray())
        {
            if (!resource.TryGetProperty("refineries", out var refineries)) continue;
            foreach (var r in refineries.EnumerateArray())
            {
                var station = r.GetProperty("station").GetString() ?? "";
                var system = r.GetProperty("system").GetString() ?? "";
                if (!stations.Add($"{system}|{station}")) continue;
                if (RefineryPlaces.Resolve(Map, new RefineryYield(station, system, 0)) is null)
                    unresolved.Add(station);
            }
        }

        Assert.NotEmpty(stations);
        // Levski is the only one, and it is a data gap (Delamar is absent from the object catalog),
        // not a resolver bug. If this list ever grows, the resolver silently stopped placing
        // refineries and the distance quietly vanished from the line - which is the exact failure
        // mode G10 was raised about.
        Assert.Equal(new[] { "Levski" }, unresolved);
    }
}
