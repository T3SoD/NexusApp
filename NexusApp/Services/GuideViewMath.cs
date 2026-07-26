namespace NexusApp.Services;

// Pure zoom and pan math for the Mission Guides viewer, ported from the "ZOOM MATH" block
// of the framer mock (nexus-design-lab\guides\index.html), which is the design source of
// truth. No WPF types on purpose: the main page viewer and the compact overlay viewer share
// one behavior, and the fiddly parts stay unit-testable without a window.
//
// Coordinate model: scale maps image pixels to viewport pixels, so 1.0 is native pixels.
// Pan (x, y) is the translation of the SCALED image's top-left corner inside the viewport,
// which is exactly what the viewer feeds its render transform.
public static class GuideViewMath
{
    // Largest scale at which the whole image sits inside the viewport. Returns 1 for a
    // degenerate box so the viewer can ask before layout has given it a size.
    public static double FitScale(double imgW, double imgH, double boxW, double boxH)
    {
        if (imgW <= 0 || imgH <= 0 || boxW <= 0 || boxH <= 0) return 1.0;
        return Math.Min(boxW / imgW, boxH / imgH);
    }

    // Ceiling of the zoom range: 2x native pixels (spec "Zoom range").
    public static double MaxScale() => 2.0;

    // Floor is fit, ceiling is 2x native. A viewport small enough that fit lands above the
    // 2x ceiling still has to be able to show the whole image, so the two bounds are ordered
    // rather than assumed. Math.Clamp would throw on an inverted range.
    public static double ClampScale(double s, double fit)
        => Math.Clamp(s, Math.Min(fit, MaxScale()), Math.Max(fit, MaxScale()));

    // The image can never be dragged fully out of view. An axis smaller than the box centers
    // on that axis, a larger one clamps at its edges so no dead space shows. Takes the already
    // scaled size because every caller has it to hand.
    public static (double X, double Y) ClampPan(double x, double y, double scaledW, double scaledH, double boxW, double boxH)
    {
        double cx = scaledW <= boxW ? (boxW - scaledW) / 2.0 : Math.Min(0.0, Math.Max(boxW - scaledW, x));
        double cy = scaledH <= boxH ? (boxH - scaledH) / 2.0 : Math.Min(0.0, Math.Max(boxH - scaledH, y));
        return (cx, cy);
    }

    // Cursor centered zoom: the image point under the cursor stays under the cursor.
    // The scale is clamped first and the pan is anchored with the CLAMPED scale, so a wheel
    // notch that runs into a bound does not drift the image. Pan clamping stays with the
    // caller, the only side that knows the image and box sizes (the mock runs clampPan on
    // this result too).
    public static (double S, double X, double Y) ZoomAt(double cursorX, double cursorY, double oldS, double newS, double oldX, double oldY, double fit)
    {
        double s = ClampScale(newS, fit);
        if (oldS <= 0) return (s, oldX, oldY);
        double k = s / oldS;
        return (s, cursorX - (cursorX - oldX) * k, cursorY - (cursorY - oldY) * k);
    }
}
