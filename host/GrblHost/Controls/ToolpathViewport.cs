using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using GrblHost.Core.GCode;

namespace GrblHost.Controls;

/// <summary>
/// 3D view of a toolpath over the machine table. Depth levels are meshed in
/// the background, one model per level; the level being cut (simulation or
/// live job) is meshed again up to the current move. The table grid lies
/// just below the deepest cut. Left mouse button
/// rotates, right or middle button pans, the wheel zooms, a double click
/// resets the view.
/// </summary>
public sealed class ToolpathViewport : Border
{
    // ---------------------------------------------------------------- properties

    public static readonly DependencyProperty ToolpathProperty = DependencyProperty.Register(
        nameof(Toolpath), typeof(Toolpath), typeof(ToolpathViewport),
        new PropertyMetadata(null, (d, _) => ((ToolpathViewport)d).OnToolpathChanged()));

    public static readonly DependencyProperty MaxLayerProperty = DependencyProperty.Register(
        nameof(MaxLayer), typeof(int), typeof(ToolpathViewport),
        new PropertyMetadata(int.MaxValue, OnViewChanged));

    public static readonly DependencyProperty SegmentLimitProperty = DependencyProperty.Register(
        nameof(SegmentLimit), typeof(int), typeof(ToolpathViewport),
        new PropertyMetadata(int.MaxValue, OnViewChanged));

    public static readonly DependencyProperty ShowRapidsProperty = DependencyProperty.Register(
        nameof(ShowRapids), typeof(bool), typeof(ToolpathViewport),
        new PropertyMetadata(false, OnViewChanged));

    public static readonly DependencyProperty ToolPositionProperty = DependencyProperty.Register(
        nameof(ToolPosition), typeof(Point3D), typeof(ToolpathViewport),
        new PropertyMetadata(new Point3D(), (d, _) => ((ToolpathViewport)d).UpdateTool()));

    public static readonly DependencyProperty ShowToolProperty = DependencyProperty.Register(
        nameof(ShowTool), typeof(bool), typeof(ToolpathViewport),
        new PropertyMetadata(false, (d, _) => ((ToolpathViewport)d).UpdateTool()));

    public static readonly DependencyProperty BedWidthProperty = DependencyProperty.Register(
        nameof(BedWidth), typeof(double), typeof(ToolpathViewport),
        new PropertyMetadata(220.0, (d, _) => ((ToolpathViewport)d).BuildBed()));

    public static readonly DependencyProperty BedDepthProperty = DependencyProperty.Register(
        nameof(BedDepth), typeof(double), typeof(ToolpathViewport),
        new PropertyMetadata(180.0, (d, _) => ((ToolpathViewport)d).BuildBed()));

    public static readonly DependencyProperty HeightMapProperty = DependencyProperty.Register(
        nameof(HeightMap), typeof(HeightMap), typeof(ToolpathViewport),
        new PropertyMetadata(null, (d, _) => ((ToolpathViewport)d).BuildHeightMap()));

    public static readonly DependencyProperty HeightMapVersionProperty = DependencyProperty.Register(
        nameof(HeightMapVersion), typeof(int), typeof(ToolpathViewport),
        new PropertyMetadata(0, (d, _) => ((ToolpathViewport)d).BuildHeightMap()));

    public static readonly DependencyProperty ShowHeightMapProperty = DependencyProperty.Register(
        nameof(ShowHeightMap), typeof(bool), typeof(ToolpathViewport),
        new PropertyMetadata(true, (d, _) => ((ToolpathViewport)d).BuildHeightMap()));

    /// <summary>Probed surface, drawn over the work area (heights to scale, colored low blue … high red).</summary>
    public HeightMap? HeightMap
    {
        get => (HeightMap?)GetValue(HeightMapProperty);
        set => SetValue(HeightMapProperty, value);
    }

    /// <summary>Changes when points of the map were probed: draw it again.</summary>
    public int HeightMapVersion
    {
        get => (int)GetValue(HeightMapVersionProperty);
        set => SetValue(HeightMapVersionProperty, value);
    }

    public bool ShowHeightMap
    {
        get => (bool)GetValue(ShowHeightMapProperty);
        set => SetValue(ShowHeightMapProperty, value);
    }

