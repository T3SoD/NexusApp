using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NexusApp.Services;

/// <summary>
/// Diagnostic-only watcher for OS foreground-window changes. On each change it logs the new
/// foreground PROCESS NAME — never the window title, which could contain personal info — to
/// nexus.log under [FG].
///
/// This is the cause-agnostic signal for the "tabbed out of Star Citizen mid-session" reports:
/// whatever pulls focus from the game (Nexus itself, a toast, Explorer, Discord, an installer…)
/// shows up here with a timestamp, so the real trigger is identified from evidence rather than
/// assumed. It is read-only: a WINEVENT_OUTOFCONTEXT hook that only observes; it installs no
/// keyboard/mouse hooks and alters no other window.
/// </summary>
public sealed class ForegroundMonitor : IDisposable
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT   = 0x0000;

    private delegate void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventProc lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private readonly WinEventProc _proc;   // held as a field so the GC can't collect the delegate the hook calls
    private IntPtr _hook;
    private int _lastPid = -1;

    public ForegroundMonitor() => _proc = OnForegroundChanged;

    public void Start()
    {
        if (_hook != IntPtr.Zero) return;
        _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT);
        Logger.Info(_hook != IntPtr.Zero
            ? "[FG] foreground monitor started"
            : "[FG] foreground monitor failed to install");
    }

    private void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            if (hwnd == IntPtr.Zero) return;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0 || (int)pid == _lastPid) return;   // ignore repeated focus of the same process
            _lastPid = (int)pid;

            string name;
            try { name = Process.GetProcessById((int)pid).ProcessName; }
            catch { name = $"pid {pid}"; }                  // process gone / access denied — name only, no title
            Logger.Info($"[FG] foreground -> {name}");
        }
        catch { /* diagnostics must never throw */ }
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
