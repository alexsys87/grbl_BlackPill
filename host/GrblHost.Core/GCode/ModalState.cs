using System.Globalization;
using System.Numerics;
using System.Text;

namespace GrblHost.Core.GCode;

public enum MotionMode
{
    Rapid,
    Linear,
    ArcCw,
    ArcCcw,
    Probe,
    None,
}

public enum Plane
{
    XY,
    XZ,
    YZ,
}

/// <summary>
/// Modal state of a grbl G-code program as the lines are read: motion mode,
/// units, distance modes, plane, feed, spindle, coolant, the G92 offset and
/// the position (program coordinates, mm). Used by the toolpath builder and
/// to restart a job in the middle (<see cref="Preamble"/>).
/// </summary>
public sealed class ModalState
{
    public MotionMode Motion { get; private set; } = MotionMode.Rapid;
    public bool Absolute { get; private set; } = true;
    /// <summary>G90.1: arc centers absolute (grbl default is G91.1, incremental).</summary>
    public bool ArcAbsolute { get; private set; }
    public bool Inches { get; private set; }
    public bool InverseTime { get; private set; }
    public Plane Plane { get; private set; } = Plane.XY;
    /// <summary>Work coordinate system, 54 … 59.</summary>
    public int CoordinateSystem { get; private set; } = 54;
    /// <summary>Feed, mm/min (G94) or the inverse time value (G93).</summary>
    public double Feed { get; private set; }
    public double SpindleSpeed { get; private set; }
    public bool SpindleOn { get; private set; }
    public bool SpindleCcw { get; private set; }
    public bool Flood { get; private set; }
    public bool Mist { get; private set; }
    public int Tool { get; private set; }
    /// <summary>Position in program coordinates (after G92), mm.</summary>
    public Vector3 Position { get; private set; }
    /// <summary>G92 offset, mm.</summary>
    public Vector3 G92Offset { get; private set; }
    /// <summary>M2 / M30 seen.</summary>
    public bool ProgramEnd { get; private set; }

    /// <summary>What a block did, beyond changing the modal state.</summary>
    public readonly struct Step
    {
        public MotionMode? Motion { get; init; }
        /// <summary>Dwell, s (G4 P is seconds in grbl).</summary>
        public double Dwell { get; init; }
        /// <summary>Arc: center (program coordinates) or radius (R word, sign kept).</summary>
        public Vector3 ArcCenter { get; init; }
        public double? ArcRadius { get; init; }
        public int ArcTurns { get; init; }
        /// <summary>G93: 1/minutes for this move, 0 otherwise.</summary>
        public double InverseTimeFeed { get; init; }
        public bool Unsupported { get; init; }
    }

    private double Unit => Inches ? 25.4 : 1.0;

    public ModalState Clone() => (ModalState)MemberwiseClone();

    /// <summary>Apply one block, return what it does.</summary>
    public Step Apply(GCodeBlock b)
    {
        bool unsupported = false;
        MotionMode? motion = null;
        bool nonModalMachine = false;       // G53
        bool g92Set = false, g92Reset = false, homeMove = false, dwell = false, axisUser = false;

        foreach (double g in b.G)
        {
            switch (g)
            {
                case 0: motion = MotionMode.Rapid; break;
                case 1: motion = MotionMode.Linear; break;
                case 2: motion = MotionMode.ArcCw; break;
                case 3: motion = MotionMode.ArcCcw; break;
                case 4: dwell = true; break;
                case 10: axisUser = true; break;                 // G10 L2 / L20: offsets, no move here.
                case 17: Plane = Plane.XY; break;
                case 18: Plane = Plane.XZ; break;
                case 19: Plane = Plane.YZ; break;
                case 20: Inches = true; break;
                case 21: Inches = false; break;
                case 28: case 30: homeMove = true; break;
                case 28.1: case 30.1: axisUser = true; break;
                case 38.2: case 38.3: case 38.4: case 38.5: motion = MotionMode.Probe; break;
                case 40: case 49: case 43.1: case 61: case 61.1: case 64: case 50: case 98: case 99: break;
                case 53: nonModalMachine = true; break;
                case 54: case 55: case 56: case 57: case 58: case 59:
                    CoordinateSystem = (int)g;
                    break;
                case 59.1: case 59.2: case 59.3: break;
                case 80: motion = MotionMode.None; break;
                case 90: Absolute = true; break;
                case 91: Absolute = false; break;
                case 90.1: ArcAbsolute = true; break;
                case 91.1: ArcAbsolute = false; break;
                case 92: g92Set = true; break;
                case 92.1: case 92.2: g92Reset = true; break;
                case 93: InverseTime = true; break;
                case 94: InverseTime = false; break;
                default: unsupported = true; break;
            }
        }
        foreach (int mc in b.M)
        {
            switch (mc)
            {
                case 0: case 1: break;
                case 2: case 30:
                    ProgramEnd = true;
                    SpindleOn = false;
                    Flood = Mist = false;
                    Absolute = true;
                    break;
                case 3: SpindleOn = true; SpindleCcw = false; break;
                case 4: SpindleOn = true; SpindleCcw = true; break;
                case 5: SpindleOn = false; break;
                case 6: break;
                case 7: Mist = true; break;
                case 8: Flood = true; break;
                case 9: Flood = Mist = false; break;
                case 56: case 62: case 63: case 64: case 65: case 66: case 67: case 68: break;
                default: unsupported = true; break;
            }
        }
        if (b.TryGet('F', out double f))
            Feed = InverseTime ? f : f * Unit;
        if (b.TryGet('S', out double s))
            SpindleSpeed = s;
        if (b.TryGet('T', out double t))
            Tool = (int)t;

        if (dwell)
            return new Step { Dwell = b.Get('P'), Unsupported = unsupported };

        var target = Position;
        bool axes = b.HasAxisWords;
        if (g92Set)
        {
            // G92 X a: the current position becomes a.
            var off = G92Offset;
            var mach = Position + G92Offset;
            if (b.TryGet('X', out double x)) off.X = mach.X - (float)(x * Unit);
            if (b.TryGet('Y', out double y)) off.Y = mach.Y - (float)(y * Unit);
            if (b.TryGet('Z', out double z)) off.Z = mach.Z - (float)(z * Unit);
            Position = mach - off;
            G92Offset = off;
            return new Step { Unsupported = unsupported };
        }
        if (g92Reset)
        {
            Position += G92Offset;
            G92Offset = Vector3.Zero;
        }
        if (axisUser)
            return new Step { Unsupported = unsupported };

        if (homeMove)
        {
            // G28 / G30 move through the given point to the stored position,
            // which isn't known here: only the intermediate point is shown.
            if (axes)
            {
                target = Target(b, Position, Absolute);
                Position = target;
                return new Step { Motion = MotionMode.Rapid, Unsupported = unsupported };
            }
            return new Step { Unsupported = unsupported };
        }

        if (motion != null && motion != MotionMode.Probe)
            Motion = motion.Value;
        var mode = motion ?? Motion;
        if (!axes || mode == MotionMode.None)
            return new Step { Unsupported = unsupported };

        // G53: machine coordinates, shown as absolute program coordinates.
        target = Target(b, Position, Absolute || nonModalMachine);
        var step = new Step { Motion = mode, Unsupported = unsupported };
        if (mode is MotionMode.ArcCw or MotionMode.ArcCcw)
        {
            if (b.TryGet('R', out double r))
            {
                step = step with { ArcRadius = r * Unit };
            }
            else
            {
                var c = Position;
                double i = b.Get('I') * Unit, j = b.Get('J') * Unit, k = b.Get('K') * Unit;
                if (ArcAbsolute)
                    c = new Vector3(b.Has('I') ? (float)i : c.X, b.Has('J') ? (float)j : c.Y, b.Has('K') ? (float)k : c.Z);
                else
                    c += new Vector3((float)i, (float)j, (float)k);
                step = step with { ArcCenter = c };
            }
            step = step with { ArcTurns = b.Has('P') ? Math.Max(1, (int)b.Get('P')) : 1 };
        }
        if (InverseTime && mode != MotionMode.Rapid)
            step = step with { InverseTimeFeed = Feed };
        Position = target;
        return step;
    }

