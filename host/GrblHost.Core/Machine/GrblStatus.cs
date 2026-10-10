using System.Globalization;

namespace GrblHost.Core.Machine;

/// <summary>Machine state from the status report (grbl 1.1 / grblHAL).</summary>
public enum MachineState
{
    Unknown,
    Idle,
    Run,
    Hold,
    Jog,
    Alarm,
    Door,
    Check,
    Home,
    Sleep,
    Tool,
}

/// <summary>Position of the axes, mm. Three axes on the 3018, more are parsed but ignored.</summary>
public readonly record struct Axes(double X, double Y, double Z)
{
    public static Axes Zero => default;

    public static Axes operator +(Axes a, Axes b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Axes operator -(Axes a, Axes b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public double this[int axis] => axis switch { 0 => X, 1 => Y, 2 => Z, _ => 0 };

    public Axes With(int axis, double value) => axis switch
    {
        0 => this with { X = value },
        1 => this with { Y = value },
        2 => this with { Z = value },
        _ => this,
    };

    /// <summary>"1.000,2.000,3.000" (more values ignored), null if not parsable.</summary>
    public static Axes? Parse(string text)
    {
        var parts = text.Split(',');
        if (parts.Length < 3)
            return null;
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y) ||
            !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double z))
            return null;
        return new Axes(x, y, z);
    }

    public string Format(int decimals = 3)
    {
        string f = "F" + decimals;
        return string.Join(",", X.ToString(f, CultureInfo.InvariantCulture), Y.ToString(f, CultureInfo.InvariantCulture),
            Z.ToString(f, CultureInfo.InvariantCulture));
    }
}

/// <summary>
/// One status report, "&lt;Idle|MPos:0.000,0.000,0.000|Bf:100,1023|FS:0,0|WCO:0,0,0&gt;".
/// Fields that weren't in the report are null.
/// </summary>
public sealed class GrblStatus
{
    public MachineState State { get; init; }

    /// <summary>Sub state after ':' (Hold:0 complete, Hold:1 decelerating, Door:n, Alarm:n), -1 if none.</summary>
    public int SubState { get; init; } = -1;

    /// <summary>The state word as sent, e.g. "Hold:1".</summary>
    public string StateText { get; init; } = "";

    public Axes? MPos { get; init; }
    public Axes? WPos { get; init; }
    public Axes? Wco { get; init; }

    /// <summary>Free planner blocks and free input buffer bytes (Bf).</summary>
    public int? PlannerFree { get; init; }
    public int? RxFree { get; init; }

    public int? LineNumber { get; init; }

    /// <summary>Current feed, mm/min, and spindle speed, rpm (FS or F).</summary>
    public double? Feed { get; init; }
    public double? Spindle { get; init; }

    /// <summary>Overrides in percent: feed, rapid, spindle (Ov).</summary>
    public int? FeedOverride { get; init; }
    public int? RapidOverride { get; init; }
    public int? SpindleOverride { get; init; }

    /// <summary>Active input pins (Pn): X Y Z limits, P probe, D door, H hold, R reset, S start, E e-stop.</summary>
    public string? Pins { get; init; }

    /// <summary>Accessory state (A): S spindle CW, C spindle CCW, F flood, M mist.</summary>
    public string? Accessories { get; init; }

    public string Raw { get; init; } = "";

    public bool PinActive(char pin) => Pins?.IndexOf(pin) >= 0;

    public static bool IsStatusReport(string line) => line.Length > 2 && line[0] == '<' && line[^1] == '>';

    public static MachineState ParseState(string word) => word switch
    {
        "Idle" => MachineState.Idle,
        "Run" => MachineState.Run,
        "Hold" => MachineState.Hold,
        "Jog" => MachineState.Jog,
        "Alarm" => MachineState.Alarm,
        "Door" => MachineState.Door,
        "Check" => MachineState.Check,
        "Home" => MachineState.Home,
        "Sleep" => MachineState.Sleep,
        "Tool" => MachineState.Tool,
        _ => MachineState.Unknown,
    };

