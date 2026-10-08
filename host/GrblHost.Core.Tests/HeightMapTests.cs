using System.Numerics;
using GrblHost.Core.GCode;
using GrblHost.Core.Machine;
using static GrblHost.Core.Tests.ProtocolTests;

namespace GrblHost.Core.Tests;

public class HeightMapTests
{
    private static HeightMap Plane(Func<double, double, double> z, int nx = 4, int ny = 3)
    {
        var m = new HeightMap(10, 20, 30, 20, nx, ny);
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < ny; j++)
                m[i, j] = z(m.PointX(i), m.PointY(j));
        return m;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TiltedPlaneIsExact(bool bilinear)
    {
        static double Z(double x, double y) => 0.01 * x - 0.02 * y + 0.3;
        var m = Plane(Z);
        m.Bilinear = bilinear;
        Assert.True(m.IsComplete);
        foreach (var (x, y) in new[] { (10.0, 20.0), (17.3, 26.1), (40.0, 40.0), (33.3, 21.0), (25.0, 30.0) })
            Assert.Equal(Z(x, y), m.At(x, y), 9);
        // Outside the area the edge continues (no extrapolation of the tilt).
        Assert.Equal(m.At(10, 20), m.At(0, 0), 9);
        Assert.Equal(m.At(40, 40), m.At(99, 99), 9);
    }

    [Fact]
    public void PassesThroughTheProbedPoints()
    {
        var m = Plane((x, y) => Math.Sin(x / 7) * Math.Cos(y / 5), 5, 5);
        for (int i = 0; i < 5; i++)
            for (int j = 0; j < 5; j++)
                Assert.Equal(m[i, j], m.At(m.PointX(i), m.PointY(j)), 9);
        // Between the points the surface stays near the smooth original.
        Assert.InRange(m.At(21, 27) - Math.Sin(21.0 / 7) * Math.Cos(27.0 / 5), -0.08, 0.08);
    }

    [Fact]
    public void ZigzagOrderVisitsEveryPointOnce()
    {
        var m = new HeightMap(0, 0, 10, 10, 3, 2);
        var order = m.ProbeOrder().ToList();
        Assert.Equal(new[] { (0, 0), (1, 0), (2, 0), (2, 1), (1, 1), (0, 1) }, order);
    }

    [Fact]
    public void SaveAndLoadRoundTrip()
    {
        var m = Plane((x, y) => x * 0.001 + y * 0.002);
        m[1, 2] = double.NaN;
        var copy = HeightMap.Load(m.Save());
        Assert.Equal((m.X, m.Y, m.Width, m.Height, m.PointsX, m.PointsY),
                     (copy.X, copy.Y, copy.Width, copy.Height, copy.PointsX, copy.PointsY));
        for (int i = 0; i < m.PointsX; i++)
            for (int j = 0; j < m.PointsY; j++)
                Assert.Equal(m[i, j], copy[i, j], 4);
        Assert.False(copy.IsComplete);
        Assert.Equal(11, copy.ProbedCount);
        Assert.Throws<FormatException>(() => HeightMap.Load("1;2;3"));
    }

    /// <summary>End points of the moves of a program, mm.</summary>
    private static List<Vector3> Ends(IEnumerable<string> lines)
    {
        var modal = new ModalState();
        var ends = new List<Vector3>();
        foreach (string l in lines)
            if (modal.Apply(GCodeBlock.Parse(l)).Motion != null)
                ends.Add(modal.Position);
        return ends;
    }

    [Fact]
    public void ApplierSplitsLinesAndAddsTheHeight()
    {
        static double Z(double x, double y) => 0.05 * x;
        var m = Plane(Z);
        var program = new[] { "G21 G90", "G0 X10 Y20 Z2", "G1 Z-0.5 F100", "G1 X20 Y20 M8", "G0 Z5" };
        var outLines = HeightMapApplier.Apply(program, m, segmentLength: 2);

        Assert.Contains("M8", outLines);
        Assert.Contains(outLines, l => l.StartsWith("G1 ") && l.Contains("F100"));
        var ends = Ends(outLines);
        // Rapid, plunge, the cut 10 mm long in 2 mm pieces, rapid up.
        Assert.Equal(1 + 1 + 5 + 1, ends.Count);
        Assert.Equal(2 + Z(10, 20), ends[0].Z, 3);
        for (int i = 1; i <= 6; i++)
            Assert.Equal(-0.5 + Z(ends[i].X, ends[i].Y), ends[i].Z, 3);
        Assert.Equal(new[] { 10f, 12, 14, 16, 18, 20 }, ends.Skip(1).Take(6).Select(e => MathF.Round(e.X, 3)));
        Assert.Equal(5 + Z(20, 20), ends[^1].Z, 3);
    }