    private Vector3 Target(GCodeBlock b, Vector3 from, bool absolute)
    {
        var t = from;
        if (b.TryGet('X', out double x)) t.X = (float)(absolute ? x * Unit : from.X + x * Unit);
        if (b.TryGet('Y', out double y)) t.Y = (float)(absolute ? y * Unit : from.Y + y * Unit);
        if (b.TryGet('Z', out double z)) t.Z = (float)(absolute ? z * Unit : from.Z + z * Unit);
        return t;
    }

    /// <summary>
    /// Lines that bring the controller into this modal state before a job
    /// continues from the middle: units, distance mode, plane, coordinate
    /// system, feed mode, spindle and coolant (with a dwell for the spindle
    /// to come up to speed), then XY at the safe height and down to Z.
    /// A G92 offset set by the skipped lines stays as the controller has it.
    /// </summary>
    public IReadOnlyList<string> Preamble(double safeZ, double spindleDelay = 3)
    {
        var inv = CultureInfo.InvariantCulture;
        var lines = new List<string>
        {
            Inches ? "G20" : "G21",
            "G90",
            ArcAbsolute ? "G90.1" : "G91.1",
            Plane switch { Plane.XZ => "G18", Plane.YZ => "G19", _ => "G17" },
            "G" + CoordinateSystem.ToString(inv),
            InverseTime ? "G93" : "G94",
        };
        double unit = Inches ? 25.4 : 1;
        double z = Position.Z / unit;
        double safe = Math.Max(safeZ / unit, z);
        lines.Add(string.Format(inv, "G0 Z{0:0.###}", safe));
        if (SpindleOn)
        {
            lines.Add(string.Format(inv, "{0} S{1:0.#}", SpindleCcw ? "M4" : "M3", SpindleSpeed));
            if (spindleDelay > 0)
                lines.Add(string.Format(inv, "G4 P{0:0.#}", spindleDelay));
        }
        if (Flood)
            lines.Add("M8");
        if (Mist)
            lines.Add("M7");
        lines.Add(string.Format(inv, "G0 X{0:0.###} Y{1:0.###}", Position.X / unit, Position.Y / unit));
        if (Feed > 0 && !InverseTime)
            lines.Add(string.Format(inv, "G1 Z{0:0.###} F{1:0.#}", z, Feed / unit));
        else
            lines.Add(string.Format(inv, "G0 Z{0:0.###}", z));
        if (!Absolute)
            lines.Add("G91");
        // Restore the modal motion mode for lines with coordinates only.
        lines.Add(Motion switch
        {
            MotionMode.Linear => "G1",
            MotionMode.ArcCw => "G2",
            MotionMode.ArcCcw => "G3",
            MotionMode.None => "G80",
            _ => "G0",
        });
        return lines;
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append(Motion).Append(' ').Append(Absolute ? "G90" : "G91").Append(' ').Append(Inches ? "G20" : "G21");
        return sb.ToString();
    }
}
