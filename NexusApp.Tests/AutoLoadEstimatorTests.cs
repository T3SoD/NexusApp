using NexusApp.Models;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

public class AutoLoadEstimatorTests
{
    private static readonly AutoLoadTimeTable Table = AutoLoadTimeTable.LoadEmbedded();

    [Fact] // the real 2026-08-03 run, recalibrated: 1,440 SCU as 60 x 24 SCU -> 72 + 60 x 9.0 = 612s
    public void PredictSeconds_RealRun_Is612()
        => Assert.Equal(612, AutoLoadEstimator.PredictSeconds(Table, TransactionKind.Buy,
            new[] { new CargoBoxGroup(24m, 60) }));

    [Fact] // 72 + 2 x 10.8 + 4 x 4.8 = 112.8, rounds to 113
    public void PredictSeconds_MultipleGroups_Sum()
        => Assert.Equal(113, AutoLoadEstimator.PredictSeconds(Table, TransactionKind.Buy,
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

    [Fact] // 256 SCU over "8,16,24,32": 32 -> 8x10.8+72=158.4->158 (min); 8 -> 32x4.8+72=225.6->226 (max)
    public void RangeSeconds_OfferedSizes_MinMax()
        => Assert.Equal((158, 226), AutoLoadEstimator.RangeSeconds(Table, 256, "8,16,24,32"));

    [Fact] // ceil boxing: 100 SCU at 24 -> 5 boxes, 72 + 5x9.0 = 117
    public void RangeSeconds_CeilsPartialBoxes()
        => Assert.Equal((117, 117), AutoLoadEstimator.RangeSeconds(Table, 100, "24"));

    [Fact] // no terminal data -> full 1-32 table: min 32s crates 8x10.8+72=158, max 1 SCU 256x1.2+72=379.2->379
    public void RangeSeconds_EmptySizes_FallsBackToFullTable()
        => Assert.Equal((158, 379), AutoLoadEstimator.RangeSeconds(Table, 256, ""));

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
        => Assert.Equal("2m 38s - 3m 46s", AutoLoadEstimator.FormatRange(Table, 256, "8,16,24,32"));
}
