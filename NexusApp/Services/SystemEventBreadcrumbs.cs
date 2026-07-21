using System.Threading;
using Microsoft.Win32;

namespace NexusApp.Services;

/// <summary>
/// [WIN] breadcrumbs for display, power and session transitions - the trigger class for WPF
/// render thread failures (see <see cref="CrashGuard"/>) and for overlay-position bug triage.
/// Subscribed for the app's lifetime; handlers are Logger-only and run on the SystemEvents
/// broadcast thread (see Start for how that is guaranteed). ABSENCE of these lines proves
/// nothing: borderless fullscreen flips and driver TDR resets do not reliably raise any of
/// these events.
/// </summary>
public static class SystemEventBreadcrumbs
{
    public static void Start()
    {
        // SystemEvents captures the subscriber's SynchronizationContext and Sends each event
        // to it. Subscribing from OnStartup would therefore marshal every handler onto the WPF
        // dispatcher - which is blocked (CrashGuard's non-pumping Join) or already exiting in
        // the exact crash scenarios these lines exist to explain, so the breadcrumb would never
        // be written. Clear the context for the subscription so handlers run directly on the
        // SystemEvents broadcast thread; Logger is lock-guarded and safe there.
        var prev = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            SystemEvents.DisplaySettingsChanged += (_, _) =>
                Logger.Info("[WIN] display settings changed (resolution or monitor topology)");
            SystemEvents.PowerModeChanged += (_, e) =>
            {
                if (PowerLine(e.Mode) is { } line) Logger.Info(line);
            };
            SystemEvents.SessionSwitch += (_, e) => Logger.Info(SessionLine(e.Reason));
        }
        finally { SynchronizationContext.SetSynchronizationContext(prev); }
        Logger.Info("[WIN] display/power/session breadcrumbs active");
    }

    // Suspend/Resume only: StatusChange fires on every battery/AC blip and would drown the log.
    internal static string? PowerLine(PowerModes mode) => mode switch
    {
        PowerModes.Suspend => "[WIN] system suspending (sleep)",
        PowerModes.Resume => "[WIN] system resumed from sleep",
        _ => null,
    };

    internal static string SessionLine(SessionSwitchReason reason) =>
        $"[WIN] session switch: {reason}";
}
