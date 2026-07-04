using NexusApp.Models.Cargo;
using NexusApp.Services.Cargo;
using Xunit;

namespace NexusApp.Tests.Cargo;

public class CargoPackerTests
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

    // ---- SplitToBoxes: the greedy largest-first denomination under a cap ----

    [Fact]
    public void Split_148_Cap32_IsFourThirtyTwosOneSixteenOneFour()
    {
        Assert.Equal(new[] { 32, 32, 32, 32, 16, 4 }, CargoPacker.SplitToBoxes(148, 32));
    }

    [Fact]
    public void Split_148_Cap16_IsNineSixteensOneFour()
    {
        Assert.Equal(new[] { 16, 16, 16, 16, 16, 16, 16, 16, 16, 4 }, CargoPacker.SplitToBoxes(148, 16));
    }

    [Fact]
    public void Split_RespectsCap_NeverEmitsBoxLargerThanCap()
    {
        foreach (var scu in CargoPacker.SplitToBoxes(200, 8))
            Assert.True(scu <= 8);
    }

    [Theory]
    [InlineData(148, 32)]
    [InlineData(200, 16)]
    [InlineData(37, 8)]
    [InlineData(1, 32)]
    [InlineData(0, 32)]
    public void Split_AlwaysSumsBackToTotal(int total, int cap)
    {
        Assert.Equal(total, CargoPacker.SplitToBoxes(total, cap).Sum());
    }

    // ---- AutoPack: placement validity ----

    [Fact]
    public void AutoPack_StacksCubesSupportedAndNonOverlapping()
    {
        // 2x2x4 grid, cap 8 = exactly two 8 SCU cubes stacked.
        var grids = new[] { Grid(0, 2, 2, 4, 8) };
        var result = CargoPacker.AutoPack(grids, Boxes(8, 8));

        Assert.Equal(2, result.Placed.Count);
        Assert.Empty(result.Deferred);
        AssertValidLayout(result, grids);
        // the second cube must rest on the first, not the floor
        Assert.Contains(result.Placed, p => p.Z == 2);
    }

    [Fact]
    public void AutoPack_IsDeterministic()
    {
        var grids = new[] { Grid(0, 4, 9, 3, 32), Grid(1, 2, 2, 2, 8) };
        var boxes = Boxes(32, 24, 16, 8, 8, 4, 2, 1);

        var a = CargoPacker.AutoPack(grids, boxes);
        var b = CargoPacker.AutoPack(grids, boxes);

        Assert.Equal(Signature(a), Signature(b));
    }

    [Fact]
    public void AutoPack_RespectsPerGridMaxContainer()
    {
        // grid 0 has more capacity (ordered first) but only a 16 cap; a 32 box must skip it
        // and land in the 32-cap grid 1.
        var grids = new[] { Grid(0, 8, 2, 3, 16), Grid(1, 8, 2, 2, 32) };
        var result = CargoPacker.AutoPack(grids, Boxes(32));

        var placed = Assert.Single(result.Placed);
        Assert.Equal(1, placed.GridId);
        AssertValidLayout(result, grids);
    }

    [Fact]
    public void AutoPack_OverCapacity_DefersOverflowAndConservesScu()
    {
        // 2x2x2 grid holds exactly one 8 SCU cube; three 8s means two are deferred.
        var grids = new[] { Grid(0, 2, 2, 2, 8) };
        var result = CargoPacker.AutoPack(grids, Boxes(8, 8, 8));

        Assert.Single(result.Placed);
        Assert.Equal(2, result.Deferred.Count);
        Assert.Equal(8, result.PlacedScu);
        Assert.Equal(16, result.DeferredScu);
        Assert.Equal(24, result.PlacedScu + result.DeferredScu);
        AssertValidLayout(result, grids);
    }

    [Fact]
    public void AutoPack_LongBoxUsesYawOrientationToFit()
    {
        // A 32 SCU box is natively 8 long on X, but this grid is only 2 wide on X and 8 on Y,
        // so it can only fit by yawing to 2x8x2. The packer must try that orientation.
        var grids = new[] { Grid(0, 2, 8, 2, 32) };
        var result = CargoPacker.AutoPack(grids, Boxes(32));

        var placed = Assert.Single(result.Placed);
        Assert.Equal(new CellSize(2, 8, 2), placed.Size);
        AssertValidLayout(result, grids);
    }

    [Fact]
    public void AutoPack_FillsLargestGridFirst()
    {
        var big = Grid(0, 4, 9, 3, 32);
        var small = Grid(1, 2, 2, 2, 8);
        var result = CargoPacker.AutoPack(new[] { big, small }, Boxes(32));

        var placed = Assert.Single(result.Placed);
        Assert.Equal(0, placed.GridId);   // the 32 only fits the big grid
    }

    [Fact]
    public void AutoPack_MixedSizesAllPlacedAndValid()
    {
        // A roomy grid (must be at least 6 long for the 24 SCU box) takes a mix cleanly.
        var grids = new[] { Grid(0, 6, 4, 4, 32) };
        var result = CargoPacker.AutoPack(grids, Boxes(24, 4, 2, 1, 1));
        Assert.Empty(result.Deferred);
        AssertValidLayout(result, grids);
    }

    // ---- helpers ----

    private static string Signature(PackResult r) =>
        string.Join("|", r.Placed.Select(p =>
            $"{p.GridId}:{p.Scu}:{p.X},{p.Y},{p.Z}:{p.Size.W}x{p.Size.D}x{p.Size.H}"));

    private static void AssertValidLayout(PackResult r, IReadOnlyList<GridDef> grids)
    {
        var byId = grids.ToDictionary(g => g.Id);
        var occ = grids.ToDictionary(g => g.Id, g => new bool[g.W, g.D, g.H]);

        foreach (var p in r.Placed)
        {
            var g = byId[p.GridId];
            Assert.True(p.X >= 0 && p.Y >= 0 && p.Z >= 0, "negative anchor");
            Assert.True(p.X + p.Size.W <= g.W && p.Y + p.Size.D <= g.D && p.Z + p.Size.H <= g.H, "out of bounds");
            Assert.True(p.Scu <= g.MaxContainerScu, "exceeds grid cap");
            Assert.Equal(p.Scu, p.Size.Volume);

            var cells = occ[p.GridId];
            for (int x = p.X; x < p.X + p.Size.W; x++)
                for (int y = p.Y; y < p.Y + p.Size.D; y++)
                    for (int z = p.Z; z < p.Z + p.Size.H; z++)
                    {
                        Assert.False(cells[x, y, z], "overlap");
                        cells[x, y, z] = true;
                    }
        }

        foreach (var p in r.Placed)
        {
            if (p.Z == 0) continue;
            var cells = occ[p.GridId];
            for (int x = p.X; x < p.X + p.Size.W; x++)
                for (int y = p.Y; y < p.Y + p.Size.D; y++)
                    Assert.True(cells[x, y, p.Z - 1], "floating box");
        }
    }
}
