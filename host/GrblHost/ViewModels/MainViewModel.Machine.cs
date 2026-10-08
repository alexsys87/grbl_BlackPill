using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using GrblHost.Core.Machine;
using GrblHost.Infrastructure;
using GrblHost.Services;

namespace GrblHost.ViewModels;

/// <summary>
/// Machine state and position, jogging, work zero, homing, unlock and reset,
/// spindle, coolant, overrides, the Z probe, the grbl settings and macros.
/// </summary>
public sealed partial class MainViewModel
{
    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    private static string N(double v) => v.ToString("0.###", Ci);

    // ---------------------------------------------------------------- state and position

    private MachineState _machineState = MachineState.Unknown;
    public MachineState MachineState
    {
        get => _machineState;
        private set
        {
            if (!Set(ref _machineState, value))
                return;
            OnPropertyChanged(nameof(MachineStateText));
            OnPropertyChanged(nameof(IsAlarm));
            OnPropertyChanged(nameof(CanControl));
            OnPropertyChanged(nameof(CanJog));
            RelayCommand.Refresh();
        }
    }

    private string _stateDetail = "";

    /// <summary>"Idle", "Hold (stopping)", "Alarm 1 — hard limit" … in the current language.</summary>
    public string MachineStateText
    {
        get
        {
            if (MachineState == MachineState.Unknown)
                return "—";
            string s = Loc.T("S.Machine." + MachineState);
            return _stateDetail.Length > 0 ? s + " · " + _stateDetail : s;
        }
    }

    public bool IsAlarm => MachineState == MachineState.Alarm;

    /// <summary>Commands may be sent by hand: online, no job, the machine idle (or in alarm / check mode).</summary>
    public bool CanControl => IsOnline && !IsJobRunning &&
                              MachineState is MachineState.Idle or MachineState.Alarm or MachineState.Check;

    /// <summary>Jogging is allowed when idle or while jogging (a new jog queues after the current one).</summary>
    public bool CanJog => IsOnline && !IsJobRunning && MachineState is MachineState.Idle or MachineState.Jog;

    private Axes _wpos, _mpos;
    public Axes WPos { get => _wpos; private set => Set(ref _wpos, value); }
    public Axes MPos { get => _mpos; private set => Set(ref _mpos, value); }

    private double _feed, _spindleActual;
    public double Feed { get => _feed; private set => Set(ref _feed, value); }
    public double SpindleActual { get => _spindleActual; private set => Set(ref _spindleActual, value); }

    private int _feedOverride = 100, _rapidOverride = 100, _spindleOverride = 100;
    public int FeedOverride { get => _feedOverride; private set => Set(ref _feedOverride, value); }
    public int RapidOverride { get => _rapidOverride; private set => Set(ref _rapidOverride, value); }
    public int SpindleOverride { get => _spindleOverride; private set => Set(ref _spindleOverride, value); }

    private bool _spindleOn, _floodOn, _mistOn;
    public bool SpindleOn { get => _spindleOn; private set => Set(ref _spindleOn, value); }
    public bool FloodOn { get => _floodOn; private set => Set(ref _floodOn, value); }
    public bool MistOn { get => _mistOn; private set => Set(ref _mistOn, value); }

    private string _pins = "";
    /// <summary>Active inputs from the status report (Pn:XYZPDHRS), "—" if none.</summary>
    public string Pins { get => _pins; private set => Set(ref _pins, value); }

    private int _plannerFree, _rxFree;
    public string BufferText => Loc.F("S.BufferText", _plannerFree, _rxFree);

    private string _parserState = "";
    /// <summary>Modal state from $G: "G0 G54 G17 G21 G90 G94 M5 M9 T0 F0 S0".</summary>
    public string ParserState { get => _parserState; private set => Set(ref _parserState, value); }

