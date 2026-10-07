using System.Numerics;

namespace GrblHost.Core.GCode;

public enum MoveKind : byte
{
    /// <summary>G0, rapid positioning.</summary>
    Rapid,
    /// <summary>G1 / G2 / G3 / G38.x at the programmed feed.</summary>
    Feed,
}

/// <summary>What a move does, for the colors of the views.</summary>
public enum FeatureType : byte
{
    /// <summary>Straight cut (G1) in the plane or with Z.</summary>
    Cut,
    /// <summary>Arc (G2 / G3).</summary>
    Arc,
    /// <summary>Feed move straight down (G1 Z−).</summary>
    Plunge,
    /// <summary>Feed move straight up.</summary>
    Retract,
    /// <summary>Probing (G38.x).</summary>
    Probe,
    /// <summary>Pseudo type for rapid moves (palette index).</summary>
    Rapid,
}

/// <summary>One straight move. Arcs are split into several segments.</summary>
public struct ToolpathSegment
{
    public Vector3 Start;
    public Vector3 End;
    /// <summary>Time from the start of the file when this move begins, s.</summary>
    public float StartTime;
    public float Duration;
    /// <summary>Feed, mm/s.</summary>
    public float Speed;
    /// <summary>Spindle speed while the move runs, rpm (0: off).</summary>
    public float Spindle;
    /// <summary>Zero-based line of the document.</summary>
    public int Line;
    /// <summary>Depth level (see <see cref="Toolpath.Layers"/>).</summary>
    public int Layer;
    public MoveKind Kind;
    public FeatureType Feature;

    public readonly float EndTime => StartTime + Duration;
    public readonly float Length => Vector3.Distance(Start, End);

    public readonly Vector3 PositionAt(float time)
    {
        if (Duration <= 0)
            return End;
        float f = Math.Clamp((time - StartTime) / Duration, 0f, 1f);
        return Vector3.Lerp(Start, End, f);
    }
}

/// <summary>
/// A depth level: the moves from the first cut at a new Z to the next one.
/// For a 2.5D job these are the depth passes; the viewer's slider walks them.
/// </summary>
public readonly record struct ToolpathLayer(int Index, float Z, float Height, int FirstSegment, int SegmentCount)
{
    public int EndSegment => FirstSegment + SegmentCount;
}

/// <summary>Toolpath of a whole file: segments in file order, grouped into depth levels.</summary>
public sealed class Toolpath
{
    public ToolpathSegment[] Segments { get; }
    public IReadOnlyList<ToolpathLayer> Layers { get; }
    public float TotalTime { get; }
    /// <summary>Length of the feed moves, mm.</summary>
    public float CutLength { get; }
    /// <summary>Length of the rapid moves, mm.</summary>
    public float RapidLength { get; }
    /// <summary>Bounding box of all moves (program coordinates).</summary>
    public Vector3 Min { get; }
    public Vector3 Max { get; }
    public float MaxSpindle { get; }
    public float MaxFeed { get; }
    public int LineCount { get; }
    /// <summary>Lines the builder couldn't interpret (unknown G codes), zero-based.</summary>
    public IReadOnlyList<int> UnsupportedLines { get; }

    public static Toolpath Empty { get; } = new([], [], 0, 0, 0, Vector3.Zero, Vector3.Zero, 0, 0, 0, []);

    public Toolpath(ToolpathSegment[] segments, IReadOnlyList<ToolpathLayer> layers, float totalTime,
        float cutLength, float rapidLength, Vector3 min, Vector3 max, float maxSpindle, float maxFeed, int lineCount,
        IReadOnlyList<int> unsupported)
    {
        Segments = segments;
        Layers = layers;
        TotalTime = totalTime;
        CutLength = cutLength;
        RapidLength = rapidLength;
        Min = min;
        Max = max;
        MaxSpindle = maxSpindle;
        MaxFeed = maxFeed;
        LineCount = lineCount;
        UnsupportedLines = unsupported;
    }

    /// <summary>Index of the segment running at the given time (-1 before the first).</summary>
    public int SegmentAtTime(float time)
    {
        var s = Segments;
        if (s.Length == 0 || time < s[0].StartTime)
            return -1;
        int lo = 0, hi = s.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (s[mid].StartTime <= time)
                lo = mid;
            else
                hi = mid - 1;
        }
        return lo;
    }

    /// <summary>
    /// Index of the last segment that belongs to a line at or before the given
    /// line (-1 if none). Lines without moves map to the move before them.
    /// </summary>
    public int LastSegmentAtOrBeforeLine(int line)
    {
        var s = Segments;
        if (s.Length == 0 || s[0].Line > line)
            return -1;
        int lo = 0, hi = s.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (s[mid].Line <= line)
                lo = mid;
            else
                hi = mid - 1;
        }
        return lo;
    }

    /// <summary>First segment of a line or after it, Segments.Length if none.</summary>
    public int FirstSegmentAtOrAfterLine(int line)
    {
        int i = LastSegmentAtOrBeforeLine(line - 1);
        return i + 1;
    }

    public int LayerOfSegment(int segment)
    {
        if (segment < 0 || Segments.Length == 0)
            return 0;
        return Segments[Math.Min(segment, Segments.Length - 1)].Layer;
    }
}
