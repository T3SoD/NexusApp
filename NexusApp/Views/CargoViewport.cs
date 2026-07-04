using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using NexusApp.Models.Cargo;
using NexusApp.Services.Cargo;

namespace NexusApp.Views;

// The 3D cargo view (built-in WPF Viewport3D only), rendered as the approved synthesis design: an
// expansive dim blueprint floor grid (02), flat matte containers with lighter tops and thin edges
// (06), floating "N SCU" pills billboarded above each box (06), a vertical layer-separation control
// that lifts the tiers apart (07), and a stats + legend HUD (09). Mouse-only turntable camera.
public sealed class CargoViewport : UserControl
{
    private const double Cell = 1.25;                 // metres per cell
    private const double GapCells = 2;                // spacing between a ship's grids in the synthetic layout
    private const double SeparationGap = 2.6 * Cell;  // world lift per tier at full separation

    private static readonly int[] Sizes = { 1, 2, 4, 8, 16, 24, 32 };

    private readonly Viewport3D _viewport;
    private readonly CargoTurntableCamera _camera = new();
    private readonly ModelVisual3D _staticRoot = new();     // lights + opaque floor
    private readonly ModelVisual3D _boxRoot = new();        // tier groups + connectors
    private readonly ModelVisual3D _backdropRoot = new();   // transparent schematic grids, drawn last
    private readonly Canvas _labelLayer = new() { IsHitTestVisible = false };  // floating billboard pills

    private readonly List<TranslateTransform3D> _tierLifts = new();
    private readonly List<Connector> _connectors = new();
    private readonly List<BoxLabel> _labels = new();
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _sep, _target;
    private bool _auto;

    // Custom gradient slider + HUD text.
    private const double SliderWidth = 150, ThumbSize = 14;
    private Canvas _sliderTrack = null!;
    private Border _sliderThumb = null!, _sliderFill = null!;
    private TextBlock _sepVal = null!, _pctText = null!, _scuText = null!, _boxText = null!;
    private Button _autoBtn = null!;
    private bool _draggingSlider;

    private static readonly (Point3D a, Point3D b, Point3D c, Point3D d)[] CubeFaces =
    {
        (new(0,0,1), new(1,0,1), new(1,1,1), new(0,1,1)),  // 0 front  (+z)
        (new(1,0,0), new(0,0,0), new(0,1,0), new(1,1,0)),  // 1 back   (-z)
        (new(1,0,1), new(1,0,0), new(1,1,0), new(1,1,1)),  // 2 right  (+x)
        (new(0,0,0), new(0,0,1), new(0,1,1), new(0,1,0)),  // 3 left   (-x)
        (new(0,1,1), new(1,1,1), new(1,1,0), new(0,1,0)),  // 4 top    (+y)
        (new(0,0,0), new(1,0,0), new(1,0,1), new(0,0,1)),  // 5 bottom (-y)
    };
    private static readonly MeshGeometry3D UnitCube = BuildBoxMesh(0, 1, 2, 3, 4, 5);
    private static readonly Dictionary<int, Material> BoxMaterials = BuildBoxMaterials();
    private static readonly Material DarkFloorMaterial = Frozen(new DiffuseMaterial(
        Frozen(new SolidColorBrush(Color.FromRgb(0x0A, 0x0F, 0x16)))));

    private Point _lastMouse;
    private bool _orbiting, _panning;

    private sealed class Connector
    {
        public int TierIndex;
        public double BaseY;
        public ScaleTransform3D Scale = null!;
        public SolidColorBrush Brush = null!;
    }

    private sealed class BoxLabel
    {
        public Point3D BaseTop;   // world top-centre of the box before any tier lift
        public int TierIndex;
        public Border Pill = null!;
    }