    private void OnStatus(MachineSnapshot s)
    {
        _stateDetail = s.State switch
        {
            MachineState.Hold => s.SubState == 0 ? Loc.T("S.Machine.HoldDone") : Loc.T("S.Machine.Holding"),
            MachineState.Door => Loc.T("S.Machine.DoorOpen"),
            MachineState.Alarm when _lastAlarm > 0 => _lastAlarm.ToString(Ci),
            _ => "",
        };
        MachineState = s.State;
        OnPropertyChanged(nameof(MachineStateText));
        WPos = s.WPos;
        MPos = s.MPos;
        Feed = s.Feed;
        SpindleActual = s.Spindle;
        FeedOverride = s.FeedOverride;
        RapidOverride = s.RapidOverride;
        SpindleOverride = s.SpindleOverride;
        SpindleOn = s.SpindleCw || s.SpindleCcw;
        FloodOn = s.Flood;
        MistOn = s.Mist;
        Pins = s.Pins.Length > 0 ? s.Pins : "—";
        _plannerFree = s.PlannerFree;
        _rxFree = s.RxFree;
        OnPropertyChanged(nameof(BufferText));
        if (s.State != MachineState.Alarm)
            _lastAlarm = 0;
        if (IsJobRunning && Toolpath != null)
            OnLivePosition(s.WPos);
        else if (!IsSimulation)
            ShowMachineTool(s.WPos);
    }

    private int _lastAlarm;

    private void OnAlarm(int code)
    {
        _lastAlarm = code;
        bool ru = Loc.Language == Loc.Russian;
        string text = Loc.F("S.Notice.AlarmText", code, GrblCodes.Alarm(code, ru));
        if (GrblCodes.AlarmLosesPosition(code))
            text += " " + Loc.T("S.Notice.AlarmRehome");
        Notify(Loc.T("S.Notice.Alarm"), text, NotifySeverity.Error);
    }

