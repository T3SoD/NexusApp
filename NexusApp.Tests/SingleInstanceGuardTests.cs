using System;
using System.Threading;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Issue #29: detect a second Nexus launch and hand control back to the already-running instance
// instead of opening a duplicate main window. SingleInstanceGuard's mutex/event plumbing has zero
// WPF dependency, so every scenario below runs headless. Each test claims its own GUID-suffixed
// mutex/event names so parallel test runs never collide on the same named kernel objects.
public class SingleInstanceGuardTests
{
    private static (string mutexName, string eventName) UniqueNames()
    {
        var id = Guid.NewGuid().ToString("N");
        return ($"Local\\NexusApp.Test.{id}.Mutex", $"Local\\NexusApp.Test.{id}.Activate");
    }

    // A named Mutex is thread-affine: a thread that already owns it can call WaitOne again and
    // succeed by recursion, even through a second Mutex/SingleInstanceGuard object. That reentrancy
    // is harmless in production (a second instance is a different OS process with a different main
    // thread), but it means a same-thread "second acquire" in a test would wrongly succeed. Running
    // the second guard's acquire on its own thread genuinely exercises cross-instance contention.
    private static bool AcquireOnNewThread(SingleInstanceGuard guard)
    {
        bool acquired = false;
        var t = new Thread(() => acquired = guard.TryAcquirePrimary());
        t.Start();
        t.Join();
        return acquired;
    }

    [Fact]
    public void TryAcquirePrimary_FirstGuard_Succeeds()
    {
        var (mutexName, eventName) = UniqueNames();
        using var guard = new SingleInstanceGuard(mutexName, eventName);

        Assert.True(guard.TryAcquirePrimary());
    }

    [Fact]
    public void TryAcquirePrimary_SecondGuardSameNames_Fails()
    {
        var (mutexName, eventName) = UniqueNames();
        using var first = new SingleInstanceGuard(mutexName, eventName);
        Assert.True(first.TryAcquirePrimary());

        using var second = new SingleInstanceGuard(mutexName, eventName);
        Assert.False(AcquireOnNewThread(second));
    }

    [Fact]
    public void TryAcquirePrimary_DifferentNames_BothSucceed()
    {
        // Proves the two single-instance domains (live vs demo profile) genuinely do not collide -
        // this is what stops the Admin demo-profile launcher (DemoProfile.StartDemoInstance) from
        // being mistaken for a duplicate live launch and shut down.
        var (mutexA, eventA) = UniqueNames();
        var (mutexB, eventB) = UniqueNames();
        using var guardA = new SingleInstanceGuard(mutexA, eventA);
        using var guardB = new SingleInstanceGuard(mutexB, eventB);

        Assert.True(guardA.TryAcquirePrimary());
        Assert.True(guardB.TryAcquirePrimary());
    }

    [Fact]
    public void SignalPrimary_WakesActivationListener()
    {
        var (mutexName, eventName) = UniqueNames();
        using var primary = new SingleInstanceGuard(mutexName, eventName);
        Assert.True(primary.TryAcquirePrimary());

        using var activated = new ManualResetEventSlim(false);
        primary.StartActivationListener(() => activated.Set());

        using var secondary = new SingleInstanceGuard(mutexName, eventName);
        Assert.False(AcquireOnNewThread(secondary));   // confirms this really is a second instance
        secondary.SignalPrimary();

        // Bounded wait: a listener that never fires must fail the test instead of hanging the suite.
        Assert.True(activated.Wait(TimeSpan.FromSeconds(5)),
            "activation callback did not fire within the bounded wait");
    }

    [Fact]
    public void StartActivationListener_MultipleSignals_FiresEachTime()
    {
        var (mutexName, eventName) = UniqueNames();
        using var primary = new SingleInstanceGuard(mutexName, eventName);
        Assert.True(primary.TryAcquirePrimary());

        var fireCount = 0;
        using var gate = new ManualResetEventSlim(false);
        primary.StartActivationListener(() =>
        {
            Interlocked.Increment(ref fireCount);
            gate.Set();
        });

        using (var secondary1 = new SingleInstanceGuard(mutexName, eventName))
            secondary1.SignalPrimary();
        Assert.True(gate.Wait(TimeSpan.FromSeconds(5)), "first signal did not fire");
        gate.Reset();

        using (var secondary2 = new SingleInstanceGuard(mutexName, eventName))
            secondary2.SignalPrimary();
        Assert.True(gate.Wait(TimeSpan.FromSeconds(5)), "second signal did not fire");

        Assert.Equal(2, fireCount);
    }

