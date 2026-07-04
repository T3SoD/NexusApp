using System.Windows.Media.Media3D;

namespace NexusApp.Views;

// A mouse-only turntable camera around a target point. Left-drag orbits, right/middle-drag pans,
// wheel zooms. No keyboard, satisfying the app's no-hotkey rule. World is Y-up (WPF standard).
public sealed class CargoTurntableCamera
{
    public PerspectiveCamera Camera { get; } = new() { FieldOfView = 40 };

    private Point3D _target;
    private double _radius = 40;
    private double _theta = Math.PI * 0.25;   // azimuth (mockup camera sits at +X,+Z so the walls fall behind)
    private double _phi = Math.PI * 0.32;     // polar from +Y
    private double _minRadius = 3, _maxRadius = 400;

    // The framed "home" pose, restored by Reset().
    private Point3D _homeTarget;
    private double _homeRadius = 40, _homeTheta = Math.PI * 0.25, _homePhi = Math.PI * 0.32;

    public CargoTurntableCamera() => Update();

    // Point the camera at the center of a box of the given world extent and back off to frame it.
    public void Frame(Point3D center, double extent)
    {
        _target = center;
        _radius = Math.Max(6, extent * 1.6);
        _minRadius = Math.Max(2, extent * 0.25);
        _maxRadius = Math.Max(_minRadius + 1, extent * 6);
        Update();
        _homeTarget = _target; _homeRadius = _radius; _homeTheta = _theta; _homePhi = _phi;
    }

    // Restore the framed pose (the on-screen Fit button).
    public void Reset()
    {
        _target = _homeTarget; _radius = _homeRadius; _theta = _homeTheta; _phi = _homePhi;
        Update();
    }

    // Snap to a preset azimuth/elevation (Top, Iso, ...), keeping the current target and radius.
    public void SetAngles(double theta, double phi)
    {
        _theta = theta;
        _phi = Math.Clamp(phi, 0.05, Math.PI - 0.05);
        Update();
    }

    // One discrete zoom step for the +/- buttons (reuses the wheel path).
    public void ZoomStep(bool inward) => Zoom(inward ? 120 : -120);

    public void Orbit(double dTheta, double dPhi)
    {
        _theta += dTheta;
        _phi = Math.Clamp(_phi + dPhi, 0.05, Math.PI - 0.05);   // avoid flipping over the poles
        Update();
    }

    public void Pan(double dxPixels, double dyPixels, double viewportWidth)
    {
        // Move the target in the camera's right/up plane, scaled so a drag tracks the cursor.
        var forward = _target - Camera.Position; forward.Normalize();
        var right = Vector3D.CrossProduct(forward, Camera.UpDirection); right.Normalize();
        var up = Vector3D.CrossProduct(right, forward); up.Normalize();
        double scale = _radius / Math.Max(1, viewportWidth);
        _target += right * (-dxPixels * scale) + up * (dyPixels * scale);
        Update();
    }

    public void Zoom(double wheelDelta)
    {
        double factor = wheelDelta > 0 ? 0.9 : 1.0 / 0.9;
        _radius = Math.Clamp(_radius * factor, _minRadius, _maxRadius);
        Update();
    }

    private void Update()
    {
        double sinP = Math.Sin(_phi), cosP = Math.Cos(_phi);
        var pos = new Point3D(
            _target.X + _radius * sinP * Math.Cos(_theta),
            _target.Y + _radius * cosP,
            _target.Z + _radius * sinP * Math.Sin(_theta));
        Camera.Position = pos;
        Camera.LookDirection = _target - pos;
        Camera.UpDirection = new Vector3D(0, 1, 0);
    }
}