    private void OnMessage(string m)
    {
        Log(LogKind.Warning, "[MSG] " + m);
        if (m.Contains("Pgm End", StringComparison.OrdinalIgnoreCase))
            return;
        if (m.Contains("Reset to continue", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("unlock", StringComparison.OrdinalIgnoreCase))
            Notify(Loc.T("S.Notice.Controller"), m, NotifySeverity.Warning);
    }

    // ---------------------------------------------------------------- jog

    public double[] JogSteps { get; } = { 0.01, 0.1, 1, 10, 50 };

    private double _jogStep = 1;
    public double JogStep { get => _jogStep; set => Set(ref _jogStep, value); }

    public double JogFeedXY
    {
        get => _settings.JogFeedXY;
        set
        {
            _settings.JogFeedXY = Math.Clamp(value, 10, 10000);
            OnPropertyChanged();
        }
    }

    public double JogFeedZ
    {
        get => _settings.JogFeedZ;
        set
        {
            _settings.JogFeedZ = Math.Clamp(value, 10, 5000);
            OnPropertyChanged();
        }
    }

    public RelayCommand JogCommand { get; private set; } = null!;
    public RelayCommand JogCancelCommand { get; private set; } = null!;

    /// <summary>Jog with the keyboard: arrows X / Y, Page Up / Down Z, the numeric keypad as in Candle.</summary>
    public bool KeyboardJog
    {
        get => _settings.KeyboardJog;
        set
        {
            _settings.KeyboardJog = value;
            OnPropertyChanged();
            if (!value)
                JogStop();
        }
    }

    /// <summary>Move while a jog button or key is held; the step is not used.</summary>
    public bool JogContinuous
    {
        get => _settings.JogContinuous;
        set
        {
            _settings.JogContinuous = value;
            OnPropertyChanged();
            JogStop();
        }
    }

    /// <summary>A jog button was clicked: one step (continuous jogs run from press to release).</summary>
    private void Jog(string what)
    {
        if (JogContinuous)
            return;
        JogBy(what, null);
    }

    private string? _continuousJog;

    /// <summary>Start a continuous jog (button or key pressed): to the end of the travel.</summary>
    public void JogStart(string what)
    {
        if (!CanJog || _continuousJog != null)
            return;
        _continuousJog = what;
        JogBy(what, axis => axis switch
        {
            'X' => _settings.TableWidth,
            'Y' => _settings.TableDepth,
            _ => _settings.TableHeight,
        });
    }

    /// <summary>The button or key was released: stop at once (jog cancel).</summary>
    public void JogStop()
    {
        if (_continuousJog == null)
            return;
        _continuousJog = null;
        _conn.JogCancel();
    }

    /// <summary>
    /// A key went down or up. True when it was a jog key (handled): arrows
    /// X / Y, Page Up / Down Z, keypad 4 6 8 2 (X Y), 9 3 (Z), 7 1 (diagonals),
    /// keypad + / − the step, Esc or keypad 5 stop.
    /// </summary>
    public bool JogKey(System.Windows.Input.Key key, bool down, bool repeat)
    {
        if (!KeyboardJog)
            return false;
        string? what = key switch
        {
            System.Windows.Input.Key.Left or System.Windows.Input.Key.NumPad4 => "X-",
            System.Windows.Input.Key.Right or System.Windows.Input.Key.NumPad6 => "X+",
            System.Windows.Input.Key.Up or System.Windows.Input.Key.NumPad8 => "Y+",
            System.Windows.Input.Key.Down or System.Windows.Input.Key.NumPad2 => "Y-",
            System.Windows.Input.Key.PageUp or System.Windows.Input.Key.NumPad9 => "Z+",
            System.Windows.Input.Key.PageDown or System.Windows.Input.Key.NumPad3 => "Z-",
            System.Windows.Input.Key.NumPad7 => "XY-+",
            System.Windows.Input.Key.NumPad1 => "XY--",
            _ => null,
        };
        if (what == null)
        {
            if (!down)
                return false;
            switch (key)
            {
                case System.Windows.Input.Key.Add:
                case System.Windows.Input.Key.Subtract:
                    int i = Array.IndexOf(JogSteps, JogStep);
                    i = Math.Clamp(i + (key == System.Windows.Input.Key.Add ? 1 : -1), 0, JogSteps.Length - 1);
                    JogStep = JogSteps[i];
                    return true;
                case System.Windows.Input.Key.Escape:
                case System.Windows.Input.Key.NumPad5:
                    JogStop();
                    _conn.JogCancel();
                    return true;
            }
            return false;
        }
        if (JogContinuous)
        {
            if (!down)
                JogStop();
            else if (!repeat)
                JogStart(what);
        }
        else if (down && !repeat && CanJog)
        {
            JogBy(what, null);
        }
        return true;
    }

    /// <summary>"X+", "Y-", "Z+", "XY++" (diagonal) … by the step, or by the distance per axis.</summary>
    private void JogBy(string what, Func<char, double>? distance)
    {
        int split = what.IndexOfAny(new[] { '+', '-' });
        if (split <= 0)
            return;
        string axes = what[..split].ToUpperInvariant();
        string signs = what[split..];
        if (signs.Length == 1 && axes.Length > 1)
            signs = new string(signs[0], axes.Length);
        var words = new List<string>();
        bool z = false;
        for (int i = 0; i < axes.Length && i < signs.Length; i++)
        {
            double d = distance?.Invoke(axes[i]) ?? JogStep;
            words.Add(axes[i] + N(signs[i] == '-' ? -d : d));
            z |= axes[i] == 'Z';
        }
        double feed = z ? JogFeedZ : JogFeedXY;
        _conn.Jog("G91 G21 " + string.Join(' ', words) + " F" + N(feed));
    }

    // ---------------------------------------------------------------- zero, home, unlock, reset

    public RelayCommand ZeroCommand { get; private set; } = null!;
    public RelayCommand GoToZeroCommand { get; private set; } = null!;
    public RelayCommand HomeCommand { get; private set; } = null!;
    public RelayCommand UnlockCommand { get; private set; } = null!;
    public RelayCommand ResetCommand { get; private set; } = null!;
    public RelayCommand FeedHoldCommand { get; private set; } = null!;
    public RelayCommand CycleStartCommand { get; private set; } = null!;
    public RelayCommand CheckModeCommand { get; private set; } = null!;
    public RelayCommand SleepCommand { get; private set; } = null!;
    public RelayCommand SendRawCommand { get; private set; } = null!;
    /// <summary>A command that resets settings or leaves the firmware: asks first.</summary>
    public RelayCommand ConfirmRawCommand { get; private set; } = null!;

    /// <summary>Work zero of the active coordinate system at the current position ("X", "Y", "Z", "XY", "XYZ").</summary>
    private void Zero(string axes)
    {
        if (axes.Length == 0)
            return;
        var words = axes.ToUpperInvariant().Select(a => a + "0");
        _conn.Send("G10 L20 P0 " + string.Join(' ', words));
        _conn.Send("$#", SendKind.Query);
    }

    // ---------------------------------------------------------------- spindle, coolant, overrides

    private double _spindleRpm;
    public double SpindleRpm
    {
        get => _spindleRpm;
        set => Set(ref _spindleRpm, Math.Clamp(value, 0, 100000));
    }

    private double _spindleMax = 10000;
    /// <summary>Top of the speed slider: the controller's maximum spindle speed ($30).</summary>
    public double SpindleMax { get => _spindleMax; private set => Set(ref _spindleMax, value); }

    public RelayCommand SpindleApplyCommand { get; private set; } = null!;

    public RelayCommand SpindleCwCommand { get; private set; } = null!;
    public RelayCommand SpindleCcwCommand { get; private set; } = null!;
    public RelayCommand SpindleOffCommand { get; private set; } = null!;
    public RelayCommand FloodCommand { get; private set; } = null!;
    public RelayCommand MistCommand { get; private set; } = null!;
    public RelayCommand CoolantOffCommand { get; private set; } = null!;
    public RelayCommand OverrideCommand { get; private set; } = null!;

    // ---------------------------------------------------------------- probe

    public double ProbePlate
    {
        get => _settings.ProbePlate;
        set
        {
            _settings.ProbePlate = Math.Clamp(value, 0, 100);
            OnPropertyChanged();
        }
    }

    public double ProbeDistance
    {
        get => _settings.ProbeDistance;
        set
        {
            _settings.ProbeDistance = Math.Clamp(value, 1, 100);
            OnPropertyChanged();
        }
    }

    public double ProbeFeed
    {
        get => _settings.ProbeFeed;
        set
        {
            _settings.ProbeFeed = Math.Clamp(value, 5, 1000);
            OnPropertyChanged();
        }
    }

    private bool _probing;
    private string _probeText = "";
    public string ProbeText { get => _probeText; private set => Set(ref _probeText, value); }

    public RelayCommand ProbeZCommand { get; private set; } = null!;

    /// <summary>
    /// Touch the plate: probe down, set the work Z to the plate thickness,
    /// back off 2 mm. Runs as one script; an error or a miss stops it.
    /// </summary>
    private void ProbeZ()
    {
        if (MessageBox.Show(Loc.F("S.Ask.Probe", N(ProbePlate)), Loc.T("S.Ask.ProbeTitle"),
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        _probing = true;
        ProbeText = Loc.T("S.Probe.Running");
        _conn.Send($"G91 G38.2 Z-{N(ProbeDistance)} F{N(ProbeFeed)}");
        _conn.Send("G90");
    }

    private void OnProbe(Axes pos, bool ok)
    {
        if (OnMapProbe(pos, ok))
            return;
        if (!_probing)
            return;
        _probing = false;
        if (!ok)
        {
            ProbeText = Loc.T("S.Probe.Missed");
            return;
        }
        // The machine stands on the plate: work Z there is the plate thickness.
        _conn.Send($"G10 L20 P0 Z{N(ProbePlate)}");
        _conn.Send("G91 G0 Z2");
        _conn.Send("G90");
        _conn.Send("$#", SendKind.Query);
        ProbeText = Loc.F("S.Probe.Done", pos.Z, ProbePlate);
        Log(LogKind.Info, ProbeText);
    }

    // ---------------------------------------------------------------- grbl settings ($$)

    /// <summary>The $ settings as reported by the controller, in order.</summary>
    public ObservableCollection<SettingItem> GrblSettings { get; } = new();

    private readonly Dictionary<int, SettingItem> _settingById = new();

    private string _settingsFilter = "";
    public string SettingsFilter
    {
        get => _settingsFilter;
        set
        {
            if (Set(ref _settingsFilter, value))
                OnPropertyChanged(nameof(FilteredSettings));
        }
    }

    public IEnumerable<SettingItem> FilteredSettings
    {
        get
        {
            string f = SettingsFilter.Trim();
            if (f.Length == 0)
                return GrblSettings;
            return GrblSettings.Where(s => s.Code.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                                        s.Name.Contains(f, StringComparison.OrdinalIgnoreCase));
        }
    }

    public RelayCommand ReadSettingsCommand { get; private set; } = null!;
    public RelayCommand WriteSettingsCommand { get; private set; } = null!;

    private void ClearSettings()
    {
        GrblSettings.Clear();
        _settingById.Clear();
        OnPropertyChanged(nameof(FilteredSettings));
    }

    private void OnSetting(int id, string value)
    {
        if (_settingById.TryGetValue(id, out var item))
        {
            item.Reported(value);
        }
        else
        {
            item = new SettingItem(id, value);
            _settingById[id] = item;
            int at = 0;
            while (at < GrblSettings.Count && GrblSettings[at].Id < id)
                at++;
            GrblSettings.Insert(at, item);
            OnPropertyChanged(nameof(FilteredSettings));
        }
        // The work area of the viewer follows the max travel settings.
        if (double.TryParse(value, NumberStyles.Float, Ci, out double v) && v > 0)
        {
            switch (id)
            {
                case 130: _settings.TableWidth = v; OnPropertyChanged(nameof(BedWidth)); break;
                case 131: _settings.TableDepth = v; OnPropertyChanged(nameof(BedDepth)); break;
                case 132: _settings.TableHeight = v; break;
                case 30: SpindleMax = v; break;
            }
        }
        if (id is >= 110 and <= 112 or >= 120 and <= 122 or 11 or 12)
            _toolpathOptionsDirty = true;
    }

    private void WriteSettings()
    {
        var changed = GrblSettings.Where(s => s.IsChanged).ToList();
        if (changed.Count == 0)
            return;
        foreach (var s in changed)
        {
            if (!double.TryParse(s.Value, NumberStyles.Float, Ci, out _) && !s.Value.All(char.IsLetterOrDigit))
            {
                Notify(Loc.T("S.Notice.BadSetting"), Loc.F("S.Notice.BadSettingText", s.Code, s.Value), NotifySeverity.Warning);
                return;
            }
        }
        foreach (var s in changed)
            _conn.Send(s.Code + "=" + s.Value);
        Log(LogKind.Info, Loc.F("S.Log.SettingsWritten", changed.Count));
    }

    // ---------------------------------------------------------------- macros

    public ObservableCollection<MacroItem> Macros { get; } = new();

    private MacroItem? _selectedMacro;
    public MacroItem? SelectedMacro { get => _selectedMacro; set => Set(ref _selectedMacro, value); }

    public RelayCommand RunMacroCommand { get; private set; } = null!;
    public RelayCommand AddMacroCommand { get; private set; } = null!;
    public RelayCommand DeleteMacroCommand { get; private set; } = null!;

    // ---------------------------------------------------------------- commands

    private void CreateMachineCommands()
    {
        bool Online() => IsOnline;
        bool Control() => CanControl;

        JogCommand = new RelayCommand(p => Jog(p as string ?? ""), _ => CanJog);
        JogCancelCommand = new RelayCommand(() => _conn.JogCancel(), Online);
        ZeroCommand = new RelayCommand(p => Zero(p as string ?? ""), _ => CanControl);
        GoToZeroCommand = new RelayCommand(p =>
        {
            string axes = p as string ?? "XY";
            if (axes.Contains('Z'))
            {
                _conn.Send("G90 G0 Z0");
                return;
            }
            // Lift to a safe height first when below it.
            if (WPos.Z < _settings.SafeZ)
                _conn.Send($"G90 G0 Z{N(_settings.SafeZ)}");
            _conn.Send("G90 G0 X0 Y0");
        }, _ => CanControl && !IsAlarm);
        HomeCommand = new RelayCommand(() => _conn.Send("$H"), Control);
        UnlockCommand = new RelayCommand(() => _conn.Send("$X"), Online);
        ResetCommand = new RelayCommand(() =>
        {
            _conn.SoftReset();
            Log(LogKind.Warning, Loc.T("S.Log.SoftReset"));
        }, () => IsConnected);
        FeedHoldCommand = new RelayCommand(() => _conn.FeedHold(), Online);
        CycleStartCommand = new RelayCommand(() => _conn.CycleStart(), Online);
        CheckModeCommand = new RelayCommand(() => _conn.Send("$C"), Control);
        SleepCommand = new RelayCommand(() => _conn.Send("$SLP"), Control);
        SendRawCommand = new RelayCommand(p =>
        {
            if (p is string s)
                _conn.SendScript(s);
        }, _ => IsOnline);

        ConfirmRawCommand = new RelayCommand(p =>
        {
            if (p is string s && MessageBox.Show(Loc.F("S.Ask.Raw." + s.TrimStart('$').Replace("=", "").Replace("$", "S")
                        .Replace("#", "H"), s), AppName, MessageBoxButton.YesNo, MessageBoxImage.Warning) ==
                    MessageBoxResult.Yes)
                _conn.Send(s);
        }, _ => CanControl);

        SpindleCwCommand = new RelayCommand(() => _conn.Send($"M3 S{N(SpindleRpm)}"), Control);
        SpindleCcwCommand = new RelayCommand(() => _conn.Send($"M4 S{N(SpindleRpm)}"), Control);
        SpindleOffCommand = new RelayCommand(() => _conn.Send("M5"), Control);
        // The speed of the running spindle changes with a bare S word.
        SpindleApplyCommand = new RelayCommand(() => _conn.Send($"S{N(SpindleRpm)}"), () => CanControl && SpindleOn);
        FloodCommand = new RelayCommand(() =>
        {
            if (CanControl)
                _conn.Send("M8");
            else
                _conn.SendRealtime(RealtimeCommand.FloodToggle);
        }, Online);
        MistCommand = new RelayCommand(() =>
        {
            if (CanControl)
                _conn.Send("M7");
            else
                _conn.SendRealtime(RealtimeCommand.MistToggle);
        }, Online);
        CoolantOffCommand = new RelayCommand(() => _conn.Send("M9"), Control);
        OverrideCommand = new RelayCommand(p =>
        {
            byte? b = (p as string) switch
            {
                "F0" => RealtimeCommand.FeedOvReset,
                "F+10" => RealtimeCommand.FeedOvCoarsePlus,
                "F-10" => RealtimeCommand.FeedOvCoarseMinus,
                "F+1" => RealtimeCommand.FeedOvFinePlus,
                "F-1" => RealtimeCommand.FeedOvFineMinus,
                "R100" => RealtimeCommand.RapidOvReset,
                "R50" => RealtimeCommand.RapidOvMedium,
                "R25" => RealtimeCommand.RapidOvLow,
                "S0" => RealtimeCommand.SpindleOvReset,
                "S+10" => RealtimeCommand.SpindleOvCoarsePlus,
                "S-10" => RealtimeCommand.SpindleOvCoarseMinus,
                "S+1" => RealtimeCommand.SpindleOvFinePlus,
                "S-1" => RealtimeCommand.SpindleOvFineMinus,
                _ => null,
            };
            if (b != null)
                _conn.SendRealtime(b.Value);
        }, _ => IsOnline);

        ProbeZCommand = new RelayCommand(ProbeZ, () => CanControl && !IsAlarm);

        ReadSettingsCommand = new RelayCommand(() => _conn.Send("$$", SendKind.Query), Online);
        WriteSettingsCommand = new RelayCommand(WriteSettings, () => CanControl && GrblSettings.Any(s => s.IsChanged));

        RunMacroCommand = new RelayCommand(p =>
        {
            if (p is MacroItem m)
            {
                Log(LogKind.Info, Loc.F("S.Log.Macro", m.Name));
                _conn.SendScript(m.Script);
            }
        }, _ => CanControl);
        AddMacroCommand = new RelayCommand(() =>
        {
            var m = new MacroItem(Loc.T("S.NewMacro"), "G0 X0 Y0");
            Macros.Add(m);
            SelectedMacro = m;
        });
        DeleteMacroCommand = new RelayCommand(() =>
        {
            if (SelectedMacro != null)
                Macros.Remove(SelectedMacro);
        }, () => SelectedMacro != null);
    }
}
