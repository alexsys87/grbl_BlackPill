using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace GrblHost.Core.Machine;

public enum ConnectionState
{
    Disconnected,
    /// <summary>The port is open, waiting for the first answer of the controller.</summary>
    Connecting,
    Online,
    /// <summary>The link broke; the port or socket is opened again until it works.</summary>
    Reconnecting,
}

/// <summary>Messages of the connection itself, the UI turns them into text in its language.</summary>
public enum ConnectionMessage
{
    /// <summary>The controller answered; detail: firmware welcome or version.</summary>
    Online,
    /// <summary>The port failed (cable pulled); detail: the error.</summary>
    LinkLost,
    /// <summary>No status report for a long time, the link is treated as broken; detail: seconds.</summary>
    LinkTimeout,
    /// <summary>The port or socket is open again.</summary>
    Reconnected,
    /// <summary>The controller restarted (welcome message) without being asked to.</summary>
    ControllerReset,
    /// <summary>The job is lost: the controller restarted while it ran.</summary>
    JobLostOnReset,
    /// <summary>The job is lost: the link broke while it ran; detail: last confirmed line.</summary>
    JobLostOnLinkLoss,
    /// <summary>The job was stopped by an error; detail: "line: error text".</summary>
    JobStoppedByError,
    /// <summary>The job was stopped by an alarm; detail: alarm code.</summary>
    JobStoppedByAlarm,
}

/// <summary>Where a sent line comes from, for the console and the bookkeeping.</summary>
public enum SendKind
{
    /// <summary>Typed in the console, a button or a macro.</summary>
    Manual,
    /// <summary>A line of the running job.</summary>
    Job,
    /// <summary>Queries the program sends by itself ($I, $$, $G, $#).</summary>
    Query,
    /// <summary>Jog command ($J=...).</summary>
    Jog,
    /// <summary>Empty line that clears the error state of grblHAL.</summary>
    Sync,
}

/// <summary>A line to send with the zero-based line of the source document it came from.</summary>
public readonly record struct JobLine(string Command, int SourceLine);

/// <summary>How a job ended.</summary>
public enum JobResult
{
    Done,
    Cancelled,
    Error,
    Alarm,
    LinkLost,
    ControllerReset,
}

/// <summary>Real time command bytes of grbl 1.1 / grblHAL.</summary>
public static class RealtimeCommand
{
    public const byte StatusReport = (byte)'?';
    public const byte CycleStart = (byte)'~';
    public const byte FeedHold = (byte)'!';
    public const byte SoftReset = 0x18;
    public const byte SafetyDoor = 0x84;
    public const byte JogCancel = 0x85;
    public const byte FeedOvReset = 0x90;
    public const byte FeedOvCoarsePlus = 0x91;
    public const byte FeedOvCoarseMinus = 0x92;
    public const byte FeedOvFinePlus = 0x93;
    public const byte FeedOvFineMinus = 0x94;
    public const byte RapidOvReset = 0x95;
    public const byte RapidOvMedium = 0x96;
    public const byte RapidOvLow = 0x97;
    public const byte SpindleOvReset = 0x99;
    public const byte SpindleOvCoarsePlus = 0x9A;
    public const byte SpindleOvCoarseMinus = 0x9B;
    public const byte SpindleOvFinePlus = 0x9C;
    public const byte SpindleOvFineMinus = 0x9D;
    public const byte SpindleStop = 0x9E;
    public const byte FloodToggle = 0xA0;
    public const byte MistToggle = 0xA1;
}

/// <summary>
/// The host side of the grbl protocol, as used by grblHAL:
/// <list type="bullet">
/// <item>lines are streamed with character counting: as many lines are on
/// their way as fit into the controller's input buffer (size from [OPT:]),
/// every "ok" or "error:n" frees the oldest one;</item>
/// <item>status reports are polled with the real time command '?';</item>
/// <item>real time commands (feed hold, cycle start, reset, jog cancel,
/// overrides) go out at once, past the queue;</item>
/// <item>after an error grblHAL ignores all G-code until an empty line: a
/// job stops (feed hold, then reset when the machine stands), a manual
/// command is followed by an empty line;</item>
/// <item>a broken link is opened again by itself (cable pulled, USB
/// re-enumerated, network down). A running job can't survive that.</item>
/// </list>
/// Events are raised on background threads.
/// </summary>
public sealed class GrblConnection : IDisposable
{
    /// <summary>Longest line grblHAL accepts (LINE_BUFFER_SIZE - 1).</summary>
    public const int MaxLineLength = 256;