    /// <summary>Parse a status report line, null if it isn't one.</summary>
    public static GrblStatus? Parse(string line)
    {
        if (!IsStatusReport(line))
            return null;
        var fields = line[1..^1].Split('|');
        string stateText = fields[0];
        string word = stateText;
        int sub = -1;
        int colon = stateText.IndexOf(':');
        if (colon > 0)
        {
            word = stateText[..colon];
            int.TryParse(stateText[(colon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out sub);
        }

        Axes? mpos = null, wpos = null, wco = null;
        int? pf = null, rf = null, ln = null, fo = null, ro = null, so = null;
        double? feed = null, spindle = null;
        string? pins = null, acc = null;

        for (int i = 1; i < fields.Length; i++)
        {
            string f = fields[i];
            int c = f.IndexOf(':');
            string key = c > 0 ? f[..c] : f;
            string val = c > 0 ? f[(c + 1)..] : "";
            switch (key)
            {
                case "MPos":
                    mpos = Axes.Parse(val);
                    break;
                case "WPos":
                    wpos = Axes.Parse(val);
                    break;
                case "WCO":
                    wco = Axes.Parse(val);
                    break;
                case "Bf":
                {
                    var p = val.Split(',');
                    if (p.Length >= 2)
                    {
                        pf = Int(p[0]);
                        rf = Int(p[1]);
                    }
                    break;
                }
                case "Ln":
                    ln = Int(val);
                    break;
                case "F":
                    feed = Dbl(val);
                    break;
                case "FS":
                {
                    var p = val.Split(',');
                    feed = Dbl(p[0]);
                    if (p.Length > 1)
                        spindle = Dbl(p[1]);
                    break;
                }
                case "Ov":
                {
                    var p = val.Split(',');
                    if (p.Length >= 3)
                    {
                        fo = Int(p[0]);
                        ro = Int(p[1]);
                        so = Int(p[2]);
                    }
                    break;
                }
                case "Pn":
                    pins = val;
                    break;
                case "A":
                    acc = val;
                    break;
            }
        }

        return new GrblStatus
        {
            State = ParseState(word),
            SubState = sub,
            StateText = stateText,
            MPos = mpos,
            WPos = wpos,
            Wco = wco,
            PlannerFree = pf,
            RxFree = rf,
            LineNumber = ln,
            Feed = feed,
            Spindle = spindle,
            FeedOverride = fo,
            RapidOverride = ro,
            SpindleOverride = so,
            Pins = pins,
            Accessories = acc,
            Raw = line,
        };
    }

    private static int? Int(string s) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : null;

    private static double? Dbl(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;
}

/// <summary>
/// The parts of the machine state that come and go in the reports (WCO and
/// Ov only in some of them, Pn / A only while active), merged into one
/// complete picture.
/// </summary>
public sealed class MachineSnapshot
{
    public MachineState State { get; set; } = MachineState.Unknown;
    public int SubState { get; set; } = -1;
    public string StateText { get; set; } = "";
    public Axes MPos { get; set; }
    public Axes Wco { get; set; }
    public Axes WPos => MPos - Wco;
    public double Feed { get; set; }
    public double Spindle { get; set; }
    public int FeedOverride { get; set; } = 100;
    public int RapidOverride { get; set; } = 100;
    public int SpindleOverride { get; set; } = 100;
    public string Pins { get; set; } = "";
    public string Accessories { get; set; } = "";
    public int PlannerFree { get; set; }
    /// <summary>The status reports carry the planner level ("Bf:"); PlannerFree means nothing before that.</summary>
    public bool PlannerKnown { get; set; }
    public int RxFree { get; set; }
    public int LineNumber { get; set; } = -1;

    public bool SpindleCw => Accessories.Contains('S');
    public bool SpindleCcw => Accessories.Contains('C');
    public bool Flood => Accessories.Contains('F');
    public bool Mist => Accessories.Contains('M');

    public void Apply(GrblStatus s)
    {
        State = s.State;
        SubState = s.SubState;
        StateText = s.StateText;
        if (s.Wco is { } wco)
            Wco = wco;
        if (s.MPos is { } m)
            MPos = m;
        else if (s.WPos is { } w)
            MPos = w + Wco;
        if (s.Feed is { } f)
            Feed = f;
        if (s.Spindle is { } sp)
            Spindle = sp;
        if (s.FeedOverride is { } fo)
        {
            FeedOverride = fo;
            RapidOverride = s.RapidOverride ?? RapidOverride;
            SpindleOverride = s.SpindleOverride ?? SpindleOverride;
            // Ov comes with A when an accessory is on: no A means all off.
            Accessories = s.Accessories ?? "";
        }
        else if (s.Accessories != null)
        {
            Accessories = s.Accessories;
        }
        Pins = s.Pins ?? "";
        if (s.PlannerFree is { } pf)
        {
            PlannerFree = pf;
            PlannerKnown = true;
        }
        if (s.RxFree is { } rf)
            RxFree = rf;
        if (s.LineNumber is { } ln)
            LineNumber = ln;
    }

    public MachineSnapshot Clone() => (MachineSnapshot)MemberwiseClone();
}
