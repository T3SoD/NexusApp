using System;
using System.IO;
using System.Runtime.InteropServices;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Crash recovery for WPF render thread death (UCEERR_RENDERTHREADFAILURE 0x88980406), seen in a
// user 6.2.0 field log: Star Citizen's teardown killed the GPU display state, WPF's composition
// partition zombied, and every subsequent window operation rethrew the same COMException. WPF
// cannot recover this in-process, so the app must recognize the HRESULT, skip all UI, and
// exit + relaunch - with a freshness marker so a machine that immediately fails again does not
// enter an infinite relaunch loop.
public class CrashGuardTests
{
    private static Exception RenderFailure() =>
        new COMException("UCEERR_RENDERTHREADFAILURE (0x88980406)", unchecked((int)0x88980406));

    // ── IsRenderThreadFailure: HRESULT detection across the exception chain ─────

    [Fact]
    public void DirectComExceptionWithRenderHresult_IsDetected()
    {
        Assert.True(CrashGuard.IsRenderThreadFailure(RenderFailure()));
    }

    [Fact]
    public void RenderHresultOnNonComExceptionType_IsDetected()
    {
        // WPF can surface the same failed HRESULT as InvalidOperationException or others;
        // detection must key on HResult, not on the exception type.
        var ex = new InvalidOperationException("An unspecified error occurred on the render thread.");
        ex.HResult = unchecked((int)0x88980406);
        Assert.True(CrashGuard.IsRenderThreadFailure(ex));
    }

    [Fact]
    public void WrappedRenderFailure_IsDetectedThroughInnerChain()
    {
        var wrapped = new InvalidOperationException("outer",
            new ApplicationException("middle", RenderFailure()));
        Assert.True(CrashGuard.IsRenderThreadFailure(wrapped));
    }

    [Fact]
    public void AggregateContainingRenderFailure_IsDetected()
    {
        var agg = new AggregateException(new InvalidOperationException("other"), RenderFailure());
        Assert.True(CrashGuard.IsRenderThreadFailure(agg));
    }

    [Fact]
    public void OrdinaryExceptions_AreNotDetected()
    {
        Assert.False(CrashGuard.IsRenderThreadFailure(new InvalidOperationException("boom")));
        Assert.False(CrashGuard.IsRenderThreadFailure(new COMException("other", unchecked((int)0x80004005))));
        Assert.False(CrashGuard.IsRenderThreadFailure(null));
    }

    [Fact]
    public void ZombiePartitionNotification_IsDetectedByStackFrame()
    {
        // WPF's idle-path surfacing (MediaContext.NotifyPartitionIsZombie) throws a PLAIN
        // InvalidOperationException: default HResult, no inner exception, no 0x88980406
        // anywhere. Detection must key on the throwing frame, which is invariant across
        // locales (the message string is localized; never match on it).
        Exception caught = null!;
        try { NotifyPartitionIsZombie(); }
        catch (InvalidOperationException ex) { caught = ex; }
        Assert.True(CrashGuard.IsRenderThreadFailure(caught));
    }

    // Named for the real WPF frame so the thrown exception's stack trace contains it.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void NotifyPartitionIsZombie() =>
        throw new InvalidOperationException("An unspecified error occurred on the render thread.");

    [Fact]
    public void NestedAggregates_AreTraversed()
    {
        Assert.True(CrashGuard.IsRenderThreadFailure(
            new AggregateException(new AggregateException(RenderFailure()))));
        Assert.True(CrashGuard.IsRenderThreadFailure(
            new InvalidOperationException("outer", new AggregateException(RenderFailure()))));
    }

    [Fact]
    public void ChainDeeperThanDepthCap_IsNotDetected()
    {
        // Pins the documented depth cap: a pathological 24-deep chain stops being walked.
        Exception ex = RenderFailure();
        for (int i = 0; i < 24; i++) ex = new ApplicationException($"wrap {i}", ex);
        Assert.False(CrashGuard.IsRenderThreadFailure(ex));
    }

    // ── Relaunch-loop marker: at most one auto-relaunch per freshness window ────

    [Fact]
    public void MissingMarker_IsNotFresh()
    {
        var path = TempMarkerPath();
        Assert.False(CrashGuard.IsMarkerFresh(path, DateTime.UtcNow, CrashGuard.RelaunchLoopWindow));
    }