    public CargoViewport()
    {
        _viewport = new Viewport3D { ClipToBounds = false, Camera = _camera.Camera };
        RenderOptions.SetEdgeMode(_viewport, EdgeMode.Aliased);
        _viewport.Children.Add(_staticRoot);
        _viewport.Children.Add(_boxRoot);
        _viewport.Children.Add(_backdropRoot);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += OnTimerTick;

        var host = new Grid();
        host.Children.Add(_viewport);
        host.Children.Add(_labelLayer);
        host.Children.Add(BuildCornerBrackets());
        host.Children.Add(BuildTitle());
        host.Children.Add(BuildStats());
        host.Children.Add(BuildLegend());
        host.Children.Add(BuildLayerPanel());
        host.Children.Add(BuildControls());
        Content = host;
        Background = Brushes.Transparent;
        Focusable = false;

        MouseLeftButtonDown += OnLeftDown;
        MouseRightButtonDown += OnRightDown;
        MouseMove += OnMove;
        MouseUp += OnUp;
        MouseWheel += OnWheel;
        SizeChanged += (_, _) => UpdateLabels();
        Loaded += (_, _) => UpdateLabels();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { if (_auto || Math.Abs(_target - _sep) > 0.001) EnsureTimer(); }
            else _timer.Stop();
        };
        Unloaded += (_, _) => _timer.Stop();
    }

    // Draw one trip's placements packed into the given ship. Empty trip clears the box layer.
    public void RenderTrip(PackResult? trip, ShipCargoDef? ship)
    {
        _staticRoot.Children.Clear();
        _boxRoot.Children.Clear();
        _backdropRoot.Children.Clear();
        _labelLayer.Children.Clear();
        _tierLifts.Clear();
        _connectors.Clear();
        _labels.Clear();
        AddLights();

        if (ship is null || ship.Grids.Count == 0)
        {
            _camera.Frame(new Point3D(0, 0, 0), 10);
            UpdateStats(null, null);
            return;
        }

        // Lay the grids side by side along world X (their true hull offsets are not in the data).
        var offsets = new Dictionary<int, double>();
        double cursorX = 0, maxY = 0, maxZ = 0;
        foreach (var g in ship.Grids)
        {
            offsets[g.Id] = cursorX;
            cursorX += (g.W + GapCells) * Cell;
            maxY = Math.Max(maxY, g.H * Cell);
            maxZ = Math.Max(maxZ, g.D * Cell);
        }
        double spanX = Math.Max(cursorX - GapCells * Cell, Cell);

        var placed = trip?.Placed ?? new List<Placement>();
        var tiers = placed.Select(p => p.Z).Distinct().OrderBy(z => z).ToList();
        int nTiers = tiers.Count;
        double wallHeight = maxY + (nTiers > 1 ? nTiers - 1 : 0) * SeparationGap + 2 * Cell;

        AddBackdrop(spanX, maxZ, wallHeight);

        var tierIndexByZ = new Dictionary<int, int>();
        var tierGroupByZ = new Dictionary<int, Model3DGroup>();
        for (int i = 0; i < tiers.Count; i++)
        {
            int z = tiers[i];
            tierIndexByZ[z] = i;
            var grp = new Model3DGroup();
            tierGroupByZ[z] = grp;
            var lift = new TranslateTransform3D(0, 0, 0);
            _tierLifts.Add(lift);
            _boxRoot.Children.Add(new ModelVisual3D { Content = grp, Transform = lift });
        }
        foreach (var p in placed)
        {
            double offX = offsets.GetValueOrDefault(p.GridId);
            tierGroupByZ[p.Z].Children.Add(BuildBox(offX, p));
            AddConnector(offX, p, tierIndexByZ[p.Z]);
            AddLabel(offX, p, tierIndexByZ[p.Z]);
        }

        var center = new Point3D(spanX / 2, maxY / 2, maxZ / 2);
        _camera.Frame(center, Math.Max(spanX, Math.Max(maxY, maxZ)));
        ApplySep(_sep);
        UpdateStats(trip, ship);
        UpdateLabels();
        if (_auto || Math.Abs(_target - _sep) > 0.001) EnsureTimer();
    }

    // -- on-screen camera controls (mouse-only, no hotkeys) -----------------------

    public void ResetView()   { _camera.Reset();               UpdateLabels(); }
    public void ZoomIn()      { _camera.ZoomStep(true);        UpdateLabels(); }
    public void ZoomOut()     { _camera.ZoomStep(false);       UpdateLabels(); }
    public void RotateLeft()  { _camera.Orbit(-0.26, 0);       UpdateLabels(); }
    public void RotateRight() { _camera.Orbit(0.26, 0);        UpdateLabels(); }
    public void TiltUp()      { _camera.Orbit(0, -0.20);       UpdateLabels(); }
    public void TiltDown()    { _camera.Orbit(0, 0.20);        UpdateLabels(); }
    public void TopView()     { _camera.SetAngles(Math.PI * 0.25, 0.08);          UpdateLabels(); }
    public void IsoView()     { _camera.SetAngles(Math.PI * 0.25, Math.PI * 0.32); UpdateLabels(); }

    // -- layer separation ----------------------------------------------------------

    private void ApplySep(double f)
    {
        for (int i = 0; i < _tierLifts.Count; i++) _tierLifts[i].OffsetY = i * SeparationGap * f;
        foreach (var c in _connectors)
        {
            double lift = c.TierIndex * SeparationGap * f;
            c.Scale.ScaleY = f < 0.02 ? 0.0001 : Math.Max(0.0001, c.BaseY + lift);
            c.Brush.Opacity = 0.28 * f;
        }
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (_auto)
        {
            double t = _clock.Elapsed.TotalSeconds;
            _target = 0.5 - 0.5 * Math.Cos(t * 0.45);
        }
        _sep += (_target - _sep) * 0.09;
        bool settled = !_auto && Math.Abs(_target - _sep) < 0.001;
        if (settled) _sep = _target;
        ApplySep(_sep);
        UpdateLabels();
        UpdateSliderVisual();
        if (settled) _timer.Stop();
    }

    private void EnsureTimer() { if (!_timer.IsEnabled) _timer.Start(); }

    private void SetSeparationTarget(double v)
    {
        _target = Math.Clamp(v, 0, 1);
        if (_auto) { _auto = false; UpdateAutoBtn(); }
        UpdateSliderVisual();
        EnsureTimer();
    }

    private void UpdateAutoBtn()
    {
        _autoBtn.Content = "AUTO CYCLE: " + (_auto ? "ON" : "OFF");
        if (_auto)
        {
            _autoBtn.Background = Brush(0x7F, 0xE9, 0xE0);
            _autoBtn.Foreground = Brush(0x05, 0x07, 0x0A);
            _autoBtn.FontWeight = FontWeights.Bold;
        }
        else
        {
            _autoBtn.Background = Brush(0x14, 0x7F, 0xE9, 0xE0);
            _autoBtn.Foreground = Brush(0x7F, 0xE9, 0xE0);
            _autoBtn.FontWeight = FontWeights.Normal;
        }
    }

    private void UpdateStats(PackResult? trip, ShipCargoDef? ship)
    {
        int cap = ship?.TotalScu ?? 0;
        int scu = trip?.PlacedScu ?? 0;
        int boxes = trip?.Placed.Count ?? 0;
        int pct = cap > 0 ? (int)Math.Round(scu / (double)cap * 100) : 0;
        _pctText.Text = $"{pct}%";
        _scuText.Text = $"{scu} / {cap}";
        _boxText.Text = boxes.ToString();
    }

    // -- floating billboard labels -------------------------------------------------

    private void AddLabel(double offsetX, Placement p, int tierIndex)
    {
        double cx = offsetX + (p.X + p.Size.W / 2.0) * Cell;
        double cz = (p.Y + p.Size.D / 2.0) * Cell;
        double topY = (p.Z + p.Size.H) * Cell;
        var pill = MakePill(p.Scu);
        _labelLayer.Children.Add(pill);
        _labels.Add(new BoxLabel { BaseTop = new Point3D(cx, topY, cz), TierIndex = tierIndex, Pill = pill });
    }

    private Border MakePill(int scu)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(new TextBlock
        {
            Text = scu.ToString(), FontFamily = new FontFamily("Segoe UI"), FontSize = 17, FontWeight = FontWeights.Bold,
            Foreground = Brush(0xEA, 0xF1, 0xF6), HorizontalAlignment = HorizontalAlignment.Center, LineHeight = 17,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        });
        stack.Children.Add(new TextBlock
        {
            Text = "SCU", FontFamily = new FontFamily("Segoe UI"), FontSize = 8, FontWeight = FontWeights.SemiBold,
            Foreground = Brush(0x8A, 0x98, 0xA6), HorizontalAlignment = HorizontalAlignment.Center,
        });
        return new Border
        {
            CornerRadius = new CornerRadius(6), Padding = new Thickness(7, 3, 7, 3),
            Background = Brush(0xD8, 0x05, 0x07, 0x0A),
            BorderBrush = Brush(0x40, 0xFF, 0xFF, 0xFF), BorderThickness = new Thickness(0.7),
            Child = stack,
        };
    }

    // Project each box's (lifted) top-centre to the screen and place its pill just above that point.
    private void UpdateLabels()
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 1 || h < 1 || _labels.Count == 0) return;
        var view = GetViewMatrix();
        var vp = Matrix3D.Multiply(view, GetProjectionMatrix(w / h));
        foreach (var lbl in _labels)
        {
            var world = new Point3D(lbl.BaseTop.X, lbl.BaseTop.Y + lbl.TierIndex * SeparationGap * _sep, lbl.BaseTop.Z);
            var vpt = view.Transform(world);
            if (vpt.Z > -0.05) { lbl.Pill.Visibility = Visibility.Collapsed; continue; }   // behind camera
            var ndc = vp.Transform(world);
            double sx = (ndc.X * 0.5 + 0.5) * w;
            double sy = (0.5 - ndc.Y * 0.5) * h;
            lbl.Pill.Visibility = Visibility.Visible;
            lbl.Pill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var sz = lbl.Pill.DesiredSize;
            Canvas.SetLeft(lbl.Pill, sx - sz.Width / 2);
            Canvas.SetTop(lbl.Pill, sy - sz.Height - 8);
        }
    }

    private Matrix3D GetViewMatrix()
    {
        var cam = _camera.Camera;
        Vector3D z = cam.LookDirection; z.Normalize(); z = -z;
        Vector3D x = Vector3D.CrossProduct(cam.UpDirection, z); x.Normalize();
        Vector3D y = Vector3D.CrossProduct(z, x);
        var pos = new Vector3D(cam.Position.X, cam.Position.Y, cam.Position.Z);
        return new Matrix3D(
            x.X, y.X, z.X, 0,
            x.Y, y.Y, z.Y, 0,
            x.Z, y.Z, z.Z, 0,
            -Vector3D.DotProduct(x, pos), -Vector3D.DotProduct(y, pos), -Vector3D.DotProduct(z, pos), 1);
    }

    private Matrix3D GetProjectionMatrix(double aspect)
    {
        var cam = _camera.Camera;
        double fov = cam.FieldOfView * Math.PI / 180.0;   // WPF field of view is horizontal
        double xs = 1.0 / Math.Tan(fov / 2.0);
        double ys = xs * aspect;
        double n = cam.NearPlaneDistance, fp = cam.FarPlaneDistance;
        return new Matrix3D(
            xs, 0, 0, 0,
            0, ys, 0, 0,
            0, 0, fp / (n - fp), -1,
            0, 0, n * fp / (n - fp), 0);
    }

    // -- overlays ------------------------------------------------------------------

    private UIElement BuildControls()
    {
        Button Ctl(string glyph, string tip, Action act, double width = 30)
        {
            var b = new Button
            {
                Content = glyph, Width = width, Height = 26, Margin = new Thickness(2),
                ToolTip = tip, Cursor = Cursors.Hand, FontSize = 13, FontWeight = FontWeights.Bold,
                Foreground = Brush(0x7F, 0xE9, 0xE0),
                Background = Brush(0xCC, 0x0E, 0x14, 0x1C),
                BorderBrush = Brush(0x77, 0x7F, 0xE9, 0xE0),
                BorderThickness = new Thickness(1),
            };
            b.Click += (_, _) => act();
            return b;
        }

        var col = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        col.Children.Add(Ctl("Fit", "Fit the whole ship in view", ResetView, 44));

        var zoom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        zoom.Children.Add(Ctl("+", "Zoom in", ZoomIn));
        zoom.Children.Add(Ctl("-", "Zoom out", ZoomOut));
        col.Children.Add(zoom);

        var pad = new Grid();
        for (int i = 0; i < 3; i++) { pad.RowDefinitions.Add(new RowDefinition()); pad.ColumnDefinitions.Add(new ColumnDefinition()); }
        void Put(UIElement e, int r, int c) { Grid.SetRow(e, r); Grid.SetColumn(e, c); pad.Children.Add(e); }
        Put(Ctl("▲", "Tilt up", TiltUp), 0, 1);
        Put(Ctl("◄", "Rotate left", RotateLeft), 1, 0);
        Put(Ctl("►", "Rotate right", RotateRight), 1, 2);
        Put(Ctl("▼", "Tilt down", TiltDown), 2, 1);
        col.Children.Add(pad);

        var pre = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        var top = Ctl("Top", "Top-down view", TopView, 44); top.FontSize = 10; top.FontWeight = FontWeights.Normal;
        var iso = Ctl("Iso", "Isometric view", IsoView, 44); iso.FontSize = 10; iso.FontWeight = FontWeights.Normal;
        pre.Children.Add(top); pre.Children.Add(iso);
        col.Children.Add(pre);

        var border = new Border
        {
            Child = col, Padding = new Thickness(4),
            Background = Brush(0x88, 0x05, 0x07, 0x0A),
            BorderBrush = Brush(0x55, 0x7F, 0xE9, 0xE0),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 12, 12),
        };
        border.MouseLeftButtonDown += (_, e) => e.Handled = true;
        border.MouseRightButtonDown += (_, e) => e.Handled = true;
        return border;
    }

    private UIElement BuildLayerPanel()
    {
        var col = new StackPanel();
        col.Children.Add(new TextBlock
        {
            Text = "LAYER SEPARATION", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = Brush(0x7C, 0x8A, 0x99), Margin = new Thickness(0, 0, 0, 9),
        });

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(BuildSlider());
        _sepVal = new TextBlock
        {
            Text = "0%", Width = 40, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush(0xFF, 0xD0, 0x89), FontFamily = new FontFamily("Consolas"), FontSize = 12,
            Margin = new Thickness(10, 0, 0, 0),
        };
        row.Children.Add(_sepVal);
        col.Children.Add(row);

        _autoBtn = new Button
        {
            Content = "AUTO CYCLE: OFF", Cursor = Cursors.Hand, FontSize = 10,
            Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 11, 0, 0),
            BorderBrush = Brush(0x4D, 0x7F, 0xE9, 0xE0), BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
        };
        _autoBtn.Click += (_, _) => { _auto = !_auto; UpdateAutoBtn(); if (_auto) EnsureTimer(); };
        UpdateAutoBtn();
        col.Children.Add(_autoBtn);

        var border = new Border
        {
            Child = col, Padding = new Thickness(14, 12, 16, 12),
            Background = Brush(0x99, 0x0A, 0x0E, 0x14),
            BorderBrush = Brush(0x40, 0x7F, 0xE9, 0xE0),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(12, 0, 0, 12),
        };
        border.MouseLeftButtonDown += (_, e) => e.Handled = true;
        border.MouseRightButtonDown += (_, e) => e.Handled = true;
        return border;
    }

    // Cyan-to-gold gradient track with a glowing gold thumb, dragged with the mouse (no hotkeys).
    private UIElement BuildSlider()
    {
        _sliderTrack = new Canvas { Width = SliderWidth, Height = ThumbSize + 6, Background = Brushes.Transparent, Cursor = Cursors.Hand };

        var rail = new Border
        {
            Width = SliderWidth, Height = 4, CornerRadius = new CornerRadius(2),
            Background = new LinearGradientBrush(Color.FromRgb(0x7F, 0xE9, 0xE0), Color.FromRgb(0xFF, 0xB2, 0x3E), 0),
        };
        Canvas.SetLeft(rail, 0);
        Canvas.SetTop(rail, (ThumbSize + 6) / 2 - 2);
        _sliderTrack.Children.Add(rail);

        _sliderFill = new Border { Width = 0, Height = 4, CornerRadius = new CornerRadius(2), Background = Brush(0xFF, 0xD0, 0x89) };
        Canvas.SetLeft(_sliderFill, 0);
        Canvas.SetTop(_sliderFill, (ThumbSize + 6) / 2 - 2);
        // The fill sits under the thumb; keep it subtle so the gradient rail reads through.
        _sliderFill.Opacity = 0.0;
        _sliderTrack.Children.Add(_sliderFill);

        _sliderThumb = new Border
        {
            Width = ThumbSize, Height = ThumbSize, CornerRadius = new CornerRadius(ThumbSize / 2),
            Background = Brush(0xFF, 0xD0, 0x89), BorderBrush = Brush(0x05, 0x07, 0x0A), BorderThickness = new Thickness(2),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Color.FromRgb(0xFF, 0xB2, 0x3E), BlurRadius = 8, ShadowDepth = 0, Opacity = 0.9,
            },
        };
        Canvas.SetTop(_sliderThumb, 3);
        Canvas.SetLeft(_sliderThumb, 0);
        _sliderTrack.Children.Add(_sliderThumb);

        _sliderTrack.MouseLeftButtonDown += (_, e) =>
        {
            _draggingSlider = true; _sliderTrack.CaptureMouse();
            SetSeparationTarget(e.GetPosition(_sliderTrack).X / SliderWidth);
            e.Handled = true;
        };
        _sliderTrack.MouseMove += (_, e) =>
        {
            if (_draggingSlider) SetSeparationTarget(e.GetPosition(_sliderTrack).X / SliderWidth);
        };
        _sliderTrack.MouseLeftButtonUp += (_, _) => { _draggingSlider = false; _sliderTrack.ReleaseMouseCapture(); };
        return _sliderTrack;
    }

    private void UpdateSliderVisual()
    {
        double v = _draggingSlider ? _target : _sep;
        double x = Math.Clamp(v, 0, 1) * (SliderWidth - ThumbSize);
        Canvas.SetLeft(_sliderThumb, x);
        _sliderFill.Width = x + ThumbSize / 2;
        _sepVal.Text = $"{(int)Math.Round(_sep * 100)}%";
    }

    private UIElement BuildStats()
    {
        var col = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 24, 32, 0), IsHitTestVisible = false,
        };
        col.Children.Add(Stat("CAPACITY FILLED", out _pctText, Brush(0x66, 0xE6, 0xA6)));
        col.Children.Add(Stat("TOTAL SCU", out _scuText, Brush(0xEA, 0xF1, 0xF6)));
        col.Children.Add(Stat("CONTAINERS", out _boxText, Brush(0xEA, 0xF1, 0xF6)));
        return col;
    }

    private UIElement Stat(string label, out TextBlock value, SolidColorBrush valColor)
    {
        var s = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 0, 9) };
        s.Children.Add(new TextBlock { Text = label, FontSize = 9, Foreground = Brush(0x7C, 0x8A, 0x99), HorizontalAlignment = HorizontalAlignment.Right });
        value = new TextBlock
        {
            Text = "0", FontSize = 22, FontWeight = FontWeights.Bold, Foreground = valColor,
            HorizontalAlignment = HorizontalAlignment.Right, FontFamily = new FontFamily("Consolas"),
        };
        s.Children.Add(value);
        return s;
    }

    private UIElement BuildLegend()
    {
        var col = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 32, 150), IsHitTestVisible = false,
        };
        col.Children.Add(new TextBlock
        {
            Text = "SCU BY SIZE", FontSize = 9, Foreground = Brush(0x7C, 0x8A, 0x99),
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 0, 6),
        });
        foreach (var scu in Sizes)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 0, 3) };
            row.Children.Add(new TextBlock
            {
                Text = $"{scu} SCU", FontSize = 10, Foreground = Brush(0xEA, 0xF1, 0xF6),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
            });
            row.Children.Add(new Border
            {
                Width = 12, Height = 12, CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(SizeColor(scu)),
                BorderBrush = Brush(0x30, 0xFF, 0xFF, 0xFF), BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
            });
            col.Children.Add(row);
        }
        return col;
    }

    private UIElement BuildTitle()
    {
        var s = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(32, 24, 0, 0), IsHitTestVisible = false,
        };
        s.Children.Add(new TextBlock { Text = "NEXUS // CARGO PLANNER", FontSize = 11, Foreground = Brush(0x7C, 0x8A, 0x99) });
        s.Children.Add(new TextBlock { Text = "CARGO GRID", FontSize = 18, FontWeight = FontWeights.Bold, Foreground = Brush(0xFF, 0xB2, 0x3E) });
        return s;
    }

    private UIElement BuildCornerBrackets()
    {
        var g = new Grid { IsHitTestVisible = false };
        g.Children.Add(Bracket(HorizontalAlignment.Left, VerticalAlignment.Top, new Thickness(2, 2, 0, 0)));
        g.Children.Add(Bracket(HorizontalAlignment.Right, VerticalAlignment.Top, new Thickness(0, 2, 2, 0)));
        g.Children.Add(Bracket(HorizontalAlignment.Left, VerticalAlignment.Bottom, new Thickness(2, 0, 0, 2)));
        g.Children.Add(Bracket(HorizontalAlignment.Right, VerticalAlignment.Bottom, new Thickness(0, 0, 2, 2)));
        return g;
    }

    private Border Bracket(HorizontalAlignment h, VerticalAlignment v, Thickness t) => new()
    {
        Width = 30, Height = 30, HorizontalAlignment = h, VerticalAlignment = v, Margin = new Thickness(16),
        BorderBrush = Brush(0x80, 0xFF, 0xB2, 0x3E), BorderThickness = t,
    };

    // -- scene building ------------------------------------------------------------

    private void AddLights()
    {
        // Directional key from above so container tops read lighter than their sides (the 06 look);
        // modest ambient keeps the sides from going black.
        var lights = new Model3DGroup();
        lights.Children.Add(new AmbientLight(Color.FromRgb(0x6E, 0x6E, 0x6E)));
        lights.Children.Add(new DirectionalLight(Color.FromRgb(0x8C, 0x8C, 0x8C), new Vector3D(-0.32, -0.9, -0.32)));
        lights.Children.Add(new DirectionalLight(Color.FromRgb(0x22, 0x27, 0x2C), new Vector3D(0.6, -0.2, 0.5)));
        _staticRoot.Children.Add(new ModelVisual3D { Content = lights });
    }

    // Blueprint backdrop (02): an expansive, fine, dim floor lattice plus two very faint schematic
    // walls. No bright grid-volume wireframe (the mock has none). Transparent grids go in the backdrop
    // root, drawn AFTER the boxes so they blend over the containers instead of depth-culling them.
    private void AddBackdrop(double spanX, double maxZ, double wallHeight)
    {
        double reach = Math.Max(spanX, maxZ) + 16;         // extend well past the hold, like the mockup
        double cxx = spanX / 2, czz = maxZ / 2;
        double fx0 = cxx - reach, fx1 = cxx + reach, fz0 = czz - reach, fz1 = czz + reach;

        AddPlane(_staticRoot, new(fx0, -0.05, fz1), new(fx1, -0.05, fz1), new(fx1, -0.05, fz0), new(fx0, -0.05, fz0), DarkFloorMaterial);

        double step = Cell / 2;
        int fA = Cells((fx1 - fx0) / step), fD = Cells((fz1 - fz0) / step);
        AddPlane(_backdropRoot, new(fx0, -0.02, fz1), new(fx1, -0.02, fz1), new(fx1, -0.02, fz0), new(fx0, -0.02, fz0),
            GridMaterial(fA, fD, 8, 0.4, wall: false));

        // Faint back (-Z) and left (-X) schematic walls framing the hold.
        double wx0 = -2 * Cell, wx1 = spanX + 2 * Cell, wz0 = -2 * Cell, wz1 = maxZ + 2 * Cell;
        int wX = Cells((wx1 - wx0) / Cell), wZ = Cells((wz1 - wz0) / Cell), wY = Cells(wallHeight / Cell);
        AddPlane(_backdropRoot, new(wx0, 0, wz0), new(wx1, 0, wz0), new(wx1, wallHeight, wz0), new(wx0, wallHeight, wz0),
            GridMaterial(wX, wY, 4, 0.22, wall: true));
        AddPlane(_backdropRoot, new(wx0, 0, wz0), new(wx0, 0, wz1), new(wx0, wallHeight, wz1), new(wx0, wallHeight, wz0),
            GridMaterial(wZ, wY, 4, 0.22, wall: true));
    }

    private static int Cells(double n) => Math.Max(1, (int)Math.Round(n));

    private static void AddPlane(ModelVisual3D root, Point3D a, Point3D b, Point3D c, Point3D d, Material mat)
    {
        var m = new MeshGeometry3D();
        m.Positions.Add(a); m.Positions.Add(b); m.Positions.Add(c); m.Positions.Add(d);
        m.TextureCoordinates.Add(new Point(0, 1)); m.TextureCoordinates.Add(new Point(1, 1));
        m.TextureCoordinates.Add(new Point(1, 0)); m.TextureCoordinates.Add(new Point(0, 0));
        m.TriangleIndices.Add(0); m.TriangleIndices.Add(1); m.TriangleIndices.Add(2);
        m.TriangleIndices.Add(0); m.TriangleIndices.Add(2); m.TriangleIndices.Add(3);
        m.Freeze();
        var model = new GeometryModel3D(m, mat) { BackMaterial = mat };
        root.Children.Add(new ModelVisual3D { Content = model });
    }

    private static Material GridMaterial(int across, int down, int majorEvery, double opacity, bool wall)
    {
        var brush = new ImageBrush(GridBitmap(across, down, majorEvery, wall))
        {
            Stretch = Stretch.Fill, TileMode = TileMode.None, Opacity = opacity,
        };
        brush.Freeze();
        return Frozen(new EmissiveMaterial(brush));
    }

    private static readonly Dictionary<(int, int, int, bool), BitmapSource> GridBmpCache = new();

    private static BitmapSource GridBitmap(int across, int down, int majorEvery, bool wall)
    {
        across = Math.Clamp(across, 1, 220);
        down = Math.Clamp(down, 1, 220);
        var key = (across, down, majorEvery, wall);
        if (GridBmpCache.TryGetValue(key, out var cached)) return cached;
        var bmp = BuildGridBitmap(across, down, majorEvery, wall);
        GridBmpCache[key] = bmp;
        return bmp;
    }

    // Floor: dim minor line per cell, slightly brighter major every majorEvery. Wall: a uniform dim grid.
    private static BitmapSource BuildGridBitmap(int across, int down, int majorEvery, bool wall)
    {
        int cellPx = Math.Clamp(2048 / Math.Max(across, down), 4, 20);
        int w = across * cellPx, h = down * cellPx;

        Pen line, major;
        if (wall)
        {
            line = new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x0F, 0x28, 0x30))), 1); line.Freeze();
            major = line;
        }
        else
        {
            line = new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x14, 0x33, 0x3B))), 1); line.Freeze();
            major = new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x24, 0x55, 0x5A))), 1.4); major.Freeze();
        }

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            for (int i = 0; i <= across; i++)
            {
                double x = Math.Min(i * cellPx + 0.5, w - 0.5);
                dc.DrawLine(!wall && i % majorEvery == 0 ? major : line, new Point(x, 0), new Point(x, h));
            }
            for (int j = 0; j <= down; j++)
            {
                double y = Math.Min(j * cellPx + 0.5, h - 0.5);
                dc.DrawLine(!wall && j % majorEvery == 0 ? major : line, new Point(0, y), new Point(w, y));
            }
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    // Cell (x=W, y=D, z=H vertical) maps to world (x = offset + X*Cell, y = Z*Cell up, z = Y*Cell).
    private Model3D BuildBox(double offsetX, Placement p)
    {
        const double inset = 0.06;   // gap between flush boxes reads as a thin dark seam
        var scale = new ScaleTransform3D(
            (p.Size.W - inset) * Cell, (p.Size.H - inset) * Cell, (p.Size.D - inset) * Cell);
        var translate = new TranslateTransform3D(
            offsetX + (p.X + inset / 2) * Cell, (p.Z + inset / 2) * Cell, (p.Y + inset / 2) * Cell);
        var xform = new Transform3DGroup();
        xform.Children.Add(scale);
        xform.Children.Add(translate);

        var mat = MaterialFor(p.Scu);
        return new GeometryModel3D(UnitCube, mat) { Transform = xform, BackMaterial = mat };
    }

    private void AddConnector(double offsetX, Placement p, int tierIndex)
    {
        const double t = 0.03;
        double cx = offsetX + (p.X + p.Size.W / 2.0) * Cell;
        double cz = (p.Y + p.Size.D / 2.0) * Cell;

        var scale = new ScaleTransform3D(t, 0.0001, t);
        var group = new Transform3DGroup();
        group.Children.Add(scale);
        group.Children.Add(new TranslateTransform3D(cx - t / 2, 0, cz - t / 2));

        var brush = new SolidColorBrush(Color.FromRgb(0x7F, 0xE9, 0xE0)) { Opacity = 0 };
        var mat = new EmissiveMaterial(brush);
        var model = new GeometryModel3D(UnitCube, mat) { Transform = group, BackMaterial = mat };
        _boxRoot.Children.Add(new ModelVisual3D { Content = model });
        _connectors.Add(new Connector { TierIndex = tierIndex, BaseY = p.Z * Cell, Scale = scale, Brush = brush });
    }

    private static Material MaterialFor(int scu) =>
        BoxMaterials.TryGetValue(scu, out var m) ? m : BoxMaterials[1];

    // -- static resources ----------------------------------------------------------

    // Plain matte container face: the size colour with a thin dark edge so adjacent boxes read as
    // separate. No labels are baked here - the size labels float above the boxes as 2D pills.
    private static Dictionary<int, Material> BuildBoxMaterials()
    {
        var map = new Dictionary<int, Material>();
        foreach (var scu in Sizes)
        {
            var tex = RenderBoxFace(SizeColor(scu));
            var brush = new ImageBrush(tex) { Stretch = Stretch.Fill, TileMode = TileMode.None };
            brush.Freeze();
            map[scu] = Frozen(new DiffuseMaterial(brush));
        }
        return map;
    }

    private static BitmapSource RenderBoxFace(Color color)
    {
        const int px = 128;
        var root = new Grid { Width = px, Height = px, Background = new SolidColorBrush(color) };
        root.Children.Add(new Border { BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x05, 0x07, 0x0A)), BorderThickness = new Thickness(2) });
        root.Measure(new Size(px, px));
        root.Arrange(new Rect(0, 0, px, px));
        root.UpdateLayout();

        var rtb = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(root);
        rtb.Freeze();
        return rtb;
    }

    // SCU-by-size ramp, copied verbatim from the synthesis mockup's SIZE_COLOR (06 direction).
    private static Color SizeColor(int scu) => scu switch
    {
        1 => Color.FromRgb(0x7F, 0xE9, 0xE0),
        2 => Color.FromRgb(0x66, 0xE6, 0xA6),
        4 => Color.FromRgb(0x8F, 0xD0, 0xE8),
        8 => Color.FromRgb(0xB9, 0xC4, 0xD0),
        16 => Color.FromRgb(0xFF, 0xD0, 0x89),
        24 => Color.FromRgb(0xFF, 0xB2, 0x3E),
        _ => Color.FromRgb(0xF0, 0x8A, 0x4B),
    };

    private static MeshGeometry3D BuildBoxMesh(params int[] faceIdx)
    {
        var m = new MeshGeometry3D();
        int b = 0;
        foreach (var idx in faceIdx)
        {
            var f = CubeFaces[idx];
            m.Positions.Add(f.a); m.Positions.Add(f.b); m.Positions.Add(f.c); m.Positions.Add(f.d);
            m.TextureCoordinates.Add(new Point(0, 1)); m.TextureCoordinates.Add(new Point(1, 1));
            m.TextureCoordinates.Add(new Point(1, 0)); m.TextureCoordinates.Add(new Point(0, 0));
            m.TriangleIndices.Add(b); m.TriangleIndices.Add(b + 1); m.TriangleIndices.Add(b + 2);
            m.TriangleIndices.Add(b); m.TriangleIndices.Add(b + 2); m.TriangleIndices.Add(b + 3);
            b += 4;
        }
        m.Freeze();
        return m;
    }

    private static SolidColorBrush Brush(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));
    private static SolidColorBrush Brush(byte a, byte r, byte g, byte b) => new(Color.FromArgb(a, r, g, b));

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    // -- mouse camera --------------------------------------------------------------

    private void OnLeftDown(object s, MouseButtonEventArgs e)
    {
        _orbiting = true; _lastMouse = e.GetPosition(this); CaptureMouse();
    }

    private void OnRightDown(object s, MouseButtonEventArgs e)
    {
        _panning = true; _lastMouse = e.GetPosition(this); CaptureMouse();
    }

    private void OnMove(object s, MouseEventArgs e)
    {
        if (!_orbiting && !_panning) return;
        var p = e.GetPosition(this);
        double dx = p.X - _lastMouse.X, dy = p.Y - _lastMouse.Y;
        _lastMouse = p;
        if (_orbiting) _camera.Orbit(dx * 0.01, -dy * 0.01);   // invert Y: drag up tilts the view up
        else if (_panning) _camera.Pan(dx, dy, Math.Max(1, ActualWidth));
        UpdateLabels();
    }

    private void OnUp(object s, MouseButtonEventArgs e)
    {
        _orbiting = _panning = false; ReleaseMouseCapture();
    }

    private void OnWheel(object s, MouseWheelEventArgs e) { _camera.Zoom(e.Delta); UpdateLabels(); }
}
