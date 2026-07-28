namespace NexusApp.Services;

/// <summary>Which side of the rail the ghost panel slides toward (issue #27).</summary>
public enum GhostExpandDirection { Left, Right }

/// <summary>A rectangle in physical pixels. WPF-free so the ghost placement math stays
/// headlessly testable; callers convert DIPs at the boundary.</summary>
public readonly record struct PxRect(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;
    public double CenterX => Left + Width / 2;
}

/// <summary>
/// Pure placement math for the overlay's ghost mode (issue #27): which way a panel
/// expands, where the rail lands on collapse, and where the window lands on expand.
/// Everything is physical pixels because under Per-Monitor-DPI V2 WPF DIP positioning
/// lands on the wrong monitor across a DPI boundary (issue #6 lesson); the window applies
/// these rects with MoveWindow.
/// </summary>
public static class GhostGeometry
{
    /// <summary>The panel slides toward the monitor's center: a rail in the right half
    /// expands left. Ties (exact center) go Right.</summary>
    public static GhostExpandDirection DirectionFor(PxRect rail, PxRect monitor)
        => rail.CenterX > monitor.CenterX ? GhostExpandDirection.Left : GhostExpandDirection.Right;

    /// <summary>Collapsing hugs the screen-nearest horizontal edge of the prior window
    /// footprint, so collapsing against the right screen edge does not strand the rail a
    /// panel-width away from where the user parked the overlay.</summary>
    public static PxRect CollapsedRect(PxRect window, PxRect monitor, double railW, double railH)
    {
        double left = window.CenterX > monitor.CenterX ? window.Right - railW : window.Left;
        return Clamp(new PxRect(left, window.Top, railW, railH), monitor);
    }

    /// <summary>Expanding keeps the rail's screen-side edge fixed and grows toward the
    /// monitor center: dir Left grows leftward from the rail's right edge.</summary>
    public static PxRect ExpandedRect(PxRect rail, PxRect monitor, GhostExpandDirection dir, double totalW, double totalH)
    {
        double left = dir == GhostExpandDirection.Left ? rail.Right - totalW : rail.Left;
        return Clamp(new PxRect(left, rail.Top, totalW, totalH), monitor);
    }

    /// <summary>Keeps a rect on the monitor. If the rect is larger than the monitor the
    /// monitor's near edge wins (top/left visible beats bottom/right).</summary>
    public static PxRect Clamp(PxRect r, PxRect monitor)
    {
        double left = Math.Max(monitor.Left, Math.Min(r.Left, monitor.Right - r.Width));
        double top = Math.Max(monitor.Top, Math.Min(r.Top, monitor.Bottom - r.Height));
        return r with { Left = left, Top = top };
    }
}
