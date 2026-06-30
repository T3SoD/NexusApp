using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace NexusApp.Views;

/// <summary>
/// An easing function defined by a cubic Bezier curve with control points
/// P1=(X1,Y1) and P2=(X2,Y2), so the app can express the exact
/// <c>cubic-bezier(x1,y1,x2,y2)</c> curves the MOBIGLAS mocks are authored with
/// (e.g. the count-up "settle" 0.16,0.8,0.3,1 and the page reveal 0.2,0.8,0.2,1).
/// WPF has no built-in arbitrary-bezier easing, so this maps the animation's
/// normalized time t to eased progress by solving X(u)=t for the curve parameter u
/// then returning Y(u). EasingMode defaults to EaseIn so the curve is applied
/// exactly as authored (EasingFunctionBase otherwise mirrors EaseInCore).
/// </summary>
public sealed class CubicBezierEase : EasingFunctionBase
{
    public static readonly DependencyProperty X1Property =
        DependencyProperty.Register(nameof(X1), typeof(double), typeof(CubicBezierEase), new PropertyMetadata(0.0));
    public static readonly DependencyProperty Y1Property =
        DependencyProperty.Register(nameof(Y1), typeof(double), typeof(CubicBezierEase), new PropertyMetadata(0.0));
    public static readonly DependencyProperty X2Property =
        DependencyProperty.Register(nameof(X2), typeof(double), typeof(CubicBezierEase), new PropertyMetadata(1.0));
    public static readonly DependencyProperty Y2Property =
        DependencyProperty.Register(nameof(Y2), typeof(double), typeof(CubicBezierEase), new PropertyMetadata(1.0));

    public double X1 { get => (double)GetValue(X1Property); set => SetValue(X1Property, value); }
    public double Y1 { get => (double)GetValue(Y1Property); set => SetValue(Y1Property, value); }
    public double X2 { get => (double)GetValue(X2Property); set => SetValue(X2Property, value); }
    public double Y2 { get => (double)GetValue(Y2Property); set => SetValue(Y2Property, value); }

    static CubicBezierEase()
    {
        // The bezier encodes the complete curve, so default EasingMode to EaseIn:
        // EasingFunctionBase then applies EaseInCore(t) directly without mirroring it.
        EasingModeProperty.OverrideMetadata(typeof(CubicBezierEase),
            new PropertyMetadata(EasingMode.EaseIn));
    }

    public CubicBezierEase() { }

    public CubicBezierEase(double x1, double y1, double x2, double y2)
    {
        X1 = x1; Y1 = y1; X2 = x2; Y2 = y2;
    }

    protected override Freezable CreateInstanceCore() => new CubicBezierEase();

    protected override double EaseInCore(double t)
    {
        if (t <= 0) return 0;
        if (t >= 1) return 1;
        double u = SolveForU(t, X1, X2);
        return Bezier(u, Y1, Y2);
    }

    // Cubic Bezier component with P0=0, P3=1: 3(1-u)^2 u p1 + 3(1-u) u^2 p2 + u^3.
    private static double Bezier(double u, double p1, double p2)
    {
        double mu = 1 - u;
        return (3 * mu * mu * u * p1) + (3 * mu * u * u * p2) + (u * u * u);
    }

    // d/du of Bezier above: 3(1-u)^2 p1 + 6(1-u)u (p2-p1) + 3u^2 (1-p2).
    private static double BezierDerivative(double u, double p1, double p2)
    {
        double mu = 1 - u;
        return (3 * mu * mu * p1) + (6 * mu * u * (p2 - p1)) + (3 * u * u * (1 - p2));
    }

    // Solve X(u) = t for u in [0,1] via Newton-Raphson, falling back to bisection.
    private static double SolveForU(double t, double x1, double x2)
    {
        double u = t; // initial guess: parameter ~= time
        for (int i = 0; i < 8; i++)
        {
            double x = Bezier(u, x1, x2) - t;
            if (Math.Abs(x) < 1e-6) return Math.Clamp(u, 0, 1);
            double d = BezierDerivative(u, x1, x2);
            if (Math.Abs(d) < 1e-6) break;
            u -= x / d;
        }
        double lo = 0, hi = 1;
        u = Math.Clamp(t, 0, 1);
        for (int i = 0; i < 24; i++)
        {
            double x = Bezier(u, x1, x2);
            if (Math.Abs(x - t) < 1e-6) break;
            if (x < t) lo = u; else hi = u;
            u = (lo + hi) / 2;
        }
        return u;
    }
}
