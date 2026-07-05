using NexusApp.Models.Cargo;
using NexusApp.Services.Cargo;
using Xunit;

namespace NexusApp.Tests.Cargo;

public class GridValidationTests
{
    private static GridDef Grid(int w, int d, int h, params int[] accepts) =>
        new() { Id = 0, W = w, D = d, H = h, AcceptedCaps = accepts };

    [Fact]
    public void Problem_Null_WhenLargestAcceptedFits()
    {
        // A 1x1x1 grid accepting only 1 SCU (a 1x1x1 box) is consistent.
        Assert.Null(GridValidation.Problem(Grid(1, 1, 1, 1)));
    }

    [Fact]
    public void Problem_Reported_WhenAcceptedSizeCannotFit()
    {
        // A 1x1x1 grid that claims to accept 32 SCU cannot physically hold it.
        Assert.NotNull(GridValidation.Problem(Grid(1, 1, 1, 32)));
    }

    [Fact]
    public void FirstProblem_FindsTheOffendingGrid()
    {
        var grids = new List<GridDef> { Grid(4, 4, 4, 1), Grid(1, 1, 1, 32) };
        var problem = GridValidation.FirstProblem(grids);
        Assert.NotNull(problem);
        Assert.Contains("Grid 1", problem);   // the 1x1x1 grid is Id 0 -> "Grid 1"
    }
}