    public Toolpath? Toolpath
    {
        get => (Toolpath?)GetValue(ToolpathProperty);
        set => SetValue(ToolpathProperty, value);
    }

    /// <summary>Highest layer shown (zero-based).</summary>
    public int MaxLayer
    {
        get => (int)GetValue(MaxLayerProperty);
        set => SetValue(MaxLayerProperty, value);
    }

    /// <summary>Only segments with a smaller index are shown (simulation / job progress).</summary>
    public int SegmentLimit
    {
        get => (int)GetValue(SegmentLimitProperty);
        set => SetValue(SegmentLimitProperty, value);
    }

    public bool ShowRapids
    {
        get => (bool)GetValue(ShowRapidsProperty);
        set => SetValue(ShowRapidsProperty, value);
    }

    public Point3D ToolPosition
    {
        get => (Point3D)GetValue(ToolPositionProperty);
        set => SetValue(ToolPositionProperty, value);
    }

    public bool ShowTool
    {
        get => (bool)GetValue(ShowToolProperty);
        set => SetValue(ShowToolProperty, value);
    }

    public double BedWidth
    {
        get => (double)GetValue(BedWidthProperty);
        set => SetValue(BedWidthProperty, value);
    }

    public double BedDepth
    {
        get => (double)GetValue(BedDepthProperty);
        set => SetValue(BedDepthProperty, value);
    }

    private static void OnViewChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((ToolpathViewport)d).ScheduleUpdate();

    // ---------------------------------------------------------------- scene

    private readonly Viewport3D _viewport = new() { ClipToBounds = true, IsHitTestVisible = false };
    private readonly PerspectiveCamera _camera = new() { FieldOfView = 40, NearPlaneDistance = 1, FarPlaneDistance = 20000, UpDirection = new Vector3D(0, 0, 1) };
    private readonly Model3DGroup _bedGroup = new();
    private readonly Model3DGroup _cutGroup = new();
    private readonly Model3DGroup _rapidGroup = new();
    private readonly Model3DGroup _partialGroup = new();
    private readonly Model3DGroup _mapGroup = new();
    private readonly GeometryModel3D _tool;
    private readonly TranslateTransform3D _toolTransform = new();
    private readonly Material _pathMaterial;

    private GeometryModel3D?[] _layerCut = [];
    private GeometryModel3D?[] _layerRapid = [];
    private int _buildVersion;
    private CancellationTokenSource? _buildCts;

    private int _shownCut = -1, _shownRapid = -1, _shownVersion = -1;
    private (int Layer, int Limit, bool Rapids, Toolpath? Tp) _partialKey = (-1, -1, false, null);
    private bool _updatePending;

    // Orbit camera.
    private Point3D _target = new(110, 90, 0);
    private double _yaw = -60, _pitch = 35, _distance = 350;
    private Point _lastMouse;
    private MouseButton? _dragButton;

