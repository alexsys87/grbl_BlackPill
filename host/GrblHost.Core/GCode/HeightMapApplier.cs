using System.Globalization;
using System.Numerics;
using System.Text;

namespace GrblHost.Core.GCode;

/// <summary>
/// Applies a <see cref="HeightMap"/> to a G-code program (auto leveling, as
/// in Candle): every feed move (G1, G2, G3) is split into pieces of at most
/// <c>segmentLength</c>, arcs into chords, and each end point gets the
/// surface height at its X / Y added to Z. Rapid moves (G0) get the height
/// too, without splitting. Other words stay as they were. The moves are
/// written in absolute coordinates in the program's units; a program in
/// G91 gets G90 before such a move and G91 back after it.
/// </summary>
public static class HeightMapApplier
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static List<string> Apply(IReadOnlyList<string> lines, HeightMap map, double segmentLength = 1.0,
        double arcTolerance = 0.002)
    {
        segmentLength = Math.Max(segmentLength, 0.01);
        var output = new List<string>(lines.Count * 2) { "(Height map applied by Grbl Host)" };
        var modal = new ModalState();

        foreach (string line in lines)
        {
            var block = GCodeBlock.Parse(line);
            if (block.IsEmpty || block.BadFormat)
            {
                output.Add(line);
                continue;
            }

            var start = modal.Position;
            bool wasAbsolute = modal.Absolute;
            var step = modal.Apply(block);
            var end = modal.Position;

            // Lines without a move (or moves this can't follow: G28 / G30,
            // probing, G92, G53) pass unchanged.
            bool plainMove = step.Motion is MotionMode.Rapid or MotionMode.Linear or MotionMode.ArcCw or MotionMode.ArcCcw &&
                             !block.HasG(28) && !block.HasG(30) && !block.HasG(53) && !block.HasG(92);
            // G93 feeds belong to the whole move, splitting would change them.
            if (!plainMove || step.Unsupported || modal.InverseTime)
            {
                output.Add(line);
                continue;
            }

            // Words that are not part of the move go first on their own line.
            string rest = OtherWords(line);
            if (rest.Length > 0)
                output.Add(rest);

            double unit = modal.Inches ? 25.4 : 1;
            int digits = modal.Inches ? 5 : 4;     // 0.1 µm or 0.25 µm.
            IEnumerable<Vector3> points = step.Motion switch
            {
                MotionMode.Rapid => new[] { end },
                MotionMode.Linear => Split(start, end, segmentLength),
                _ => ArcPoints(start, end, step, modal.Plane, segmentLength, arcTolerance),
            };

            var sb = new StringBuilder();
            if (!wasAbsolute || !modal.Absolute)
                output.Add("G90");
            string g = step.Motion == MotionMode.Rapid ? "G0" : "G1";
            bool first = true;
            foreach (var p in points)
            {
                double z = p.Z + map.At(p.X, p.Y);
                sb.Clear();
                sb.Append(first ? g : "").Append(first ? " " : "")
                  .Append('X').Append(Num(p.X / unit, digits))
                  .Append(" Y").Append(Num(p.Y / unit, digits))
                  .Append(" Z").Append(Num(z / unit, digits));
                if (first && block.TryGet('F', out double f) && step.Motion != MotionMode.Rapid)
                    sb.Append(" F").Append(Num(f, 4));
                output.Add(sb.ToString());
                first = false;
            }
            if (!modal.Absolute)
                output.Add("G91");
        }
        return output;
    }

    private static string Num(double v, int digits) => Math.Round(v, digits).ToString("0.#####", Inv);

    /// <summary>The line without the motion words: G0 … G3 and X Y Z I J K R P (P only with an arc).</summary>
    private static string OtherWords(string line)
    {
        var sb = new StringBuilder();
        var block = GCodeBlock.Parse(line);
        foreach (double g in block.G)
        {
            if (g is 0 or 1 or 2 or 3 or 90 or 91)
                continue;
            sb.Append('G').Append(g.ToString("0.#", Inv)).Append(' ');
        }
        // G90 / G91 on a move line take effect for the move, the move is
        // written absolute; the mode itself is restored after it.
        foreach (int m in block.M)
            sb.Append('M').Append(m.ToString(Inv)).Append(' ');
        foreach (char c in "STD")
            if (block.TryGet(c, out double v))
                sb.Append(c).Append(v.ToString("0.####", Inv)).Append(' ');
        if (block.Comment != null)
            sb.Append('(').Append(block.Comment.Replace('(', '[').Replace(')', ']')).Append(')');
        return sb.ToString().Trim();
    }

    /// <summary>Points along a straight move, every <paramref name="maxLength"/> mm in X / Y.</summary>
    private static IEnumerable<Vector3> Split(Vector3 a, Vector3 b, double maxLength)
    {
        double dxy = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
        int n = Math.Max(1, (int)Math.Ceiling(dxy / maxLength - 1e-9));
        for (int i = 1; i <= n; i++)
            yield return i == n ? b : Vector3.Lerp(a, b, (float)i / n);
    }

    /// <summary>Chords of an arc: within the arc tolerance and at most <paramref name="maxLength"/> long.</summary>
    private static IEnumerable<Vector3> ArcPoints(Vector3 start, Vector3 end, ModalState.Step step, Plane plane,
        double maxLength, double tolerance)
    {
        int a, bAx, l;
        switch (plane)
        {
            case Plane.XZ: a = 2; bAx = 0; l = 1; break;
            case Plane.YZ: a = 1; bAx = 2; l = 0; break;
            default: a = 0; bAx = 1; l = 2; break;
        }
        static double S(Vector3 v, int i) => i == 0 ? v.X : i == 1 ? v.Y : v.Z;

        double sa = S(start, a), sb = S(start, bAx), ea = S(end, a), eb = S(end, bAx);
        bool cw = step.Motion == MotionMode.ArcCw;
        double ca, cb;
        if (step.ArcRadius is double r)
        {
            double dx = ea - sa, dy = eb - sb;
            double d = Math.Sqrt(dx * dx + dy * dy);
            if (d < 1e-9 || d > 2 * Math.Abs(r) + 1e-4)
                return Split(start, end, maxLength);
            double h = Math.Sqrt(Math.Max(0, r * r - d * d / 4));
            double sign = cw ? -1 : 1;
            if (r < 0)
                sign = -sign;
            ca = sa + dx / 2 - sign * h * dy / d;
            cb = sb + dy / 2 + sign * h * dx / d;
        }
        else
        {
            ca = S(step.ArcCenter, a);
            cb = S(step.ArcCenter, bAx);
        }

        double radius = Math.Sqrt((sa - ca) * (sa - ca) + (sb - cb) * (sb - cb));
        double a0 = Math.Atan2(sb - cb, sa - ca);
        double a1 = Math.Atan2(eb - cb, ea - ca);
        double sweep = a1 - a0;
        if (cw)
        {
            if (sweep >= -1e-9) sweep -= 2 * Math.PI;
        }
        else if (sweep <= 1e-9)
        {
            sweep += 2 * Math.PI;
        }
        if (step.ArcTurns > 1)
            sweep += (cw ? -2 : 2) * Math.PI * (step.ArcTurns - 1);

        double tol = Math.Max(tolerance, 1e-4);
        int byTolerance = radius > tol ? (int)Math.Ceiling(Math.Abs(0.5 * sweep * radius) / Math.Sqrt(tol * (2 * radius - tol))) : 1;
        int byLength = (int)Math.Ceiling(Math.Abs(sweep) * radius / maxLength);
        int n = Math.Clamp(Math.Max(byTolerance, byLength), 1, 10000);

        double sl = S(start, l), el = S(end, l);
        var list = new List<Vector3>(n);
        for (int i = 1; i <= n; i++)
        {
            if (i == n)
            {
                list.Add(end);
                break;
            }
            double t = (double)i / n;
            double ang = a0 + sweep * t;
            var v = new double[3];
            v[a] = ca + radius * Math.Cos(ang);
            v[bAx] = cb + radius * Math.Sin(ang);
            v[l] = sl + (el - sl) * t;
            list.Add(new Vector3((float)v[0], (float)v[1], (float)v[2]));
        }
        return list;
    }
}
