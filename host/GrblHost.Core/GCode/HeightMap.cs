using System.Globalization;
using System.Text;

namespace GrblHost.Core.GCode;

/// <summary>
/// Height map of the stock surface for auto leveling (as in Candle): a grid
/// of probed Z values over a rectangle in work coordinates. Between the
/// points the height is interpolated bicubically (bilinear on request),
/// outside the rectangle the edge values continue.
/// </summary>
public sealed class HeightMap
{
    private readonly double[,] _z;     // [ix, iy], NaN = not probed yet

    public HeightMap(double x, double y, double width, double height, int pointsX, int pointsY)
    {
        if (pointsX < 2 || pointsY < 2)
            throw new ArgumentOutOfRangeException(nameof(pointsX), "At least 2 x 2 points.");
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "The area must not be empty.");
        X = x;
        Y = y;
        Width = width;
        Height = height;
        PointsX = pointsX;
        PointsY = pointsY;
        _z = new double[pointsX, pointsY];
        for (int i = 0; i < pointsX; i++)
            for (int j = 0; j < pointsY; j++)
                _z[i, j] = double.NaN;
    }

    /// <summary>Lower left corner and size of the probed area, work coordinates, mm.</summary>
    public double X { get; }
    public double Y { get; }
    public double Width { get; }
    public double Height { get; }
    public int PointsX { get; }
    public int PointsY { get; }

    public double StepX => Width / (PointsX - 1);
    public double StepY => Height / (PointsY - 1);

    public bool Bilinear { get; set; }

    /// <summary>Probed height of a grid point, NaN if not probed yet.</summary>
    public double this[int ix, int iy]
    {
        get => _z[ix, iy];
        set => _z[ix, iy] = value;
    }

    public double PointX(int ix) => X + ix * StepX;
    public double PointY(int iy) => Y + iy * StepY;

    public bool IsComplete
    {
        get
        {
            foreach (double z in _z)
                if (double.IsNaN(z))
                    return false;
            return true;
        }
    }

    public int ProbedCount
    {
        get
        {
            int n = 0;
            foreach (double z in _z)
                if (!double.IsNaN(z))
                    n++;
            return n;
        }
    }

    public double Min => _z.Cast<double>().Where(z => !double.IsNaN(z)).DefaultIfEmpty(0).Min();
    public double Max => _z.Cast<double>().Where(z => !double.IsNaN(z)).DefaultIfEmpty(0).Max();

    /// <summary>
    /// Probe order: rows from the lower left, every other row backwards
    /// (zigzag, short moves).
    /// </summary>
    public IEnumerable<(int Ix, int Iy)> ProbeOrder()
    {
        for (int iy = 0; iy < PointsY; iy++)
        {
            for (int k = 0; k < PointsX; k++)
            {
                int ix = iy % 2 == 0 ? k : PointsX - 1 - k;
                yield return (ix, iy);
            }
        }
    }

    // ---------------------------------------------------------------- interpolation

    /// <summary>Height at a point, interpolated; edge values outside the area.</summary>
    public double At(double x, double y)
    {
        double fx = Math.Clamp((x - X) / StepX, 0, PointsX - 1);
        double fy = Math.Clamp((y - Y) / StepY, 0, PointsY - 1);
        int ix = Math.Min((int)Math.Floor(fx), PointsX - 2);
        int iy = Math.Min((int)Math.Floor(fy), PointsY - 2);
        double tx = fx - ix, ty = fy - iy;

        if (Bilinear)
        {
            double z0 = Lerp(Z(ix, iy), Z(ix + 1, iy), tx);
            double z1 = Lerp(Z(ix, iy + 1), Z(ix + 1, iy + 1), tx);
            return Lerp(z0, z1, ty);
        }

        // Bicubic: Catmull-Rom through the 4 x 4 neighbourhood, the grid
        // edge is repeated outside. Planes stay planes, the surface passes
        // through every probed point.
        Span<double> col = stackalloc double[4];
        for (int m = 0; m < 4; m++)
        {
            int j = iy - 1 + m;
            col[m] = CatmullRom(Z(ix - 1, j), Z(ix, j), Z(ix + 1, j), Z(ix + 2, j), tx);
        }
        return CatmullRom(col[0], col[1], col[2], col[3], ty);
    }

    private double Z(int ix, int iy)
    {
        // Outside the grid: extend linearly from the edge, so a tilted plane
        // stays a plane up to the border.
        if (ix < 0)
            return 2 * Z(0, iy) - Z(1, iy);
        if (ix >= PointsX)
            return 2 * Z(PointsX - 1, iy) - Z(PointsX - 2, iy);
        if (iy < 0)
            return 2 * Z(ix, 0) - Z(ix, 1);
        if (iy >= PointsY)
            return 2 * Z(ix, PointsY - 1) - Z(ix, PointsY - 2);
        double z = _z[ix, iy];
        return double.IsNaN(z) ? 0 : z;
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private static double CatmullRom(double p0, double p1, double p2, double p3, double t)
    {
        double t2 = t * t, t3 = t2 * t;
        return 0.5 * (2 * p1 + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t2 + (-p0 + 3 * p1 - 3 * p2 + p3) * t3);
    }

    // ---------------------------------------------------------------- file

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// Text file: a header with the area and the grid size, then one line of
    /// heights per grid row from the lowest Y up, values separated by ';'
    /// ("nan" for points not probed).
    /// </summary>
    public string Save()
    {
        var sb = new StringBuilder();
        sb.AppendLine("; Grbl Host height map: x;y;width;height;pointsX;pointsY, then Z rows from the lowest Y");
        sb.AppendLine(string.Join(";", new[] { X, Y, Width, Height }.Select(v => v.ToString("0.####", Inv)))
                      + ";" + PointsX.ToString(Inv) + ";" + PointsY.ToString(Inv));
        for (int iy = 0; iy < PointsY; iy++)
        {
            var row = new string[PointsX];
            for (int ix = 0; ix < PointsX; ix++)
                row[ix] = double.IsNaN(_z[ix, iy]) ? "nan" : _z[ix, iy].ToString("0.####", Inv);
            sb.AppendLine(string.Join(";", row));
        }
        return sb.ToString();
    }

    public static HeightMap Load(string text)
    {
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith(';')).ToList();
        if (lines.Count < 1)
            throw new FormatException("Empty height map file.");
        var h = lines[0].Split(';');
        if (h.Length < 6)
            throw new FormatException("Bad height map header.");
        double D(string s) => double.Parse(s, NumberStyles.Float, Inv);
        int I(string s) => int.Parse(s, NumberStyles.Integer, Inv);
        var map = new HeightMap(D(h[0]), D(h[1]), D(h[2]), D(h[3]), I(h[4]), I(h[5]));
        if (lines.Count - 1 < map.PointsY)
            throw new FormatException("Height map rows missing.");
        for (int iy = 0; iy < map.PointsY; iy++)
        {
            var row = lines[1 + iy].Split(';');
            if (row.Length < map.PointsX)
                throw new FormatException("Height map values missing.");
            for (int ix = 0; ix < map.PointsX; ix++)
                map._z[ix, iy] = row[ix].Equals("nan", StringComparison.OrdinalIgnoreCase) ? double.NaN : D(row[ix]);
        }
        return map;
    }
}