    private readonly object _sync = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Timer _timer;

    private IGrblTransport? _transport;
    private Func<IGrblTransport>? _factory;
    private ConnectionState _state = ConnectionState.Disconnected;
    private string? _portName;

    // Character counting.
    private readonly Queue<Entry> _inflight = new();
    private readonly Queue<Entry> _pending = new();
    private int _inflightChars;
    private int _rxBufferSize = 128;          // Classic grbl until [OPT:] says more.
    private int _plannerBlocks;               // From [OPT:], 0 while unknown.

    // Jog lines. The controller throws away what it has not read yet when it gets a jog cancel
    // and answers none of it, so a jog line is never sent together with other lines (the lines
    // that are not answered must be known), and the lines of a cancelled jog are forgotten.
    private double _lastJogWrite = -10;       // When the last jog line went out.
    private double _guardUntil;               // No sending until then (the answer of a dropped line may still come).

    // Job.
    private IReadOnlyList<JobLine>? _job;
    private int _jobNext;                     // Next job line to send.
    private int _jobAcked = -1;               // Last job line answered.
    private bool _jobStreaming;               // False once stopped, the rest isn't sent.
    private bool _jobFinishing;               // Everything answered, waiting for Idle.
    private JobResult? _jobEndPending;

    // Status.
    private readonly MachineSnapshot _snapshot = new();
    private double _lastReceived;
    private double _lastStatus;
    private double _lastStatusRequest = -10;
    private double _lastReconnectTry;
    private double _resetWhenHeldSince = -1;  // Stop requested: reset once the hold is complete.
    private double _expectWelcomeUntil = -1;  // Our own reset: the welcome is expected.

    private sealed class Entry
    {
        public Entry(string text, SendKind kind, int jobIndex)
        {
            Text = text;
            Kind = kind;
            JobIndex = jobIndex;
        }

        public string Text { get; }
        public SendKind Kind { get; }
        public int JobIndex { get; }
        public int Length => Text.Length + 1;
        public double SentAt { get; set; }
    }

    public GrblConnection()
    {
        _timer = new Timer(_ => Tick(), null, 50, 50);
    }

    // ---------------------------------------------------------------- events

    public event Action<ConnectionState>? StateChanged;

    /// <summary>Every line from the controller except status reports.</summary>
    public event Action<string>? LineReceived;

    public event Action<string, SendKind>? LineSent;

    public event Action<ConnectionMessage, string>? Info;

    /// <summary>A status report was merged into <see cref="Snapshot"/> (a copy is passed).</summary>
    public event Action<MachineSnapshot>? StatusReceived;

    /// <summary>A command was answered: the command, its kind, 0 for "ok" or the error code.</summary>
    public event Action<string, SendKind, int>? CommandCompleted;

    public event Action<int>? AlarmRaised;

    /// <summary>Text of a [MSG:...] message.</summary>
    public event Action<string>? MessageReceived;

    /// <summary>"$n=value" of a settings dump.</summary>
    public event Action<int, string>? SettingReceived;

    /// <summary>Modal state of the G-code parser from "[GC:...]", the text inside.</summary>
    public event Action<string>? ParserStateReceived;

    /// <summary>"[PRB:x,y,z:1]": machine position of the probe and whether it touched.</summary>
    public event Action<Axes, bool>? ProbeResult;

    /// <summary>Last job line (index into the job) the controller confirmed.</summary>
    public event Action<int>? JobProgress;

    public event Action<JobResult>? JobCompleted;

    // ---------------------------------------------------------------- state

    public ConnectionState State
    {
        get { lock (_sync) return _state; }
    }

