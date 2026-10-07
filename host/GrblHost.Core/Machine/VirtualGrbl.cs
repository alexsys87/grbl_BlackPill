using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using GrblHost.Core.GCode;

namespace GrblHost.Core.Machine;

/// <summary>
/// A simulated grblHAL controller (the CNC 3018 firmware of this project):
/// input buffer with character counting, planner, motion at the programmed
/// feed (no acceleration), feed hold, jog and jog cancel, overrides, soft
/// reset (alarm 3 when in motion), probing against a virtual plate, homing,
/// $ commands and settings, check mode, alarms and the grblHAL error lock
/// (G-code is refused after an error until an empty line). Good enough to
/// try the program without a machine and for the protocol tests.
/// </summary>
public sealed class VirtualGrbl : IGrblTransport
{
    public const int RxBufferSize = 1024;
    public const int PlannerBlocks = 35;

    private readonly object _lock = new();
    private readonly StringBuilder _rx = new();
    private readonly Queue<Block> _planner = new();
    private Thread? _thread;
    private volatile bool _running;
    private readonly Stopwatch _clock = new();

    // Machine.
    private Vector3 _mpos;
    private Vector3[] _wcs = new Vector3[6];
    private int _wcsIndex;                  // 0 = G54
    private Vector3 _g92;
    private ModalLite _gc = new();
    private string _state = "Idle";
    private int _alarm;
    private bool _hold;
    private double _holdUntil;              // Hold:1 (decelerating) until then.
    private bool _check;
    private bool _homed;
    private Block? _current;
    private double _currentDone;            // mm done of the current block.
    private int _feedOv = 100, _rapidOv = 100, _spindleOv = 100;
    private bool _spindleOn, _spindleCcw, _flood, _mist;
    private double _spindleRpm;
    private int _lastError;                 // grblHAL: G-code refused until an empty line.
    private int _reportCount;
    private Vector3 _lastWcoReported = new(float.NaN);
    private Vector3 _probePos;
    private bool _probeOk;
    private double _dwellUntil = -1;
    private bool _syncWait;                 // A command waits for the planner to run empty.
    private string? _syncLine;

    private readonly SortedDictionary<int, string> _settings = new();

    public VirtualGrbl()
    {
        RestoreDefaults();
    }

    /// <summary>Speed of time: 10 means the machine moves ten times faster than real.</summary>
    public double TimeScale { get; set; } = 1;

    /// <summary>Machine Z of the virtual probe plate, mm (G38.x touches it).</summary>
    public double ProbePlateZ { get; set; } = -30;

    /// <summary>Lines received, for tests.</summary>
    public List<string> Received { get; } = new();

    public string Name => "Virtual grblHAL";
    public bool IsOpen => _running;

    public event Action<string>? LineReceived;
    public event Action<Exception>? Faulted { add { } remove { } }

    /// <summary>Machine position, for tests.</summary>
    public Vector3 MachinePosition
    {
        get { lock (_lock) return _mpos; }
    }

    public string StateName
    {
        get { lock (_lock) return _state; }
    }

    private sealed class Block
    {
        public Vector3 Start, End;
        public Vector3 Center;              // Arc center (machine coordinates).
        public int Arc;                     // 0 line, 1 CW, -1 CCW (XY plane).
        public double Sweep, Radius;
        public double Feed;                 // mm/min
        public bool Rapid, Jog, Probe;
        public bool ProbeAway, ProbeNoAlarm;
        public double Length;

        public Vector3 At(double d)
        {
            if (Length <= 0)
                return End;
            double t = Math.Clamp(d / Length, 0, 1);
            if (Arc == 0)
                return Vector3.Lerp(Start, End, (float)t);
            double a0 = Math.Atan2(Start.Y - Center.Y, Start.X - Center.X);
            double a = a0 + Sweep * t;
            return new Vector3((float)(Center.X + Radius * Math.Cos(a)), (float)(Center.Y + Radius * Math.Sin(a)),
                Start.Z + (End.Z - Start.Z) * (float)t);
        }
    }

    private sealed class ModalLite
    {
        public int Motion;                  // 0, 1, 2, 3, 80
        public bool Absolute = true, Inches, ArcAbsolute;
        public double Feed, Spindle;
        public int Plane = 17;
        public int Tool;
    }

