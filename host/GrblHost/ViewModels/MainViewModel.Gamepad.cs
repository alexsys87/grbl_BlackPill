using GrblHost.Core.Input;
using GrblHost.Services;

namespace GrblHost.ViewModels;

/// <summary>
/// Jogging with a gamepad or joystick: the setting, the state shown in the Axes tab and what
/// <see cref="GamepadController"/> calls. Sticks and the D-pad use the same jogs as the buttons
/// and the keyboard (step or continuous, as set), so the jog cancel (0x85) stops them.
/// </summary>
public sealed partial class MainViewModel
{
    private PadJogDriver? _padDriver;

    /// <summary>Counts the status reports received: a report after a jog cancel shows the machine really stopped.</summary>
    private int _statusSeq;

    /// <summary>Jog with a gamepad or joystick while the window is active.</summary>
    public bool GamepadJog
    {
        get => _settings.GamepadJog;
        set
        {
            if (_settings.GamepadJog == value)
                return;
            _settings.GamepadJog = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(GamepadStatus));
            if (!value)
                PadDirections(JogDirections.None);
        }
    }

    private string? _gamepadName;

    /// <summary>"Gamepad: Xbox / XInput #1", "Gamepad: not found" or "Gamepad: off".</summary>
    public string GamepadStatus =>
        !GamepadJog ? Loc.T("S.Pad.Off") :
        _gamepadName == null ? Loc.T("S.Pad.None") :
        Loc.F("S.Pad.Connected", _gamepadName);

    /// <summary>Name of the connected gamepad, null if there is none.</summary>
    public void SetGamepadName(string? name)
    {
        if (_gamepadName == name)
            return;
        _gamepadName = name;
        OnPropertyChanged(nameof(GamepadStatus));
    }

    /// <summary>The next (+1) or previous (-1) step of the list.</summary>
    public void ChangeJogStep(int delta) => JogStep = JogStepPicker.Next(JogSteps, JogStep, delta);

    /// <summary>The directions held on the pad now (called every few milliseconds; None when it is let go).</summary>
    public void PadDirections(JogDirections directions)
    {
        _padDriver ??= new PadJogDriver(new JogTarget
        {
            Start = JogStart,
            Stop = JogStop,
            Step = what => JogBy(what, null),
        });
        _padDriver.Update(directions, GamepadJog && CanJog, JogContinuous, MachineState == MachineState.Idle, _statusSeq);
    }

    /// <summary>Buttons pressed on the pad. Stop and step work always, the rest only when the machine may be controlled.</summary>
    public void PadActions(GamepadActions a)
    {
        if (!GamepadJog)
            return;
        if (a.HasFlag(GamepadActions.StepDown))
            ChangeJogStep(-1);
        if (a.HasFlag(GamepadActions.StepUp))
            ChangeJogStep(+1);
        if (a.HasFlag(GamepadActions.Stop))
        {
            JogStop();
            _conn.JogCancel();
            _padDriver?.Cancelled();
        }
        if (a.HasFlag(GamepadActions.GoToZeroXY) && GoToZeroCommand.CanExecute("XY"))
            GoToZeroCommand.Execute("XY");
        if (a.HasFlag(GamepadActions.ZeroXY) && ZeroCommand.CanExecute("XY"))
            ZeroCommand.Execute("XY");
        if (a.HasFlag(GamepadActions.ZeroZ) && ZeroCommand.CanExecute("Z"))
            ZeroCommand.Execute("Z");
        if (a.HasFlag(GamepadActions.Home) && HomeCommand.CanExecute(null))
            HomeCommand.Execute(null);
    }
}
