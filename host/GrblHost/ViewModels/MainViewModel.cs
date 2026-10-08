using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using GrblHost.Core.Machine;
using GrblHost.Infrastructure;
using GrblHost.Services;

namespace GrblHost.ViewModels;

/// <summary>
/// The whole application state. Split into partial files: connection and
/// console here, machine control (DRO, jog, spindle, probe, settings), the
/// job and the viewer (simulation, G-code listing) in the others.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    /// <summary>Item of the port list that stands for the virtual controller (shown localized).</summary>
    public const string VirtualPortName = "\u0001virtual";

    /// <summary>Item of the port list for a Telnet / TCP serial bridge (shown localized).</summary>
    public const string NetworkPortName = "\u0001network";
    private const int MaxLogEntries = 3000;
    private const string AppName = "Grbl Host";

    private readonly AppSettings _settings;
    private readonly GrblConnection _conn = new();
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _pump;
    private readonly ConcurrentQueue<LogEntry> _pendingLog = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public MainViewModel(AppSettings settings)
    {
        _settings = settings;
        _dispatcher = Application.Current.Dispatcher;

        _isDarkTheme = ThemeService.IsDark;
        foreach (var m in settings.Macros ?? DefaultMacros())
            Macros.Add(new MacroItem(m.Name, m.Script));
        _selectedBaud = settings.BaudRate;
        _showRapids = settings.ShowRapids;
        _hideServiceLines = settings.HideStatusLines;
        _showJobLines = settings.ShowJobLines;
        _virtualTimeScale = settings.VirtualTimeScale;
        _spindleRpm = settings.SpindleRpm;

        HookConnection();
        CreateCommands();
        Loc.Changed += OnLanguageChanged;
        RefreshPorts();
        _selectedPort = settings.Port != null && Ports.Contains(settings.Port) ? settings.Port : Ports.FirstOrDefault();

        _pump = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(40),
        };
        _pump.Tick += (_, _) => OnPump();
        _pump.Start();
    }

    public AppSettings Settings => _settings;

    // ---------------------------------------------------------------- theme and language

    private bool _isDarkTheme;
    public bool IsDarkTheme
    {
        get => _isDarkTheme;
        set
        {
            if (!Set(ref _isDarkTheme, value))
                return;
            ThemeService.Apply(value);
            _settings.DarkTheme = value;
        }
    }

    /// <summary>Code of the current language for the toolbar button, "RU" or "EN".</summary>
    public string LanguageCode => Loc.Language.ToUpperInvariant();

    public RelayCommand ToggleThemeCommand { get; private set; } = null!;
    public RelayCommand ToggleLanguageCommand { get; private set; } = null!;

    private void ToggleLanguage()
    {
        Loc.Apply(Loc.Language == Loc.Russian ? Loc.English : Loc.Russian);
        _settings.Language = Loc.Language;
    }

    /// <summary>Texts built in code follow the new language.</summary>
    private void OnLanguageChanged()
    {
        RefreshPorts();
        RebuildFileInfo();
        foreach (var s in GrblSettings)
            s.Refresh();
        // Refresh every computed text (connection, state, legend, line numbers …).
        OnPropertyChanged(string.Empty);
    }

    private static IEnumerable<MacroSettings> DefaultMacros() => new MacroSettings[]
    {
        new() { Name = Loc.T("S.Macro.SafeZ"), Script = "G53 G0 Z-1" },
        new() { Name = Loc.T("S.Macro.GoZero"), Script = "G90 G0 Z5\nG0 X0 Y0" },
        new() { Name = Loc.T("S.Macro.Park"), Script = "G53 G0 Z-1\nG53 G0 X-5 Y-5" },
        new() { Name = Loc.T("S.Macro.Frame"), Script = "G90 G0 Z5\nG0 X0 Y0\nG0 X60\nG0 Y40\nG0 X0\nG0 Y0" },
        new() { Name = Loc.T("S.Macro.ToolChange"), Script = "M5\nG53 G0 Z-1\nG53 G0 X-150 Y-90" },
    };

    // ---------------------------------------------------------------- connection

    public ObservableCollection<string> Ports { get; } = new();
    public int[] BaudRates { get; } = { 115200, 230400, 250000, 500000, 921600, 57600, 38400, 19200, 9600 };

    private string? _selectedPort;
    public string? SelectedPort
    {
        get => _selectedPort;
        set
        {
            if (!Set(ref _selectedPort, value))
                return;
            OnPropertyChanged(nameof(IsVirtualSelected));
            OnPropertyChanged(nameof(IsNetworkSelected));
            OnPropertyChanged(nameof(IsSerialSelected));
        }
    }

    public bool IsVirtualSelected => SelectedPort == VirtualPortName;
    public bool IsNetworkSelected => SelectedPort == NetworkPortName;
    public bool IsSerialSelected => !IsVirtualSelected && !IsNetworkSelected;

    /// <summary>Telnet bridge: "host" or "host:port" (23 by default).</summary>
    public string NetworkAddress
    {
        get => _settings.NetworkAddress;
        set
        {
            _settings.NetworkAddress = value.Trim();
            OnPropertyChanged();
        }
    }

    /// <summary>Open the link again by itself after it breaks (COM port gone, network down).</summary>
    public bool AutoReconnect
    {
        get => _settings.AutoReconnect;
        set
        {
            _settings.AutoReconnect = value;
            _conn.AutoReconnect = value;
            OnPropertyChanged();
        }
    }

    private int _selectedBaud;
    public int SelectedBaud
    {
        get => _selectedBaud;
        set => Set(ref _selectedBaud, value);
    }

    private double _virtualTimeScale;
    /// <summary>Speed of time of the virtual controller.</summary>
    public double VirtualTimeScale
    {
        get => _virtualTimeScale;
        set => Set(ref _virtualTimeScale, Math.Clamp(value, 1, 1000));
    }

    private ConnectionState _connectionState = ConnectionState.Disconnected;
    public ConnectionState ConnectionState
    {
        get => _connectionState;
        private set
        {
            if (!Set(ref _connectionState, value))
                return;
            OnPropertyChanged(nameof(IsConnected));
            OnPropertyChanged(nameof(IsOnline));
            OnPropertyChanged(nameof(IsDisconnected));
            OnPropertyChanged(nameof(ConnectionText));
            OnPropertyChanged(nameof(CanControl));
            OnPropertyChanged(nameof(CanJog));
            RelayCommand.Refresh();
        }
    }

    public bool IsConnected => ConnectionState != ConnectionState.Disconnected;
    public bool IsDisconnected => ConnectionState == ConnectionState.Disconnected;
    public bool IsOnline => ConnectionState == ConnectionState.Online;

    public string ConnectionText => ConnectionState switch
    {
        ConnectionState.Disconnected => Loc.T("S.State.Disconnected"),
        ConnectionState.Connecting => Loc.T("S.State.Connecting"),
        ConnectionState.Online => Loc.F("S.State.Online",
            _conn.PortName == "Virtual grblHAL" ? Loc.T("S.VirtualMachine") : _conn.PortName ?? ""),
        ConnectionState.Reconnecting => Loc.T("S.State.Reconnecting"),
        _ => "",
    };

    private string _firmwareName = "";
    /// <summary>"grblHAL 1.1f.20261004 · CNC3018 BlackPill" from $I.</summary>
    public string FirmwareName
    {
        get => _firmwareName;
        private set => Set(ref _firmwareName, value);
    }

    public RelayCommand RefreshPortsCommand { get; private set; } = null!;
    public RelayCommand ConnectCommand { get; private set; } = null!;
    public RelayCommand DisconnectCommand { get; private set; } = null!;

    public void RefreshPorts()
    {
        var current = SelectedPort;
        Ports.Clear();
        foreach (var p in SerialPortTransport.GetPortNames())
            Ports.Add(p);
        Ports.Add(NetworkPortName);
        Ports.Add(VirtualPortName);
        SelectedPort = current != null && Ports.Contains(current) ? current : Ports.FirstOrDefault();
    }

    private void Connect()
    {
        if (SelectedPort == null)
            return;
        try
        {
            _conn.AutoReconnect = AutoReconnect;
            ClearSettings();
            if (IsVirtualSelected)
            {
                Log(LogKind.Info, Loc.F("S.Log.ConnectingVirtual", VirtualTimeScale));
                // The stock of the virtual machine is a little tilted and wavy (a few
                // tenths of a mm), so a height map shows something.
                _conn.Connect(new VirtualGrbl
                {
                    TimeScale = VirtualTimeScale,
                    ProbePlateZ = -25,
                    ProbeSurface = (x, y) => -25 + 0.002 * x - 0.0015 * y + 0.15 * Math.Sin(x / 25) * Math.Cos(y / 20),
                });
            }
            else if (IsNetworkSelected)
            {
                if (string.IsNullOrWhiteSpace(NetworkAddress))
                {
                    Notify(Loc.T("S.Notice.ConnectFailed"), Loc.T("S.Notice.AddressEmpty"), NotifySeverity.Warning);
                    return;
                }
                var (host, port) = TcpTransport.ParseAddress(NetworkAddress);
                Log(LogKind.Info, Loc.F("S.Log.ConnectingNetwork", host, port));
                // A factory: every reconnect opens a new socket.
                _conn.Connect(() => new TcpTransport(host, port));
            }
            else
            {
                string port = SelectedPort;
                int baud = SelectedBaud;
                Log(LogKind.Info, Loc.F("S.Log.Connecting", port, baud));
                _conn.Connect(() => new SerialPortTransport(port, baud));
            }
            _settings.Port = SelectedPort;
            _settings.BaudRate = SelectedBaud;
        }
        catch (Exception ex)
        {
            Log(LogKind.Error, Loc.F("S.Log.PortFailed", ex.Message));
            Notify(Loc.T("S.Notice.ConnectFailed"), ex.Message, NotifySeverity.Error);
        }
    }

    private void Disconnect()
    {
        if (IsJobRunning &&
            MessageBox.Show(Loc.T("S.Ask.DisconnectWhileRunning"), AppName,
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        _conn.Disconnect();
    }

    private void HookConnection()
    {
        _conn.StateChanged += s => Ui(() => OnStateChanged(s));
        _conn.LineReceived += OnLineReceived;
        _conn.LineSent += OnLineSent;
        _conn.Info += (msg, detail) => Ui(() => OnInfo(msg, detail));
        _conn.StatusReceived += s => Interlocked.Exchange(ref _pendingStatus, s);
        _conn.CommandCompleted += OnCommandCompleted;
        _conn.AlarmRaised += a => Ui(() => OnAlarm(a));
        _conn.MessageReceived += m => Ui(() => OnMessage(m));
        _conn.SettingReceived += (id, v) => Ui(() => OnSetting(id, v));
        _conn.ParserStateReceived += p => Ui(() => ParserState = p);
        _conn.ProbeResult += (p, ok) => Ui(() => OnProbe(p, ok));
        _conn.JobProgress += i => Interlocked.Exchange(ref _pendingJobAck, i);
        _conn.JobCompleted += r => Ui(() => OnJobCompleted(r));
        _conn.AutoReconnect = _settings.AutoReconnect;
    }

    private void Ui(Action a) => _dispatcher.BeginInvoke(a, DispatcherPriority.Normal);

    private void OnStateChanged(ConnectionState s)
    {
        var old = ConnectionState;
        ConnectionState = s;
        if (s == ConnectionState.Disconnected)
        {
            FirmwareName = "";
            MachineState = MachineState.Unknown;
            Log(LogKind.Info, Loc.T("S.Log.Disconnected"));
        }
        else if (s == ConnectionState.Online && old == ConnectionState.Reconnecting && !IsJobRunning)
        {
            Notify(Loc.T("S.Notice.LinkRestored"), Loc.T("S.Conn.Reconnected"), NotifySeverity.Success);
        }
        else if (s == ConnectionState.Reconnecting)
        {
            Notify(Loc.T("S.Notice.LinkLost"),
                Loc.T(IsJobRunning ? "S.Notice.LinkLostRunning" : "S.Notice.LinkLostText"),
                NotifySeverity.Warning);
        }
        OnPropertyChanged(nameof(CanControl));
        OnPropertyChanged(nameof(CanJog));
    }

    private void OnInfo(ConnectionMessage msg, string detail)
    {
        string text = msg switch
        {
            ConnectionMessage.JobStoppedByError => JobErrorText(detail),
            ConnectionMessage.JobStoppedByAlarm when int.TryParse(detail, out int a) =>
                Loc.F("S.Conn.JobStoppedByAlarm", a, GrblCodes.Alarm(a, Loc.Language == Loc.Russian)),
            _ => Loc.F("S.Conn." + msg, detail),
        };
        bool bad = msg is ConnectionMessage.LinkLost or ConnectionMessage.LinkTimeout or ConnectionMessage.ControllerReset
            or ConnectionMessage.JobLostOnReset or ConnectionMessage.JobLostOnLinkLoss
            or ConnectionMessage.JobStoppedByError or ConnectionMessage.JobStoppedByAlarm;
        Log(bad ? LogKind.Warning : LogKind.Info, text);
        if (msg is ConnectionMessage.JobLostOnReset or ConnectionMessage.JobLostOnLinkLoss
            or ConnectionMessage.JobStoppedByError or ConnectionMessage.JobStoppedByAlarm)
            Notify(Loc.T("S.Notice.JobAborted"), text, NotifySeverity.Error);
    }

    /// <summary>"line|code|command" → text with the error explained.</summary>
    private static string JobErrorText(string detail)
    {
        var p = detail.Split('|', 3);
        if (p.Length < 3 || !int.TryParse(p[1], out int code))
            return Loc.F("S.Conn.JobStoppedByError", detail, 0, "", "");
        return Loc.F("S.Conn.JobStoppedByError", p[0], code, GrblCodes.Error(code, Loc.Language == Loc.Russian), p[2]);
    }

    // ---------------------------------------------------------------- notifications

    public enum NotifySeverity
    {
        Info,
        Success,
        Warning,
        Error,
    }

    private string _noticeTitle = "";
    public string NoticeTitle
    {
        get => _noticeTitle;
        private set => Set(ref _noticeTitle, value);
    }

    private string _noticeMessage = "";
    public string NoticeMessage
    {
        get => _noticeMessage;
        private set => Set(ref _noticeMessage, value);
    }

    private NotifySeverity _noticeSeverity;
    public NotifySeverity NoticeSeverity
    {
        get => _noticeSeverity;
        private set => Set(ref _noticeSeverity, value);
    }

    private bool _noticeOpen;
    public bool NoticeOpen
    {
        get => _noticeOpen;
        set => Set(ref _noticeOpen, value);
    }

    private DispatcherTimer? _noticeTimer;

    public void Notify(string title, string message, NotifySeverity severity = NotifySeverity.Info)
    {
        NoticeTitle = title;
        NoticeMessage = message;
        NoticeSeverity = severity;
        NoticeOpen = true;
        // Errors and warnings stay until closed, the rest go away by themselves.
        _noticeTimer?.Stop();
        if (severity is NotifySeverity.Info or NotifySeverity.Success)
        {
            _noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            _noticeTimer.Tick += (_, _) =>
            {
                _noticeTimer?.Stop();
                NoticeOpen = false;
            };
            _noticeTimer.Start();
        }
    }

    // ---------------------------------------------------------------- console

    public ObservableCollection<LogEntry> LogEntries { get; } = new();

    private string _consoleInput = "";
    public string ConsoleInput
    {
        get => _consoleInput;
        set => Set(ref _consoleInput, value);
    }

    private bool _hideServiceLines;
    /// <summary>Hide "ok" and the answers to the queries the program sends by itself.</summary>
    public bool HideServiceLines
    {
        get => _hideServiceLines;
        set
        {
            if (Set(ref _hideServiceLines, value))
                _settings.HideStatusLines = value;
        }
    }

    private bool _showJobLines;
    /// <summary>Show the lines of a running job in the console.</summary>
    public bool ShowJobLines
    {
        get => _showJobLines;
        set
        {
            if (Set(ref _showJobLines, value))
                _settings.ShowJobLines = value;
        }
    }

    private bool _autoScrollConsole = true;
    public bool AutoScrollConsole
    {
        get => _autoScrollConsole;
        set => Set(ref _autoScrollConsole, value);
    }

    private readonly List<string> _history = new();
    private int _historyPos;

    public RelayCommand SendConsoleCommand { get; private set; } = null!;
    public RelayCommand ClearConsoleCommand { get; private set; } = null!;

    /// <summary>Raised after new console lines were added (the view scrolls down).</summary>
    public event Action? LogAppended;

    private void SendConsole()
    {
        string text = ConsoleInput.Trim();
        if (text.Length == 0)
            return;
        if (_history.Count == 0 || _history[^1] != text)
            _history.Add(text);
        _historyPos = _history.Count;
        ConsoleInput = "";
        // Real time commands typed as such: "?", "!", "~", Ctrl-X as "^X".
        switch (text)
        {
            case "?":
                _conn.SendRealtime(RealtimeCommand.StatusReport);
                return;
            case "!":
                _conn.FeedHold();
                return;
            case "~":
                _conn.CycleStart();
                return;
            case "^X":
            case "^x":
                _conn.SoftReset();
                return;
        }
        foreach (var part in text.Split(new[] { '\n', '|' }, StringSplitOptions.RemoveEmptyEntries))
            _conn.Send(part.Trim().ToUpperInvariant());
    }

    public void HistoryUp()
    {
        if (_history.Count == 0)
            return;
        _historyPos = Math.Max(0, _historyPos - 1);
        ConsoleInput = _history[_historyPos];
    }

    public void HistoryDown()
    {
        if (_history.Count == 0)
            return;
        _historyPos = Math.Min(_history.Count, _historyPos + 1);
        ConsoleInput = _historyPos < _history.Count ? _history[_historyPos] : "";
    }

    // Lines answering the queries sent on connect ($I, $$, $G, $#) are shown
    // only when the console doesn't hide service lines.
    private int _queryAnswers;

    private void OnLineReceived(string line)
    {
        if (line.StartsWith("[VER:", StringComparison.Ordinal))
        {
            string ver = line[5..^1];
            int colon = ver.IndexOf(':');
            string fw = colon >= 0 ? ver[..colon] : ver;
            string name = colon >= 0 ? ver[(colon + 1)..] : "";
            Ui(() => FirmwareName = "grblHAL " + fw + (name.Length > 0 ? " · " + name : ""));
        }
        if (_hideServiceLines && (line == "ok" || Volatile.Read(ref _queryAnswers) > 0 && line != "" &&
                                  !line.StartsWith("error", StringComparison.Ordinal) &&
                                  !line.StartsWith("ALARM", StringComparison.Ordinal)))
            return;
        LogKind kind = LogKind.Received;
        string text = line;
        if (line.StartsWith("error:", StringComparison.Ordinal) && int.TryParse(line.AsSpan(6), out int e))
        {
            kind = LogKind.Error;
            text = line + "  " + GrblCodes.Error(e, Loc.Language == Loc.Russian);
        }
        else if (line.StartsWith("ALARM:", StringComparison.Ordinal) && int.TryParse(line.AsSpan(6), out int a))
        {
            kind = LogKind.Error;
            text = line + "  " + GrblCodes.Alarm(a, Loc.Language == Loc.Russian);
        }
        else if (line.StartsWith("[MSG:", StringComparison.Ordinal))
        {
            kind = LogKind.Warning;
        }
        _pendingLog.Enqueue(new LogEntry(kind, text));
    }

    private void OnLineSent(string text, SendKind kind)
    {
        if (kind == SendKind.Query)
            Interlocked.Increment(ref _queryAnswers);
        if (kind == SendKind.Job && !_showJobLines)
            return;
        if (kind is SendKind.Query or SendKind.Sync && _hideServiceLines)
            return;
        _pendingLog.Enqueue(new LogEntry(LogKind.Sent, text));
    }

    private void OnCommandCompleted(string text, SendKind kind, int code)
    {
        if (kind == SendKind.Query)
            Interlocked.Decrement(ref _queryAnswers);
        if (kind is SendKind.Manual or SendKind.Jog && code != 0)
        {
            string msg = Loc.F("S.Log.CommandError", text, code, GrblCodes.Error(code, Loc.Language == Loc.Russian));
            Ui(() => Notify(Loc.T("S.Notice.CommandError"), msg, NotifySeverity.Warning));
        }
        if (kind == SendKind.Manual && code == 0 && IsSettingWrite(text))
            Ui(() => _conn.Send("$$", SendKind.Query));
    }

    private static bool IsSettingWrite(string text) =>
        text.Length > 2 && text[0] == '$' && char.IsDigit(text[1]) && text.Contains('=');

    public void Log(LogKind kind, string text) => _pendingLog.Enqueue(new LogEntry(kind, text));

    private void FlushLog()
    {
        if (_pendingLog.IsEmpty)
            return;
        int added = 0;
        while (added < 500 && _pendingLog.TryDequeue(out var e))
        {
            LogEntries.Add(e);
            added++;
        }
        if (LogEntries.Count > MaxLogEntries)
        {
            int remove = LogEntries.Count - MaxLogEntries + 300;
            for (int i = 0; i < remove; i++)
                LogEntries.RemoveAt(0);
        }
        LogAppended?.Invoke();
    }

    // ---------------------------------------------------------------- pump

    private int _pendingJobAck = -1;
    private MachineSnapshot? _pendingStatus;

    private void OnPump()
    {
        FlushLog();

        var status = Interlocked.Exchange(ref _pendingStatus, null);
        if (status != null)
            OnStatus(status);

        int ack = Interlocked.Exchange(ref _pendingJobAck, -1);
        if (ack >= 0)
            OnJobAck(ack);

        SimulationTick();
        UpdateElapsed();
    }

    // ---------------------------------------------------------------- commands

    private void CreateCommands()
    {
        RefreshPortsCommand = new RelayCommand(RefreshPorts, () => IsDisconnected);
        ConnectCommand = new RelayCommand(Connect, () => IsDisconnected && SelectedPort != null);
        DisconnectCommand = new RelayCommand(Disconnect, () => IsConnected);
        ToggleThemeCommand = new RelayCommand(() => IsDarkTheme = !IsDarkTheme);
        ToggleLanguageCommand = new RelayCommand(ToggleLanguage);
        SendConsoleCommand = new RelayCommand(SendConsole, () => IsConnected);
        ClearConsoleCommand = new RelayCommand(() => LogEntries.Clear());
        CreateMachineCommands();
        CreateJobCommands();
        CreateViewerCommands();
    }

    public void SaveSettings()
    {
        _settings.ShowRapids = ShowRapids;
        _settings.VirtualTimeScale = VirtualTimeScale;
        _settings.BaudRate = SelectedBaud;
        _settings.SpindleRpm = SpindleRpm;
        _settings.Macros = Macros.Select(m => new MacroSettings { Name = m.Name, Script = m.Script }).ToList();
        _settings.Save();
    }

    public void Dispose()
    {
        Loc.Changed -= OnLanguageChanged;
        _pump.Stop();
        _conn.Dispose();
    }
}
