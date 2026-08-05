namespace NexusApp.Services;

// Issue #36: Windows Snap (drag-to-top / Win+Up) maximizes the borderless overlay, and a
// maximized WPF window can neither DragMove (silent no-op) nor show its resize grip - stuck
// until app exit. The overlay reverts every OS maximize to Normal; this decides what happens
// next. Normal mode: fill the monitor's WORK area (the coverage the user asked Snap for, minus
// the taskbar, still movable and resizable). Ghost mode: no fill - the rail's footprint is
// mode-derived and a work-area fill would corrupt it.
public static class SnapMaximizeGuard
{
    public static PxRect? FillRect(bool ghostActive, PxRect monitor, PxRect workArea)
        => ghostActive ? null : workArea;
}
