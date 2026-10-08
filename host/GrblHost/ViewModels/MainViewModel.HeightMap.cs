using System.IO;
using System.Windows;
using Microsoft.Win32;
using GrblHost.Core.GCode;
using GrblHost.Core.Machine;
using GrblHost.Infrastructure;
using GrblHost.Services;

namespace GrblHost.ViewModels;

/// <summary>
/// Height map (auto leveling, as in Candle): probe a grid over the stock,
/// show it, save and load it, and run the program with every move following
/// the surface.
/// </summary>
public sealed partial class MainViewModel
{
    // ---------------------------------------------------------------- area and grid

    public double MapX
    {
        get => _settings.MapX;
        set { _settings.MapX = value; OnPropertyChanged(); OnPropertyChanged(nameof(MapStepText)); }
    }

    public double MapY
    {
        get => _settings.MapY;
        set { _settings.MapY = value; OnPropertyChanged(); OnPropertyChanged(nameof(MapStepText)); }
    }

    public double MapWidth
    {
        get => _settings.MapWidth;
        set { _settings.MapWidth = Math.Max(1, value); OnPropertyChanged(); OnPropertyChanged(nameof(MapStepText)); }
    }

    public double MapHeight
    {
        get => _settings.MapHeight;
        set { _settings.MapHeight = Math.Max(1, value); OnPropertyChanged(); OnPropertyChanged(nameof(MapStepText)); }
    }

    public int MapPointsX
    {
        get => _settings.MapPointsX;
        set { _settings.MapPointsX = Math.Clamp(value, 2, 50); OnPropertyChanged(); OnPropertyChanged(nameof(MapStepText)); }
    }

    public int MapPointsY
    {
        get => _settings.MapPointsY;
        set { _settings.MapPointsY = Math.Clamp(value, 2, 50); OnPropertyChanged(); OnPropertyChanged(nameof(MapStepText)); }
    }

    /// <summary>"Step 10 × 7.5 mm, 20 points".</summary>
    public string MapStepText => Loc.F("S.Map.Step", MapWidth / (MapPointsX - 1), MapHeight / (MapPointsY - 1),
        MapPointsX * MapPointsY);

    public double MapSafeZ
    {
        get => _settings.MapSafeZ;
        set { _settings.MapSafeZ = Math.Clamp(value, 0.5, 50); OnPropertyChanged(); }
    }

    public double MapProbeZ
    {
        get => _settings.MapProbeZ;
        set { _settings.MapProbeZ = Math.Clamp(value, -50, 0); OnPropertyChanged(); }
    }

    public double MapFeed
    {
        get => _settings.MapFeed;
        set { _settings.MapFeed = Math.Clamp(value, 5, 1000); OnPropertyChanged(); }
    }

    /// <summary>Bicubic between the points (smooth); off: bilinear.</summary>
    public bool MapBicubic
    {
        get => _settings.MapBicubic;
        set
        {
            _settings.MapBicubic = value;
            if (_heightMap != null)
                _heightMap.Bilinear = !value;
            OnPropertyChanged();
            HeightMapVersion++;
            ReapplyHeightMap();
        }
    }

    /// <summary>Longest straight piece of a leveled move, mm.</summary>
    public double MapSegment
    {
        get => _settings.MapSegment;
        set
        {
            _settings.MapSegment = Math.Clamp(value, 0.1, 50);
            OnPropertyChanged();
            ReapplyHeightMap();
        }
    }

    // ---------------------------------------------------------------- the map

    private HeightMap? _heightMap;
    public HeightMap? HeightMap
    {
        get => _heightMap;
        private set
        {
            if (!Set(ref _heightMap, value))
                return;
            HeightMapVersion++;
            OnPropertyChanged(nameof(HasHeightMap));
            RelayCommand.Refresh();
        }
    }

    public bool HasHeightMap => HeightMap != null;

    private int _heightMapVersion;
    /// <summary>Changes with every probed point: the viewer redraws the map.</summary>
    public int HeightMapVersion
    {
        get => _heightMapVersion;
        private set
        {
            Set(ref _heightMapVersion, value);
            OnPropertyChanged(nameof(MapStatus));
        }
    }

    public string MapStatus
    {
        get
        {
            var m = HeightMap;
            if (m == null)
                return Loc.T("S.Map.None");
            int total = m.PointsX * m.PointsY;
            return m.ProbedCount == 0
                ? Loc.F("S.Map.Empty", total)
                : Loc.F("S.Map.Status", m.ProbedCount, total, m.Min, m.Max, m.Max - m.Min);
        }
    }

    private bool _showHeightMap = true;
    /// <summary>Draw the map in the 3D view.</summary>
    public bool ShowHeightMap { get => _showHeightMap; set => Set(ref _showHeightMap, value); }

