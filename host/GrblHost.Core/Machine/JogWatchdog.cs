namespace GrblHost.Core.Machine;

/// <summary>
/// Safety net for a continuous jog that nobody holds any more: the controller reports the Jog
/// state, but no button, key or stick keeps the jog going. That happens when a release is lost
/// (a mouse button released outside the window, a key up that went to another window) or when a
/// jog line reaches the controller after its cancel. A jog like that runs to the end of the travel.
/// <para>
/// The first status report that shows it only starts the clock: after a normal release the machine
/// needs a moment to stop. If it still runs after <see cref="GraceMs"/>, a jog cancel is asked for,
/// and again every <see cref="RepeatMs"/> while it goes on.
/// </para>
/// </summary>
public sealed class JogWatchdog
{
    /// <summary>The machine may take this long to stop after a release (50 mm/s² from 1000 mm/min takes 0.3 s).</summary>
    public double GraceMs { get; set; } = 1500;

    public double RepeatMs { get; set; } = 1500;

    private double _since = double.NaN;
    private double _lastCancel = double.NegativeInfinity;

    /// <param name="nowMs">Monotonic time in milliseconds.</param>
    /// <param name="machineJogging">The last status report said Jog.</param>
    /// <param name="jogHeld">A button, key or stick is holding a continuous jog.</param>
    /// <param name="continuousMode">The "continuous" setting; step jogs end by themselves.</param>
    /// <returns>True when the jog should be cancelled now.</returns>
    public bool Update(double nowMs, bool machineJogging, bool jogHeld, bool continuousMode)
    {
        if (!machineJogging || jogHeld || !continuousMode)
        {
            _since = double.NaN;
            return false;
        }
        if (double.IsNaN(_since))
        {
            _since = nowMs;
            return false;
        }
        if (nowMs - _since < GraceMs || nowMs - _lastCancel < RepeatMs)
            return false;
        _lastCancel = nowMs;
        return true;
    }
}
