using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media.Media3D;
using Microsoft.Win32;
using GrblHost.Core.GCode;
using GrblHost.Core.Machine;
using GrblHost.Infrastructure;
using GrblHost.Services;

namespace GrblHost.ViewModels;

/// <summary>G-code files and running them on the machine (streaming with character counting).</summary>
public sealed partial class MainViewModel
{
    // ---------------------------------------------------------------- file

    private GCodeDocument? _document;
    public GCodeDocument? Document { get => _document; private set => Set(ref _document, value); }

    private Toolpath? _toolpath;
    public Toolpath? Toolpath
    {
        get => _toolpath;
        private set
        {
            if (!Set(ref _toolpath, value))
                return;
            OnPropertyChanged(nameof(LayerCount));
            OnPropertyChanged(nameof(LayerSliderMax));
            OnPropertyChanged(nameof(HasToolpath));
            OnPropertyChanged(nameof(CanSimulate));
            OnPropertyChanged(nameof(SimTotal));
            OnPropertyChanged(nameof(SimTimeText));
            OnPropertyChanged(nameof(Legend));
        }
    }

    public bool HasToolpath => Toolpath is { Segments.Length: > 0 };

    private IReadOnlyList<GCodeLineItem> _gcodeLines = [];
    public IReadOnlyList<GCodeLineItem> GCodeLines { get => _gcodeLines; private set => Set(ref _gcodeLines, value); }

    private string _fileInfo = Loc.T("S.NoFile");
    public string FileInfo { get => _fileInfo; private set => Set(ref _fileInfo, value); }

    private string _fileName = "";
    public string FileName { get => _fileName; private set => Set(ref _fileName, value); }

    private string _fileWarning = "";
    /// <summary>Lines with codes grbl doesn't know, a work area overrun …</summary>
    public string FileWarning { get => _fileWarning; private set => Set(ref _fileWarning, value); }

    private bool _isLoading;
    public bool IsLoading { get => _isLoading; private set => Set(ref _isLoading, value); }

    private double _loadProgress;
    public double LoadProgress { get => _loadProgress; private set => Set(ref _loadProgress, value); }

    /// <summary>Kinds of moves present in the file, for the legend.</summary>
    public IReadOnlyList<LegendItem> Legend
    {
        get
        {
            var tp = Toolpath;
            if (tp == null)
                return [];
            var seen = new bool[Controls.FeaturePalette.Count];
            foreach (ref readonly var s in tp.Segments.AsSpan())
                seen[(int)s.Feature] = true;
            var list = new List<LegendItem>();
            for (int i = 0; i < seen.Length; i++)
                if (seen[i])
                    list.Add(new LegendItem((FeatureType)i));
            return list;
        }
    }

    public RelayCommand OpenFileCommand { get; private set; } = null!;
    public RelayCommand OpenDemoCommand { get; private set; } = null!;
    public RelayCommand ReloadCommand { get; private set; } = null!;

    private void OpenFile()
    {
        var dlg = new OpenFileDialog
        {
            Title = Loc.T("S.OpenDialogTitle"),
            Filter = Loc.T("S.OpenDialogFilter"),
            InitialDirectory = _settings.LastFolder ?? "",
        };
        if (dlg.ShowDialog() != true)
            return;
        _settings.LastFolder = Path.GetDirectoryName(dlg.FileName);
        _ = LoadFileAsync(dlg.FileName);
    }

    public bool CanLoadFile => !IsLoading && !IsJobRunning;

    public async Task LoadFileAsync(string path)
    {
        if (!CanLoadFile)
            return;
        await LoadAsync(Path.GetFileName(path), () => GCodeDocument.Load(path));
        if (_sourceDocument?.Path == path)
            AddRecent(_settings.RecentFiles, path);
        else
            RemoveRecent(_settings.RecentFiles, path);
        OnPropertyChanged(nameof(RecentFiles));
    }