    public ToolpathViewport()
    {
        Background = Brushes.Transparent;
        ClipToBounds = true;
        Focusable = true;

        var texture = FeaturePalette.CreateTexture();
        var brush = new ImageBrush(texture)
        {
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, 1, 1),
            Stretch = Stretch.Fill,
        };
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.NearestNeighbor);
        brush.Freeze();
        var group = new MaterialGroup();
        group.Children.Add(new DiffuseMaterial(brush));
        group.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), 30));
        group.Freeze();
        _pathMaterial = group;

        var toolMaterial = ToolpathMesh.Solid(Color.FromRgb(0xE0, 0xE6, 0xEA), 40);
        _tool = new GeometryModel3D(ToolpathMesh.CreateTool(), toolMaterial)
        {
            BackMaterial = toolMaterial,
            Transform = _toolTransform,
        };

        var lights = new Model3DGroup();
        lights.Children.Add(new AmbientLight(Color.FromRgb(0x5A, 0x5A, 0x5A)));
        lights.Children.Add(new DirectionalLight(Color.FromRgb(0xB0, 0xB0, 0xB0), new Vector3D(-0.6, -0.8, -1.4)));
        lights.Children.Add(new DirectionalLight(Color.FromRgb(0x50, 0x50, 0x58), new Vector3D(0.8, 0.6, -0.4)));

        var root = new Model3DGroup();
        root.Children.Add(lights);
        root.Children.Add(_bedGroup);
        root.Children.Add(_cutGroup);
        root.Children.Add(_rapidGroup);
        root.Children.Add(_partialGroup);
        root.Children.Add(_mapGroup);
        _viewport.Camera = _camera;
        _viewport.Children.Add(new ModelVisual3D { Content = root });
        Child = _viewport;

        BuildBed();
        ResetView();
        ViewColors.Follow(this, BuildBed);
    }

    // ---------------------------------------------------------------- camera

    /// <summary>Look at the toolpath (or the table) from the front right.</summary>
    public void ResetView() => SetView(-60, 35);

    public void ViewTop() => SetView(-90, 89);

    public void ViewFront() => SetView(-90, 8);

    public void ViewIso() => SetView(-45, 35);

    private void SetView(double yaw, double pitch)
    {
        var tp = Toolpath;
        double size;
        if (tp != null && tp.Segments.Length > 0 && tp.Max.X > tp.Min.X)
        {
            _target = new Point3D((tp.Min.X + tp.Max.X) / 2, (tp.Min.Y + tp.Max.Y) / 2, (tp.Min.Z + tp.Max.Z) / 2);
            size = Math.Max(Math.Max(tp.Max.X - tp.Min.X, tp.Max.Y - tp.Min.Y), tp.Max.Z - tp.Min.Z);
            size = Math.Max(size, 20);
            _distance = size * 2.4;
        }
        else
        {
            _target = new Point3D(BedWidth / 2, BedDepth / 2, 0);
            size = Math.Max(BedWidth, BedDepth);
            _distance = size * 1.6;
        }
        _yaw = yaw;
        _pitch = pitch;
        UpdateCamera();
    }

    /// <summary>Show the whole work area.</summary>
    public void ViewBed()
    {
        _target = new Point3D(BedWidth / 2, BedDepth / 2, 0);
        _distance = Math.Max(BedWidth, BedDepth) * 1.6;
        UpdateCamera();
    }

    private void UpdateCamera()
    {
        double yaw = _yaw * Math.PI / 180, pitch = _pitch * Math.PI / 180;
        var dir = new Vector3D(Math.Cos(pitch) * Math.Cos(yaw), Math.Cos(pitch) * Math.Sin(yaw), Math.Sin(pitch));
        _camera.Position = _target + dir * _distance;
        _camera.LookDirection = -dir * _distance;
        _camera.NearPlaneDistance = Math.Max(0.1, _distance / 200);
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.ClickCount == 2 && e.ChangedButton == MouseButton.Left)
        {
            ResetView();
            return;
        }
        _dragButton = e.ChangedButton;
        _lastMouse = e.GetPosition(this);
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (_dragButton == e.ChangedButton)
        {
            _dragButton = null;
            ReleaseMouseCapture();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragButton == null)
            return;
        var p = e.GetPosition(this);
        var d = p - _lastMouse;
        _lastMouse = p;
        if (_dragButton == MouseButton.Left && Keyboard.Modifiers != ModifierKeys.Shift)
        {
            _yaw -= d.X * 0.4;
            _pitch = Math.Clamp(_pitch + d.Y * 0.4, -89, 89);
        }
        else
        {
            // Pan in the camera plane.
            var look = _camera.LookDirection;
            look.Normalize();
            var right = Vector3D.CrossProduct(look, new Vector3D(0, 0, 1));
            if (right.Length < 1e-6)
                right = new Vector3D(1, 0, 0);
            right.Normalize();
            var up = Vector3D.CrossProduct(right, look);
            double k = _distance * 0.0016;
            _target += (-right * d.X + up * d.Y) * k;
        }
        UpdateCamera();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        _distance = Math.Clamp(_distance * (e.Delta > 0 ? 0.87 : 1.15), 5, 5000);
        UpdateCamera();
        e.Handled = true;
    }

    // ---------------------------------------------------------------- table

    private void BuildBed()
    {
        _bedGroup.Children.Clear();
        double w = BedWidth, d = BedDepth;
        // Below the deepest cut, so the grid doesn't hide it.
        double z0 = Math.Min(0, Toolpath?.Min.Z ?? 0) - 0.3;
        var tableShift = new TranslateTransform3D(0, 0, z0);
        tableShift.Freeze();

        var plate = new MeshGeometry3D();
        ToolpathMesh.AddLine(plate, 0, d / 2, w, d / 2, -0.06, d);
        var plateMat = ToolpathMesh.Solid(ViewColors.Bed);
        _bedGroup.Children.Add(new GeometryModel3D(plate, plateMat) { BackMaterial = plateMat, Transform = tableShift });

        var minor = new MeshGeometry3D();
        var major = new MeshGeometry3D();
        for (int x = 0; x <= (int)w; x += 10)
            ToolpathMesh.AddLine(x % 50 == 0 ? major : minor, x, 0, x, d, -0.02, x % 50 == 0 ? 0.45 : 0.25);
        for (int y = 0; y <= (int)d; y += 10)
            ToolpathMesh.AddLine(y % 50 == 0 ? major : minor, 0, y, w, y, -0.02, y % 50 == 0 ? 0.45 : 0.25);
        // Table outline.
        ToolpathMesh.AddLine(major, 0, 0, w, 0, -0.01, 0.8);
        ToolpathMesh.AddLine(major, w, 0, w, d, -0.01, 0.8);
        ToolpathMesh.AddLine(major, w, d, 0, d, -0.01, 0.8);
        ToolpathMesh.AddLine(major, 0, d, 0, 0, -0.01, 0.8);
        _bedGroup.Children.Add(new GeometryModel3D(minor, ToolpathMesh.Solid(ViewColors.Grid)) { Transform = tableShift });
        _bedGroup.Children.Add(new GeometryModel3D(major, ToolpathMesh.Solid(ViewColors.GridMajor)) { Transform = tableShift });

        // Axes at the work zero: X red, Y green, Z blue.
        AddAxis(new Point3D(0, -0.6, 0), new Point3D(25, 0.6, 1.2), Color.FromRgb(0xE5, 0x39, 0x35));
        AddAxis(new Point3D(-0.6, 0, 0), new Point3D(0.6, 25, 1.2), Color.FromRgb(0x43, 0xA0, 0x47));
        AddAxis(new Point3D(-0.6, -0.6, 0), new Point3D(0.6, 0.6, 25), Color.FromRgb(0x1E, 0x88, 0xE5));

        if (ShowTool)
            _bedGroup.Children.Add(_tool);
    }

    private void AddAxis(Point3D a, Point3D b, Color c)
    {
        var mesh = new MeshGeometry3D();
        ToolpathMesh.AddBox(mesh, a, b);
        var m = ToolpathMesh.Solid(c);
        _bedGroup.Children.Add(new GeometryModel3D(mesh, m) { BackMaterial = m });
    }

    private void UpdateTool()
    {
        var p = ToolPosition;
        _toolTransform.OffsetX = p.X;
        _toolTransform.OffsetY = p.Y;
        _toolTransform.OffsetZ = p.Z;
        bool inScene = _bedGroup.Children.Contains(_tool);
        if (ShowTool && !inScene)
            _bedGroup.Children.Add(_tool);
        else if (!ShowTool && inScene)
            _bedGroup.Children.Remove(_tool);
    }

    // ---------------------------------------------------------------- height map

    private void BuildHeightMap()
    {
        _mapGroup.Children.Clear();
        var map = HeightMap;
        if (map == null || !ShowHeightMap)
            return;

        // Surface: the interpolation over the probed part, a few cells per grid
        // step; the texture coordinate carries the height for the color.
        double min = map.Min, max = map.Max;
        double range = Math.Max(max - min, 1e-6);
        if (map.ProbedCount >= map.PointsX * map.PointsY)
        {
            const int sub = 4;
            int nx = (map.PointsX - 1) * sub + 1, ny = (map.PointsY - 1) * sub + 1;
            var mesh = new MeshGeometry3D();
            for (int j = 0; j < ny; j++)
            {
                for (int i = 0; i < nx; i++)
                {
                    double x = map.X + map.Width * i / (nx - 1), y = map.Y + map.Height * j / (ny - 1);
                    double z = map.At(x, y);
                    mesh.Positions.Add(new Point3D(x, y, z));
                    mesh.TextureCoordinates.Add(new Point((z - min) / range, 0.5));
                }
            }
            for (int j = 0; j < ny - 1; j++)
            {
                for (int i = 0; i < nx - 1; i++)
                {
                    int a = j * nx + i, b = a + 1, c = a + nx, d = c + 1;
                    mesh.TriangleIndices.Add(a); mesh.TriangleIndices.Add(b); mesh.TriangleIndices.Add(d);
                    mesh.TriangleIndices.Add(a); mesh.TriangleIndices.Add(d); mesh.TriangleIndices.Add(c);
                }
            }
            var gradient = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0.5),
                EndPoint = new Point(1, 0.5),
                Opacity = 0.55,
            };
            gradient.GradientStops.Add(new GradientStop(Color.FromRgb(0x25, 0x63, 0xEB), 0));
            gradient.GradientStops.Add(new GradientStop(Color.FromRgb(0x22, 0xC5, 0x5E), 0.5));
            gradient.GradientStops.Add(new GradientStop(Color.FromRgb(0xEF, 0x44, 0x44), 1));
            gradient.Freeze();
            var material = new DiffuseMaterial(gradient);
            material.Freeze();
            _mapGroup.Children.Add(new GeometryModel3D(mesh, material) { BackMaterial = material });
        }

        // Points: probed ones as small cubes at their height, the rest flat
        // and grey at Z 0.
        var done = new MeshGeometry3D();
        var todo = new MeshGeometry3D();
        const double r = 0.6;
        for (int i = 0; i < map.PointsX; i++)
        {
            for (int j = 0; j < map.PointsY; j++)
            {
                double x = map.PointX(i), y = map.PointY(j), z = map[i, j];
                if (double.IsNaN(z))
                    ToolpathMesh.AddBox(todo, new Point3D(x - r, y - r, -0.05), new Point3D(x + r, y + r, 0.05));
                else
                    ToolpathMesh.AddBox(done, new Point3D(x - r, y - r, z - r), new Point3D(x + r, y + r, z + r));
            }
        }
        var doneMat = ToolpathMesh.Solid(Color.FromRgb(0xF5, 0x9E, 0x0B));
        var todoMat = ToolpathMesh.Solid(Color.FromRgb(0x9C, 0xA3, 0xAF));
        if (done.Positions.Count > 0)
            _mapGroup.Children.Add(new GeometryModel3D(done, doneMat) { BackMaterial = doneMat });
        if (todo.Positions.Count > 0)
            _mapGroup.Children.Add(new GeometryModel3D(todo, todoMat) { BackMaterial = todoMat });
    }

    // ---------------------------------------------------------------- toolpath

    private void OnToolpathChanged()
    {
        _buildCts?.Cancel();
        _buildCts = null;
        _cutGroup.Children.Clear();
        _rapidGroup.Children.Clear();
        _partialGroup.Children.Clear();
        _partialKey = (-1, -1, false, null);
        _shownCut = _shownRapid = -1;
        _buildVersion++;
        BuildBed();

        var tp = Toolpath;
        if (tp == null || tp.Layers.Count == 0)
        {
            _layerCut = [];
            _layerRapid = [];
            ResetView();
            return;
        }
        _layerCut = new GeometryModel3D?[tp.Layers.Count];
        _layerRapid = new GeometryModel3D?[tp.Layers.Count];
        ResetView();

        var cts = new CancellationTokenSource();
        _buildCts = cts;
        var material = _pathMaterial;
        var dispatcher = Dispatcher;
        int version = _buildVersion;
        Task.Run(() => BuildLayers(tp, material, dispatcher, version, cts.Token));
    }

    private void BuildLayers(Toolpath tp, Material material, Dispatcher dispatcher, int version,
        CancellationToken cancel)
    {
        var batch = new List<(int Layer, GeometryModel3D? Cut, GeometryModel3D? Rapid)>();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        foreach (var layer in tp.Layers)
        {
            if (cancel.IsCancellationRequested)
                return;
            batch.Add((layer.Index,
                Model(ToolpathMesh.Build(tp, layer.FirstSegment, layer.EndSegment, false), material),
                Model(ToolpathMesh.Build(tp, layer.FirstSegment, layer.EndSegment, true), material)));
            if (watch.ElapsedMilliseconds > 150 || layer.Index == tp.Layers.Count - 1)
            {
                var items = batch.ToArray();
                batch.Clear();
                watch.Restart();
                dispatcher.InvokeAsync(() =>
                {
                    if (version != _buildVersion)
                        return;
                    foreach (var (i, ex, tr) in items)
                    {
                        _layerCut[i] = ex;
                        _layerRapid[i] = tr;
                    }
                    _shownVersion = -1;            // Models changed, resync the groups.
                    ScheduleUpdate();
                }, DispatcherPriority.Background);
            }
        }
    }

    private static GeometryModel3D? Model(MeshGeometry3D? mesh, Material material)
    {
        if (mesh == null)
            return null;
        var m = new GeometryModel3D(mesh, material) { BackMaterial = material };
        m.Freeze();
        return m;
    }

    private void ScheduleUpdate()
    {
        if (_updatePending)
            return;
        _updatePending = true;
        Dispatcher.InvokeAsync(() =>
        {
            _updatePending = false;
            UpdateVisibility();
        }, DispatcherPriority.Render);
    }

    private void UpdateVisibility()
    {
        var tp = Toolpath;
        if (tp == null || tp.Layers.Count == 0)
            return;

        int total = tp.Segments.Length;
        int limit = Math.Clamp(SegmentLimit, 0, total);
        int maxLayer = Math.Clamp(MaxLayer, 0, tp.Layers.Count - 1);
        int full;
        int partial = -1;
        if (limit < total)
        {
            int current = limit > 0 ? tp.Segments[limit - 1].Layer : 0;
            if (current <= maxLayer)
            {
                full = current;
                partial = limit > tp.Layers[current].FirstSegment ? current : -1;
                if (partial >= 0 && limit >= tp.Layers[current].EndSegment)
                {
                    full = current + 1;
                    partial = -1;
                }
            }
            else
            {
                full = maxLayer + 1;
            }
        }
        else
        {
            full = maxLayer + 1;
        }

        Sync(_cutGroup, _layerCut, full, ref _shownCut);
        Sync(_rapidGroup, _layerRapid, ShowRapids ? full : 0, ref _shownRapid);
        _shownVersion = _buildVersion;

        // The level being cut, up to the current move.
        var key = (partial, partial >= 0 ? limit : -1, ShowRapids, (Toolpath?)tp);
        if (key != _partialKey)
        {
            _partialKey = key;
            _partialGroup.Children.Clear();
            if (partial >= 0)
            {
                var layer = tp.Layers[partial];
                var ex = Model(ToolpathMesh.Build(tp, layer.FirstSegment, limit, false), _pathMaterial);
                if (ex != null)
                    _partialGroup.Children.Add(ex);
                if (ShowRapids)
                {
                    var tr = Model(ToolpathMesh.Build(tp, layer.FirstSegment, limit, true), _pathMaterial);
                    if (tr != null)
                        _partialGroup.Children.Add(tr);
                }
            }
        }
    }

    /// <summary>Make the group hold the models of layers [0, count).</summary>
    private void Sync(Model3DGroup group, GeometryModel3D?[] models, int count, ref int shown)
    {
        count = Math.Min(count, models.Length);
        if (count == shown && _shownVersion == _buildVersion)
            return;
        if (count > shown && shown >= 0 && _shownVersion == _buildVersion)
        {
            for (int i = shown; i < count; i++)
                if (models[i] != null)
                    group.Children.Add(models[i]!);
        }
        else if (count < shown && _shownVersion == _buildVersion)
        {
            // Layers without moves of this kind have no model: count the real ones.
            int keep = 0;
            for (int i = 0; i < count; i++)
                if (models[i] != null)
                    keep++;
            while (group.Children.Count > keep)
                group.Children.RemoveAt(group.Children.Count - 1);
        }
        else
        {
            var children = new Model3DCollection(count);
            for (int i = 0; i < count; i++)
                if (models[i] != null)
                    children.Add(models[i]!);
            group.Children = children;
        }
        shown = count;
    }
}
