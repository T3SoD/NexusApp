using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NexusApp.Services;

/// <summary>
/// Diagnostic-only watcher for OS foreground-window changes. On each change it logs the new
/// foreground PROCESS NAME - never the window title, which could contain personal info - to
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

    // The "in use" set: scanning only makes sense when Nexus itself or Star Citizen is in front. The own
    // process name is resolved at runtime so it holds for both the portable and installer builds (NexusApp).
    private static readonly HashSet<string> RelevantProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        Process.GetCurrentProcess().ProcessName, "StarCitizen",
    };
    private bool _relevant = true;   // assume in front at launch
    private readonly GameProcessPresence _scPresence = new();

    // Existence probe for the exit breadcrumb: a process-list snapshot only - by explicit
    // product decision this never opens a handle to the game process (no anti-cheat risk).
    private static bool StarCitizenProcessExists()
    {
        try
        {
            var procs = Process.GetProcessesByName("StarCitizen");
            foreach (var p in procs) p.Dispose();
            return procs.Length > 0;
        }
        catch { return true; }   // on doubt assume alive: never log a false exit line
    }

    /// <summary>True while the foreground window belongs to Nexus or Star Citizen.</summary>
    public bool IsRelevantForeground => _relevant;
    /// <summary>Raised only when foreground relevance flips, so OCR auto-scans can pause/resume.</summary>
    public event Action<bool>? RelevanceChanged;

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
            catch { name = $"pid {pid}"; }                  // process gone / access denied - name only, no title
            Logger.Info($"[FG] foreground -> {name}");

            // One authoritative line when the game process disappears, so log triage doesn't
            // have to infer a game crash from WerFault/launcher focus patterns. Event-driven,
            // not polled: the line lands on the first foreground change AFTER the death, so its
            // timestamp is "at latest by", not "at" - and a background death followed by a
            // relaunch before any focus change goes undetected (accepted tradeoff for no
            // polling and no game-process handle).
            if (_scPresence.Update(name.Equals("StarCitizen", StringComparison.OrdinalIgnoreCase),
                    StarCitizenProcessExists))
                Logger.Info("[FG] StarCitizen process no longer running (crashed or quit)");

            bool relevant = RelevantProcesses.Contains(name);
            if (relevant != _relevant) { _relevant = relevant; RelevanceChanged?.Invoke(relevant); }
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

/// <summary>
/// Decision core for the "[FG] StarCitizen process no longer running" breadcrumb: arms when
/// the game takes foreground, fires exactly once per game lifetime when a later foreground
/// change finds the process gone. Handle-free by explicit product decision (no anti-cheat
/// risk): the existence probe is a process-list snapshot, never a handle to the game process.
/// </summary>
internal sealed class GameProcessPresence
{
    private bool _wasRunning;

    /// <summary>Feed every foreground change; true exactly once when the armed game has gone away.</summary>
    public bool Update(bool gameIsForeground, Func<bool> gameProcessExists)
    {
        if (gameIsForeground) { _wasRunning = true; return false; }
        if (!_wasRunning || gameProcessExists()) return false;
        _wasRunning = false;
        return true;
    }
}
