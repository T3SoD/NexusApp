using NexusApp.Services;
using Xunit;

public class TwoTapConfirmTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0);

    [Fact]
    public void FirstTap_ArmsWithoutExecuting()
    {
        var fired = false;
        var c = new TwoTapConfirm(TimeSpan.FromSeconds(3), () => fired = true);
        Assert.True(c.Tap(T0));
        Assert.False(fired);
        Assert.True(c.IsArmed(T0.AddSeconds(1)));
    }

    [Fact]
    public void SecondTapInsideWindow_Executes()
    {
        var fired = false;
        var c = new TwoTapConfirm(TimeSpan.FromSeconds(3), () => fired = true);
        c.Tap(T0);
        Assert.False(c.Tap(T0.AddSeconds(2)));
        Assert.True(fired);
        Assert.False(c.IsArmed(T0.AddSeconds(2)));
    }

    [Fact]
    public void SecondTapAfterWindow_RearmsInsteadOfExecuting()
    {
        var fired = false;
        var c = new TwoTapConfirm(TimeSpan.FromSeconds(3), () => fired = true);
        c.Tap(T0);
        Assert.True(c.Tap(T0.AddSeconds(4)));
        Assert.False(fired);
    }

    [Fact]
    public void IsArmed_ExpiresAfterWindow()
    {
        var c = new TwoTapConfirm(TimeSpan.FromSeconds(3), () => { });
        c.Tap(T0);
        Assert.False(c.IsArmed(T0.AddSeconds(3.5)));
    }

    [Fact]
    public void Reset_Disarms()
    {
        var fired = false;
        var c = new TwoTapConfirm(TimeSpan.FromSeconds(3), () => fired = true);
        c.Tap(T0);
        c.Reset();
        Assert.True(c.Tap(T0.AddSeconds(1)));
        Assert.False(fired);
    }
}
