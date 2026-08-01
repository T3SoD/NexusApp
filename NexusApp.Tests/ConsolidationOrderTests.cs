using NexusApp.Models;
using NexusApp.Services.Map;
using Xunit;

namespace NexusApp.Tests;

// ConsolidationOrder puts a hauler's collect/deliver stops in nearest-first order. Before this they
// rendered in dictionary insertion order, which is the order contracts happened to be accepted in -
// the app had the coordinates and the live player position all along and used neither.
//
// Stop locations are FREE TEXT from contract OCR or Game.log leg destinations, not terminal ids, so
// plenty will never resolve ("Pickup (TBD)", "Stanton System", an OCR misread). How those are
// handled is the interesting half of this and most of what these tests pin.
public class ConsolidationOrderTests
{
    private static readonly MapCatalog Map = MapCatalog.LoadEmbedded();

    private static ConsolidationStop Stop(string location) => new() { Location = location };
    private static MapObject? At(string system, string name) => Map.ByName(system, name);
    private static List<string> Names(IEnumerable<ConsolidationStop> s) => s.Select(x => x.Location).ToList();

    [Fact]
    public void OrdersNearestFirst()
    {
        // Everus Harbor orbits Hurston; Baijini Point orbits ArcCorp. Standing at Hurston, the
        // Hurston-side stop must come first whatever order they arrived in.
        var ordered = ConsolidationOrder.ByDistanceFrom(
            new[] { Stop("Baijini Point"), Stop("Everus Harbor") }, Map, At("Stanton", "Hurston"));

        Assert.Equal("Everus Harbor", ordered[0].Location);
    }

    [Fact]
    public void NoPlayerPosition_LeavesTheOrderExactlyAsItCame()
    {
        // Sorting by distance from nowhere would be theatre. This is the normal state with the game
        // closed, so it must be a clean passthrough rather than a reshuffle.
        var input = new[] { Stop("Baijini Point"), Stop("Everus Harbor"), Stop("Area18") };
        var ordered = ConsolidationOrder.ByDistanceFrom(input, Map, from: null);

        Assert.Equal(Names(input), Names(ordered));
    }

    [Fact]
    public void UnplaceableStops_SortLast_AndKeepTheirRelativeOrder()
    {
        // An unknown distance is not a large one, so these must never displace a stop we can place -
        // but they also must not be shuffled against each other, since there is nothing to sort by.
        var ordered = ConsolidationOrder.ByDistanceFrom(
            new[] { Stop("Pickup (TBD)"), Stop("Everus Harbor"), Stop("Stanton System") },
            Map, At("Stanton", "Hurston"));

        Assert.Equal("Everus Harbor", ordered[0].Location);
        Assert.Equal(new List<string> { "Pickup (TBD)", "Stanton System" }, Names(ordered).Skip(1).ToList());
    }

    [Fact]
    public void NothingResolves_LeavesTheOrderAlone()
    {
        // With no stop placeable there is no ordering to apply, so the original survives rather than
        // being rearranged by a comparison that means nothing.
        var input = new[] { Stop("Pickup (TBD)"), Stop("Stanton System") };
        var ordered = ConsolidationOrder.ByDistanceFrom(input, Map, At("Stanton", "Hurston"));

        Assert.Equal(Names(input), Names(ordered));
    }

    [Fact]
    public void CrossSystemStops_CountAsUnplaceable_NotAsInfinitelyFar()
    {
        // DistanceMeters is null across systems because jump travel is not Euclidean. That lands a
        // Pyro stop in the same bucket as an unreadable one - correct, since neither has a real
        // distance from here, and a fabricated large number would sort it as if it did.
        var ordered = ConsolidationOrder.ByDistanceFrom(
            new[] { Stop("Ruin Station"), Stop("Everus Harbor") }, Map, At("Stanton", "Hurston"));

        Assert.Equal("Everus Harbor", ordered[0].Location);
        Assert.Equal("Ruin Station", ordered[1].Location);
    }

    [Fact]
    public void SingleStop_IsReturnedUntouched()
        => Assert.Single(ConsolidationOrder.ByDistanceFrom(new[] { Stop("Everus Harbor") }, Map, At("Stanton", "Hurston")));

    [Fact]
    public void EmptyInput_DoesNotThrow()
        => Assert.Empty(ConsolidationOrder.ByDistanceFrom(Array.Empty<ConsolidationStop>(), Map, At("Stanton", "Hurston")));

    // ── DistanceTo, the per-stop label ──

    [Fact]
    public void DistanceTo_PlaceableStop_IsAFormattedFigure()
    {
        var label = ConsolidationOrder.DistanceTo(Stop("Everus Harbor"), Map, At("Stanton", "Hurston"));
        Assert.EndsWith(" Gm", label);
    }

    [Fact]
    public void DistanceTo_IsSilentWhenItCannotAnswer()
    {
        // Same silence rule the price surfaces follow: the caller appends this unconditionally and
        // renders nothing on null.
        Assert.Null(ConsolidationOrder.DistanceTo(Stop("Everus Harbor"), Map, from: null));
        Assert.Null(ConsolidationOrder.DistanceTo(Stop("Pickup (TBD)"), Map, At("Stanton", "Hurston")));
        Assert.Null(ConsolidationOrder.DistanceTo(Stop("Ruin Station"), Map, At("Stanton", "Hurston")));
    }
}
