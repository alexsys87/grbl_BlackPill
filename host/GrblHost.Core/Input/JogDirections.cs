namespace GrblHost.Core.Input;

/// <summary>Axis directions held down on a stick or a D-pad.</summary>
[Flags]
public enum JogDirections
{
    None = 0,
    XPlus = 1,
    XMinus = 2,
    YPlus = 4,
    YMinus = 8,
    ZPlus = 16,
    ZMinus = 32,
}

public static class JogDirectionsExtensions
{
    private const JogDirections XAxis = JogDirections.XPlus | JogDirections.XMinus;
    private const JogDirections YAxis = JogDirections.YPlus | JogDirections.YMinus;
    private const JogDirections ZAxis = JogDirections.ZPlus | JogDirections.ZMinus;

    /// <summary>Opposite directions of one axis cancel each other; Z wins over X/Y (they have different feed rates).</summary>
    public static JogDirections Normalize(this JogDirections d)
    {
        if ((d & XAxis) == XAxis)
            d &= ~XAxis;
        if ((d & YAxis) == YAxis)
            d &= ~YAxis;
        if ((d & ZAxis) == ZAxis)
            d &= ~ZAxis;
        if ((d & ZAxis) != 0)
            d &= ZAxis;
        return d;
    }

    /// <summary>-1, 0 or +1 for the given axis letter of a normalized value.</summary>
    public static int Sign(this JogDirections d, char axis) => char.ToUpperInvariant(axis) switch
    {
        'X' => (d.HasFlag(JogDirections.XPlus) ? 1 : 0) - (d.HasFlag(JogDirections.XMinus) ? 1 : 0),
        'Y' => (d.HasFlag(JogDirections.YPlus) ? 1 : 0) - (d.HasFlag(JogDirections.YMinus) ? 1 : 0),
        'Z' => (d.HasFlag(JogDirections.ZPlus) ? 1 : 0) - (d.HasFlag(JogDirections.ZMinus) ? 1 : 0),
        _ => 0,
    };

    /// <summary>
    /// The jog description of the view model: "X+", "Y-", "Z+", or "XY-+" for a diagonal (X−, Y+).
    /// Null when nothing is held.
    /// </summary>
    public static string? ToJogWhat(this JogDirections d)
    {
        d = d.Normalize();
        int x = d.Sign('X'), y = d.Sign('Y'), z = d.Sign('Z');
        static char C(int s) => s < 0 ? '-' : '+';
        if (z != 0)
            return "Z" + C(z);
        if (x != 0 && y != 0)
            return "XY" + C(x) + C(y);
        if (x != 0)
            return "X" + C(x);
        if (y != 0)
            return "Y" + C(y);
        return null;
    }
}

/// <summary>Picks the next or previous value of the step list.</summary>
public static class JogStepPicker
{
    /// <summary>The step after (+1) or before (-1) the current one; the nearest list item is used if it is not on the list.</summary>
    public static double Next(IReadOnlyList<double> steps, double current, int delta)
    {
        if (steps.Count == 0)
            return current;
        int best = 0;
        for (int i = 1; i < steps.Count; i++)
        {
            if (Math.Abs(steps[i] - current) < Math.Abs(steps[best] - current))
                best = i;
        }
        return steps[Math.Clamp(best + Math.Sign(delta), 0, steps.Count - 1)];
    }
}