    [Fact]
    public void ApplierFollowsArcsWithinTheTolerance()
    {
        var m = Plane((x, y) => 0);
        var program = new[] { "G21 G90 G0 X20 Y30", "G1 Z-1 F200", "G2 X30 Y30 I5 J0", "G3 X20 Y30 R5" };
        var outLines = HeightMapApplier.Apply(program, m, segmentLength: 1, arcTolerance: 0.01);
        Assert.DoesNotContain(outLines, l => l.StartsWith("G2 ") || l.StartsWith("G3 "));
        var pts = Ends(outLines).Skip(2).ToList();
        Assert.True(pts.Count > 20);
        // Every chord end lies on one of the two circles.
        foreach (var p in pts)
        {
            double r = Vector2.Distance(new Vector2(p.X, p.Y), new Vector2(25, 30));
            Assert.InRange(r, 4.99, 5.01);
        }
        // The CW arc from X20 to X30 passes above (Y > 30), the CCW one back as well.
        Assert.True(pts.Max(p => p.Y) > 34.9);
        Assert.Equal(new Vector3(20, 30, -1), pts[^1]);
    }

    [Fact]
    public void ApplierKeepsIncrementalAndInchPrograms()
    {
        var m = Plane((x, y) => 0.1);
        var program = new[] { "G20 G90 G0 X0.5 Y1", "G91", "G1 X0.4 F10", "Y0.2", "G90 G0 Z0.2" };
        var outLines = HeightMapApplier.Apply(program, m, segmentLength: 50);
        var original = Ends(program);
        var leveled = Ends(outLines);
        Assert.Equal(original.Count, leveled.Count);
        for (int i = 0; i < original.Count; i++)
        {
            Assert.Equal(original[i].X, leveled[i].X, 3);
            Assert.Equal(original[i].Y, leveled[i].Y, 3);
            Assert.Equal(original[i].Z + 0.1, leveled[i].Z, 3);
        }
        // The program ends in G91 again after every move it owns.
        Assert.Contains("G91", outLines);
        Assert.Contains("G20", outLines[1]);
    }

    [Fact]
    public void ApplierPassesWhatItCannotFollow()
    {
        var m = Plane((x, y) => 0.1);
        var program = new[] { "G38.2 Z-5 F50", "G28 X0 Y0", "G53 G0 Z-1", "G93 G1 X1 F2", "(note)", "M5" };
        var outLines = HeightMapApplier.Apply(program, m);
        Assert.Equal(program, outLines.Skip(1));
    }

    [Fact]
    public async Task ProbesAnUnevenSurface()
    {
        var (c, v) = Online(200);
        // Machine Z of the stock: tilted and bent; work Z = machine Z + 30.
        static double Surface(double x, double y) => -30 + 0.02 * x - 0.01 * y + 0.0004 * x * y;
        v.ProbeSurface = Surface;
        using (c)
        {
            await WaitFor(() => c.State == ConnectionState.Online);
            c.Send("G10 L2 P1 X0 Y0 Z-30");
            c.Send("$#", SendKind.Query);
            await WaitFor(() => Math.Abs(c.Snapshot.Wco.Z + 30) < 1e-6);

            var map = new HeightMap(0, 0, 40, 30, 3, 3);
            var probe = new HeightMapProbe(map, safeZ: 3, probeZ: -3, feed: 300);
            var wco = c.Snapshot.Wco;
            var done = new TaskCompletionSource<JobResult>();
            c.ProbeResult += (pos, ok) => probe.Record(pos, ok, wco);
            c.JobCompleted += r => done.TrySetResult(r);
            c.StartJob(probe.BuildJob());
            Assert.Equal(JobResult.Done, await done.Task.WaitAsync(TimeSpan.FromSeconds(60)));

            Assert.True(probe.Finished);
            Assert.True(map.IsComplete);
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    Assert.Equal(Surface(map.PointX(i), map.PointY(j)) + 30, map[i, j], 2);
        }
    }
}