    // ---------------------------------------------------------------- transport

    public void Open()
    {
        _running = true;
        _clock.Restart();
        _thread = new Thread(Run) { IsBackground = true, Name = "Virtual grblHAL" };
        _thread.Start();
        Emit("");
        Emit("GrblHAL 1.1f ['$' or '$HELP' for help]");
    }

    public void Close()
    {
        _running = false;
        _thread?.Join(1000);
        _thread = null;
    }

    public void Dispose() => Close();

    public void WriteLine(string line)
    {
        lock (_lock)
        {
            // A full buffer drops characters, like the real one would.
            int room = RxBufferSize - 1 - _rx.Length;
            string data = line + "\n";
            if (data.Length > room)
                data = data[..Math.Max(0, room)];
            _rx.Append(data);
            Received.Add(line);
        }
    }

    public void WriteRaw(byte value)
    {
        lock (_lock)
            Realtime(value);
    }

    private void Emit(string line)
    {
        try
        {
            LineReceived?.Invoke(line);
        }
        catch
        {
            // A faulty handler must not stop the machine.
        }
    }

    // ---------------------------------------------------------------- real time

    private double Now => _clock.Elapsed.TotalSeconds * TimeScale;

    private void Realtime(byte c)
    {
        switch (c)
        {
            case RealtimeCommand.StatusReport:
            case 0x80:
                Emit(StatusReport());
                break;
            case RealtimeCommand.FeedHold:
            case 0x82:
                if (_state is "Run" or "Jog")
                {
                    if (_state == "Jog")
                    {
                        CancelJog();
                    }
                    else
                    {
                        _hold = true;
                        _holdUntil = Now + 0.15;
                        _state = "Hold";
                    }
                }
                break;
            case RealtimeCommand.CycleStart:
            case 0x81:
                if (_hold)
                {
                    _hold = false;
                    _state = _current != null || _planner.Count > 0 ? "Run" : "Idle";
                }
                break;
            case RealtimeCommand.SoftReset:
                Reset();
                break;
            case RealtimeCommand.JogCancel:
                if (_state == "Jog")
                    CancelJog();
                break;
            case RealtimeCommand.FeedOvReset: _feedOv = 100; break;
            case RealtimeCommand.FeedOvCoarsePlus: _feedOv = Math.Min(200, _feedOv + 10); break;
            case RealtimeCommand.FeedOvCoarseMinus: _feedOv = Math.Max(10, _feedOv - 10); break;
            case RealtimeCommand.FeedOvFinePlus: _feedOv = Math.Min(200, _feedOv + 1); break;
            case RealtimeCommand.FeedOvFineMinus: _feedOv = Math.Max(10, _feedOv - 1); break;
            case RealtimeCommand.RapidOvReset: _rapidOv = 100; break;
            case RealtimeCommand.RapidOvMedium: _rapidOv = 50; break;
            case RealtimeCommand.RapidOvLow: _rapidOv = 25; break;
            case RealtimeCommand.SpindleOvReset: _spindleOv = 100; break;
            case RealtimeCommand.SpindleOvCoarsePlus: _spindleOv = Math.Min(200, _spindleOv + 10); break;
            case RealtimeCommand.SpindleOvCoarseMinus: _spindleOv = Math.Max(10, _spindleOv - 10); break;
            case RealtimeCommand.SpindleOvFinePlus: _spindleOv = Math.Min(200, _spindleOv + 1); break;
            case RealtimeCommand.SpindleOvFineMinus: _spindleOv = Math.Max(10, _spindleOv - 1); break;
            case RealtimeCommand.FloodToggle:
                if (_state is "Idle" or "Run" or "Hold")
                    _flood = !_flood;
                break;
            case RealtimeCommand.MistToggle:
                if (_state is "Idle" or "Run" or "Hold")
                    _mist = !_mist;
                break;
        }
    }

    private void CancelJog()
    {
        _planner.Clear();
        _current = null;
        _state = "Idle";
        _rx.Clear();
    }

