using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using NexusApp.Services;

namespace NexusApp.Views;

// The Mining Codex ambient hero: a rotating amber wireframe (shape per ore class) inside a
// composition ring. One CompositionTarget.Rendering subscription, active ONLY while running
// (Pause unhooks it, so an inactive window costs nothing). All visual constants mirror the
// approved codex-motion mock in the design lab - keep them in sync with the frozen values doc.
public sealed class NexusHologram : FrameworkElement
{
    // Frozen mock values (docs/superpowers/specs/2026-07-09-codex-motion-values.md).
    private const double YawSpeed = 0.35;          // rad/s
    private const double Tilt = -0.35;             // rad
    private const double RingGapDeg = 4.0;
    private const double RingDrawInMs = 600;
    private static readonly Color Stroke = Color.FromRgb(0xFF, 0xB2, 0x3E);   // MOBIGLAS amber

    // Pens are compile-time-constant colors/widths, so they're built once and frozen
    // instead of allocated every OnRender call.
    private static readonly Pen RingPen = FrozenPen(0xB0, 2);
    private static readonly Pen EdgePen = FrozenPen(0xE0, 1.4);

    private readonly HologramState _state = new();
    private HologramGeometry.Wireframe _wire = HologramGeometry.For("Metal");
    private HologramGeometry.RingSegment[] _ring = System.Array.Empty<HologramGeometry.RingSegment>();
    private double _yaw;
    private double _shownAtMs = -1;                // for the ring draw-in
    private double _pausedAtMs;                    // _clock reading at the last Pause
    private System.Diagnostics.Stopwatch? _clock;
    private long _lastTicks;
    private bool _hooked;

    public NexusHologram()
    {
        // The render event is a static hook (CompositionTarget.Rendering); a host that
        // tears this control out of the tree without calling Stop() must not leak it.
        Unloaded += (_, _) => Stop();
    }

    public void Show(string oreName, string oreClass, IReadOnlyList<double> compositionPercentages)
    {
        _wire = HologramGeometry.For(oreClass);
        _ring = HologramGeometry.ComputeRing(compositionPercentages, RingGapDeg);
        _clock ??= System.Diagnostics.Stopwatch.StartNew();
        _shownAtMs = _clock.Elapsed.TotalMilliseconds;
        if (_state.Show())
        {
            Logger.Info($"[UI] Codex hologram: start ({oreName}, {oreClass})");
            Hook();
        }
        InvalidateVisual();
    }

    public void Pause()
    {
        if (!_state.Pause()) return;
        Logger.Info("[UI] Codex hologram: pause (window inactive)");
        _pausedAtMs = _clock?.Elapsed.TotalMilliseconds ?? 0;
        Unhook();
    }

    public void Resume()
    {
        if (!_state.Resume()) return;
        Logger.Info("[UI] Codex hologram: resume");
        // The clock keeps running while paused, so shift the draw-in origin forward by
        // the paused duration - otherwise a Pause during the 600ms draw-in plus a later
        // Resume snaps the ring straight to fully drawn.
        if (_shownAtMs >= 0 && _clock is not null)
            _shownAtMs += _clock.Elapsed.TotalMilliseconds - _pausedAtMs;
        Hook();
    }

    public void Stop()
    {
        if (!_state.Stop()) return;
        Logger.Info("[UI] Codex hologram: stop");
        Unhook();
    }

    private void Hook()
    {
        if (_hooked) return;
        _lastTicks = _clock?.ElapsedTicks ?? 0;
        CompositionTarget.Rendering += OnFrame;
        _hooked = true;
    }

    private void Unhook()
    {
        if (!_hooked) return;
        CompositionTarget.Rendering -= OnFrame;
        _hooked = false;
    }

    private void OnFrame(object? sender, System.EventArgs e)
    {
        if (_clock is null) return;
        long now = _clock.ElapsedTicks;
        double dt = (now - _lastTicks) / (double)System.Diagnostics.Stopwatch.Frequency;
        _lastTicks = now;
        _yaw = (_yaw + YawSpeed * dt) % (System.Math.PI * 2);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_ring.Length == 0 && _wire.Vertices.Length == 0) return;
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        var center = new Point(w / 2, h / 2);
        double radius = System.Math.Min(w, h) / 2 - 4;
        if (radius <= 0) return;   // too small to draw; also guards ArcGeometry's Size(r, r)

        // Ring draw-in progress: 0..1 over RingDrawInMs from the last Show.
        double t = _clock is null || _shownAtMs < 0 ? 1
            : System.Math.Clamp((_clock.Elapsed.TotalMilliseconds - _shownAtMs) / RingDrawInMs, 0, 1);
        double ease = 1 - (1 - t) * (1 - t);      // ease-out quad, mirrors the mock

        foreach (var seg in _ring)
            dc.DrawGeometry(null, RingPen, ArcGeometry(center, radius, seg.StartDeg, seg.SweepDeg * ease));

        var pts = HologramGeometry.Project(_wire.Vertices, _yaw, Tilt, radius * 0.52, center);
        foreach (var edge in _wire.Edges)
            dc.DrawLine(EdgePen, pts[edge.A], pts[edge.B]);
    }

    private static Pen FrozenPen(byte alpha, double width)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(alpha, Stroke.R, Stroke.G, Stroke.B)), width);
        pen.Freeze();
        return pen;
    }

    private static StreamGeometry ArcGeometry(Point center, double r, double startDeg, double sweepDeg)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            Point at(double deg)
            {
                double rad = deg * System.Math.PI / 180.0;
                return new Point(center.X + r * System.Math.Cos(rad), center.Y + r * System.Math.Sin(rad));
            }
            ctx.BeginFigure(at(startDeg), false, false);
            ctx.ArcTo(at(startDeg + sweepDeg), new Size(r, r), 0, sweepDeg > 180,
                SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        return g;
    }
}
