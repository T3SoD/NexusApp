using NexusApp.Models.Cargo;
using NexusApp.Services.Cargo;
using Xunit;

namespace NexusApp.Tests.Cargo;

public class MultiTripPlannerTests
{
    private static GridDef Grid(int id, int w, int d, int h, int cap) =>
        new() { Id = id, W = w, D = d, H = h, AcceptedCaps = BoxType.SizesDesc.Where(s => s <= cap).ToArray(), Name = $"g{id}" };

    private static List<PackBox> Boxes(params int[] scus)
    {
        var list = new List<PackBox>();
        for (int i = 0; i < scus.Length; i++)
            list.Add(new PackBox { Scu = scus[i], OrderIndex = i });
        return list;
    }

    [Fact]
    public void Plan_OneTrip_WhenCargoFits()
    {
        var grids = new[] { Grid(0, 2, 2, 4, 8) };
        var plan = MultiTripPlanner.Plan(grids, Boxes(8, 8));

        Assert.True(plan.FitsInOneTrip);
        Assert.Equal(1, plan.TripCount);
        Assert.Empty(plan.Unplaceable);
    }

    [Fact]
    public void Plan_SplitsAcrossTrips_WhenOverCapacity()
    {
        // A grid that holds exactly one 8 SCU cube: three cubes means three trips.
        var grids = new[] { Grid(0, 2, 2, 2, 8) };
        var plan = MultiTripPlanner.Plan(grids, Boxes(8, 8, 8));

        Assert.Equal(3, plan.TripCount);
        Assert.Empty(plan.Unplaceable);
        Assert.All(plan.Trips, t => Assert.Equal(8, t.PlacedScu));
        Assert.Equal(24, plan.TotalPlacedScu);
    }

    [Fact]
    public void Plan_MarksUnplaceable_WhenBoxShapeFitsNoGrid()
    {
        // A 24 SCU box is 6x2x2; a 4x4x4 grid has no axis 6 long, so it can never hold it.
        var grids = new[] { Grid(0, 4, 4, 4, 32) };
        var plan = MultiTripPlanner.Plan(grids, Boxes(24));

        Assert.Equal(0, plan.TripCount);
        Assert.Single(plan.Unplaceable);
    }

    [Fact]
    public void Plan_PacksWhatFits_AndDefersTheUnplaceableShape()
    {
        var grids = new[] { Grid(0, 4, 4, 4, 32) };
        var plan = MultiTripPlanner.Plan(grids, Boxes(8, 24));

        Assert.Equal(1, plan.TripCount);
        Assert.Single(plan.Unplaceable);
        Assert.False(plan.FitsInOneTrip);   // one box could never be carried
    }

    [Fact]
    public void Plan_IsDeterministic()
    {
        var grids = new[] { Grid(0, 2, 2, 2, 8) };
        var boxes = Boxes(8, 8, 8, 8, 8);
        var a = MultiTripPlanner.Plan(grids, boxes);
        var b = MultiTripPlanner.Plan(grids, boxes);
        Assert.Equal(a.TripCount, b.TripCount);
        Assert.Equal(a.TotalPlacedScu, b.TotalPlacedScu);
    }
}