    private bool _useHeightMap;
    /// <summary>The program shown and run follows the map.</summary>
    public bool UseHeightMap
    {
        get => _useHeightMap;
        set
        {
            if (value && HeightMap is not { IsComplete: true })
            {
                Notify(Loc.T("S.Map.Title"), Loc.T("S.Map.NotComplete"), NotifySeverity.Warning);
                OnPropertyChanged();
                return;
            }
            if (!Set(ref _useHeightMap, value))
                return;
            ReapplyHeightMap(force: true);
        }
    }

    private bool HeightMapActive => _useHeightMap && HeightMap is { IsComplete: true };

    /// <summary>The program as loaded or edited; <see cref="Document"/> is it with the map applied.</summary>
    private GCodeDocument? _sourceDocument;

    /// <summary>Build the shown program again from the source (map on, off, or changed).</summary>
    private void ReapplyHeightMap(bool force = false)
    {
        if (_sourceDocument == null || (!force && !HeightMapActive) || IsJobRunning)
            return;
        var src = _sourceDocument;
        _ = LoadAsync(src.Name, () => src);
    }

    /// <summary>The program to show and run for a loaded one: leveled when the map is on.</summary>
    private static GCodeDocument Leveled(GCodeDocument src, HeightMap? map, double segment)
    {
        if (map == null)
            return src;
        var lines = HeightMapApplier.Apply(src.Lines, map, segment);
        return GCodeDocument.FromBytes(src.Name, src.Path, System.Text.Encoding.UTF8.GetBytes(string.Join("\n", lines)));
    }

    /// <summary>The XY area of the feed moves of the loaded program.</summary>
    private (double X0, double Y0, double X1, double Y1)? CutArea(Toolpath? tp)
    {
        if (tp == null)
            return null;
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (ref readonly var s in tp.Segments.AsSpan())
        {
            if (s.Kind == MoveKind.Rapid)
                continue;
            x0 = Math.Min(x0, Math.Min(s.Start.X, s.End.X));
            y0 = Math.Min(y0, Math.Min(s.Start.Y, s.End.Y));
            x1 = Math.Max(x1, Math.Max(s.Start.X, s.End.X));
            y1 = Math.Max(y1, Math.Max(s.Start.Y, s.End.Y));
        }
        return x1 >= x0 ? (x0, y0, x1, y1) : null;
    }

    /// <summary>Warning when the program cuts outside the probed area.</summary>
    private string? HeightMapWarning(Toolpath tp)
    {
        var m = HeightMap;
        if (!HeightMapActive || m == null || CutArea(tp) is not { } a)
            return null;
        const double tol = 0.5;
        if (a.X0 < m.X - tol || a.Y0 < m.Y - tol || a.X1 > m.X + m.Width + tol || a.Y1 > m.Y + m.Height + tol)
            return Loc.T("S.Map.Outside");
        return null;
    }

    // ---------------------------------------------------------------- commands

    public RelayCommand MapFromProgramCommand { get; private set; } = null!;
    public RelayCommand ProbeMapCommand { get; private set; } = null!;
    public RelayCommand ClearMapCommand { get; private set; } = null!;
    public RelayCommand OpenMapCommand { get; private set; } = null!;
    public RelayCommand SaveMapCommand { get; private set; } = null!;
    public RelayCommand OpenRecentMapCommand { get; private set; } = null!;

    /// <summary>The area of the loaded program's cuts (with the program's offset).</summary>
    private void MapFromProgram()
    {
        // The source toolpath: with the map on, the shown one is the leveled program (same XY).
        if (CutArea(Toolpath) is not { } a)
            return;
        MapX = Math.Round(a.X0, 3);
        MapY = Math.Round(a.Y0, 3);
        MapWidth = Math.Max(1, Math.Round(a.X1 - a.X0, 3));
        MapHeight = Math.Max(1, Math.Round(a.Y1 - a.Y0, 3));
        // About 10 mm between the points, at least 3 per side.
        MapPointsX = Math.Clamp((int)Math.Ceiling(MapWidth / 10) + 1, 3, 20);
        MapPointsY = Math.Clamp((int)Math.Ceiling(MapHeight / 10) + 1, 3, 20);
    }

    private HeightMapProbe? _mapProbe;
    private Axes _mapWco;

