using System.Numerics;
using GrblHost.Core.GCode;

namespace GrblHost.Core.Tests;

public class GCodeTests
{
    [Fact]
    public void ParsesBlocksWithSeveralWords()
    {
        var b = GCodeBlock.Parse("N10 G21 G90 G0 X1.5 Y-2 (move) ; rest");
        Assert.Equal(new[] { 21.0, 90.0, 0.0 }, b.G);
        Assert.Equal(1.5, b.Get('X'));
        Assert.Equal(-2, b.Get('Y'));
        Assert.False(b.Has('Z'));
        Assert.Equal("move", b.Comment);
        Assert.True(b.HasAxisWords);

        b = GCodeBlock.Parse("g38.2 z-10 f50");
        Assert.True(b.HasG(38.2));
        Assert.Equal(50, b.Get('F'));

        b = GCodeBlock.Parse("M3 S1000 M8");
        Assert.Equal(new[] { 3, 8 }, b.M);

        Assert.True(GCodeBlock.Parse("G1 X1 X2").RepeatedWord);
        Assert.True(GCodeBlock.Parse("G1 X").BadFormat);
        Assert.True(GCodeBlock.Parse("(only a comment)").IsEmpty);
    }

    [Fact]
    public void ModalMotionContinues()
    {
        var tp = ToolpathBuilder.Build(new[] { "G21 G90", "G0 X10 Y0", "G1 Z-1 F100", "X20", "Y10", "G0 Z5" });
        Assert.Equal(5, tp.Segments.Length);
        Assert.Equal(MoveKind.Rapid, tp.Segments[0].Kind);
        Assert.Equal(FeatureType.Plunge, tp.Segments[1].Feature);
        Assert.Equal(FeatureType.Cut, tp.Segments[2].Feature);
        Assert.Equal(new Vector3(20, 0, -1), tp.Segments[2].End);
        Assert.Equal(new Vector3(20, 10, -1), tp.Segments[3].End);
        Assert.Equal(FeatureType.Rapid, tp.Segments[4].Feature);
        Assert.Equal(21, tp.CutLength, 3);          // Plunge 1 + 10 + 10.
    }

    [Fact]
    public void IncrementalAndInches()
    {
        var tp = ToolpathBuilder.Build(new[] { "G20 G91 G1 X1 F10", "X1", "G21 G90 G0 X0" });
        Assert.Equal(new Vector3(25.4f, 0, 0), tp.Segments[0].End);
        Assert.Equal(new Vector3(50.8f, 0, 0), tp.Segments[1].End);
        Assert.Equal(Vector3.Zero, tp.Segments[2].End);
    }

    [Fact]
    public void ArcsEndWhereProgrammed()
    {
        // Full circle with I, half circle with R, arc in G18.
        var tp = ToolpathBuilder.Build(new[] { "G0 X0 Y0", "G2 X0 Y0 I10 J0 F500", "G3 X20 Y0 R10", "G18 G2 X30 Z-10 I5 K0" });
        var arc = tp.Segments.Where(s => s.Feature == FeatureType.Arc).ToArray();
        Assert.True(arc.Length > 20);
        // Full circle: radius 10 around (10, 0), length 2π·10.
        float full = tp.Segments.Where(s => s.Line == 1).Sum(s => s.Length);
        Assert.InRange(full, 62.0f, 62.9f);
        foreach (var s in tp.Segments.Where(s => s.Line == 1))
            Assert.InRange(Vector3.Distance(s.End, new Vector3(10, 0, 0)), 9.99f, 10.01f);
        Assert.Equal(new Vector3(20, 0, 0), tp.Segments.Last(s => s.Line == 2).End);
        var last = tp.Segments[^1];
        Assert.InRange(last.End.X, 29.999f, 30.001f);
        Assert.InRange(last.End.Z, -10.001f, -9.999f);
        Assert.Equal(new Vector3(20, 0, 0), tp.Segments.First(s => s.Line == 3).Start);
    }

    [Fact]
    public void G92OffsetMovesTheProgramFrame()
    {
        var tp = ToolpathBuilder.Build(new[] { "G0 X10", "G92 X0", "G0 X5", "G92.1", "G0 X5" });
        Assert.Equal(new Vector3(5, 0, 0), tp.Segments[1].End);
        Assert.Equal(new Vector3(15, 0, 0), tp.Segments[1].End + new Vector3(10, 0, 0));
        // After G92.1 the position is the old machine position (15), then X5.
        Assert.Equal(new Vector3(15, 0, 0), tp.Segments[2].Start);
        Assert.Equal(new Vector3(5, 0, 0), tp.Segments[2].End);
    }

    [Fact]
    public void TimeEstimateUsesFeedAndDwell()
    {
        // 100 mm at 600 mm/min = 10 s plus acceleration, plus a 2 s dwell.
        var tp = ToolpathBuilder.Build(new[] { "G1 X100 F600", "G4 P2" });
        Assert.InRange(tp.TotalTime, 12.0f, 12.5f);
        Assert.Equal(600 / 60f, tp.Segments[0].Speed, 3);
    }

    [Fact]
    public void UnknownCodesAreReported()
    {
        var tp = ToolpathBuilder.Build(new[] { "G21", "G81 X1 Y1 Z-1 R1", "M123" });
        Assert.Equal(new[] { 1, 2 }, tp.UnsupportedLines);
    }

    [Fact]
    public void DepthLevelsFollowTheCuts()
    {
        var doc = GCodeDocument.FromText("demo", DemoGCode.Generate());
        var tp = ToolpathBuilder.Build(doc.Lines);
        Assert.Empty(tp.UnsupportedLines);
        Assert.True(tp.Layers.Count >= 3);
        Assert.InRange(tp.Min.Z, -1.5001f, -1.4999f);
        Assert.True(tp.Max.X <= 60 && tp.Min.X >= 0);
        Assert.True(tp.Max.Y <= 40 && tp.Min.Y >= 0);
        Assert.Equal(10000, tp.MaxSpindle);
        Assert.True(tp.TotalTime > 60);
        // The depth levels cover all segments, in order.
        Assert.Equal(0, tp.Layers[0].FirstSegment);
        for (int i = 1; i < tp.Layers.Count; i++)
            Assert.Equal(tp.Layers[i - 1].EndSegment, tp.Layers[i].FirstSegment);
        Assert.Equal(tp.Segments.Length, tp.Layers[^1].EndSegment);
    }

    [Fact]
    public void PreambleRestoresTheModalState()
    {
        var m = new ModalState();
        foreach (var l in new[] { "G21 G90 G55", "M3 S8000", "M8", "G0 X10 Y20", "G1 Z-1 F300", "X30" })
            m.Apply(GCodeBlock.Parse(l));
        var p = m.Preamble(5, 2).ToList();
        Assert.Contains("G55", p);
        Assert.Contains("M3 S8000", p);
        Assert.Contains("G4 P2", p);
        Assert.Contains("M8", p);
        Assert.Contains("G0 Z5", p);
        Assert.Contains("G0 X30 Y20", p);
        Assert.Contains("G1 Z-1 F300", p);
        Assert.Equal("G1", p[^1]);
        Assert.True(p.IndexOf("G0 Z5") < p.IndexOf("G0 X30 Y20"));
    }
}
