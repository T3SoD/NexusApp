// NexusApp.Tests/ExecHangarCycleTests.cs
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

public class ExecHangarCycleTests
{
    // The embedded anchor is the instant an open phase began (4.9.0-LIVE calibration).
    private static readonly DateTime Anchor = new(2026, 7, 22, 23, 37, 42, 439, DateTimeKind.Utc);

    [Fact]
    public void AtAnchor_IsOpen_FiveGreens()
    {
        var s = ExecHangarCycle.At(Anchor, null);
        Assert.True(s.IsOpen);
        Assert.Equal(5, s.GreensLit);
        Assert.False(s.IsFinalActiveTail);
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(11, 5)]
    [InlineData(12, 4)]
    [InlineData(23, 4)]
    [InlineData(24, 3)]
    [InlineData(36, 2)]
    [InlineData(48, 1)]
    [InlineData(59, 1)]
    [InlineData(60, 0)]
    [InlineData(64, 0)]
    public void OpenWindow_LightsDegrade_In12MinuteSteps(int minutes, int expectedGreens)
    {
        var s = ExecHangarCycle.At(Anchor.AddMinutes(minutes), null);
        Assert.True(s.IsOpen);
        Assert.Equal(expectedGreens, s.GreensLit);
    }

    [Fact]
    public void FinalFiveMinutes_StillOpen_TailFlagged()
    {
        var s = ExecHangarCycle.At(Anchor.AddMinutes(62), null);
        Assert.True(s.IsOpen);
        Assert.True(s.IsFinalActiveTail);
        Assert.Equal(0, s.GreensLit);
    }

    [Fact]
    public void ExactOpenBoundary_ClosesAtOpenMs()
    {
        var lastOpenInstant = Anchor.AddMilliseconds(3_900_092);
        var firstClosedInstant = Anchor.AddMilliseconds(3_900_093);
        Assert.True(ExecHangarCycle.At(lastOpenInstant, null).IsOpen);
        Assert.False(ExecHangarCycle.At(firstClosedInstant, null).IsOpen);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(23, 0)]
    [InlineData(24, 1)]
    [InlineData(48, 2)]
    [InlineData(72, 3)]
    [InlineData(96, 4)]
    [InlineData(119, 4)]
    public void ClosedWindow_LightsRecover_In24MinuteSteps(int minutesClosed, int expectedGreens)
    {
        var s = ExecHangarCycle.At(Anchor.AddMilliseconds(3_900_093).AddMinutes(minutesClosed), null);
        Assert.False(s.IsOpen);
        Assert.Equal(expectedGreens, s.GreensLit);
    }

    [Fact]
    public void FarFuture_NoDrift_CyclePositionExact()
    {
        // 1000 whole cycles after the anchor lands exactly at an open start.
        var t = Anchor.AddMilliseconds(11_100_266L * 1000);
        var s = ExecHangarCycle.At(t, null);
        Assert.True(s.IsOpen);
        Assert.Equal(5, s.GreensLit);
        Assert.Equal(TimeSpan.FromMilliseconds(3_900_093), s.TimeToTransition);
    }

    [Fact]
    public void PreAnchor_Past_StillResolves()
    {
        // 90 minutes before the anchor sits inside the previous closed window.
        var s = ExecHangarCycle.At(Anchor.AddMinutes(-90), null);
        Assert.False(s.IsOpen);
    }

    [Fact]
    public void Override_ReplacesAnchor()
    {
        var custom = new DateTime(2026, 7, 27, 12, 0, 0, DateTimeKind.Utc);
        var s = ExecHangarCycle.At(custom.AddMinutes(5), custom);
        Assert.True(s.IsOpen);
        Assert.Equal(5, s.GreensLit);
    }

    [Fact]
    public void UpcomingOpens_ThreeEntries_SpacedOneCycle()
    {
        var s = ExecHangarCycle.At(Anchor.AddMinutes(10), null);
        Assert.Equal(3, s.UpcomingOpensUtc.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(11_100_266), s.UpcomingOpensUtc[1] - s.UpcomingOpensUtc[0]);
        Assert.Equal(TimeSpan.FromMilliseconds(11_100_266), s.UpcomingOpensUtc[2] - s.UpcomingOpensUtc[1]);
        Assert.True(s.UpcomingOpensUtc[0] > Anchor.AddMinutes(10));
    }

    [Fact]
    public void NextOpenAndClose_ConsistentWhileOpen()
    {
        var now = Anchor.AddMinutes(10);
        var s = ExecHangarCycle.At(now, null);
        Assert.Equal(now + s.TimeToTransition, s.NextCloseUtc);
        Assert.Equal(s.UpcomingOpensUtc[0], s.NextOpenUtc);
    }

    [Theory]
    [InlineData(0, 0, 9, "0m 09s")]
    [InlineData(0, 42, 10, "42m 10s")]
    [InlineData(1, 54, 10, "1h 54m 10s")]
    [InlineData(2, 0, 0, "2h 00m 00s")]
    public void FormatCountdown_HourAndMinuteForms(int h, int m, int s, string expected)
        => Assert.Equal(expected, ExecHangarCycle.FormatCountdown(new TimeSpan(h, m, s)));
}
