using System.Numerics;

namespace GrblHost.Core.GCode;

/// <summary>Machine limits used for the time estimate (grbl settings).</summary>
public sealed class ToolpathOptions
{
    /// <summary>Maximum rates, mm/min ($110 … $112).</summary>
    public double MaxRateX { get; set; } = 1000;
    public double MaxRateY { get; set; } = 1000;
    public double MaxRateZ { get; set; } = 600;
    /// <summary>Accelerations, mm/s² ($120 … $122).</summary>
    public double AccelX { get; set; } = 50;
    public double AccelY { get; set; } = 50;
    public double AccelZ { get; set; } = 50;
    /// <summary>Junction deviation, mm ($11).</summary>
    public double JunctionDeviation { get; set; } = 0.01;
    /// <summary>Arc tolerance, mm ($12).</summary>
    public double ArcTolerance { get; set; } = 0.002;
    /// <summary>Feed used before the first F word, mm/min (grbl would answer error 22).</summary>
    public double DefaultFeed { get; set; } = 100;
    /// <summary>Upper limit of the segments of one arc in the preview.</summary>
    public int MaxArcSegments { get; set; } = 2000;
}

/// <summary>
/// Interprets CNC G-code (the grbl dialect) into a <see cref="Toolpath"/>:
/// modal motion (lines with only coordinates continue the last G0/G1/G2/G3),
/// G90/G91, G20/G21, G17/G18/G19 arcs in all planes, G90.1/G91.1, G92
/// offsets, G53, G93 inverse time; it estimates the time of every move with
/// grbl's acceleration and junction deviation model.
/// </summary>
public sealed class ToolpathBuilder
{
    private readonly ToolpathOptions _opt;
    private readonly List<ToolpathSegment> _segments = new();
    private readonly List<ToolpathLayer> _layers = new();
    private readonly List<int> _unsupported = new();
    private readonly ModalState _m = new();

    // Time estimate.
    private double _time;
    private double _pendingDelay;
    private int _open = -1;
    private double _openEntry;
    private Vector3 _openDir;
    private double _openSpeed;
    private double _openAccel;

    private float _layerZ = float.NaN;
    private int _firstAfterCut;
    private double _cut, _rapid;
    private float _maxSpindle, _maxFeed;
    private Vector3 _min = new(float.MaxValue);
    private Vector3 _max = new(float.MinValue);
    private int _line;

    public ToolpathBuilder(ToolpathOptions? options = null)
    {
        _opt = options ?? new ToolpathOptions();
    }

    /// <summary>Modal state after the lines processed so far.</summary>
    public ModalState State => _m;

    public static Toolpath Build(IReadOnlyList<string> lines, ToolpathOptions? options = null,
        CancellationToken cancel = default, IProgress<double>? progress = null)
    {
        var b = new ToolpathBuilder(options);
        int n = lines.Count;
        int step = Math.Max(1, n / 100);
        for (int i = 0; i < n; i++)
        {
            if (i % step == 0)
            {
                cancel.ThrowIfCancellationRequested();
                progress?.Report((double)i / n);
            }
            b.ProcessLine(i, lines[i]);
        }
        return b.Finish(n);
    }

    public void ProcessLine(int lineIndex, string text)
    {
        _line = lineIndex;
        if (text.Length == 0)
            return;
        var b = GCodeBlock.Parse(text);
        if (b.IsEmpty)
            return;

        var start = _m.Position;
        var step = _m.Apply(b);
        if (step.Unsupported)
            _unsupported.Add(lineIndex);
        if (step.Dwell > 0)
        {
            CloseOpen(0);
            _pendingDelay += step.Dwell;
        }
        if (step.Motion == null)
            return;

        float spindle = (float)(_m.SpindleOn ? _m.SpindleSpeed : 0);
        switch (step.Motion)
        {
            case MotionMode.Rapid:
                AddMove(start, _m.Position, MaxRate(start, _m.Position), MoveKind.Rapid, FeatureType.Rapid, spindle);
                break;
            case MotionMode.Linear:
            case MotionMode.Probe:
            {
                var end = _m.Position;
                double feed = FeedFor(start, end, step.InverseTimeFeed);
                var feature = step.Motion == MotionMode.Probe ? FeatureType.Probe : Classify(start, end);
                AddMove(start, end, feed, MoveKind.Feed, feature, spindle);
                break;
            }
            case MotionMode.ArcCw:
            case MotionMode.ArcCcw:
                Arc(start, step, spindle);
                break;
        }
    }

