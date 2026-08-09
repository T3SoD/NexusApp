using NexusApp.Models;
using NexusApp.Services;
using NexusApp.Views;
using Xunit;

namespace NexusApp.Tests;

public class AutoLoadStatusTextTests
{
    private static readonly DateTime T0 = new(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);
    private static readonly AutoLoadTimeTable Dark = AutoLoadTimeTable.Load(new MemoryStream("x"u8.ToArray()));   // calibrated false
    private static readonly AutoLoadTimeTable Lit = AutoLoadTimeTable.LoadEmbedded();   // calibrated true

    private static AutoLoadEntry Entry(TransactionKind kind = TransactionKind.Buy, int? predicted = 1020,
                                       string? commodityName = null, string? placeLabel = null,
                                       bool placeIsArea = false)
        => new(T0, kind, "SCShop_Admin", commodityName, placeLabel, placeIsArea, 1440m,
            new[] { new CargoBoxGroup(24m, 60) }, predicted);

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
        => Assert.Null(AutoLoadStatusText.EstLine(Entry(), Dark));

    [Fact]
    public void IsOver_FalseWhileUncalibrated()
        => Assert.False(AutoLoadStatusText.IsOver(Entry(), Dark, T0.AddHours(1)));

    // Recorded release condition: the embedded table shipped calibrated true from
    // 2026-08-08, live-kiosk-derived (72s base + 9.0s/24-SCU crate).
    [Fact]
    public void EstLine_RendersWhenCalibrated()
        => Assert.Equal("est 10m 12s", AutoLoadStatusText.EstLine(Entry(predicted: 612), Lit));

    [Fact]
    public void IsOver_TrueWhenPastPrediction()
    {
        var e = Entry(predicted: 612);
        Assert.False(AutoLoadStatusText.IsOver(e, Lit, T0.AddSeconds(611)));
        Assert.True(AutoLoadStatusText.IsOver(e, Lit, T0.AddSeconds(613)));
    }

    [Fact]
    public void Clock_CountsDownFromPrediction()
        => Assert.Equal("9m 19s", AutoLoadStatusText.Clock(Entry(predicted: 612), Lit, T0.AddSeconds(53)));

    [Fact]
    public void Clock_HoldsAtZeroPastPrediction()
        => Assert.Equal("0m 00s", AutoLoadStatusText.Clock(Entry(predicted: 612), Lit, T0.AddSeconds(700)));

    [Fact]
    public void Clock_FallsBackToElapsedWhenDark()
        => Assert.Equal("0m 53s", AutoLoadStatusText.Clock(Entry(predicted: 612), Dark, T0.AddSeconds(53)));

    [Fact]
    public void CargoLine_JoinsMultipleGroups()
    {
        var e = new AutoLoadEntry(T0, TransactionKind.Buy, "SCShop_Admin", null, null, false, 1472m,
            new[] { new CargoBoxGroup(24m, 60), new CargoBoxGroup(8m, 4) }, 612);
        Assert.Equal("1,472 SCU - 60 x 24 SCU + 4 x 8 SCU", AutoLoadStatusText.CargoLine(e));
    }

    [Fact]
    public void Title_UsesCommodityThenFallsBack()
    {
        Assert.Equal("Laranite", AutoLoadStatusText.Title(Entry(commodityName: "Laranite")));
        Assert.Equal("AUTO-LOAD", AutoLoadStatusText.Title(Entry()));
    }

    // ProfitDisplay.WhereText prefers the player's stamped place over the shop token
    // (string.IsNullOrWhiteSpace(placeLabel) ? ShopLabel(shopName) : ...). A non-area place
    // renders as-is; a null place falls back to ProfitDisplay.ShopLabel("SCShop_RestStop_Pharmacy-001")
    // ("RestStop Pharmacy" - the same mapping WalletDisplayTests.ShopDisplayName_StripsThePrefixAndInstanceSuffix
    // already proves for that token).
    [Fact]
    public void Location_UsesStampedPlaceThenFallsBackToShop()
    {
        var withPlace = new AutoLoadEntry(T0, TransactionKind.Buy, "SCShop_RestStop_Pharmacy-001", null,
            "Stanton Gateway", false, 1440m, new[] { new CargoBoxGroup(24m, 60) }, 1020);
        Assert.Equal("Stanton Gateway", AutoLoadStatusText.Location(withPlace));

        var noPlace = new AutoLoadEntry(T0, TransactionKind.Buy, "SCShop_RestStop_Pharmacy-001", null,
            null, false, 1440m, new[] { new CargoBoxGroup(24m, 60) }, 1020);
        Assert.Equal("RestStop Pharmacy", AutoLoadStatusText.Location(noPlace));
    }

    [Fact]
    public void Progress_QuartersThenClampsThenNull()
    {
        var e = Entry(predicted: 612);
        Assert.Equal(0.25, AutoLoadStatusText.Progress(e, Lit, T0.AddSeconds(153)));   // 153 / 612 exactly
        Assert.Equal(1.0, AutoLoadStatusText.Progress(e, Lit, T0.AddSeconds(700)));    // past prediction, clamped
        Assert.Null(AutoLoadStatusText.Progress(e, Dark, T0.AddSeconds(153)));         // uncalibrated hides the bar
    }
}