    [Fact]
    public void JustWrittenMarker_IsFresh()
    {
        var path = TempMarkerPath();
        try
        {
            CrashGuard.WriteMarker(path);
            Assert.True(CrashGuard.IsMarkerFresh(path, DateTime.UtcNow, CrashGuard.RelaunchLoopWindow));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void MarkerOlderThanWindow_IsNotFresh()
    {
        var path = TempMarkerPath();
        try
        {
            CrashGuard.WriteMarker(path);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow - CrashGuard.RelaunchLoopWindow - TimeSpan.FromSeconds(30));
            Assert.False(CrashGuard.IsMarkerFresh(path, DateTime.UtcNow, CrashGuard.RelaunchLoopWindow));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void WriteMarker_CreatesMissingDirectory_AndOverwrites()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NexusCrashGuardTest_" + Guid.NewGuid().ToString("N"), "nested");
        var path = Path.Combine(dir, "render_relaunch.marker");
        try
        {
            CrashGuard.WriteMarker(path);
            Assert.True(File.Exists(path));

            // Re-writing (a later crash outside the window) must refresh the timestamp, not throw.
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow - TimeSpan.FromDays(1));
            CrashGuard.WriteMarker(path);
            Assert.True(CrashGuard.IsMarkerFresh(path, DateTime.UtcNow, CrashGuard.RelaunchLoopWindow));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void FutureMarkerWithinWindow_IsFresh()
    {
        // Clock skew tolerance: a marker mtime slightly in the future (NTP step between
        // crash and relaunch) must still count as fresh and suppress a relaunch loop.
        var path = TempMarkerPath();
        try
        {
            CrashGuard.WriteMarker(path);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow + TimeSpan.FromMinutes(1));
            Assert.True(CrashGuard.IsMarkerFresh(path, DateTime.UtcNow, CrashGuard.RelaunchLoopWindow));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void FutureMarkerBeyondWindow_IsNotFresh()
    {
        // A far-future mtime must NOT read as fresh forever (that would silently disable
        // auto-relaunch until the wall clock catches up).
        var path = TempMarkerPath();
        try
        {
            CrashGuard.WriteMarker(path);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow + TimeSpan.FromDays(1));
            Assert.False(CrashGuard.IsMarkerFresh(path, DateTime.UtcNow, CrashGuard.RelaunchLoopWindow));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void UnwritableMarkerPath_NeverThrows()
    {
        // The crash path must never throw. A child path under an existing FILE fails to
        // create on every machine (unlike an unmapped drive letter, which may exist on
        // some dev boxes); WriteMarker must swallow it and IsMarkerFresh read not-fresh.
        var bad = Path.Combine(typeof(CrashGuardTests).Assembly.Location, "m.marker");
        CrashGuard.WriteMarker(bad);
        Assert.False(CrashGuard.IsMarkerFresh(bad, DateTime.UtcNow, CrashGuard.RelaunchLoopWindow));
    }

    // ── ShouldRelaunch: the loop-guard decision itself ──────────────────────────

    [Fact]
    public void ShouldRelaunch_FreshMarker_SuppressesAndLeavesMarkerUntouched()
    {
        var path = TempMarkerPath();
        try
        {
            CrashGuard.WriteMarker(path);
            var stamped = File.GetLastWriteTimeUtc(path);
            Assert.False(CrashGuard.ShouldRelaunch(path, DateTime.UtcNow, CrashGuard.RelaunchLoopWindow));
            // The window stays anchored at the LAST RELAUNCH: a suppressed attempt must not
            // refresh the marker and extend its own suppression.
            Assert.Equal(stamped, File.GetLastWriteTimeUtc(path));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void ShouldRelaunch_MissingMarker_RelaunchesAndStampsMarker()
    {
        var path = TempMarkerPath();
        try
        {
            Assert.True(CrashGuard.ShouldRelaunch(path, DateTime.UtcNow, CrashGuard.RelaunchLoopWindow));
            Assert.True(CrashGuard.IsMarkerFresh(path, DateTime.UtcNow, CrashGuard.RelaunchLoopWindow));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void ShouldRelaunch_StaleMarker_RelaunchesAndRefreshesMarker()
    {
        var path = TempMarkerPath();
        try
        {
            CrashGuard.WriteMarker(path);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow - CrashGuard.RelaunchLoopWindow - TimeSpan.FromHours(1));
            Assert.True(CrashGuard.ShouldRelaunch(path, DateTime.UtcNow, CrashGuard.RelaunchLoopWindow));
            Assert.True(CrashGuard.IsMarkerFresh(path, DateTime.UtcNow, CrashGuard.RelaunchLoopWindow));
        }
        finally { Cleanup(path); }
    }

    private static string TempMarkerPath() =>
        Path.Combine(Path.GetTempPath(), "NexusCrashGuardTest_" + Guid.NewGuid().ToString("N") + ".marker");

    private static void Cleanup(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            var dir = Path.GetDirectoryName(path);
            if (dir != null && dir.Contains("NexusCrashGuardTest_")) Directory.Delete(Path.GetDirectoryName(dir)!, recursive: true);
        }
        catch { /* best-effort test cleanup */ }
    }
}