    private static FeatureType Classify(Vector3 a, Vector3 b)
    {
        float dxy = MathF.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
        float dz = b.Z - a.Z;
        if (dxy < 1e-4f && dz < -1e-4f)
            return FeatureType.Plunge;
        if (dxy < 1e-4f && dz > 1e-4f)
            return FeatureType.Retract;
        return FeatureType.Cut;
    }

    /// <summary>Feed in mm/min for a move; G93: the F word is 1/minutes of this move.</summary>
    private double FeedFor(Vector3 a, Vector3 b, double inverseTime)
    {
        if (inverseTime > 0)
            return Vector3.Distance(a, b) * inverseTime;
        return _m.Feed > 0 ? _m.Feed : _opt.DefaultFeed;
    }

    private double MaxRate(Vector3 a, Vector3 b)
    {
        var d = b - a;
        double len = d.Length();
        if (len < 1e-9)
            return _opt.MaxRateX;
        double rate = double.MaxValue;
        if (Math.Abs(d.X) > 1e-9) rate = Math.Min(rate, _opt.MaxRateX * len / Math.Abs(d.X));
        if (Math.Abs(d.Y) > 1e-9) rate = Math.Min(rate, _opt.MaxRateY * len / Math.Abs(d.Y));
        if (Math.Abs(d.Z) > 1e-9) rate = Math.Min(rate, _opt.MaxRateZ * len / Math.Abs(d.Z));
        return rate;
    }

