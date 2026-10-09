namespace GrblHost.Core.Input;

/// <summary>What the driver can do to the machine; the view model provides it.</summary>
public sealed class JogTarget
{
    /// <summary>Start a continuous jog ("X+", "XY-+" …) that runs until <see cref="Stop"/>.</summary>
    public required Action<string> Start { get; init; }

    /// <summary>Jog cancel (0x85).</summary>
    public required Action Stop { get; init; }

    /// <summary>One jog of the selected step.</summary>
    public required Action<string> Step { get; init; }
}

/// <summary>
/// Turns the directions held on a gamepad into jog commands.
/// <list type="bullet">
/// <item>Step mode: one step each time the direction changes (like a key press; no repeat).</item>
/// <item>Continuous mode: the jog runs while the direction is held and is cancelled on release.
/// When the direction changes the old jog is cancelled and the new one starts only after the
/// machine has reported Idle again, so the new jog can't be lost to the cancel.</item>
/// </list>
/// Everything the driver needs from the outside is passed to <see cref="Update"/>, so it can be tested.
/// </summary>
public sealed class PadJogDriver
{
    private readonly JogTarget _target;
    private string? _held;          // direction held now
    private string? _waiting;       // continuous jog that waits for the machine to stop
    private bool _running;          // a continuous jog was started by us and not cancelled yet
    private int _cancelSeq = int.MinValue;

    public PadJogDriver(JogTarget target) => _target = target;

    /// <param name="allowed">The pad is enabled and the machine may be jogged.</param>
    /// <param name="continuous">The "continuous" setting.</param>
    /// <param name="idle">The last status report said Idle.</param>
    /// <param name="statusSeq">Counts the status reports received (to tell a report after the cancel from an older one).</param>
    public void Update(JogDirections directions, bool allowed, bool continuous, bool idle, int statusSeq)
    {
        string? what = allowed ? directions.ToJogWhat() : null;

        if (!continuous)
        {
            // The setting was switched while a jog ran: the view model has stopped it already.
            _running = false;
            _waiting = null;
        }

        if (what != _held)
        {
            bool wasRunning = _running;
            _held = what;
            _waiting = null;
            if (wasRunning)
            {
                _running = false;
                _cancelSeq = statusSeq;
                _target.Stop();
            }
            if (what != null)
            {
                if (!continuous)
                    _target.Step(what);
                else
                    _waiting = what;
            }
        }

        if (_waiting != null && _waiting == _held && idle && statusSeq > _cancelSeq)
        {
            string w = _waiting;
            _waiting = null;
            _running = true;
            _target.Start(w);
        }
    }

    /// <summary>The jog was stopped from outside (the Stop button): don't start it again until the direction changes.</summary>
    public void Cancelled()
    {
        _running = false;
        _waiting = null;
    }
}
