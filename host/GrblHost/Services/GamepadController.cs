using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using GrblHost.Core.Input;
using GrblHost.ViewModels;

namespace GrblHost.Services;

/// <summary>
/// Reads a gamepad or joystick (XInput first, then generic USB joysticks) every 20 ms and hands
/// the held directions and pressed buttons to the view model. Acts only while the program
/// window is active; the buttons are read in the background too, so a press made there is not
/// carried out later.
/// </summary>
public sealed class GamepadController : IDisposable
{
    private readonly MainViewModel _vm;
    private readonly Window _window;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly GamepadMapper _mapper = new();
    private GamepadPoller? _poller;

    public GamepadController(MainViewModel vm, Window window)
    {
        _vm = vm;
        _window = window;
        _timer = new DispatcherTimer(DispatcherPriority.Input, window.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(20),
        };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    public void Dispose() => _timer.Stop();

    private void Tick()
    {
        if (!_vm.GamepadJog)
        {
            // Nothing is read while it is off; a jog that was running is stopped.
            _mapper.Reset();
            _vm.SetGamepadName(null);
            _vm.PadDirections(JogDirections.None);
            return;
        }

        _poller ??= new GamepadPoller();
        var pad = _poller.Poll(_clock.Elapsed.TotalMilliseconds);
        _vm.SetGamepadName(pad.Connected ? _poller.DeviceName : null);
        var pressed = _mapper.Pressed(pad);
        bool active = _window.IsActive;
        _vm.PadDirections(active ? _mapper.Directions(pad) : JogDirections.None);
        if (active && pressed != GamepadActions.None)
            _vm.PadActions(pressed);
    }
}
