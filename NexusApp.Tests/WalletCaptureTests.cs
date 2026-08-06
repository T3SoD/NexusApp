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
        public long? Estimate;
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
                delay: ts => { Delays.Add(ts); Now += ts; return Task.CompletedTask; },
                currentEstimate: () => Estimate);
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
        // settle 250 ms + one grab spacing 300 ms (owner, 2026-08-06: faster than the original
        // 2 s feel), measured on the injected clock, applied to the trigger line's own stamp:
        // the anchor never touches the wall clock.
        Assert.Equal(TriggerUtc + TimeSpan.FromMilliseconds(550), hit.CaptureUtc);
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
    public void AllDisagreeingGrabsTimeOut()
    {
        var h = new Harness();
        h.Grabs.Enqueue("1,111");
        h.Grabs.Enqueue("2,222");
        h.Grabs.Enqueue("3,333");
        h.Grabs.Enqueue("4,444");
        h.Trigger();

        Assert.Empty(h.Captured);
        Assert.Equal("timeout", h.Cap.LastOutcome);
    }

    // The extra grabs exist so the faster cadence still spans the mobiGlas boot animation: a
    // value from grab 1 confirming at the last grab must succeed.
    [Fact]
    public void SixthGrabCanStillConfirm()
    {
        var h = new Harness();
        foreach (var t in new[] { "5,230,346", "1,111", "2,222", "3,333", "4,444", "5,230,346" })
            h.Grabs.Enqueue(t);
        h.Trigger();

        var hit = Assert.Single(h.Captured);
        Assert.Equal(5230346, hit.Balance);
    }

    // Speed: a single read that exactly matches the current estimate confirms instantly - it
    // agrees with what the tracker already believes, so a second grab adds nothing.
    [Fact]
    public void InstantConfirmWhenTheGrabMatchesTheEstimate()
    {
        var h = new Harness { Estimate = 5230346 };
        h.Grabs.Enqueue("5,230,346");
        h.Trigger();

        var hit = Assert.Single(h.Captured);
        Assert.Equal(5230346, hit.Balance);
        // settle only: no grab spacing was ever awaited
        Assert.Equal(TriggerUtc + WalletCapture.SettleDelay, hit.CaptureUtc);
        Assert.Single(h.Delays);
    }

    [Fact]
    public void MismatchedEstimateStillNeedsAgreement()
    {
        var h = new Harness { Estimate = 999 };
        h.Grabs.Enqueue("5,230,346");
        h.Grabs.Enqueue("5,230,346");
        h.Trigger();

        var hit = Assert.Single(h.Captured);
        Assert.Equal(5230346, hit.Balance);
        Assert.Equal(2, h.Delays.Count); // settle + one spacing: the normal two-read path
    }

    // Live 17:01: a correct single read sat unconfirmed for 3 s because the balance had moved
    // by 240 since the anchor and the animation starved the partner read. A single vetted read
    // within the tolerance band of the estimate confirms immediately; only large moves demand
    // the second opinion.
    [Fact]
    public void SingleReadWithinToleranceConfirmsImmediately()
    {
        var h = new Harness { Estimate = 5_101_183 };
        h.Grabs.Enqueue("5,100,943"); // 240 under the estimate, inside the band
        h.Trigger();

        var hit = Assert.Single(h.Captured);
        Assert.Equal(5100943, hit.Balance);
        Assert.Single(h.Delays); // settle only
    }

    [Fact]
    public void ALargeMoveStillDemandsASecondRead()
    {
        var h = new Harness { Estimate = 5_101_183 };
        h.Grabs.Enqueue("4,000,000"); // far outside the band
        h.Grabs.Enqueue("4,000,000");
        h.Trigger();

        var hit = Assert.Single(h.Captured);
        Assert.Equal(4000000, hit.Balance);
        Assert.Equal(2, h.Delays.Count);
    }

    // Live evidence 16:15: a cold mobiGlas boot takes over a second before the balance renders,
    // so the first readable grab lands around grab 5-6 and needs a partner AFTER that. The
    // burst must keep grabbing to its time budget, not a small attempt count.
    [Fact]
    public void ALateFirstReadStillGetsItsPartner()
    {
        var h = new Harness();
        foreach (var t in new string?[] { null, null, null, null, null, "846", "846" })
            h.Grabs.Enqueue(t);
        h.Trigger();

        var hit = Assert.Single(h.Captured);
        Assert.Equal(846, hit.Balance);
    }

    // Speed: an unreadable grab retries fast; the boot animation resolves in fractions of the
    // normal spacing.
    [Fact]
    public void EmptyGrabsRetryFast()
    {
        var h = new Harness();
        h.Grabs.Enqueue(null);
        h.Grabs.Enqueue("846");
        h.Grabs.Enqueue("846");
        h.Trigger();

        Assert.Single(h.Captured);
        Assert.Contains(WalletCapture.RetrySpacing, h.Delays);
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