    public string? PortName
    {
        get { lock (_sync) return _portName; }
    }

    /// <summary>Copy of the merged machine state.</summary>
    public MachineSnapshot Snapshot
    {
        get { lock (_sync) return _snapshot.Clone(); }
    }

    /// <summary>Input buffer size of the controller, from "[OPT:...,blocks,rx]".</summary>
    public int RxBufferSize
    {
        get { lock (_sync) return _rxBufferSize; }
    }

    /// <summary>Planner blocks of the controller, from "[OPT:...,blocks,rx]"; 0 while unknown.</summary>
    public int PlannerBlocks
    {
        get { lock (_sync) return _plannerBlocks; }
    }

    /// <summary>"1.1f.20261004:CNC3018 BlackPill" from [VER:].</summary>
    public string Version { get; private set; } = "";

    /// <summary>Board name from [BOARD:] (grblHAL).</summary>
    public string Board { get; private set; } = "";

    public bool IsJobRunning
    {
        get { lock (_sync) return _job != null; }
    }

    public int JobLength
    {
        get { lock (_sync) return _job?.Count ?? 0; }
    }

    /// <summary>Open the link again by itself after it broke.</summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>Status report interval, ms.</summary>
    public int StatusIntervalMs { get; set; } = 200;

    /// <summary>No status report for this long: the link is broken, ms.</summary>
    public int LinkTimeoutMs { get; set; } = 5000;

    private double Now => _clock.Elapsed.TotalSeconds;

    // ---------------------------------------------------------------- connect

    /// <summary>Connect through a factory: every reconnect makes a new transport.</summary>
    public void Connect(Func<IGrblTransport> factory)
    {
        var t = factory();
        Connect(t, factory);
    }

    /// <summary>Connect to a transport that can't be reopened (the virtual controller).</summary>
    public void Connect(IGrblTransport transport) => Connect(transport, null);

    private void Connect(IGrblTransport transport, Func<IGrblTransport>? factory)
    {
        Disconnect();
        transport.LineReceived += OnLine;
        transport.Faulted += OnFaulted;
        try
        {
            transport.Open();
        }
        catch
        {
            transport.LineReceived -= OnLine;
            transport.Faulted -= OnFaulted;
            transport.Dispose();
            throw;
        }
        lock (_sync)
        {
            _transport = transport;
            _factory = factory;
            _portName = transport.Name;
            ResetProtocol();
            _lastReceived = Now;
            _lastStatusRequest = -10;
        }
        SetState(ConnectionState.Connecting);
        // grbl on an Arduino resets when the port opens, grblHAL doesn't:
        // the status query works for both, the queries follow when online.
        WriteRaw(RealtimeCommand.StatusReport);
    }

    public void Disconnect()
    {
        IGrblTransport? t;
        bool hadJob;
        lock (_sync)
        {
            t = _transport;
            _transport = null;
            _factory = null;
            hadJob = _job != null;
            _job = null;
            ResetProtocol();
        }
        if (t != null)
        {
            t.LineReceived -= OnLine;
            t.Faulted -= OnFaulted;
            try
            {
                t.Dispose();
            }
            catch
            {
                // Port already gone.
            }
        }
        if (hadJob)
            JobCompleted?.Invoke(JobResult.Cancelled);
        SetState(ConnectionState.Disconnected);
    }

    private void ResetProtocol()
    {
        _inflight.Clear();
        _pending.Clear();
        _inflightChars = 0;
        _guardUntil = 0;
        _lastJogWrite = -10;
        _resetWhenHeldSince = -1;
        _expectWelcomeUntil = -1;
    }

    private void SetState(ConnectionState s)
    {
        bool changed;
        lock (_sync)
        {
            changed = _state != s;
            _state = s;
        }
        if (changed)
            StateChanged?.Invoke(s);
    }

    private void OnFaulted(Exception ex)
    {
        LinkLost(ConnectionMessage.LinkLost, ex.Message);
    }