    private void ProbeMap()
    {
        if (MapProbeZ >= MapSafeZ)
            return;
        int points = MapPointsX * MapPointsY;
        if (MessageBox.Show(Loc.F("S.Ask.MapProbe", points, N(MapSafeZ), N(MapProbeZ)), Loc.T("S.Map.Title"),
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        var map = new HeightMap(MapX, MapY, MapWidth, MapHeight, MapPointsX, MapPointsY) { Bilinear = !MapBicubic };
        var probe = new HeightMapProbe(map, MapSafeZ, MapProbeZ, MapFeed);
        var job = probe.BuildJob();
        try
        {
            StopSimulation();
            UseHeightMap = false;
            _mapWco = _conn.Snapshot.Wco;
            _mapProbe = probe;
            HeightMap = map;
            _jobLines = job;
            _jobPreamble = int.MaxValue;         // No program lines to follow in the listing.
            _conn.StartJob(job);
            IsJobRunning = true;
            IsPaused = false;
            JobProgress = 0;
            _jobStart = _clock.Elapsed.TotalSeconds;
            _pausedTotal = 0;
            _liveEstimateDone = _liveEstimateStart = 0;
            Log(LogKind.Info, Loc.F("S.Log.MapStarted", points));
        }
        catch (Exception ex)
        {
            _mapProbe = null;
            _jobLines = null;
            Notify(Loc.T("S.Notice.JobNotStarted"), ex.Message, NotifySeverity.Error);
        }
    }

    /// <summary>A [PRB:] while the map is probed. True when it was taken.</summary>
    private bool OnMapProbe(Axes pos, bool ok)
    {
        if (_mapProbe == null)
            return false;
        _mapProbe.Record(pos, ok, _mapWco);
        HeightMapVersion++;
        return true;
    }

    /// <summary>The probing job ended. True when it was the probing job.</summary>
    private bool OnMapJobCompleted(JobResult result)
    {
        var probe = _mapProbe;
        if (probe == null)
            return false;
        _mapProbe = null;
        IsJobRunning = false;
        IsPaused = false;
        _jobLines = null;
        RemainingText = "";
        HeightMapVersion++;
        if (result == JobResult.Done && probe.Map.IsComplete)
        {
            JobProgress = 100;
            Log(LogKind.Info, MapStatus);
            Notify(Loc.T("S.Map.Title"), Loc.T("S.Map.Done") + " " + MapStatus, NotifySeverity.Success);
        }
        else
        {
            Log(LogKind.Warning, Loc.F("S.Log.MapAborted", probe.Done, probe.Order.Count));
        }
        return true;
    }

    private void ClearMap()
    {
        UseHeightMap = false;
        HeightMap = null;
    }

    private void OpenMap(string? path = null)
    {
        if (path == null)
        {
            var dlg = new OpenFileDialog
            {
                Title = Loc.T("S.Map.OpenTitle"),
                Filter = Loc.T("S.Map.Filter"),
                InitialDirectory = _settings.LastFolder ?? "",
            };
            if (dlg.ShowDialog() != true)
                return;
            path = dlg.FileName;
        }
        try
        {
            var map = HeightMap.Load(File.ReadAllText(path));
            map.Bilinear = !MapBicubic;
            UseHeightMap = false;
            HeightMap = map;
            MapX = map.X;
            MapY = map.Y;
            MapWidth = map.Width;
            MapHeight = map.Height;
            MapPointsX = map.PointsX;
            MapPointsY = map.PointsY;
            AddRecent(_settings.RecentMaps, path);
            OnPropertyChanged(nameof(RecentMaps));
            Log(LogKind.Info, Loc.F("S.Log.MapOpened", Path.GetFileName(path)) + " " + MapStatus);
        }
        catch (Exception ex)
        {
            RemoveRecent(_settings.RecentMaps, path);
            OnPropertyChanged(nameof(RecentMaps));
            Notify(Loc.T("S.Notice.OpenFailed"), ex.Message, NotifySeverity.Error);
        }
    }

    private void SaveMap()
    {
        var map = HeightMap;
        if (map == null)
            return;
        var dlg = new SaveFileDialog
        {
            Title = Loc.T("S.Map.SaveTitle"),
            Filter = Loc.T("S.Map.Filter"),
            DefaultExt = ".map",
            FileName = Path.GetFileNameWithoutExtension(_sourceDocument?.Name ?? "heightmap") + ".map",
            InitialDirectory = _settings.LastFolder ?? "",
        };
        if (dlg.ShowDialog() != true)
            return;
        try
        {
            File.WriteAllText(dlg.FileName, map.Save());
            AddRecent(_settings.RecentMaps, dlg.FileName);
            OnPropertyChanged(nameof(RecentMaps));
        }
        catch (Exception ex)
        {
            Notify(Loc.T("S.Notice.SaveFailed"), ex.Message, NotifySeverity.Error);
        }
    }

    public IReadOnlyList<RecentItem> RecentMaps => _settings.RecentMaps.Select(p => new RecentItem(p)).ToList();

    private void CreateHeightMapCommands()
    {
        MapFromProgramCommand = new RelayCommand(MapFromProgram, () => HasToolpath && !IsJobRunning);
        ProbeMapCommand = new RelayCommand(ProbeMap, () => CanControl && !IsAlarm && MapProbeZ < MapSafeZ);
        ClearMapCommand = new RelayCommand(ClearMap, () => HasHeightMap && !IsJobRunning);
        OpenMapCommand = new RelayCommand(() => OpenMap(), () => !IsJobRunning);
        SaveMapCommand = new RelayCommand(SaveMap, () => HasHeightMap);
        OpenRecentMapCommand = new RelayCommand(p => OpenMap(p as string), _ => !IsJobRunning);
    }
}