    private void Arc(Vector3 start, ModalState.Step step, float spindle)
    {
        var end = _m.Position;
        // Plane axes: (a, b) in the plane, l the linear axis.
        int a, bAx, l;
        switch (_m.Plane)
        {
            case Plane.XZ: a = 2; bAx = 0; l = 1; break;   // G18: Z, X
            case Plane.YZ: a = 1; bAx = 2; l = 0; break;   // G19: Y, Z
            default: a = 0; bAx = 1; l = 2; break;          // G17: X, Y
        }
        float S(Vector3 v, int i) => i == 0 ? v.X : i == 1 ? v.Y : v.Z;

        double sa = S(start, a), sb = S(start, bAx), ea = S(end, a), eb = S(end, bAx);
        bool cw = step.Motion == MotionMode.ArcCw;
        double ca, cb;
        if (step.ArcRadius is double r)
        {
            double dx = ea - sa, dy = eb - sb;
            double d = Math.Sqrt(dx * dx + dy * dy);
            if (d < 1e-9 || d > 2 * Math.Abs(r) + 1e-4)
            {
                AddMove(start, end, FeedFor(start, end, step.InverseTimeFeed), MoveKind.Feed, FeatureType.Arc, spindle);
                return;
            }
            double h = Math.Sqrt(Math.Max(0, r * r - d * d / 4));
            // grbl: h_x2_div_d negative for CW, R < 0 means the long way round.
            double sign = cw ? -1 : 1;
            if (r < 0)
                sign = -sign;
            ca = sa + dx / 2 - sign * h * dy / d;
            cb = sb + dy / 2 + sign * h * dx / d;
        }
        else
        {
            var off = step.ArcCenter;
            ca = S(off, a);
            cb = S(off, bAx);
        }

        double radius = Math.Sqrt((sa - ca) * (sa - ca) + (sb - cb) * (sb - cb));
        double a0 = Math.Atan2(sb - cb, sa - ca);
        double a1 = Math.Atan2(eb - cb, ea - ca);
        double sweep = a1 - a0;
        if (cw)
        {
            if (sweep >= -1e-9) sweep -= 2 * Math.PI;
        }
        else
        {
            if (sweep <= 1e-9) sweep += 2 * Math.PI;
        }
        if (step.ArcTurns > 1)
            sweep += (cw ? -2 : 2) * Math.PI * (step.ArcTurns - 1);

        // Segments like grbl's mc_arc(): chord error within $12.
        double tol = Math.Max(_opt.ArcTolerance, 1e-4);
        int n = radius > tol
            ? (int)Math.Floor(Math.Abs(0.5 * sweep * radius) / Math.Sqrt(tol * (2 * radius - tol)))
            : 1;
        n = Math.Clamp(n, 1, _opt.MaxArcSegments);

        double totalLen = Math.Abs(sweep) * radius;
        double feed = step.InverseTimeFeed > 0 ? Math.Max(totalLen, Vector3.Distance(start, end)) * step.InverseTimeFeed
            : _m.Feed > 0 ? _m.Feed : _opt.DefaultFeed;
        double sl = S(start, l), el = S(end, l);
        var prev = start;
        for (int i = 1; i <= n; i++)
        {
            Vector3 p;
            if (i == n)
            {
                p = end;
            }
            else
            {
                double t = (double)i / n;
                double ang = a0 + sweep * t;
                var v = new double[3];
                v[a] = ca + radius * Math.Cos(ang);
                v[bAx] = cb + radius * Math.Sin(ang);
                v[l] = sl + (el - sl) * t;
                p = new Vector3((float)v[0], (float)v[1], (float)v[2]);
            }
            AddMove(prev, p, feed, MoveKind.Feed, FeatureType.Arc, spindle);
            prev = p;
        }
    }

    private void AddMove(Vector3 start, Vector3 target, double feedMmMin, MoveKind kind, FeatureType feature, float spindle)
    {
        var delta = target - start;
        double len = delta.Length();
        if (len < 1e-6)
            return;

        // Axis limits like grbl's planner.
        double speed = Math.Min(feedMmMin, MaxRate(start, target)) / 60.0;
        speed = Math.Max(speed, 0.01);
        var dir = delta / (float)len;
        double accel = double.MaxValue;
        if (Math.Abs(dir.X) > 1e-6) accel = Math.Min(accel, _opt.AccelX / Math.Abs(dir.X));
        if (Math.Abs(dir.Y) > 1e-6) accel = Math.Min(accel, _opt.AccelY / Math.Abs(dir.Y));
        if (Math.Abs(dir.Z) > 1e-6) accel = Math.Min(accel, _opt.AccelZ / Math.Abs(dir.Z));

        if (kind == MoveKind.Feed)
        {
            // Depth levels: a new one starts with the moves leading to the first cut at a new Z.
            float z = Math.Min(start.Z, target.Z);
            if (float.IsNaN(_layerZ) || Math.Abs(z - _layerZ) > 1e-3f)
                StartLayer(z);
            _cut += len;
            _maxFeed = Math.Max(_maxFeed, (float)(speed * 60));
            _firstAfterCut = _segments.Count + 1;
        }
        else
        {
            _rapid += len;
        }
        _maxSpindle = Math.Max(_maxSpindle, spindle);
        _min = Vector3.Min(_min, Vector3.Min(start, target));
        _max = Vector3.Max(_max, Vector3.Max(start, target));

        double entry = 0;
        if (_open >= 0)
        {
            double junction = JunctionSpeed(_openDir, dir, Math.Min(_openSpeed, speed), Math.Min(_openAccel, accel));
            CloseOpen(junction);
            entry = junction;
        }

        _segments.Add(new ToolpathSegment
        {
            Start = start,
            End = target,
            Speed = (float)speed,
            Spindle = spindle,
            Line = _line,
            Layer = Math.Max(0, _layers.Count - 1),
            Kind = kind,
            Feature = feature,
        });
        _open = _segments.Count - 1;
        _openEntry = entry;
        _openDir = dir;
        _openSpeed = speed;
        _openAccel = accel;
    }

