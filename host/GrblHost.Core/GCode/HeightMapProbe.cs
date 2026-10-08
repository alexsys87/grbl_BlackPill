using System.Globalization;
using GrblHost.Core.Machine;

namespace GrblHost.Core.GCode;

/// <summary>
/// Probing a <see cref="HeightMap"/>: the job that visits the grid points
/// (zigzag, at the safe height, G38.2 down at every point) and the
/// bookkeeping that puts the probe results ([PRB:...], machine coordinates)
/// into the map as work Z.
/// </summary>
public sealed class HeightMapProbe
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly List<(int Ix, int Iy)> _order;

    /// <param name="map">The grid to fill, work coordinates.</param>
    /// <param name="safeZ">Work Z for the moves between the points, above the stock.</param>
    /// <param name="probeZ">Work Z the probe may go down to; no touch above it is an alarm.</param>
    /// <param name="feed">Probe feed, mm/min.</param>
    public HeightMapProbe(HeightMap map, double safeZ, double probeZ, double feed)
    {
        if (probeZ >= safeZ)
            throw new ArgumentOutOfRangeException(nameof(probeZ), "The probe depth must be below the safe height.");
        if (feed <= 0)
            throw new ArgumentOutOfRangeException(nameof(feed));
        Map = map;
        SafeZ = safeZ;
        ProbeZ = probeZ;
        Feed = feed;
        _order = map.ProbeOrder().ToList();
    }

    public HeightMap Map { get; }
    public double SafeZ { get; }
    public double ProbeZ { get; }
    public double Feed { get; }

    public IReadOnlyList<(int Ix, int Iy)> Order => _order;

    /// <summary>Points probed so far.</summary>
    public int Done { get; private set; }

    public bool Finished => Done >= _order.Count;

    /// <summary>
    /// The probing job. <see cref="JobLine.SourceLine"/> is the index of the
    /// point a line belongs to.
    /// </summary>
    public List<JobLine> BuildJob()
    {
        var job = new List<JobLine>
        {
            new("G21 G90", 0),
            new("G0 Z" + Num(SafeZ), 0),
        };
        for (int i = 0; i < _order.Count; i++)
        {
            var (ix, iy) = _order[i];
            job.Add(new JobLine("G0 X" + Num(Map.PointX(ix)) + " Y" + Num(Map.PointY(iy)), i));
            job.Add(new JobLine("G38.2 Z" + Num(ProbeZ) + " F" + Num(Feed), i));
            job.Add(new JobLine("G0 Z" + Num(SafeZ), i));
        }
        return job;
    }

    /// <summary>
    /// A probe result arrived: machine position of the touch, and the work
    /// offset (WCO) of the job. Returns the grid point it belongs to, null
    /// when all points are done already. A miss leaves the point unprobed.
    /// </summary>
    public (int Ix, int Iy)? Record(Axes machine, bool touched, Axes wco)
    {
        if (Finished)
            return null;
        var p = _order[Done++];
        if (touched)
            Map[p.Ix, p.Iy] = machine.Z - wco.Z;
        return p;
    }

    private static string Num(double v) => Math.Round(v, 4).ToString("0.####", Inv);
}