    private void OpenRecent(string? path)
    {
        if (path == null)
            return;
        if (!File.Exists(path))
        {
            RemoveRecent(_settings.RecentFiles, path);
            OnPropertyChanged(nameof(RecentFiles));
            Notify(Loc.T("S.Notice.OpenFailed"), Loc.F("S.Notice.FileGone", path), NotifySeverity.Warning);
            return;
        }
        _ = LoadFileAsync(path);
    }

    public IReadOnlyList<RecentItem> RecentFiles => _settings.RecentFiles.Select(p => new RecentItem(p)).ToList();

    private const int MaxRecent = 10;

    private static void AddRecent(List<string> list, string path)
    {
        list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, path);
        if (list.Count > MaxRecent)
            list.RemoveRange(MaxRecent, list.Count - MaxRecent);
    }

    private static void RemoveRecent(List<string> list, string path) =>
        list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));

    // ---------------------------------------------------------------- editing and saving

    private bool _isEditing;
    /// <summary>The listing is replaced by an editor with the program text.</summary>
    public bool IsEditing
    {
        get => _isEditing;
        private set
        {
            if (Set(ref _isEditing, value))
                RelayCommand.Refresh();
        }
    }

    private string _editText = "";
    public string EditText { get => _editText; set => Set(ref _editText, value); }

    public RelayCommand EditProgramCommand { get; private set; } = null!;
    public RelayCommand ApplyEditCommand { get; private set; } = null!;
    public RelayCommand CancelEditCommand { get; private set; } = null!;
    public RelayCommand SaveProgramAsCommand { get; private set; } = null!;
    public RelayCommand OpenRecentCommand { get; private set; } = null!;

    private void EditProgram()
    {
        StopSimulation();
        EditText = _sourceDocument != null ? string.Join(Environment.NewLine, _sourceDocument.Lines) : "";
        IsEditing = true;
    }

    private void ApplyEdit()
    {
        var src = _sourceDocument;
        string name = src?.Name ?? Loc.T("S.NewProgramName");
        string? path = src?.Path;
        string text = EditText;
        _ = LoadAsync(name, () => GCodeDocument.FromBytes(name, path, System.Text.Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>Save the program; "leveled": the program with the height map applied.</summary>
    private void SaveProgramAs(bool leveled)
    {
        var doc = leveled ? Document : _sourceDocument;
        if (doc == null)
            return;
        string baseName = Path.GetFileNameWithoutExtension(doc.Name);
        var dlg = new SaveFileDialog
        {
            Title = Loc.T("S.SaveDialogTitle"),
            Filter = Loc.T("S.OpenDialogFilter"),
            DefaultExt = ".nc",
            FileName = (leveled ? baseName + "_leveled" : baseName) + Path.GetExtension(doc.Name),
            InitialDirectory = _settings.LastFolder ?? "",
        };
        if (dlg.ShowDialog() != true)
            return;
        try
        {
            File.WriteAllText(dlg.FileName, string.Join(Environment.NewLine, doc.Lines) + Environment.NewLine);
            _settings.LastFolder = Path.GetDirectoryName(dlg.FileName);
            Log(LogKind.Info, Loc.F("S.Log.Saved", dlg.FileName));
            if (!leveled)
                _ = LoadFileAsync(dlg.FileName);
        }
        catch (Exception ex)
        {
            Notify(Loc.T("S.Notice.SaveFailed"), ex.Message, NotifySeverity.Error);
        }
    }

    private Task LoadDemoAsync() =>
        LoadAsync("test3018.nc", () => GCodeDocument.FromText("test3018.nc", DemoGCode.Generate()));

    private bool _toolpathOptionsDirty;

    /// <summary>Machine limits for the time estimate, from the controller's settings when known.</summary>
    private ToolpathOptions MakeToolpathOptions()
    {
        var o = new ToolpathOptions();
        double Get(int id, double fallback) =>
            _settingById.TryGetValue(id, out var s) && double.TryParse(s.Value, NumberStyles.Float, Ci, out double v) && v > 0
                ? v
                : fallback;
        o.MaxRateX = Get(110, o.MaxRateX);
        o.MaxRateY = Get(111, o.MaxRateY);
        o.MaxRateZ = Get(112, o.MaxRateZ);
        o.AccelX = Get(120, o.AccelX);
        o.AccelY = Get(121, o.AccelY);
        o.AccelZ = Get(122, o.AccelZ);
        o.JunctionDeviation = Get(11, o.JunctionDeviation);
        o.ArcTolerance = Get(12, o.ArcTolerance);
        return o;
    }

    private async Task LoadAsync(string name, Func<GCodeDocument> load)
    {
        StopSimulation();
        IsLoading = true;
        LoadProgress = 0;
        FileInfo = Loc.F("S.Loading", name);
        try
        {
            var progress = new Progress<double>(p => LoadProgress = p * 100);
            var options = MakeToolpathOptions();
            _toolpathOptionsDirty = false;
            var map = HeightMapActive ? HeightMap : null;
            double segment = MapSegment;
            var (src, doc, tp, items) = await Task.Run(() =>
            {
                var s = load();
                var d = Leveled(s, map, segment);
                var t = ToolpathBuilder.Build(d.Lines, options, default, progress);
                var lines = new GCodeLineItem[d.Lines.Count];
                for (int i = 0; i < lines.Length; i++)
                    lines[i] = new GCodeLineItem(i, d.Lines[i]);
                return (s, d, t, lines);
            });
            _sourceDocument = src;
            IsEditing = false;
            SetDocument(doc, tp, items);
            if (map != null)
                FileName = doc.Name + Loc.T("S.Map.Suffix");
            Log(LogKind.Info, Loc.F("S.Log.Opened", doc.Name, doc.Lines.Count, tp.Layers.Count));
        }
        catch (Exception ex)
        {
            FileInfo = Loc.T("S.LoadError");
            Notify(Loc.T("S.Notice.OpenFailed"), ex.Message, NotifySeverity.Error);
        }
        finally
        {
            IsLoading = false;
            RelayCommand.Refresh();
        }
    }

    private void SetDocument(GCodeDocument doc, Toolpath tp, IReadOnlyList<GCodeLineItem> items)
    {
        Document = doc;
        GCodeLines = items;
        Toolpath = tp;
        FileName = doc.Name;
        StartLine = 1;
        RebuildFileInfo();
        ViewerMode = ViewerMode.Preview;
        ShowAll();
        SimTime = 0;
        CurrentLine = -1;
    }

    private void RebuildFileInfo()
    {
        var doc = Document;
        var tp = Toolpath;
        if (doc == null || tp == null)
        {
            FileInfo = Loc.T("S.NoFile");
            FileWarning = "";
            return;
        }
        var size = tp.Max - tp.Min;
        FileInfo = Loc.F("S.FileInfo", doc.Lines.Count, size.X, size.Y, size.Z, tp.Min.Z,
            FormatTime(tp.TotalTime), tp.CutLength / 1000, tp.MaxSpindle);
        var warn = new List<string>();
        if (tp.UnsupportedLines.Count > 0)
        {
            string lines = string.Join(", ", tp.UnsupportedLines.Take(5).Select(l => (l + 1).ToString(Ci)));
            if (tp.UnsupportedLines.Count > 5)
                lines += " …";
            warn.Add(Loc.F("S.Warn.Unsupported", tp.UnsupportedLines.Count, lines));
        }
        if (size.X > _settings.TableWidth + 1e-3 || size.Y > _settings.TableDepth + 1e-3 ||
            size.Z > _settings.TableHeight + 1e-3)
            warn.Add(Loc.F("S.Warn.TooBig", _settings.TableWidth, _settings.TableDepth, _settings.TableHeight));
        if (tp.MaxSpindle <= 0 && tp.CutLength > 0)
            warn.Add(Loc.T("S.Warn.NoSpindle"));
        if (HeightMapWarning(tp) is { } mapWarning)
            warn.Add(mapWarning);
        FileWarning = string.Join("\n", warn);
    }

    public static string FormatTime(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0)
            seconds = 0;
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    /// <summary>Lines to send: comments and empty lines removed, source lines kept.</summary>
    private static List<JobLine> MakeJobLines(GCodeDocument doc, int firstLine)
    {
        var job = new List<JobLine>(doc.Lines.Count);
        for (int i = firstLine; i < doc.Lines.Count; i++)
        {
            string cmd = GrblConnection.Clean(doc.Lines[i]);
            if (cmd.Length > 0)
                job.Add(new JobLine(cmd, i));
        }
        return job;
    }

    // ---------------------------------------------------------------- running a job

    private bool _isJobRunning;
    public bool IsJobRunning
    {
        get => _isJobRunning;
        private set
        {
            if (!Set(ref _isJobRunning, value))
                return;
            OnPropertyChanged(nameof(CanControl));
            OnPropertyChanged(nameof(CanJog));
            OnPropertyChanged(nameof(CanLoadFile));
            RelayCommand.Refresh();
        }
    }

    private bool _isPaused;
    public bool IsPaused { get => _isPaused; private set => Set(ref _isPaused, value); }

    private double _jobProgress;
    /// <summary>0…100.</summary>
    public double JobProgress { get => _jobProgress; private set => Set(ref _jobProgress, value); }

    private string _elapsedText = "";
    public string ElapsedText { get => _elapsedText; private set => Set(ref _elapsedText, value); }

    private string _remainingText = "";
    public string RemainingText { get => _remainingText; private set => Set(ref _remainingText, value); }

    private int _startLine = 1;
    /// <summary>First line to run (one-based); above 1 the modal state of the skipped lines is restored first.</summary>
    public int StartLine
    {
        get => _startLine;
        set => Set(ref _startLine, Math.Clamp(value, 1, Math.Max(1, Document?.Lines.Count ?? 1)));
    }

    public double SafeZ
    {
        get => _settings.SafeZ;
        set
        {
            _settings.SafeZ = Math.Clamp(value, 0, 100);
            OnPropertyChanged();
        }
    }

    private List<JobLine>? _jobLines;
    private int _jobPreamble;
    private double _jobStart;
    private double _pauseStart;
    private double _pausedTotal;
    private float _liveEstimateDone;     // Estimated time of the moves done, s.
    private float _liveEstimateStart;    // Estimated time of the skipped lines, s.

    public RelayCommand StartJobCommand { get; private set; } = null!;
    public RelayCommand PauseCommand { get; private set; } = null!;
    public RelayCommand ResumeCommand { get; private set; } = null!;
    public RelayCommand StopCommand { get; private set; } = null!;
    public RelayCommand StartFromCurrentLineCommand { get; private set; } = null!;

    private void StartJob()
    {
        var doc = Document;
        if (doc == null)
            return;
        if (_toolpathOptionsDirty && Toolpath != null)
            Log(LogKind.Info, Loc.T("S.Log.EstimateOld"));
        int first = Math.Clamp(StartLine - 1, 0, doc.Lines.Count - 1);
        var lines = new List<JobLine>();
        if (first > 0)
        {
            // Restore what the skipped lines set up: units, modes, spindle, coolant, position.
            var modal = new ModalState();
            for (int i = 0; i < first; i++)
            {
                var b = GCodeBlock.Parse(doc.Lines[i]);
                if (!b.IsEmpty)
                    modal.Apply(b);
            }
            if (MessageBox.Show(Loc.F("S.Ask.StartFromLine", first + 1, N(SafeZ)), AppName,
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
                return;
            foreach (var l in modal.Preamble(SafeZ, _settings.SpindleDelay))
                lines.Add(new JobLine(l, first));
        }
        _jobPreamble = lines.Count;
        lines.AddRange(MakeJobLines(doc, first));
        if (lines.Count == 0)
            return;
        var longLine = lines.FirstOrDefault(l => l.Command.Length > GrblConnection.MaxLineLength);
        if (longLine.Command != null)
        {
            Notify(Loc.T("S.Notice.JobNotStarted"), Loc.F("S.Log.LongLine", longLine.SourceLine + 1), NotifySeverity.Error);
            return;
        }
        if (IsAlarm)
        {
            Notify(Loc.T("S.Notice.JobNotStarted"), Loc.T("S.Notice.UnlockFirst"), NotifySeverity.Warning);
            return;
        }
        try
        {
            StopSimulation();
            _jobLines = lines;
            _conn.StartJob(lines);
            BeginLiveJob(first);
            Log(LogKind.Info, Loc.F("S.Log.JobStarted", doc.Name, lines.Count, first + 1));
        }
        catch (Exception ex)
        {
            Notify(Loc.T("S.Notice.JobNotStarted"), ex.Message, NotifySeverity.Error);
        }
    }

    private void BeginLiveJob(int firstLine)
    {
        IsJobRunning = true;
        IsPaused = false;
        JobProgress = 0;
        _jobStart = _clock.Elapsed.TotalSeconds;
        _pausedTotal = 0;
        _liveEstimateDone = 0;
        _liveEstimateStart = 0;
        ViewerMode = ViewerMode.Live;
        var tp = Toolpath;
        if (tp != null)
        {
            int seg = tp.LastSegmentAtOrBeforeLine(firstLine - 1);
            _liveEstimateStart = seg >= 0 ? tp.Segments[seg].EndTime : 0;
            ShowProgress(seg, ToolPoint(WPos));
        }
    }

    private void EndLiveJob(string message)
    {
        if (!IsJobRunning)
            return;
        IsJobRunning = false;
        IsPaused = false;
        _jobLines = null;
        RemainingText = "";
        ViewerMode = ViewerMode.Preview;
        ShowAll();
        CurrentLine = -1;
        Log(LogKind.Info, message);
    }

    private void Pause()
    {
        if (!IsJobRunning)
            return;
        _conn.PauseJob();
        IsPaused = true;
        _pauseStart = _clock.Elapsed.TotalSeconds;
        Log(LogKind.Info, Loc.T("S.Log.Pause"));
    }

    private void Resume()
    {
        if (!IsJobRunning)
            return;
        _conn.ResumeJob();
        IsPaused = false;
        _pausedTotal += _clock.Elapsed.TotalSeconds - _pauseStart;
        Log(LogKind.Info, Loc.T("S.Log.Resume"));
    }

    private void Stop()
    {
        if (MessageBox.Show(Loc.T("S.Ask.StopJob"), AppName, MessageBoxButton.YesNo, MessageBoxImage.Warning) !=
            MessageBoxResult.Yes)
            return;
        // Feed hold, then a reset once the machine stands: the position stays valid.
        _conn.StopJob();
        Log(LogKind.Info, Loc.T("S.Log.Stopping"));
    }

    private void OnJobCompleted(JobResult result)
    {
        if (OnMapJobCompleted(result))
            return;
        if (!IsJobRunning)
            return;
        double elapsed = _clock.Elapsed.TotalSeconds - _jobStart - _pausedTotal;
        switch (result)
        {
            case JobResult.Done:
                JobProgress = 100;
                Notify(Loc.T("S.Notice.JobDone"), Loc.F("S.Notice.JobDoneText", FileName, FormatTime(elapsed)),
                    NotifySeverity.Success);
                EndLiveJob(Loc.F("S.Log.JobDone", FormatTime(elapsed)));
                break;
            case JobResult.Cancelled:
                EndLiveJob(Loc.T("S.Log.JobCancelled"));
                break;
            default:
                // Error, alarm, reset or link loss: the reason was logged and shown already.
                EndLiveJob(Loc.F("S.Log.JobAborted", Loc.T("S.Result." + result)));
                break;
        }
    }

    private void OnJobAck(int index)
    {
        if (!IsJobRunning || _jobLines == null || index >= _jobLines.Count)
            return;
        JobProgress = (index + 1) * 100.0 / _jobLines.Count;
        if (index >= _jobPreamble)
            ShowLiveLine(_jobLines[index].SourceLine);
    }

    private void UpdateElapsed()
    {
        if (!IsJobRunning)
            return;
        double now = _clock.Elapsed.TotalSeconds;
        double paused = _pausedTotal + (IsPaused ? now - _pauseStart : 0);
        double elapsed = now - _jobStart - paused;
        ElapsedText = FormatTime(elapsed);
        var tp = Toolpath;
        float done = _liveEstimateDone - _liveEstimateStart;
        if (tp == null || tp.TotalTime <= 0 || done <= 0)
        {
            RemainingText = "";
            return;
        }
        double remaining = tp.TotalTime - _liveEstimateDone;
        // After a few minutes correct the estimate by how fast the machine really is.
        if (elapsed > 120 && done > 60)
            remaining *= Math.Clamp(elapsed / done, 0.5, 3);
        RemainingText = "≈ " + FormatTime(remaining);
    }

    /// <summary>Tool marker at the machine's work position during a job.</summary>
    private void OnLivePosition(Axes wpos)
    {
        ToolPosition = ToolPoint(wpos);
        ShowTool = true;
    }

    /// <summary>Outside jobs and simulations the marker shows where the machine is.</summary>
    private void ShowMachineTool(Axes wpos)
    {
        if (!IsOnline)
            return;
        ToolPosition = ToolPoint(wpos);
        ShowTool = true;
        ToolText = Loc.F("S.ToolText", wpos.X, wpos.Y, wpos.Z, Feed);
    }

    private static Point3D ToolPoint(Axes a) => new(a.X, a.Y, a.Z);

    private void CreateJobCommands()
    {
        OpenFileCommand = new RelayCommand(OpenFile, () => CanLoadFile);
        OpenDemoCommand = new RelayCommand(() => _ = LoadDemoAsync(), () => CanLoadFile);
        ReloadCommand = new RelayCommand(() =>
        {
            var path = Document?.Path;
            if (path != null)
                _ = LoadFileAsync(path);
        }, () => CanLoadFile && Document?.Path != null);
        OpenRecentCommand = new RelayCommand(p => OpenRecent(p as string), _ => CanLoadFile);
        EditProgramCommand = new RelayCommand(EditProgram, () => CanLoadFile && !IsEditing);
        ApplyEditCommand = new RelayCommand(ApplyEdit, () => CanLoadFile && IsEditing);
        CancelEditCommand = new RelayCommand(() => IsEditing = false, () => IsEditing);
        SaveProgramAsCommand = new RelayCommand(p => SaveProgramAs(p as string == "leveled"),
            p => p as string == "leveled" ? HeightMapActive && Document != null : _sourceDocument != null);
        CreateHeightMapCommands();

        StartJobCommand = new RelayCommand(StartJob,
            () => IsOnline && Document != null && !IsJobRunning && !IsLoading &&
                  MachineState is MachineState.Idle or MachineState.Check or MachineState.Alarm);
        PauseCommand = new RelayCommand(Pause, () => IsJobRunning && !IsPaused && IsOnline);
        ResumeCommand = new RelayCommand(Resume, () => IsJobRunning && IsPaused && IsOnline);
        StopCommand = new RelayCommand(Stop, () => IsJobRunning && IsConnected);
        StartFromCurrentLineCommand = new RelayCommand(() => StartLine = CurrentLine + 1,
            () => !IsJobRunning && CurrentLine >= 0);
    }
}

/// <summary>A file of a recent list: the name shown, the path as tool tip.</summary>
public sealed class RecentItem
{
    public RecentItem(string path)
    {
        Path = path;
    }

    public string Path { get; }
    public string Name => System.IO.Path.GetFileName(Path);
}

public sealed class LegendItem
{
    public LegendItem(FeatureType type)
    {
        Type = type;
    }

    public FeatureType Type { get; }
    public string Name => Loc.T("S.Feature." + Type);
    public System.Windows.Media.Brush Brush => Controls.FeaturePalette.BrushOf(Type);
}