    private void Reset()
    {
        bool moving = _current != null && _state is "Run" or "Jog" && !_hold;
        _planner.Clear();
        _current = null;
        _rx.Clear();
        _hold = false;
        _syncWait = false;
        _syncLine = null;
        _dwellUntil = -1;
        _spindleOn = false;
        _flood = _mist = false;
        _lastError = 0;
        _feedOv = _rapidOv = _spindleOv = 100;
        _gc.Motion = 0;
        _gc.Absolute = true;
        if (moving)
        {
            _alarm = 3;
            _state = "Alarm";
            Emit("ALARM:3");
        }
        else if (_state != "Alarm")
        {
            _state = _check ? "Check" : "Idle";
        }
        Emit("");
        Emit("GrblHAL 1.1f ['$' or '$HELP' for help]");
        if (_alarm != 0)
            Emit("[MSG:'$H'|'$X' to unlock]");
    }

    private string StatusReport()
    {
        var inv = CultureInfo.InvariantCulture;
        string state = _state == "Hold" ? (Now < _holdUntil ? "Hold:1" : "Hold:0") : _state;
        double feed = _current != null && !_hold ? (_current.Rapid ? MaxRate() : _current.Feed) * Ov(_current) : 0;
        var sb = new StringBuilder();
        sb.Append('<').Append(state);
        sb.Append("|MPos:").Append(Fmt(_mpos));
        sb.Append("|Bf:").Append(PlannerBlocks - _planner.Count - (_current != null ? 1 : 0)).Append(',')
          .Append(RxBufferSize - 1 - _rx.Length);
        sb.Append("|FS:").Append(feed.ToString("0", inv)).Append(',')
          .Append((_spindleOn ? _spindleRpm * _spindleOv / 100 : 0).ToString("0", inv));
        var pins = new StringBuilder();
        if (_mpos.Z <= ProbePlateZ)
            pins.Append('P');
        if (pins.Length > 0)
            sb.Append("|Pn:").Append(pins);
        var wco = Wco();
        _reportCount++;
        if (wco != _lastWcoReported || _reportCount % 10 == 0)
        {
            sb.Append("|WCO:").Append(Fmt(wco));
            _lastWcoReported = wco;
        }
        else if (_reportCount % 10 == 5)
        {
            sb.Append("|Ov:").Append(_feedOv).Append(',').Append(_rapidOv).Append(',').Append(_spindleOv);
            var acc = new StringBuilder();
            if (_spindleOn) acc.Append(_spindleCcw ? 'C' : 'S');
            if (_flood) acc.Append('F');
            if (_mist) acc.Append('M');
            if (acc.Length > 0)
                sb.Append("|A:").Append(acc);
        }
        sb.Append('>');
        return sb.ToString();
    }

    private static string Fmt(Vector3 v) =>
        string.Format(CultureInfo.InvariantCulture, "{0:0.000},{1:0.000},{2:0.000}", v.X, v.Y, v.Z);

    private Vector3 Wco() => _wcs[_wcsIndex] + _g92;

    private double MaxRate() => Setting(110);

    private double Ov(Block b) => b.Rapid ? _rapidOv / 100.0 : b.Jog ? 1 : _feedOv / 100.0;

    // ---------------------------------------------------------------- main loop

    private void Run()
    {
        double last = Now;
        while (_running)
        {
            Thread.Sleep(2);
            var output = new List<string>();
            lock (_lock)
            {
                double now = Now;
                double dt = now - last;
                last = now;
                Motion(dt, output);
                ReadLines(output);
            }
            foreach (var l in output)
                Emit(l);
        }
    }

