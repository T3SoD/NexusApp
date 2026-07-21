using System;
using System.IO;
using System.Threading;

namespace NexusApp.Services;

/// <summary>
/// Recovery for WPF render thread death (UCEERR_RENDERTHREADFAILURE 0x88980406), seen in the
/// field when Star Citizen's GPU teardown (crash or quit) kills the display state out from
/// under Nexus. Once WPF's composition partition zombies it cannot be recovered in-process,
/// and every subsequent window move/show/hide/focus rethrows - so the only safe response is:
/// no WPF UI, log, exit the process, relaunch once. A file marker caps auto-relaunch at one
/// per <see cref="RelaunchLoopWindow"/> so a machine that fails again immediately (driver
/// still resetting) does not enter an infinite relaunch loop.
/// </summary>
public static class CrashGuard
{
    private const int RenderThreadFailureHresult = unchecked((int)0x88980406);

    /// <summary>Argument passed to the relaunched instance so startup can log why it was started.</summary>
    public const string RelaunchArg = "--render-relaunch";

    /// <summary>A relaunch marker younger than this suppresses further auto-relaunches (loop guard).</summary>
    public static readonly TimeSpan RelaunchLoopWindow = TimeSpan.FromMinutes(2);

    /// <summary>Marker recording the last auto-relaunch, next to settings.json (file mtime is the clock).</summary>
    public static string DefaultMarkerPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NexusApp", "render_relaunch.marker");

    /// <summary>
    /// True when the exception (or anything in its inner/aggregate chain) is a render thread
    /// death. Two real surfacings exist: MIL channel calls throw COMException carrying HRESULT
    /// 0x88980406 (via Marshal.ThrowExceptionForHR in SyncFlush/UpdateWindowSettings), while
    /// the idle-path zombie notification (MediaContext.NotifyPartitionIsZombie) throws a PLAIN
    /// InvalidOperationException or OutOfMemoryException with the default HResult - for that
    /// shape only the throwing frame identifies it (the message is localized; never match it).
    /// </summary>
    public static bool IsRenderThreadFailure(Exception? ex)
    {
        // Inner chains are fixed at construction so they cannot cycle; a depth cap is still
        // cheap insurance against pathological hand-built chains.
        return Check(ex, depth: 0);

        static bool Check(Exception? e, int depth)
        {
            if (e is null || depth > 20) return false;
            if (e.HResult == RenderThreadFailureHresult) return true;
            if (e is InvalidOperationException or OutOfMemoryException
                && e.StackTrace?.Contains("NotifyPartitionIsZombie") == true) return true;
            if (e is AggregateException agg)
            {
                foreach (var inner in agg.InnerExceptions)
                    if (Check(inner, depth + 1)) return true;
            }
            return Check(e.InnerException, depth + 1);
        }
    }

    /// <summary>Stamp (or refresh) the relaunch marker. Crash path: never throws.</summary>
    public static void WriteMarker(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, DateTime.UtcNow.ToString("o"));
        }
        catch { /* crash path must never throw */ }
    }

    /// <summary>True when the marker exists and its last write is within the window. Never throws.</summary>
    public static bool IsMarkerFresh(string path, DateTime nowUtc, TimeSpan window)
    {
        try
        {
            if (!File.Exists(path)) return false;
            // File mtime is the clock (no content parsing); the abs() tolerates minor clock skew.
            var age = nowUtc - File.GetLastWriteTimeUtc(path);
            return TimeSpan.FromTicks(Math.Abs(age.Ticks)) <= window;
        }
        catch { return false; }
    }

    /// <summary>
    /// The loop-guard decision: false when a relaunch already happened within the window
    /// (and the marker is left untouched, so the window stays anchored at the last relaunch);
    /// true otherwise, stamping the marker for the relaunch the caller is about to perform.
    /// </summary>
    internal static bool ShouldRelaunch(string markerPath, DateTime nowUtc, TimeSpan window)
    {
        if (IsMarkerFresh(markerPath, nowUtc, window)) return false;
        WriteMarker(markerPath);
        return true;
    }

    /// <summary>
    /// Terminal handler for a detected render thread failure: log, relaunch unless a relaunch
    /// already happened within <see cref="RelaunchLoopWindow"/>, then hard-exit. Never touches
    /// WPF UI (any dispatcher-pumped dialog rethrows on the dead composition channel); the
    /// repeat-failure branch may still inform the user via the native off-thread dialog, which
    /// never enters the WPF pipeline. Environment.Exit is deliberate: orderly WPF shutdown
    /// re-touches the dead render thread.
    /// </summary>
    public static void ExitForRenderThreadFailure()
    {
        try
        {
            if (ShouldRelaunch(DefaultMarkerPath, DateTime.UtcNow, RelaunchLoopWindow))
            {
                // Log BEFORE spawning: if Process.Start throws, the log must still explain
                // why the process exited (the catch below would otherwise swallow the story).
                Logger.Error("[WIN] render thread failure (0x88980406): display connection lost (usually the game crashing or quitting) - relaunching Nexus");
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe))
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { ArgumentList = { RelaunchArg } });
            }
            else
            {
                Logger.Error("[WIN] render thread failed again within the relaunch window - exiting without relaunch");
                ShowCrashMessageAndExit(
                    "Nexus lost its connection to the display twice in a row. This usually happens " +
                    "while Star Citizen or its graphics driver is crashing.\n\n" +
                    "Close the game, then start Nexus again. If this keeps happening, turn on " +
                    "CPU rendering in Settings (cog) > Diagnostics.\n\n" +
                    "Details have been saved to:\n%AppData%\\NexusApp\\logs\\nexus.log");
            }
        }
        catch { /* crash path must never throw */ }
        Environment.Exit(1);
    }

    /// <summary>
    /// Terminal handler for an ordinary unhandled exception: show the crash message via the raw
    /// Win32 MessageBox on a separate thread (never the dispatcher - its pump re-enters handlers
    /// and can be suspended, which is what killed 6.2.0 in the field), then hard-exit.
    /// </summary>
    public static void ShowCrashMessageAndExit(string message)
    {
        try
        {
            var t = new Thread(() =>
            {
                try { NativeMessageBox(message); } catch { /* crash path must never throw */ }
            })
            { IsBackground = true, Name = "NexusCrashDialog" };
            t.Start();
            // Wait on the (broken) calling thread without running a full message pump. On an
            // STA thread the CLR's Join can still dispatch incoming COM calls and cross-thread
            // SENT messages, but not the POSTED messages the dead composition channel uses, so
            // the WPF pipeline is never re-entered. Give the user two minutes to read the
            // dialog, then exit regardless so a wedged session cannot linger forever.
            t.Join(TimeSpan.FromMinutes(2));
        }
        catch { /* crash path must never throw */ }
        Environment.Exit(1);
    }

    private static void NativeMessageBox(string message)
    {
        const uint MB_OK = 0x0, MB_ICONERROR = 0x10, MB_SETFOREGROUND = 0x10000, MB_TOPMOST = 0x40000;
        _ = MessageBoxW(IntPtr.Zero, message, "Nexus - Unexpected Error",
            MB_OK | MB_ICONERROR | MB_SETFOREGROUND | MB_TOPMOST);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
