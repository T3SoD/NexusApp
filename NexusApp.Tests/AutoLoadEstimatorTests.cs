using NexusApp.Models;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

public class AutoLoadEstimatorTests
{
    private static readonly AutoLoadTimeTable Table = AutoLoadTimeTable.LoadEmbedded();

    [Fact] // the real 2026-08-03 run: 1,440 SCU as 60 x 24 SCU -> 120 + 60 x 15 = 1020s
    public void PredictSeconds_RealRun_Is1020()
        => Assert.Equal(1020, AutoLoadEstimator.PredictSeconds(Table, TransactionKind.Buy,
            new[] { new CargoBoxGroup(24m, 60) }));

    [Fact]
    public void PredictSeconds_MultipleGroups_Sum()
        => Assert.Equal(120 + 2 * 18 + 4 * 8, AutoLoadEstimator.PredictSeconds(Table, TransactionKind.Buy,
            new[] { new CargoBoxGroup(32m, 2), new CargoBoxGroup(8m, 4) }));

    [Fact]
    public void PredictSeconds_UnknownSize_Null()
        => Assert.Null(AutoLoadEstimator.PredictSeconds(Table, TransactionKind.Buy,
            new[] { new CargoBoxGroup(3m, 1) }));

    [Fact]
    public void PredictSeconds_EmptyTable_Null()
        => Assert.Null(AutoLoadEstimator.PredictSeconds(
            AutoLoadTimeTable.Load(new MemoryStream("x"u8.ToArray())),
            TransactionKind.Buy, new[] { new CargoBoxGroup(24m, 60) }));

    [Fact] // 256 SCU over "8,16,24,32": 32 -> 8x18+120=264 (min); 8 -> 32x8+120=376 (max)
    public void RangeSeconds_OfferedSizes_MinMax()
        => Assert.Equal((264, 376), AutoLoadEstimator.RangeSeconds(Table, 256, "8,16,24,32"));

    [Fact] // ceil boxing: 100 SCU at 24 -> 5 boxes
    public void RangeSeconds_CeilsPartialBoxes()
        => Assert.Equal((120 + 5 * 15, 120 + 5 * 15), AutoLoadEstimator.RangeSeconds(Table, 100, "24"));

    [Fact] // no terminal data -> full 1-32 table: min 32s crates 8x18+120=264, max 1 SCU 256x2+120=632
    public void RangeSeconds_EmptySizes_FallsBackToFullTable()
        => Assert.Equal((264, 632), AutoLoadEstimator.RangeSeconds(Table, 256, ""));

    [Fact]
    public void RangeSeconds_ZeroScu_Null()
        => Assert.Null(AutoLoadEstimator.RangeSeconds(Table, 0, "8,16"));

    [Theory]
    [InlineData(264, "4m 24s")]
    [InlineData(1020, "17m 00s")]
    [InlineData(3725, "1h 02m 05s")]
    public void FormatDuration_ExecHangarShape(int seconds, string expected)
        => Assert.Equal(expected, AutoLoadEstimator.FormatDuration(seconds));

    [Fact]
    public void FormatRange_Renders()
        => Assert.Equal("4m 24s - 6m 16s", AutoLoadEstimator.FormatRange(Table, 256, "8,16,24,32"));
}