    private void Motion(double dt, List<string> output)
    {
        if (_dwellUntil >= 0 && Now >= _dwellUntil)
            _dwellUntil = -1;
        if (_hold)
            return;
        while (dt > 0)
        {
            if (_current == null)
            {
                if (_planner.Count == 0)
                {
                    if (_state is "Run" or "Jog" && _dwellUntil < 0)
                        _state = _check ? "Check" : "Idle";
                    return;
                }
                _current = _planner.Dequeue();
                _currentDone = 0;
                if (_state is "Idle" or "Check")
                    _state = _current.Jog ? "Jog" : "Run";
            }
            var b = _current;
            double speed = (b.Rapid ? MaxRate() : b.Feed) * Ov(b) / 60.0;
            double step = speed * dt;
            if (b.Probe)
            {
                // Touch: the plate at ProbePlateZ.
                var p = b.At(Math.Min(b.Length, _currentDone + step));
                bool touch = p.Z <= ProbePlateZ;
                if (touch ^ b.ProbeAway)
                {
                    var hit = new Vector3(p.X, p.Y, b.ProbeAway ? p.Z : (float)ProbePlateZ);
                    _mpos = hit;
                    _probePos = hit;
                    _probeOk = true;
                    _current = null;
                    _planner.Clear();
                    output.Add("[PRB:" + Fmt(hit) + ":1]");
                    return;
                }
            }
            double left = b.Length - _currentDone;
            if (step < left)
            {
                _currentDone += step;
                _mpos = b.At(_currentDone);
                return;
            }
            dt -= left / Math.Max(speed, 1e-6);
            _mpos = b.End;
            _current = null;
            if (b.Probe)
            {
                _probeOk = false;
                _probePos = _mpos;
                output.Add("[PRB:" + Fmt(_mpos) + ":0]");
                if (!b.ProbeNoAlarm)
                {
                    _planner.Clear();
                    Alarm(b.ProbeAway ? 4 : 5, output);
                    return;
                }
            }
        }
    }

