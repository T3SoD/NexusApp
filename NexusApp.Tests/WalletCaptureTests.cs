using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

public class WalletCaptureTests
{
    // Every seam injected: capture dequeues scripted OCR text, delay resolves instantly while
    // advancing the fake clock, so a whole burst completes synchronously inside OnLine.
    private sealed class Harness
    {
        public DateTime Now = new(2026, 8, 6, 4, 26, 40, DateTimeKind.Utc);
        public List<TimeSpan> Delays = new();
        public Queue<string?> Grabs = new();
        public bool CanCapture = true;
        public Func<string?, string?>? OnGrab;
        public List<(long Balance, DateTime TriggerUtc, DateTime CaptureUtc)> Captured = new();
        public WalletCapture Cap;

        public Harness()
        {
            Cap = new WalletCapture(
                capture: () =>
                {
                    var text = Grabs.Count > 0 ? Grabs.Dequeue() : null;
                    if (OnGrab != null) text = OnGrab(text);
                    return Task.FromResult(text);
                },
                canCapture: () => CanCapture,
                utcNow: () => Now,
                delay: ts => { Delays.Add(ts); Now += ts; return Task.CompletedTask; });
            Cap.BalanceCaptured += (b, t, c) => Captured.Add((b, t, c));
        }

        public void Trigger(bool replay = false) => Cap.OnLine(new GameLogEntry
        {
            Raw = WalletLogFixtures.TriggerLine,
            Category = LogCategory.Other,
            IsReplay = replay,
        });
    }

    private static readonly DateTime TriggerUtc = new(2026, 8, 6, 0, 26, 37, 290, DateTimeKind.Utc);

    [Fact]
    public void ConfirmsOnTwoAgreeingGrabsInTheLogClockDomain()
    {
        var h = new Harness();
        h.Grabs.Enqueue("5,230,346 aUEC");
        h.Grabs.Enqueue("5,230,346");
        h.Trigger();

        var hit = Assert.Single(h.Captured);
        Assert.Equal(5230346, hit.Balance);
        Assert.Equal(TriggerUtc, hit.TriggerUtc);
        // settle 500 ms + one grab spacing 500 ms, measured on the injected clock, applied to
        // the trigger line's own stamp: the anchor never touches the wall clock.
        Assert.Equal(TriggerUtc + TimeSpan.FromMilliseconds(1000), hit.CaptureUtc);
        Assert.Equal("confirmed", h.Cap.LastOutcome);
    }

    [Fact]
    public void AgreementNeedNotBeConsecutive()
    {
        var h = new Harness();
        h.Grabs.Enqueue("5,230,346");
        h.Grabs.Enqueue("7,777");
        h.Grabs.Enqueue("5,230,346");
        h.Trigger();

        var hit = Assert.Single(h.Captured);
        Assert.Equal(5230346, hit.Balance);
    }

    [Fact]
    public void ThreeDisagreeingGrabsTimeOut()
    {
        var h = new Harness();
        h.Grabs.Enqueue("1,111");
        h.Grabs.Enqueue("2,222");
        h.Grabs.Enqueue("3,333");
        h.Trigger();

        Assert.Empty(h.Captured);
        Assert.Equal("timeout", h.Cap.LastOutcome);
    }

    [Fact]
    public void UnreadableTextIsAFailedGrabNotAnAbort()
    {
        var h = new Harness();
        h.Grabs.Enqueue(null);
        h.Grabs.Enqueue("846");
        h.Grabs.Enqueue("846");
        h.Trigger();

        var hit = Assert.Single(h.Captured);
        Assert.Equal(846, hit.Balance);
    }

    [Fact]
    public void FocusLossMidBurstAborts()
    {
        var h = new Harness();
        h.Grabs.Enqueue("5,230,346");
        h.Grabs.Enqueue("5,230,346");
        h.OnGrab = text => { h.CanCapture = false; return text; };
        h.Trigger();

        Assert.Empty(h.Captured);
        Assert.Equal("aborted", h.Cap.LastOutcome);
    }

    [Fact]
    public void ReplayedTriggerLineNeverFires()
    {
        var h = new Harness();
        h.Grabs.Enqueue("5,230,346");
        h.Trigger(replay: true);

        Assert.Empty(h.Captured);
        Assert.Empty(h.Delays);
        Assert.Null(h.Cap.LastOutcome);
    }

    [Fact]
    public void GatedWhenCaptureIsUnavailable()
    {
        var h = new Harness { CanCapture = false };
        h.Trigger();

        Assert.Empty(h.Captured);
        Assert.Empty(h.Delays);
        Assert.Equal("gated", h.Cap.LastOutcome);
    }

    [Fact]
    public void OverlappingTriggerIsIgnoredWhileBusy()
    {
        var h = new Harness();
        h.Grabs.Enqueue("5,230,346");
        h.Grabs.Enqueue("5,230,346");
        var reentered = false;
        h.OnGrab = text =>
        {
            if (!reentered)
            {
                reentered = true;
                h.Trigger(); // a second mobiGlas open mid-burst
            }
            return text;
        };
        h.Trigger();

        Assert.Single(h.Captured);
        // one settle + one spacing; the ignored trigger added no delays of its own
        Assert.Equal(2, h.Delays.Count);
    }

    [Fact]
    public void BudgetExhaustionStopsTheBurst()
    {
        var h = new Harness();
        h.Grabs.Enqueue("1,111");
        h.Grabs.Enqueue("2,222");
        h.Grabs.Enqueue("1,111"); // would confirm, but the budget dies first
        h.OnGrab = text => { h.Now += TimeSpan.FromSeconds(3); return text; }; // slow OCR
        h.Trigger();

        Assert.Empty(h.Captured);
        Assert.Equal("timeout", h.Cap.LastOutcome);
    }

    [Fact]
    public void NonTriggerLinesAreIgnored()
    {
        var h = new Harness();
        h.Cap.OnLine(new GameLogEntry { Raw = CommodityLogFixtures.BuyLine, Category = LogCategory.Other });
        h.Cap.OnLine(new GameLogEntry { Raw = WalletLogFixtures.NoisyTwinLine, Category = LogCategory.Other });

        Assert.Empty(h.Delays);
        Assert.Null(h.Cap.LastOutcome);
    }
}