    private void LinkLost(ConnectionMessage reason, string detail)
    {
        IGrblTransport? t;
        bool reconnect;
        int jobLine = -1;
        lock (_sync)
        {
            if (_transport == null)
                return;
            t = _transport;
            _transport = null;
            reconnect = AutoReconnect && _factory != null;
            if (_job != null)
                jobLine = _jobAcked >= 0 ? _job[_jobAcked].SourceLine : -1;
            ResetProtocol();
            _lastReconnectTry = Now;
        }
        t.LineReceived -= OnLine;
        t.Faulted -= OnFaulted;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                t.Dispose();
            }
            catch
            {
                // Gone.
            }
        });
        Info?.Invoke(reason, detail);
        if (IsJobRunning)
        {
            Info?.Invoke(ConnectionMessage.JobLostOnLinkLoss, (jobLine + 1).ToString(CultureInfo.InvariantCulture));
            EndJob(JobResult.LinkLost);
        }
        if (reconnect)
        {
            SetState(ConnectionState.Reconnecting);
        }
        else
        {
            lock (_sync)
                _factory = null;
            SetState(ConnectionState.Disconnected);
        }
    }

    private void TryReconnect()
    {
        Func<IGrblTransport>? factory;
        lock (_sync)
        {
            factory = _factory;
            if (factory == null || _transport != null)
                return;
            _lastReconnectTry = Now;
        }
        IGrblTransport t;
        try
        {
            t = factory();
            t.LineReceived += OnLine;
            t.Faulted += OnFaulted;
            t.Open();
        }
        catch
        {
            return;                         // Next try in a second.
        }
        lock (_sync)
        {
            if (_factory == null)           // Disconnect() meanwhile.
            {
                t.Dispose();
                return;
            }
            _transport = t;
            _portName = t.Name;
            ResetProtocol();
            _lastReceived = Now;
            _lastStatusRequest = -10;
        }
        Info?.Invoke(ConnectionMessage.Reconnected, t.Name);
        SetState(ConnectionState.Connecting);
        WriteRaw(RealtimeCommand.StatusReport);
    }

    // ---------------------------------------------------------------- sending

    /// <summary>Queue a line. Comments are removed, nothing is sent if nothing is left.</summary>
    public void Send(string line, SendKind kind = SendKind.Manual)
    {
        string text = Clean(line);
        if (text.Length == 0 && kind != SendKind.Sync)
            return;
        lock (_sync)
        {
            if (_transport == null)
                return;
            _pending.Enqueue(new Entry(text, kind, -1));
        }
        Pump();
    }

    /// <summary>Several lines (a macro), "\n" separated.</summary>
    public void SendScript(string script, SendKind kind = SendKind.Manual)
    {
        foreach (var l in script.Split('\n'))
            Send(l, kind);
    }

    /// <summary>Send a real time command byte at once.</summary>
    public void SendRealtime(byte value) => WriteRaw(value);

    /// <summary>Jog: "$J=" + G-code words, e.g. "G91 G21 X10 F1000".</summary>
    public void Jog(string words) => Send("$J=" + words, SendKind.Jog);

    /// <summary>A jog line is allowed this long to show up as the Jog state in a status report.</summary>
    private const double JogStatusLagSeconds = 1.5;

    /// <summary>After a jog cancel nothing is sent for this long, so the answer to a dropped line can't be mistaken for another one.</summary>
    private const double JogGuardSeconds = 0.06;

    /// <summary>
    /// Stop the jog at once (0x85). The controller also throws away the input it has not read, and
    /// that must not happen outside a jog: during a job it would take lines out of the program. So
    /// the byte is only sent while a jog may be running (state Jog, a jog line on its way, or one
    /// sent a moment ago) and never during a job. The jog lines the controller drops are forgotten,
    /// they get no answer.
    /// </summary>
    public void JogCancel()
    {
        bool send;
        lock (_sync)
        {
            send = _job == null && _transport != null &&
                   (_snapshot.State == MachineState.Jog || HasJogLine() || Now - _lastJogWrite < JogStatusLagSeconds);
            if (send)
            {
                DropJogLines();
                _guardUntil = Now + JogGuardSeconds;
            }
        }
        if (send)
            WriteRaw(RealtimeCommand.JogCancel);
    }

    private bool HasJogLine() => _pending.Any(e => e.Kind == SendKind.Jog) || _inflight.Any(e => e.Kind == SendKind.Jog);

    /// <summary>Forget the jog lines that wait to be sent or to be answered.</summary>
    private void DropJogLines()
    {
        if (_pending.Any(e => e.Kind == SendKind.Jog))
        {
            var keep = _pending.Where(e => e.Kind != SendKind.Jog).ToList();
            _pending.Clear();
            foreach (var e in keep)
                _pending.Enqueue(e);
        }
        if (_inflight.Any(e => e.Kind == SendKind.Jog))
        {
            var keep = _inflight.Where(e => e.Kind != SendKind.Jog).ToList();
            _inflight.Clear();
            _inflightChars = 0;
            foreach (var e in keep)
            {
                _inflight.Enqueue(e);
                _inflightChars += e.Length;
            }
        }
    }

    public void FeedHold() => WriteRaw(RealtimeCommand.FeedHold);

    public void CycleStart() => WriteRaw(RealtimeCommand.CycleStart);

    /// <summary>Soft reset (Ctrl-X): stops everything at once, the buffers are flushed.</summary>
    public void SoftReset()
    {
        bool hadJob;
        lock (_sync)
        {
            hadJob = _job != null;
            _jobStreaming = false;
            DropQueues();
            _expectWelcomeUntil = Now + 3;
            _resetWhenHeldSince = -1;
        }
        WriteRaw(RealtimeCommand.SoftReset);
        if (hadJob)
            EndJob(JobResult.Cancelled);
    }

    private void DropQueues()
    {
        _inflight.Clear();
        _pending.Clear();
        _inflightChars = 0;
    }

    /// <summary>Remove comments ("; ..." and "( ... )") and spaces at the ends; non-ASCII becomes '?'.</summary>
    public static string Clean(string line)
    {
        var sb = new StringBuilder(line.Length);
        int depth = 0;
        foreach (char c in line)
        {
            if (depth == 0 && c == ';')
                break;
            if (c == '(')
            {
                depth++;
                continue;
            }
            if (c == ')' && depth > 0)
            {
                depth--;
                continue;
            }
            if (depth > 0 || c == '\r' || c == '\n')
                continue;
            sb.Append(c is >= ' ' and <= '~' ? c : c == '\t' ? ' ' : '?');
        }
        return sb.ToString().Trim();
    }

    private void Pump()
    {
        var sent = new List<Entry>();
        IGrblTransport? t;
        lock (_sync)
        {
            t = _transport;
            if (t == null || _state is ConnectionState.Disconnected or ConnectionState.Reconnecting)
                return;
            while (true)
            {
                Entry? e = null;
                if (_pending.Count > 0)
                {
                    var p = _pending.Peek();
                    if (CanSend(p))
                        e = _pending.Dequeue();
                    else
                        break;
                }
                else if (_job != null && _jobStreaming && _jobNext < _job.Count && _state == ConnectionState.Online)
                {
                    var j = new Entry(_job[_jobNext].Command, SendKind.Job, _jobNext);
                    if (!CanSend(j))
                        break;
                    _jobNext++;
                    e = j;
                }
                if (e == null)
                    break;
                e.SentAt = Now;
                if (e.Kind == SendKind.Jog)
                    _lastJogWrite = e.SentAt;
                _inflight.Enqueue(e);
                _inflightChars += e.Length;
                sent.Add(e);
            }
        }
        foreach (var e in sent)
        {
            t.WriteLine(e.Text);
            LineSent?.Invoke(e.Text, e.Kind);
        }
    }

    // A line longer than the whole buffer still goes out alone.
    private bool Fits(Entry e) => _inflightChars + e.Length <= _rxBufferSize - 1 || _inflight.Count == 0;

    /// <summary>Called with the lock held.</summary>
    private bool CanSend(Entry e)
    {
        if (Now < _guardUntil)
            return false;
        // A jog line goes out alone, and nothing follows it until the controller has answered it.
        bool jogInFlight = _inflight.Count > 0 && _inflight.Peek().Kind == SendKind.Jog;
        if (e.Kind == SendKind.Jog)
            return _inflight.Count == 0;
        return !jogInFlight && Fits(e);
    }

    private void WriteRaw(byte value)
    {
        IGrblTransport? t;
        lock (_sync)
        {
            t = _transport;
            if (value == RealtimeCommand.StatusReport)
                _lastStatusRequest = Now;
        }
        t?.WriteRaw(value);
    }

    // ---------------------------------------------------------------- job

    /// <summary>Start streaming a job. The machine should be Idle.</summary>
    public void StartJob(IReadOnlyList<JobLine> lines)
    {
        lock (_sync)
        {
            if (_transport == null || _state != ConnectionState.Online)
                throw new InvalidOperationException("Not connected");
            if (_job != null)
                throw new InvalidOperationException("A job is running");
            if (lines.Count == 0)
                return;
            _job = lines;
            _jobNext = 0;
            _jobAcked = -1;
            _jobStreaming = true;
            _jobFinishing = false;
            _jobEndPending = null;
        }
        Pump();
    }

    /// <summary>Pause a job: feed hold, the moves decelerate and stop.</summary>
    public void PauseJob() => FeedHold();

    public void ResumeJob() => CycleStart();

    /// <summary>
    /// Stop the job: nothing more is sent, the machine stops with a feed hold
    /// and is reset once it stands (a reset in motion would lose the position).
    /// </summary>
    public void StopJob() => StopJobCore(JobResult.Cancelled);

    private void StopJobCore(JobResult result)
    {
        bool idle;
        lock (_sync)
        {
            if (_job == null)
                return;
            _jobStreaming = false;
            _jobEndPending = result;
            idle = _snapshot.State is MachineState.Idle or MachineState.Check or MachineState.Alarm;
            if (!idle)
                _resetWhenHeldSince = Now;
        }
        if (idle)
            ResetAfterStop();
        else
            WriteRaw(RealtimeCommand.FeedHold);
    }

    /// <summary>The machine stands after a stop: flush the controller and end the job.</summary>
    private void ResetAfterStop()
    {
        JobResult result;
        lock (_sync)
        {
            _resetWhenHeldSince = -1;
            DropQueues();
            _expectWelcomeUntil = Now + 3;
            result = _jobEndPending ?? JobResult.Cancelled;
        }
        WriteRaw(RealtimeCommand.SoftReset);
        EndJob(result);
    }

    private void EndJob(JobResult result)
    {
        lock (_sync)
        {
            if (_job == null)
                return;
            _job = null;
            _jobStreaming = false;
            _jobFinishing = false;
            _jobEndPending = null;
        }
        JobCompleted?.Invoke(result);
    }

    // ---------------------------------------------------------------- receiving

    private void OnLine(string line)
    {
        lock (_sync)
            _lastReceived = Now;

        if (GrblStatus.IsStatusReport(line))
        {
            OnStatus(line);
            return;
        }

        LineReceived?.Invoke(line);

        if (line == "ok")
        {
            Complete(0);
            BecomeOnline("");
        }
        else if (line.StartsWith("error:", StringComparison.Ordinal))
        {
            int.TryParse(line.AsSpan(6), NumberStyles.Integer, CultureInfo.InvariantCulture, out int code);
            Complete(code == 0 ? -1 : code);
        }
        else if (line.StartsWith("ALARM:", StringComparison.Ordinal))
        {
            int.TryParse(line.AsSpan(6), NumberStyles.Integer, CultureInfo.InvariantCulture, out int code);
            OnAlarm(code);
        }
        else if (line.StartsWith("Grbl", StringComparison.Ordinal) && line.Contains('['))
        {
            OnWelcome(line);
        }
        else if (line.StartsWith("[MSG:", StringComparison.Ordinal))
        {
            MessageReceived?.Invoke(line[5..^1]);
        }
        else if (line.StartsWith("[GC:", StringComparison.Ordinal))
        {
            ParserStateReceived?.Invoke(line[4..^1]);
        }
        else if (line.StartsWith("[PRB:", StringComparison.Ordinal))
        {
            OnProbe(line);
        }
        else if (line.StartsWith("[VER:", StringComparison.Ordinal))
        {
            Version = line[5..^1];
        }
        else if (line.StartsWith("[BOARD:", StringComparison.Ordinal))
        {
            Board = line[7..^1];
        }
        else if (line.StartsWith("[OPT:", StringComparison.Ordinal))
        {
            // [OPT:VNMSL,100,1024,3,0]: options, planner blocks, rx buffer, ...
            var p = line[5..^1].Split(',');
            if (p.Length >= 3 && int.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int rx) && rx >= 64)
                lock (_sync)
                    _rxBufferSize = rx;
            if (p.Length >= 2 && int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int blocks) && blocks > 0)
                lock (_sync)
                    _plannerBlocks = blocks;
        }
        else if (line.Length > 2 && line[0] == '$' && char.IsDigit(line[1]))
        {
            int eq = line.IndexOf('=');
            if (eq > 1 && int.TryParse(line.AsSpan(1, eq - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
                SettingReceived?.Invoke(id, line[(eq + 1)..]);
        }
    }

    private void BecomeOnline(string detail)
    {
        bool first;
        lock (_sync)
        {
            first = _state == ConnectionState.Connecting && _transport != null;
        }
        if (!first)
            return;
        SetState(ConnectionState.Online);
        Info?.Invoke(ConnectionMessage.Online, detail);
        // Build info (rx buffer size), settings, parser state, offsets.
        Send("$I", SendKind.Query);
        Send("$$", SendKind.Query);
        Send("$G", SendKind.Query);
        Send("$#", SendKind.Query);
    }

    private void OnStatus(string line)
    {
        var s = GrblStatus.Parse(line);
        if (s == null)
            return;
        MachineSnapshot copy;
        bool resetNow = false, jobDone = false;
        lock (_sync)
        {
            _snapshot.Apply(s);
            _lastStatus = Now;
            copy = _snapshot.Clone();
            if (_resetWhenHeldSince >= 0 &&
                (s.State == MachineState.Hold && s.SubState == 0 || s.State is MachineState.Idle or MachineState.Alarm
                     or MachineState.Check || Now - _resetWhenHeldSince > 10))
                resetNow = true;
            if (_jobFinishing && s.State is MachineState.Idle or MachineState.Check)
                jobDone = true;
        }
        BecomeOnline(s.StateText);
        StatusReceived?.Invoke(copy);
        if (resetNow)
            ResetAfterStop();
        else if (jobDone)
            EndJob(JobResult.Done);
    }

    private void Complete(int code)
    {
        Entry? e;
        int ackIndex = -1;
        bool jobAllAcked = false, jobError = false, sync = false;
        lock (_sync)
        {
            if (_inflight.Count == 0)
                return;                     // Answer to a line sent before a reset.
            e = _inflight.Dequeue();
            _inflightChars -= e.Length;
            if (e.Kind == SendKind.Job && _job != null)
            {
                _jobAcked = e.JobIndex;
                ackIndex = e.JobIndex;
                if (code != 0 && _jobStreaming)
                    jobError = true;
                else if (_jobStreaming && _jobAcked == _job.Count - 1)
                {
                    jobAllAcked = true;
                    _jobFinishing = true;
                }
            }
            else if (code != 0 && e.Kind != SendKind.Sync)
            {
                // grblHAL keeps the error until an empty line.
                sync = true;
            }
        }
        CommandCompleted?.Invoke(e.Text, e.Kind, code);
        if (ackIndex >= 0)
            JobProgress?.Invoke(ackIndex);
        if (jobError)
        {
            int src;
            lock (_sync)
                src = _job != null ? _job[e.JobIndex].SourceLine : -1;
            Info?.Invoke(ConnectionMessage.JobStoppedByError,
                string.Format(CultureInfo.InvariantCulture, "{0}|{1}|{2}", src + 1, code, e.Text));
            StopJobCore(JobResult.Error);
        }
        else if (sync)
        {
            Send("", SendKind.Sync);
        }
        if (jobAllAcked)
        {
            // Done when the machine stands: the next status report decides.
            WriteRaw(RealtimeCommand.StatusReport);
        }
        Pump();
    }

    private void OnAlarm(int code)
    {
        AlarmRaised?.Invoke(code);
        bool job;
        lock (_sync)
        {
            job = _job != null;
            // The controller flushes its buffers on an alarm.
            DropQueues();
        }
        if (job)
        {
            Info?.Invoke(ConnectionMessage.JobStoppedByAlarm, code.ToString(CultureInfo.InvariantCulture));
            EndJob(JobResult.Alarm);
        }
    }

    private void OnWelcome(string line)
    {
        bool expected, job;
        lock (_sync)
        {
            // The banner of a controller that resets as the port opens.
            if (_transport == null)
                return;
            expected = Now < _expectWelcomeUntil;
            _expectWelcomeUntil = -1;
            job = _job != null;
            DropQueues();
        }
        if (!expected)
        {
            Info?.Invoke(ConnectionMessage.ControllerReset, line);
            if (job)
            {
                Info?.Invoke(ConnectionMessage.JobLostOnReset, line);
                EndJob(JobResult.ControllerReset);
            }
        }
        if (State == ConnectionState.Connecting)
            BecomeOnline(line);
        else
        {
            // After a reset: fresh build info and parser state.
            Send("$G", SendKind.Query);
            Send("$#", SendKind.Query);
        }
    }

    private void OnProbe(string line)
    {
        // [PRB:1.000,2.000,-3.000:1]
        string body = line[5..^1];
        int colon = body.LastIndexOf(':');
        if (colon < 0)
            return;
        var pos = Axes.Parse(body[..colon]);
        if (pos != null)
            ProbeResult?.Invoke(pos.Value, body[(colon + 1)..] == "1");
    }

    // ---------------------------------------------------------------- timer

    private int _ticking;

    private void Tick()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1)
            return;
        try
        {
            ConnectionState state;
            double now = Now, lastRx, lastReq, lastTry, resetSince;
            lock (_sync)
            {
                state = _state;
                lastRx = _lastReceived;
                lastReq = _lastStatusRequest;
                lastTry = _lastReconnectTry;
                resetSince = _resetWhenHeldSince;
            }
            switch (state)
            {
                case ConnectionState.Connecting:
                case ConnectionState.Online:
                    if (now - lastReq >= StatusIntervalMs / 1000.0)
                        WriteRaw(RealtimeCommand.StatusReport);
                    ServiceQueues(now);
                    if (now - lastRx > LinkTimeoutMs / 1000.0)
                        LinkLost(ConnectionMessage.LinkTimeout,
                            ((int)(now - lastRx)).ToString(CultureInfo.InvariantCulture));
                    else if (resetSince >= 0 && now - resetSince > 10)
                        ResetAfterStop();
                    break;
                case ConnectionState.Reconnecting:
                    if (now - lastTry >= 1.0)
                        TryReconnect();
                    break;
            }
        }
        catch
        {
            // The timer must keep running.
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }

    /// <summary>
    /// Lines held back by the guard after a jog cancel go out now. A jog line is answered within
    /// milliseconds; one that is not has been dropped by the controller (a cancel we did not send,
    /// a port change), and it must not block the lines behind it for ever.
    /// </summary>
    private void ServiceQueues(double now)
    {
        bool pump = false;
        lock (_sync)
        {
            if (_inflight.Count > 0 && _inflight.Peek().Kind == SendKind.Jog && now - _inflight.Peek().SentAt > 2.0)
            {
                var jog = _inflight.Dequeue();
                _inflightChars -= jog.Length;
                pump = true;
            }
            if (_pending.Count > 0 && now >= _guardUntil)
                pump = true;
        }
        if (pump)
            Pump();
    }

    public void Dispose()
    {
        _timer.Dispose();
        Disconnect();
    }
}