    [Fact]
    public void Dispose_ReleasesMutex_SoAFreshAcquireSucceeds()
    {
        var (mutexName, eventName) = UniqueNames();
        var first = new SingleInstanceGuard(mutexName, eventName);
        Assert.True(first.TryAcquirePrimary());
        first.Dispose();

        using var second = new SingleInstanceGuard(mutexName, eventName);
        Assert.True(second.TryAcquirePrimary());
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        var (mutexName, eventName) = UniqueNames();
        var guard = new SingleInstanceGuard(mutexName, eventName);
        guard.TryAcquirePrimary();
        guard.StartActivationListener(() => { });

        guard.Dispose();
        var ex = Record.Exception(() => guard.Dispose());
        Assert.Null(ex);
    }

    [Fact]
    public void Dispose_OnSecondaryGuard_NeverOwnedMutex_DoesNotThrow()
    {
        var (mutexName, eventName) = UniqueNames();
        using var primary = new SingleInstanceGuard(mutexName, eventName);
        Assert.True(primary.TryAcquirePrimary());

        var secondary = new SingleInstanceGuard(mutexName, eventName);
        Assert.False(AcquireOnNewThread(secondary));

        // A secondary never owns the mutex, so Dispose must not attempt (or fail on) a release.
        var ex = Record.Exception(() => secondary.Dispose());
        Assert.Null(ex);
    }

    [Fact]
    public void TryAcquirePrimary_AfterAbandonedMutex_StillSucceeds()
    {
        var (mutexName, eventName) = UniqueNames();

        // Keeps the named mutex object alive for the whole test: the crashed thread's leaked
        // handle is otherwise its only reference, and a finalization between Join and the acquire
        // below would destroy the kernel object and silently downgrade this test to a plain
        // clean-mutex acquire.
        using var keepAlive = new Mutex(initiallyOwned: false, mutexName);

        // Simulate a crashed previous instance: a thread claims the mutex and dies without ever
        // releasing it. Windows marks the mutex abandoned when the thread terminates.
        var crashed = new Thread(() =>
        {
            var m = new Mutex(initiallyOwned: false, mutexName);
            m.WaitOne();
            // Deliberately no ReleaseMutex - the thread just ends, abandoning ownership.
        });
        crashed.Start();
        crashed.Join();

        // Join returns when the thread's MANAGED code finishes, but the kernel only marks the
        // mutex abandoned when the NATIVE thread finishes terminating. Under full-suite CPU load
        // that window stretches past the guard's zero-timeout acquire, which then reads the mutex
        // as held by a live instance - measured at 4-in-2000 on a saturated machine, and the
        // source of this test's long flake history. So: bounded retry until the abandonment
        // becomes visible (2 attempts sufficed in 4000 loaded runs; the deadline is headroom).
        // AbandonedMutexException still means WE now hold it (WaitOne grants ownership before it
        // throws) - the same tolerance that keeps the portable self-swap relaunch path safe.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        bool acquired = false;
        while (!acquired)
        {
            var guard = new SingleInstanceGuard(mutexName, eventName);
            acquired = guard.TryAcquirePrimary();
            guard.Dispose();
            if (!acquired)
            {
                if (DateTime.UtcNow > deadline) break;
                Thread.Sleep(5);
            }
        }
        Assert.True(acquired, "the abandoned mutex never became acquirable within the deadline");
    }

    [Fact]
    public void StartActivationListener_WhenNotPrimary_IsANoOp()
    {
        var (mutexName, eventName) = UniqueNames();
        using var primary = new SingleInstanceGuard(mutexName, eventName);
        Assert.True(primary.TryAcquirePrimary());

        using var secondary = new SingleInstanceGuard(mutexName, eventName);
        Assert.False(AcquireOnNewThread(secondary));

        // Must not throw or block, and must not somehow steal the primary's activation callback.
        var ex = Record.Exception(() => secondary.StartActivationListener(() =>
            throw new InvalidOperationException("a non-primary listener must never fire")));
        Assert.Null(ex);
    }

    [Fact]
    public void DefaultConstructor_UsesProductionNames()
    {
        // Pins the production defaults so a future rename is a deliberate, visible diff rather than
        // a silent behavior change (App.xaml.cs relies on these constants when it appends the
        // profile-scoped suffix for the live vs demo single-instance domains).
        Assert.Equal("Local\\NexusApp.SingleInstance", SingleInstanceGuard.DefaultMutexName);
        Assert.Equal("Local\\NexusApp.Activate", SingleInstanceGuard.DefaultActivateEventName);
    }
}