    private double JunctionSpeed(Vector3 prev, Vector3 next, double vmax, double accel)
    {
        double cosTheta = -Vector3.Dot(prev, next);
        if (cosTheta < -0.9999)
            return vmax;                        // Straight on.
        if (cosTheta > 0.9999)
            return 0;                           // Reversal.
        double sinHalf = Math.Sqrt(0.5 * (1 - cosTheta));
        double v = Math.Sqrt(accel * _opt.JunctionDeviation * sinHalf / (1 - sinHalf));
        return Math.Min(v, vmax);
    }

    private void CloseOpen(double exit)
    {
        if (_open < 0)
            return;
        var s = _segments[_open];
        double len = s.Length;
        double exitSpeed = Math.Min(exit, Math.Sqrt(_openEntry * _openEntry + 2 * _openAccel * len));
        s.StartTime = (float)(_time + _pendingDelay);
        s.Duration = (float)TrapezoidTime(len, _openEntry, _openSpeed, exitSpeed, _openAccel);
        _segments[_open] = s;
        _time = s.StartTime + s.Duration;
        _pendingDelay = 0;
        _open = -1;
    }

    public static double TrapezoidTime(double len, double v0, double vmax, double v1, double accel)
    {
        if (len <= 0 || vmax <= 0)
            return 0;
        if (accel <= 0 || double.IsInfinity(accel) || accel == double.MaxValue)
            return len / vmax;
        v0 = Math.Min(v0, vmax);
        v1 = Math.Min(v1, vmax);
        double dAcc = (vmax * vmax - v0 * v0) / (2 * accel);
        double dDec = (vmax * vmax - v1 * v1) / (2 * accel);
        if (dAcc + dDec <= len)
            return (vmax - v0) / accel + (vmax - v1) / accel + (len - dAcc - dDec) / vmax;
        double vp = Math.Sqrt((2 * accel * len + v0 * v0 + v1 * v1) / 2);
        if (vp < Math.Max(v0, v1))
            return 2 * len / (v0 + v1);
        return (vp - v0) / accel + (vp - v1) / accel;
    }

    private void StartLayer(float z)
    {
        int first = Math.Min(_firstAfterCut, _segments.Count);
        if (_layers.Count == 0)
            first = 0;
        if (_layers.Count > 0)
        {
            var last = _layers[^1];
            _layers[^1] = last with { SegmentCount = first - last.FirstSegment };
        }
        float height = float.IsNaN(_layerZ) ? 0 : _layerZ - z;
        _layers.Add(new ToolpathLayer(_layers.Count, z, height, first, 0));
        _layerZ = z;
    }

    public Toolpath Finish(int lineCount)
    {
        CloseOpen(0);
        if (_layers.Count == 0 && _segments.Count > 0)
            _layers.Add(new ToolpathLayer(0, _segments[0].End.Z, 0, 0, _segments.Count));
        else if (_layers.Count > 0)
            _layers[^1] = _layers[^1] with { SegmentCount = _segments.Count - _layers[^1].FirstSegment };

        var arr = _segments.ToArray();
        foreach (var layer in _layers)
            for (int i = layer.FirstSegment; i < layer.EndSegment; i++)
                arr[i].Layer = layer.Index;

        if (_min.X > _max.X)
        {
            _min = Vector3.Zero;
            _max = Vector3.Zero;
        }
        return new Toolpath(arr, _layers.ToArray(), (float)(_time + _pendingDelay), (float)_cut, (float)_rapid,
            _min, _max, _maxSpindle, _maxFeed, lineCount, _unsupported.ToArray());
    }
}
