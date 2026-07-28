namespace NexusApp.Services;

/// <summary>
/// Pure footprint math for ghost mode's window sizes (issue #27 + independent rail scale).
/// Rail furniture (rail, seam, flyout) scales by railK; the panel by panelK; everything is
/// physical pixels (multiply by dpi), applied by the window via MoveWindow. WPF-free so the
/// two-scale combinations stay headlessly testable, the same split GhostGeometry uses.
/// </summary>
public static class GhostFootprints
{
    // Base (unscaled) metrics from the ghost mock's implementation-values table.
    public const double RailW = 44, RailHCollapsed = 332, Seam = 2, FlyoutW = 230;

    public static (double W, double H) CollapsedSize(double railK, double dpi)
        => (RailW * railK * dpi, RailHCollapsed * railK * dpi);

    public static (double W, double H) ExpandedSize(double railK, double panelK, double panelW, double panelH, double dpi)
        => ((RailW + Seam) * railK * dpi + panelW * panelK * dpi,
            Math.Max(RailHCollapsed * railK, panelH * panelK) * dpi);

    public static (double W, double H) FlyoutSize(double railK, double dpi)
        => ((RailW + Seam + FlyoutW) * railK * dpi, RailHCollapsed * railK * dpi);

    /// <summary>Width at or below which the window is the rail alone (collapsed).</summary>
    public static double RailOnlyThreshold(double railK, double dpi)
        => (RailW + Seam) * railK * dpi + 1;
}
