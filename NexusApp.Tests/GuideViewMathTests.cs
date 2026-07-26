using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Zoom and pan math for the Mission Guides viewer. It is pure so the main page and the
// overlay behave identically, and so the fiddly parts (fit, bounds, cursor anchored zoom)
// are provable without a window. Numbers below were read live from the framer mock, which
// is the design source of truth for these functions.
public class GuideViewMathTests
{
    [Fact]
    public void Fit_uses_limiting_axis()  // Checkmate in the mock main viewport
        => Assert.Equal(586.0 / 3593.0, GuideViewMath.FitScale(5500, 3593, 976, 586), 6);

    [Fact]
    public void Fit_portrait()            // Onyx: height-limited in the 976x586 box
        => Assert.Equal(586.0 / 4321.0, GuideViewMath.FitScale(3841, 4321, 976, 586), 6);

    [Fact]
    public void Degenerate_box_returns_one()  // the viewer asks before layout has a size
        => Assert.Equal(1.0, GuideViewMath.FitScale(5500, 3593, 0, 0), 9);

    [Fact]
    public void Scale_clamps_to_fit_and_2x()
    {
        Assert.Equal(0.2, GuideViewMath.ClampScale(0.1, 0.2), 9);
        Assert.Equal(2.0, GuideViewMath.ClampScale(9.9, 0.2), 9);
    }

    [Fact]
    public void Tiny_box_keeps_fit_reachable()  // a fit above the 2x ceiling is still valid
        => Assert.Equal(3.0, GuideViewMath.ClampScale(3.0, 3.0), 9);

    [Fact]
    public void Smaller_image_centers()
    {
        var (x, y) = GuideViewMath.ClampPan(-500, -500, 500, 300, 976, 586);
        Assert.Equal(238, x, 6);
        Assert.Equal(143, y, 6);
    }

    [Fact]
    public void Larger_image_clamps_edges_no_dead_space()
    {
        var (x, y) = GuideViewMath.ClampPan(-99999, 99999, 2000, 1200, 976, 586);
        Assert.Equal(976 - 2000, x, 6);
        Assert.Equal(0, y, 6);
    }

    [Fact]
    public void ZoomAt_keeps_cursor_point_stationary()
    {
        double oldS = 0.5, newS = 0.6, oldX = -100, oldY = -50, cx = 400, cy = 300;
        var (s, nx, ny) = GuideViewMath.ZoomAt(cx, cy, oldS, newS, oldX, oldY, 0.1);
        double imgPtX = (cx - oldX) / oldS, imgPtY = (cy - oldY) / oldS;
        Assert.Equal(cx, nx + imgPtX * s, 4);
        Assert.Equal(cy, ny + imgPtY * s, 4);

        // The caller always runs the result through ClampPan. A 2000x1200 image at the
        // resulting scale still covers the box, so that pass must leave the pan alone.
        var (px, py) = GuideViewMath.ClampPan(nx, ny, 2000 * s, 1200 * s, 976, 586);
        Assert.Equal(nx, px, 6);
        Assert.Equal(ny, py, 6);
    }
}
