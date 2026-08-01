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
    public const double RailW = 44, Seam = 2, FlyoutW = 230;

    // Rail furniture, top to bottom, as OverlayGhostRail actually builds it: beam cap, one button
    // per tab (each with a 4px gap above it), a breathing gap, then the bottom-pinned gear and
    // close. These are the numbers behind the 332 this used to hard-code.
    internal const double RailCapH = 34, RailIconH = 32, RailIconGap = 4,
                          RailIconsToGearGap = 24, RailGearH = 32, RailCloseH = 26;

    /// <summary>Rail height at scale 1, DERIVED from the tab count rather than frozen at the six
    /// tabs the mock was drawn with. Adding the TRADE tab made the icon column taller than the old
    /// hard-coded 332, and the DockPanel resolved that by running the last icon into the settings
    /// gear (the owner's live pass, 2026-08-01). Deriving it means the next tab cannot do the same.</summary>
    public static double RailHCollapsed =>
        RailCapH
        + OverlayTabs.Ids.Length * (RailIconH + RailIconGap)
        + RailIconsToGearGap + RailGearH + RailCloseH;

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
