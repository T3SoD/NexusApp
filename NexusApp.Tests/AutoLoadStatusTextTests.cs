using NexusApp.Models;
using NexusApp.Services;
using NexusApp.Views;
using Xunit;

namespace NexusApp.Tests;

public class AutoLoadStatusTextTests
{
    private static readonly DateTime T0 = new(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);
    private static readonly AutoLoadTimeTable Live = AutoLoadTimeTable.LoadEmbedded();   // calibrated false

    private static AutoLoadEntry Entry(TransactionKind kind = TransactionKind.Buy, int? predicted = 1020)
        => new(T0, kind, "SCShop_Admin", 1440m, new[] { new CargoBoxGroup(24m, 60) }, predicted);

    [Fact]
    public void StripWord_LoadAndUnload()
    {
        Assert.Equal("AUTO-LOAD", AutoLoadStatusText.StripWord(Entry()));
        Assert.Equal("AUTO-UNLOAD", AutoLoadStatusText.StripWord(Entry(TransactionKind.Sell)));
    }

    [Fact]
    public void Elapsed_FormatsFromLogStamp()
        => Assert.Equal("12m 33s", AutoLoadStatusText.Elapsed(Entry(), T0.AddSeconds(753)));

    [Fact]
    public void OverflowCount_NullUntilTwo()
    {
        Assert.Null(AutoLoadStatusText.OverflowCount(1));
        Assert.Equal("+1", AutoLoadStatusText.OverflowCount(2));
    }

    [Fact]
    public void CargoLine_ScuAndMix()
        => Assert.Equal("1,440 SCU - 60 x 24 SCU", AutoLoadStatusText.CargoLine(Entry()));

    [Fact]
    public void EstLine_NullWhileUncalibrated()
        => Assert.Null(AutoLoadStatusText.EstLine(Entry(), Live));

    [Fact]
    public void IsOver_FalseWhileUncalibrated()
        => Assert.False(AutoLoadStatusText.IsOver(Entry(), Live, T0.AddHours(1)));
}
