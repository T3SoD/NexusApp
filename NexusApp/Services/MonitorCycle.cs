namespace NexusApp.Services;

// Issue #36: the region picker dims ONE monitor at a time and carries a NEXT MONITOR button
// (chosen over tinting every screen at once). The window enumerates monitors and applies
// the moves; the hop math and the 1-based button label live here so they stay testable.
public static class MonitorCycle
{
    public static int Next(int current, int count) => count <= 1 ? 0 : (current + 1) % count;

    public static string Label(int current, int count) => $"MONITOR {current + 1} OF {count}";
}