    private void Alarm(int code, List<string> output)
    {
        _alarm = code;
        _state = "Alarm";
        _planner.Clear();
        _current = null;
        _rx.Clear();
        output.Add("ALARM:" + code.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Execute lines from the input buffer while the planner has room.</summary>
    private void ReadLines(List<string> output)
    {
        while (true)
        {
            if (_syncWait)
            {
                if (_current != null || _planner.Count > 0 || _dwellUntil >= 0)
                    return;
                _syncWait = false;
                string l = _syncLine!;
                _syncLine = null;
                ExecuteSynced(l, output);
                continue;
            }
            if (_dwellUntil >= 0 || _planner.Count >= PlannerBlocks - 1)
                return;
            string s = _rx.ToString();
            int nl = s.IndexOf('\n');
            if (nl < 0)
                return;
            string line = s[..nl].Trim();
            _rx.Remove(0, nl + 1);
            Execute(line, output);
        }
    }

    private void Reply(int error, List<string> output)
    {
        output.Add(error == 0 ? "ok" : "error:" + error.ToString(CultureInfo.InvariantCulture));
    }

    private void Execute(string line, List<string> output)
    {
        if (line.Length == 0)
        {
            _lastError = 0;
            Reply(0, output);
            return;
        }
        if (line[0] == '$')
        {
            int e = SystemCommand(line, output);
            _lastError = e;
            if (e >= 0)
                Reply(e, output);
            return;
        }
        if (_state is "Alarm" or "Jog")
        {
            _lastError = 9;
            Reply(9, output);
            return;
        }
        if (_lastError != 0)
        {
            Reply(_lastError, output);
            return;
        }
        int err = GCode(line, output);
        _lastError = err > 0 ? err : 0;
        if (err >= 0)
            Reply(err, output);
    }

    // ---------------------------------------------------------------- $ commands

    /// <returns>Error code, 0 ok, -1 when the answer comes later.</returns>
    private int SystemCommand(string line, List<string> output)
    {
        string cmd = line.ToUpperInvariant();
        var inv = CultureInfo.InvariantCulture;
        if (cmd.StartsWith("$J="))
        {
            if (_state is not ("Idle" or "Jog"))
                return 8;
            return Jog(line[3..]);
        }
        switch (cmd)
        {
            case "$":
            case "$HELP":
                output.Add("[HLP:$$ $# $G $I $N $x=val $Nx=line $J=line $SLP $C $X $H $B ~ ! ? ctrl-x]");
                return 0;
            case "$I":
                output.Add("[VER:1.1f.20261004:CNC3018 BlackPill (virtual)]");
                output.Add("[OPT:VNMSL," + PlannerBlocks + "," + RxBufferSize + ",3,0]");
                output.Add("[AXS:3:XYZ]");
                output.Add("[FIRMWARE:grblHAL]");
                output.Add("[DRIVER:Virtual]");
                output.Add("[BOARD:CNC 3018 BlackPill]");
                return 0;
            case "$$":
                foreach (var kv in _settings)
                    output.Add("$" + kv.Key.ToString(inv) + "=" + kv.Value);
                return 0;
            case "$G":
                output.Add(ParserState());
                return 0;
            case "$#":
                for (int i = 0; i < 6; i++)
                    output.Add("[G" + (54 + i).ToString(inv) + ":" + Fmt(_wcs[i]) + "]");
                output.Add("[G28:0.000,0.000,0.000]");
                output.Add("[G30:0.000,0.000,0.000]");
                output.Add("[G92:" + Fmt(_g92) + "]");
                output.Add("[TLO:0.000]");
                output.Add("[PRB:" + Fmt(_probePos) + ":" + (_probeOk ? "1" : "0") + "]");
                return 0;
            case "$X":
                if (_state == "Alarm")
                {
                    _alarm = 0;
                    _state = "Idle";
                    output.Add("[MSG:Caution: Unlocked]");
                }
                return 0;
            case "$H":
                if (Setting(22) == 0)
                    return 5;
                if (_state is not ("Idle" or "Alarm"))
                    return 8;
                _state = "Home";
                _planner.Clear();
                _current = null;
                // Homing takes a while; the answer comes with the end of it.
                var home = new Block { Start = _mpos, End = Vector3.Zero, Rapid = false, Feed = Setting(25) };
                home.Length = Vector3.Distance(home.Start, home.End);
                _mpos = Vector3.Zero;
                _homed = true;
                _alarm = 0;
                _state = "Idle";
                return 0;
            case "$C":
                if (_state is not ("Idle" or "Check"))
                    return 8;
                _check = !_check;
                _state = _check ? "Check" : "Idle";
                output.Add(_check ? "[MSG:Enabled]" : "[MSG:Disabled]");
                if (!_check)
                {
                    // Leaving check mode resets, like grbl.
                    Reset();
                    return -1;
                }
                return 0;
            case "$SLP":
                _state = "Sleep";
                return 0;
            case "$RST=$":
            case "$RST=*":
                RestoreDefaults();
                return 0;
            case "$RST=#":
                _wcs = new Vector3[6];
                _g92 = Vector3.Zero;
                return 0;
        }
        // $n=value
        int eq = cmd.IndexOf('=');
        if (eq > 1 && int.TryParse(cmd.AsSpan(1, eq - 1), NumberStyles.Integer, inv, out int id))
        {
            if (_state is not ("Idle" or "Alarm"))
                return 8;
            if (!_settings.ContainsKey(id))
                return 3;
            if (!double.TryParse(cmd[(eq + 1)..], NumberStyles.Float, inv, out double v))
                return 2;
            if (v < 0)
                return 4;
            _settings[id] = FormatSetting(id, v);
            return 0;
        }
        if (cmd.Length > 1 && int.TryParse(cmd.AsSpan(1), NumberStyles.Integer, inv, out int one) && _settings.TryGetValue(one, out var val))
        {
            output.Add("$" + one.ToString(inv) + "=" + val);
            return 0;
        }
        return 3;
    }

    private string ParserState()
    {
        var inv = CultureInfo.InvariantCulture;
        string motion = _gc.Motion switch { 1 => "G1", 2 => "G2", 3 => "G3", 80 => "G80", _ => "G0" };
        string spindle = _spindleOn ? (_spindleCcw ? "M4" : "M3") : "M5";
        string cool = _flood && _mist ? "M7 M8" : _flood ? "M8" : _mist ? "M7" : "M9";
        return string.Format(inv, "[GC:{0} G{1} G{2} {3} {4} G94 G49 G98 G50 {5} {6} T{7} F{8:0} S{9:0}]",
            motion, 54 + _wcsIndex, _gc.Plane, _gc.Inches ? "G20" : "G21", _gc.Absolute ? "G90" : "G91",
            spindle, cool, _gc.Tool, _gc.Feed, _gc.Spindle);
    }

    private double Setting(int id) =>
        _settings.TryGetValue(id, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
            ? d
            : 0;

    private static string FormatSetting(int id, double v) =>
        id is >= 100 and < 200 || id is 11 or 12 or 24 or 25 or 27 or 0 or 30 or 31
            ? v.ToString("0.000", CultureInfo.InvariantCulture)
            : v.ToString("0.###", CultureInfo.InvariantCulture);

    private void RestoreDefaults()
    {
        _settings.Clear();
        void S(int id, double v) => _settings[id] = FormatSetting(id, v);
        S(0, 4); S(1, 25); S(2, 0); S(3, 0); S(4, 7); S(5, 7); S(6, 1); S(10, 511); S(11, 0.01); S(12, 0.002);
        S(13, 0); S(14, 79); S(15, 0); S(16, 0); S(17, 0); S(18, 0); S(19, 0); S(20, 0); S(21, 0); S(22, 0);
        S(23, 0); S(24, 100); S(25, 800); S(26, 250); S(27, 2); S(29, 0); S(30, 10000); S(31, 0); S(32, 0);
        S(33, 1000); S(34, 0); S(35, 0); S(36, 100);
        S(100, 800); S(101, 800); S(102, 800); S(110, 1000); S(111, 1000); S(112, 600);
        S(120, 50); S(121, 50); S(122, 50); S(130, 300); S(131, 180); S(132, 45);
    }

    // ---------------------------------------------------------------- G-code

    private int Jog(string words)
    {
        var b = GCodeBlock.Parse(words);
        if (!b.Has('F'))
            return 16;
        bool abs = !b.HasG(91);
        bool machine = b.HasG(53);
        double unit = b.HasG(20) ? 25.4 : 1;
        var wco = Wco();
        var start = _current?.End ?? (_planner.Count > 0 ? _planner.Last().End : _mpos);
        var target = start;
        if (b.TryGet('X', out double x)) target.X = (float)(abs ? x * unit + (machine ? 0 : wco.X) : start.X + x * unit);
        if (b.TryGet('Y', out double y)) target.Y = (float)(abs ? y * unit + (machine ? 0 : wco.Y) : start.Y + y * unit);
        if (b.TryGet('Z', out double z)) target.Z = (float)(abs ? z * unit + (machine ? 0 : wco.Z) : start.Z + z * unit);
        if (Setting(20) != 0 && !InsideTravel(target))
            return 15;
        Queue(new Block { Start = start, End = target, Feed = b.Get('F') * unit, Jog = true });
        if (_state == "Idle")
            _state = "Jog";
        return 0;
    }

    private bool InsideTravel(Vector3 p) =>
        p.X <= 0.001 && p.X >= -Setting(130) && p.Y <= 0.001 && p.Y >= -Setting(131) && p.Z <= 0.001 && p.Z >= -Setting(132);

    private void Queue(Block b)
    {
        b.Length = b.Arc == 0 ? Vector3.Distance(b.Start, b.End) : Math.Abs(b.Sweep) * b.Radius + Math.Abs(b.End.Z - b.Start.Z);
        if (b.Length < 1e-6)
            return;
        if (_check)
            return;
        _planner.Enqueue(b);
        if (_state == "Idle")
            _state = b.Jog ? "Jog" : "Run";
    }

    /// <summary>Lines that run when the planner is empty (grbl's buffer sync).</summary>
    private static bool NeedsSync(GCodeBlock b) =>
        b.M.Exists(m => m is 3 or 4 or 5 or 7 or 8 or 9 or 0 or 1 or 2 or 30) || b.HasG(4) || b.HasG(92) ||
        b.HasG(10) || b.HasG(38.2) || b.HasG(38.3) || b.HasG(38.4) || b.HasG(38.5);

    private int GCode(string line, List<string> output)
    {
        var b = GCodeBlock.Parse(line);
        if (b.BadFormat)
            return 2;
        if (b.RepeatedWord)
            return 25;
        if (NeedsSync(b) && (_current != null || _planner.Count > 0))
        {
            _syncWait = true;
            _syncLine = line;
            return -1;                       // ok when executed.
        }
        return Interpret(b, output);
    }

    private void ExecuteSynced(string line, List<string> output)
    {
        int err = _lastError != 0 ? _lastError : Interpret(GCodeBlock.Parse(line), output);
        _lastError = err > 0 ? err : 0;
        if (err >= 0)
            Reply(err, output);
    }

    private int Interpret(GCodeBlock b, List<string> output)
    {
        // Letters a 3 axis grblHAL doesn't take (20), Q left over (36).
        foreach (char letter in "ABCDEHOUVW")
            if (b.Has(letter))
                return 20;
        if (b.Has('Q'))
            return 36;
        var gc = _gc;
        int motion = -1;
        bool g53 = false, g92 = false, g921 = false, dwell = false, g10 = false, g28 = false;
        double probe = 0;
        foreach (double g in b.G)
        {
            switch (g)
            {
                case 0: case 1: case 2: case 3: motion = (int)g; break;
                case 80: motion = 80; break;
                case 4: dwell = true; break;
                case 10: g10 = true; break;
                case 17: case 18: case 19: gc.Plane = (int)g; break;
                case 20: gc.Inches = true; break;
                case 21: gc.Inches = false; break;
                case 28: case 30: g28 = true; break;
                case 28.1: case 30.1: break;
                case 38.2: case 38.3: case 38.4: case 38.5: probe = g; break;
                case 40: case 43.1: case 49: case 61: case 94: case 98: case 99: case 50: break;
                case 53: g53 = true; break;
                case 54: case 55: case 56: case 57: case 58: case 59: _wcsIndex = (int)g - 54; break;
                case 90: gc.Absolute = true; break;
                case 91: gc.Absolute = false; break;
                case 90.1: gc.ArcAbsolute = true; break;
                case 91.1: gc.ArcAbsolute = false; break;
                case 92: g92 = true; break;
                case 92.1: g921 = true; break;
                default: return 20;
            }
        }
        foreach (int m in b.M)
        {
            switch (m)
            {
                case 0: case 1: case 6: break;
                case 2: case 30:
                    _spindleOn = false; _flood = _mist = false; gc.Absolute = true; gc.Motion = 1; _wcsIndex = 0;
                    break;
                case 3: _spindleOn = true; _spindleCcw = false; break;
                case 4: _spindleOn = true; _spindleCcw = true; break;
                case 5: _spindleOn = false; break;
                case 7: _mist = true; break;
                case 8: _flood = true; break;
                case 9: _flood = _mist = false; break;
                default: return 20;
            }
        }
        double unit = gc.Inches ? 25.4 : 1;
        if (b.TryGet('F', out double f))
        {
            if (f <= 0)
                return 19;
            gc.Feed = f * unit;
        }
        if (b.TryGet('S', out double s))
        {
            gc.Spindle = s;
            _spindleRpm = Math.Min(s, Setting(30));
        }
        if (b.TryGet('T', out double t))
            gc.Tool = (int)t;

        var wco = Wco();
        if (dwell)
        {
            _dwellUntil = Now + b.Get('P');
            return 0;
        }
        if (g10)
        {
            int l = (int)b.Get('L');
            int p = (int)b.Get('P');
            int idx = p == 0 ? _wcsIndex : p - 1;
            if (idx is < 0 or > 5 || l is not (2 or 20))
                return 20;
            var w = _wcs[idx];
            if (l == 2)
            {
                if (b.TryGet('X', out double x)) w.X = (float)(x * unit);
                if (b.TryGet('Y', out double y)) w.Y = (float)(y * unit);
                if (b.TryGet('Z', out double z)) w.Z = (float)(z * unit);
            }
            else
            {
                // L20: the current position becomes the given value.
                if (b.TryGet('X', out double x)) w.X = (float)(_mpos.X - _g92.X - x * unit);
                if (b.TryGet('Y', out double y)) w.Y = (float)(_mpos.Y - _g92.Y - y * unit);
                if (b.TryGet('Z', out double z)) w.Z = (float)(_mpos.Z - _g92.Z - z * unit);
            }
            _wcs[idx] = w;
            return 0;
        }
        if (g92)
        {
            var o = _g92;
            var w = _wcs[_wcsIndex];
            if (b.TryGet('X', out double x)) o.X = (float)(_mpos.X - w.X - x * unit);
            if (b.TryGet('Y', out double y)) o.Y = (float)(_mpos.Y - w.Y - y * unit);
            if (b.TryGet('Z', out double z)) o.Z = (float)(_mpos.Z - w.Z - z * unit);
            _g92 = o;
            return 0;
        }
        if (g921)
        {
            _g92 = Vector3.Zero;
            return 0;
        }

        if (motion >= 0)
            gc.Motion = motion;
        if (!b.HasAxisWords)
            return probe > 0 || motion is 2 or 3 ? 26 : 0;

        var start = _planner.Count > 0 ? _planner.Last().End : _current?.End ?? _mpos;
        var target = start;
        bool abs = gc.Absolute || g53;
        var off = g53 ? Vector3.Zero : wco;
        if (b.TryGet('X', out double tx)) target.X = (float)(abs ? tx * unit + off.X : start.X + tx * unit);
        if (b.TryGet('Y', out double ty)) target.Y = (float)(abs ? ty * unit + off.Y : start.Y + ty * unit);
        if (b.TryGet('Z', out double tz)) target.Z = (float)(abs ? tz * unit + off.Z : start.Z + tz * unit);

        if (g28)
        {
            Queue(new Block { Start = start, End = target, Rapid = true });
            Queue(new Block { Start = target, End = Vector3.Zero, Rapid = true });
            return 0;
        }

        if (Setting(20) != 0 && _homed && !InsideTravel(target))
        {
            Alarm(2, output);
            return -1;
        }

        if (probe > 0)
        {
            if (gc.Feed <= 0)
                return 22;
            bool away = probe >= 38.4;
            bool touching = start.Z <= ProbePlateZ;
            if (touching ^ away)
            {
                Alarm(4, output);
                return -1;
            }
            Queue(new Block
            {
                Start = start, End = target, Feed = gc.Feed, Probe = true, ProbeAway = away,
                ProbeNoAlarm = probe is 38.3 or 38.5,
            });
            return 0;
        }

        switch (gc.Motion)
        {
            case 0:
                Queue(new Block { Start = start, End = target, Rapid = true });
                return 0;
            case 1:
                if (gc.Feed <= 0)
                    return 22;
                Queue(new Block { Start = start, End = target, Feed = gc.Feed });
                return 0;
            case 2:
            case 3:
            {
                if (gc.Feed <= 0)
                    return 22;
                if (gc.Plane != 17)
                {
                    // Other planes: straight line in the simulation.
                    Queue(new Block { Start = start, End = target, Feed = gc.Feed });
                    return 0;
                }
                bool cw = gc.Motion == 2;
                Vector3 c;
                if (b.TryGet('R', out double r))
                {
                    r *= unit;
                    double dx = target.X - start.X, dy = target.Y - start.Y;
                    double d = Math.Sqrt(dx * dx + dy * dy);
                    if (d < 1e-9 || d > 2 * Math.Abs(r) + 1e-4)
                        return 33;
                    double h = Math.Sqrt(Math.Max(0, r * r - d * d / 4));
                    double sign = cw ? -1 : 1;
                    if (r < 0)
                        sign = -sign;
                    c = new Vector3((float)(start.X + dx / 2 - sign * h * dy / d), (float)(start.Y + dy / 2 + sign * h * dx / d), 0);
                }
                else
                {
                    if (!b.Has('I') && !b.Has('J'))
                        return 35;
                    c = gc.ArcAbsolute
                        ? new Vector3((float)(b.Get('I') * unit + wco.X), (float)(b.Get('J') * unit + wco.Y), 0)
                        : new Vector3((float)(start.X + b.Get('I') * unit), (float)(start.Y + b.Get('J') * unit), 0);
                }
                double radius = Math.Sqrt((start.X - c.X) * (start.X - c.X) + (start.Y - c.Y) * (start.Y - c.Y));
                double a0 = Math.Atan2(start.Y - c.Y, start.X - c.X);
                double a1 = Math.Atan2(target.Y - c.Y, target.X - c.X);
                double sweep = a1 - a0;
                if (cw && sweep >= -1e-9) sweep -= 2 * Math.PI;
                if (!cw && sweep <= 1e-9) sweep += 2 * Math.PI;
                double endRadius = Math.Sqrt((target.X - c.X) * (target.X - c.X) + (target.Y - c.Y) * (target.Y - c.Y));
                if (Math.Abs(endRadius - radius) > 0.005 && Math.Abs(endRadius - radius) > 0.001 * radius)
                    return 33;
                Queue(new Block { Start = start, End = target, Center = c, Arc = cw ? 1 : -1, Sweep = sweep, Radius = radius, Feed = gc.Feed });
                return 0;
            }
        }
        return 0;
    }
}
