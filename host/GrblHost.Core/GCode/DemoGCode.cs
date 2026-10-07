using System.Globalization;
using System.Text;

namespace GrblHost.Core.GCode;

/// <summary>
/// Generates a test program for the CNC 3018: a rectangular pocket cleared
/// in zigzag passes, a round boss outside it cut with full G2 circles, four
/// drilled holes in the corners and a 45° chamfer-like ramp line. Depth
/// passes go down in steps, so the depth slider of the viewer has something
/// to show. The work origin is the lower left corner of the stock, Z0 its
/// top surface; nothing is cut below Z = −depth.
/// </summary>
public static class DemoGCode
{
    public static string Generate(double width = 60, double height = 40, double depth = 1.5,
        double stepDown = 0.5, double tool = 3.175, double feed = 400, double plunge = 120,
        double spindle = 10000, double safeZ = 5)
    {
        var g = new Writer();
        double r = tool / 2;
        int passes = Math.Max(1, (int)Math.Ceiling(depth / stepDown - 1e-9));

        g.Line($"(Grbl Host test program {g.F(width)} x {g.F(height)} mm, depth {g.F(depth)} mm)");
        g.Line($"(tool D{g.F(tool)} mm, feed {g.F(feed)} mm/min, {passes} passes)");
        g.Line("(origin: lower left corner of the stock, Z0 = top surface)");
        g.Line("G21 G90 G94 G17 G54");
        g.Line($"G0 Z{g.F(safeZ)}");
        g.Line($"M3 S{g.F(spindle)}");
        g.Line("G4 P2");

        // Pocket in the left half: inside 10 mm from the stock edges.
        double px0 = 10 + r, py0 = 10 + r;
        double px1 = width / 2 - 2 - r, py1 = height - 10 - r;
        g.Line("(pocket)");
        for (int pass = 1; pass <= passes; pass++)
        {
            double z = -Math.Min(depth, pass * stepDown);
            g.Line($"G0 X{g.F(px0)} Y{g.F(py0)}");
            g.Line($"G1 Z{g.F(z)} F{g.F(plunge)}");
            g.Line($"F{g.F(feed)}");
            // Zigzag along X, step over 40 % of the tool.
            double step = tool * 0.4;
            bool forward = true;
            for (double y = py0; y <= py1 + 1e-9; y += step)
            {
                g.Line($"G1 Y{g.F(y)}");
                g.Line($"G1 X{g.F(forward ? px1 : px0)}");
                forward = !forward;
            }
            // Finishing loop around the walls.
            g.Line($"G1 X{g.F(px0)} Y{g.F(py1)}");
            g.Line($"G1 X{g.F(px1)}");
            g.Line($"G1 Y{g.F(py0)}");
            g.Line($"G1 X{g.F(px0)}");
            g.Line($"G1 Y{g.F(py1)}");
            g.Line($"G0 Z{g.F(safeZ)}");
        }

        // Round boss in the right half: profile outside a circle, clockwise (G2),
        // which is climb milling on an outside contour with M3.
        double cx = width * 0.75, cy = height / 2;
        double boss = Math.Min(width / 4, height / 2) - 6;
        double pr = boss + r;
        g.Line("(round boss)");
        for (int pass = 1; pass <= passes; pass++)
        {
            double z = -Math.Min(depth, pass * stepDown);
            g.Line($"G0 X{g.F(cx - pr)} Y{g.F(cy)}");
            g.Line($"G1 Z{g.F(z)} F{g.F(plunge)}");
            g.Line($"G2 X{g.F(cx - pr)} Y{g.F(cy)} I{g.F(pr)} J0 F{g.F(feed)}");
            g.Line($"G0 Z{g.F(safeZ)}");
        }

        // Corner holes, pecked.
        g.Line("(holes)");
        double[,] holes = { { 4, 4 }, { width - 4, 4 }, { width - 4, height - 4 }, { 4, height - 4 } };
        for (int i = 0; i < holes.GetLength(0); i++)
        {
            g.Line($"G0 X{g.F(holes[i, 0])} Y{g.F(holes[i, 1])}");
            g.Line("G0 Z1");
            for (int pass = 1; pass <= passes; pass++)
            {
                double z = -Math.Min(depth, pass * stepDown);
                g.Line($"G1 Z{g.F(z)} F{g.F(plunge)}");
                g.Line("G0 Z1");
            }
            g.Line($"G0 Z{g.F(safeZ)}");
        }

        // Engraved line with a ramp in: from the top to full depth over 10 mm.
        g.Line("(ramp line)");
        double ly = 4;
        g.Line($"G0 X{g.F(12)} Y{g.F(ly)}");
        g.Line("G1 Z0 F" + g.F(plunge));
        g.Line($"G1 X{g.F(22)} Z{g.F(-Math.Min(depth, 0.5))} F{g.F(feed)}");
        g.Line($"G1 X{g.F(width - 12)}");
        g.Line($"G0 Z{g.F(safeZ)}");

        g.Line("M5");
        g.Line("G0 X0 Y0");
        g.Line("M30");
        return g.ToString();
    }

    private sealed class Writer
    {
        private readonly StringBuilder _sb = new();

        public string F(double v) => Math.Round(v, 3).ToString("0.###", CultureInfo.InvariantCulture);

        public void Line(string s) => _sb.Append(s).Append('\n');

        public override string ToString() => _sb.ToString();
    }
}
