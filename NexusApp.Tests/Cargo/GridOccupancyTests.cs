using NexusApp.Models.Cargo;
using NexusApp.Services.Cargo;
using Xunit;

namespace NexusApp.Tests.Cargo;

// GridOccupancy (Services/Cargo/GridOccupancy.cs) is the shared occupancy primitive both the
// auto-packer (CargoPacker) and the manual drag/placement path (GridStudioPage) rely on, but it
// had no direct tests - only indirect coverage through CargoPackerTests' end-to-end calls into
// CargoPacker.AutoPack/SplitToBoxes. GridOccupancy is public, so no access seam is needed.
public class GridOccupancyTests
{
    private static GridDef Grid(int w, int d, int h) => new() { Id = 0, W = w, D = d, H = h, Name = "g" };

    [Fact]
    public void Fits_NegativeCoordinates_ReturnsFalse()
    {
        var occ = new GridOccupancy(Grid(4, 4, 4));
        Assert.False(occ.Fits(-1, 0, 0, new CellSize(1, 1, 1)));
        Assert.False(occ.Fits(0, -1, 0, new CellSize(1, 1, 1)));
        Assert.False(occ.Fits(0, 0, -1, new CellSize(1, 1, 1)));
    }

    [Fact]
    public void Fits_ExceedingGridBounds_ReturnsFalse()
    {
        var occ = new GridOccupancy(Grid(2, 2, 2));
        Assert.False(occ.Fits(1, 0, 0, new CellSize(2, 1, 1))); // x + w = 3 > W(2)
        Assert.False(occ.Fits(0, 1, 0, new CellSize(1, 2, 1))); // y + d = 3 > D(2)
        Assert.False(occ.Fits(0, 0, 1, new CellSize(1, 1, 2))); // z + h = 3 > H(2)
    }

    [Fact]
    public void Fits_AgainstAlreadyMarkedCell_ReturnsFalse()
    {
        var occ = new GridOccupancy(Grid(4, 4, 4));
        occ.Mark(0, 0, 0, new CellSize(1, 1, 1), true);

        Assert.False(occ.Fits(0, 0, 0, new CellSize(1, 1, 1)));
        Assert.True(occ.Fits(1, 0, 0, new CellSize(1, 1, 1))); // untouched neighbor cell still free
    }

    [Fact]
    public void IsSupported_AtFloor_TrueRegardless_ButAboveFloorRequiresFullFootprint()
    {
        var occ = new GridOccupancy(Grid(2, 1, 2));

        // z == 0 always counts as supported (resting on the grid floor).
        Assert.True(occ.IsSupported(0, 0, 0, new CellSize(2, 1, 1)));

        // Only (0,0,0) filled beneath - a 2-wide box at z=1 is only PARTIALLY supported.
        occ.Mark(0, 0, 0, new CellSize(1, 1, 1), true);
        Assert.False(occ.IsSupported(0, 0, 1, new CellSize(2, 1, 1)));

        // Fill the rest of the floor beneath the footprint - now fully supported.
        occ.Mark(1, 0, 0, new CellSize(1, 1, 1), true);
        Assert.True(occ.IsSupported(0, 0, 1, new CellSize(2, 1, 1)));
    }

    [Fact]
    public void Mark_False_FreesCells_UsedCellsReflectsIt()
    {
        var occ = new GridOccupancy(Grid(3, 3, 3));
        occ.Mark(0, 0, 0, new CellSize(2, 2, 1), true);
        Assert.Equal(4, occ.UsedCells());

        occ.Mark(0, 0, 0, new CellSize(2, 2, 1), false);

        Assert.Equal(0, occ.UsedCells());
        Assert.True(occ.Fits(0, 0, 0, new CellSize(2, 2, 1))); // cells are genuinely free again
    }
}
