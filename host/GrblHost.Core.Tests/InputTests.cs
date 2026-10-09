using System.Runtime.InteropServices;
using GrblHost.Core.Input;

namespace GrblHost.Core.Tests;

/// <summary>Gamepad control: directions, jog descriptions, the buttons, the jog driver and the device readers.</summary>
public class InputTests
{
    // ---------------------------------------------------------------- directions and jog descriptions

    [Fact]
    public void OppositeDirectionsCancelAndZWins()
    {
        Assert.Equal(JogDirections.None, (JogDirections.XPlus | JogDirections.XMinus).Normalize());
        Assert.Equal(JogDirections.YPlus, (JogDirections.YPlus | JogDirections.XPlus | JogDirections.XMinus).Normalize());
        Assert.Equal(JogDirections.ZPlus, (JogDirections.XPlus | JogDirections.YMinus | JogDirections.ZPlus).Normalize());
    }

    [Fact]
    public void JogDescriptionsMatchWhatTheViewModelParses()
    {
        Assert.Null(JogDirections.None.ToJogWhat());
        Assert.Equal("X+", JogDirections.XPlus.ToJogWhat());
        Assert.Equal("X-", JogDirections.XMinus.ToJogWhat());
        Assert.Equal("Y+", JogDirections.YPlus.ToJogWhat());
        Assert.Equal("Y-", JogDirections.YMinus.ToJogWhat());
        Assert.Equal("Z+", JogDirections.ZPlus.ToJogWhat());
        Assert.Equal("Z-", (JogDirections.ZMinus | JogDirections.XPlus).ToJogWhat());
        Assert.Equal("XY++", (JogDirections.XPlus | JogDirections.YPlus).ToJogWhat());
        Assert.Equal("XY-+", (JogDirections.XMinus | JogDirections.YPlus).ToJogWhat());     // as keypad 7
        Assert.Equal("XY--", (JogDirections.XMinus | JogDirections.YMinus).ToJogWhat());    // as keypad 1
    }

    [Fact]
    public void StepListIsWalkedAndClamped()
    {
        double[] steps = { 0.01, 0.1, 1, 10, 50 };
        Assert.Equal(10, JogStepPicker.Next(steps, 1, +1));
        Assert.Equal(0.1, JogStepPicker.Next(steps, 1, -1));
        Assert.Equal(50, JogStepPicker.Next(steps, 50, +1));
        Assert.Equal(0.01, JogStepPicker.Next(steps, 0.01, -1));
        Assert.Equal(50, JogStepPicker.Next(steps, 12, +1));
    }

    // ---------------------------------------------------------------- gamepad mapping

    private static GamepadState Pad(float lx = 0, float ly = 0, float rx = 0, float ry = 0,
        float lt = 0, float rt = 0, GamepadButtons b = GamepadButtons.None) =>
        new(true, lx, ly, rx, ry, lt, rt, b);

    [Fact]
    public void StickInsideTheDeadzoneDoesNothing()
    {
        var m = new GamepadMapper();
        Assert.Equal(JogDirections.None, m.Directions(Pad(lx: 0.2f, ly: -0.2f)));
        Assert.Equal(JogDirections.None, m.Directions(default));           // not connected
    }

    [Fact]
    public void LeftStickGivesEightDirectionsWithSnappedAxes()
    {
        var m = new GamepadMapper();
        Assert.Equal(JogDirections.XPlus, m.Directions(Pad(lx: 1)));
        Assert.Equal(JogDirections.YMinus, m.Directions(Pad(ly: -0.9f)));
        // Almost straight right with a little up: still only X.
        Assert.Equal(JogDirections.XPlus, m.Directions(Pad(lx: 0.9f, ly: 0.2f)));
        Assert.Equal(JogDirections.XMinus | JogDirections.YPlus, m.Directions(Pad(lx: -0.7f, ly: 0.7f)));
    }

    [Fact]
    public void DPadAndRightStick()
    {
        var m = new GamepadMapper();
        Assert.Equal(JogDirections.XMinus, m.Directions(Pad(b: GamepadButtons.DPadLeft)));
        Assert.Equal(JogDirections.YPlus | JogDirections.XPlus,
            m.Directions(Pad(b: GamepadButtons.DPadUp | GamepadButtons.DPadRight)));
        Assert.Equal(JogDirections.ZPlus, m.Directions(Pad(ry: 0.8f)));
        Assert.Equal(JogDirections.ZMinus, m.Directions(Pad(ry: -0.8f, rx: 0.1f)));
        Assert.Equal(JogDirections.None, m.Directions(Pad(rx: 1)));           // right stick sideways: nothing
        // Z wins over the left stick.
        Assert.Equal(JogDirections.ZPlus, m.Directions(Pad(lx: 1, ry: 1)));
        // Stick and D-pad pushing opposite ways cancel.
        Assert.Equal(JogDirections.None, m.Directions(Pad(lx: 1, b: GamepadButtons.DPadLeft)));
    }

    [Fact]
    public void SafeButtonsActOncePerPress()
    {
        var m = new GamepadMapper();
        // A button held while the pad is connected does nothing.
        Assert.Equal(GamepadActions.None, m.Pressed(Pad(b: GamepadButtons.B)));
        Assert.Equal(GamepadActions.None, m.Pressed(Pad()));

        Assert.Equal(GamepadActions.Stop, m.Pressed(Pad(b: GamepadButtons.B)));
        Assert.Equal(GamepadActions.None, m.Pressed(Pad(b: GamepadButtons.B)));      // still held
        Assert.Equal(GamepadActions.None, m.Pressed(Pad()));
        Assert.Equal(GamepadActions.StepUp, m.Pressed(Pad(b: GamepadButtons.RightShoulder)));
        Assert.Equal(GamepadActions.None, m.Pressed(Pad()));
        Assert.Equal(GamepadActions.StepDown, m.Pressed(Pad(b: GamepadButtons.LeftShoulder)));
    }

    [Fact]
    public void ZeroAndHomeNeedTheShiftTrigger()
    {
        var m = new GamepadMapper();
        m.Pressed(Pad());
        // Without the shift trigger these buttons do nothing.
        foreach (var b in new[] { GamepadButtons.A, GamepadButtons.X, GamepadButtons.Y, GamepadButtons.Start, GamepadButtons.Back })
        {
            Assert.Equal(GamepadActions.None, m.Pressed(Pad(b: b)));
            Assert.Equal(GamepadActions.None, m.Pressed(Pad()));
        }
        // With it they act.
        Assert.Equal(GamepadActions.GoToZeroXY, m.Pressed(Pad(lt: 1, b: GamepadButtons.A)));
        Assert.Equal(GamepadActions.None, m.Pressed(Pad(lt: 1)));
        Assert.Equal(GamepadActions.ZeroXY, m.Pressed(Pad(lt: 1, b: GamepadButtons.X)));
        Assert.Equal(GamepadActions.None, m.Pressed(Pad(lt: 1)));
        Assert.Equal(GamepadActions.ZeroZ, m.Pressed(Pad(lt: 1, b: GamepadButtons.Y)));
        Assert.Equal(GamepadActions.None, m.Pressed(Pad(lt: 1)));
        Assert.Equal(GamepadActions.Home, m.Pressed(Pad(lt: 1, b: GamepadButtons.Start)));
        // Shift pressed after the button: not a press of the combination.
        Assert.Equal(GamepadActions.None, m.Pressed(Pad()));
        Assert.Equal(GamepadActions.None, m.Pressed(Pad(b: GamepadButtons.A)));
        Assert.Equal(GamepadActions.None, m.Pressed(Pad(lt: 1, b: GamepadButtons.A)));
    }

    [Fact]
    public void ReconnectingForgetsTheOldButtons()
    {
        var m = new GamepadMapper();
        m.Pressed(Pad());
        Assert.Equal(GamepadActions.Stop, m.Pressed(Pad(b: GamepadButtons.B)));
        Assert.Equal(GamepadActions.None, m.Pressed(default));                       // unplugged
        Assert.Equal(GamepadActions.None, m.Pressed(Pad(b: GamepadButtons.B)));      // plugged in with B held
    }

    // ---------------------------------------------------------------- jog driver

    private sealed class Rig
    {
        public readonly List<string> Log = new();
        public readonly PadJogDriver Driver;
        public int Seq;
        public Rig() => Driver = new PadJogDriver(new JogTarget
        {
            Start = w => Log.Add("start " + w),
            Stop = () => Log.Add("stop"),
            Step = w => Log.Add("step " + w),
        });
        public void Tick(JogDirections d, bool continuous, bool allowed = true, bool idle = true) =>
            Driver.Update(d, allowed, continuous, idle, Seq);
    }

    [Fact]
    public void StepModeStepsOncePerDirection()
    {
        var r = new Rig();
        for (int i = 0; i < 20; i++)
            r.Tick(JogDirections.XPlus, continuous: false);          // held for a long time: still one step
        r.Tick(JogDirections.None, continuous: false);
        r.Tick(JogDirections.XPlus | JogDirections.YPlus, continuous: false);
        r.Tick(JogDirections.XPlus, continuous: false);              // a changed direction is a new step
        Assert.Equal("step X+|step XY++|step X+", string.Join("|", r.Log));
    }

    [Fact]
    public void ContinuousJogRunsWhileHeldAndStopsOnRelease()
    {
        var r = new Rig();
        r.Tick(JogDirections.YMinus, continuous: true);
        r.Tick(JogDirections.YMinus, continuous: true);
        r.Tick(JogDirections.None, continuous: true);
        r.Tick(JogDirections.None, continuous: true);
        Assert.Equal("start Y-|stop", string.Join("|", r.Log));
    }

    [Fact]
    public void ChangingDirectionCancelsAndRestartsAfterTheMachineStopped()
    {
        var r = new Rig();
        r.Seq = 5;
        r.Tick(JogDirections.XPlus, continuous: true);
        Assert.Equal("start X+", string.Join("|", r.Log));

        // The direction changes while the machine still moves (status says Jog).
        r.Tick(JogDirections.XPlus | JogDirections.YPlus, continuous: true, idle: false);
        Assert.Equal("start X+|stop", string.Join("|", r.Log));

        // An old "Idle" report from before the cancel doesn't start the jog...
        r.Tick(JogDirections.XPlus | JogDirections.YPlus, continuous: true, idle: true);
        Assert.Equal("start X+|stop", string.Join("|", r.Log));

        // ...a report after the cancel does.
        r.Seq = 6;
        r.Tick(JogDirections.XPlus | JogDirections.YPlus, continuous: true, idle: false);
        Assert.Equal("start X+|stop", string.Join("|", r.Log));
        r.Tick(JogDirections.XPlus | JogDirections.YPlus, continuous: true, idle: true);
        Assert.Equal("start X+|stop|start XY++", string.Join("|", r.Log));
    }

    [Fact]
    public void ReleasedWhileWaitingStartsNothing()
    {
        var r = new Rig();
        r.Tick(JogDirections.XPlus, continuous: true);
        r.Tick(JogDirections.YPlus, continuous: true, idle: false);
        r.Tick(JogDirections.None, continuous: true, idle: false);
        r.Seq = 1;
        r.Tick(JogDirections.None, continuous: true, idle: true);
        Assert.Equal("start X+|stop", string.Join("|", r.Log));
    }

    [Fact]
    public void LosingPermissionStopsTheJog()
    {
        var r = new Rig();
        r.Tick(JogDirections.XPlus, continuous: true);
        r.Tick(JogDirections.XPlus, continuous: true, allowed: false);   // alarm, a job started, the window lost focus …
        r.Tick(JogDirections.XPlus, continuous: true, allowed: false);
        Assert.Equal("start X+|stop", string.Join("|", r.Log));
    }

    [Fact]
    public void StopButtonKeepsTheJogOffUntilTheDirectionChanges()
    {
        var r = new Rig();
        r.Tick(JogDirections.XPlus, continuous: true);
        r.Driver.Cancelled();                                            // B was pressed
        r.Tick(JogDirections.XPlus, continuous: true);
        r.Tick(JogDirections.XPlus, continuous: true);
        Assert.Equal("start X+", string.Join("|", r.Log));
        r.Tick(JogDirections.None, continuous: true);                    // nothing to cancel any more
        r.Tick(JogDirections.XMinus, continuous: true);
        Assert.Equal("start X+|start X-", string.Join("|", r.Log));
    }

    // ---------------------------------------------------------------- XInput

    [Fact]
    public void XInputStructsHaveTheNativeSize()
    {
        Assert.Equal(12, Marshal.SizeOf<XInputPad.XInputGamepad>());
        Assert.Equal(16, Marshal.SizeOf<XInputPad.XInputState>());
    }

    [Fact]
    public void XInputValuesAreConverted()
    {
        var g = new XInputPad.XInputGamepad
        {
            wButtons = 0x1000 | 0x0004,                 // A + D-pad left
            bLeftTrigger = 255,
            bRightTrigger = 0,
            sThumbLX = short.MaxValue,
            sThumbLY = short.MinValue,
            sThumbRX = 0,
            sThumbRY = 16384,
        };
        var s = XInputPad.Convert(g);
        Assert.True(s.Connected);
        Assert.Equal(GamepadButtons.A | GamepadButtons.DPadLeft, s.Buttons);
        Assert.Equal(1f, s.LeftX);
        Assert.Equal(-1f, s.LeftY);
        Assert.Equal(0.5f, s.RightY, 2);
        Assert.Equal(1f, s.LeftTrigger);
        Assert.Equal(0f, s.RightTrigger);
    }

    [Fact]
    public void XInputFindsTheSlotAndScansRarely()
    {
        int calls = 0;
        bool plugged = false;
        var pad = new XInputPad(i =>
        {
            calls++;
            return (plugged && i == 2 ? 0u : 1167u, default);
        });

        Assert.False(pad.Poll(0).Connected);
        int first = calls;
        Assert.Equal(4, first);                                  // all four slots once
        Assert.False(pad.Poll(500).Connected);
        Assert.Equal(first, calls);                              // too early for another scan
        plugged = true;
        Assert.False(pad.Poll(900).Connected);
        Assert.True(pad.Poll(1100).Connected);
        Assert.Equal("Xbox / XInput #3", pad.DeviceName);

        int before = calls;
        Assert.True(pad.Poll(1120).Connected);
        Assert.Equal(before + 1, calls);                         // a known slot is read directly

        plugged = false;
        Assert.False(pad.Poll(1140).Connected);
        Assert.Null(pad.DeviceName);
    }

    // ---------------------------------------------------------------- generic joystick

    [Fact]
    public void JoystickStructHasTheNativeSize() =>
        Assert.Equal(52, Marshal.SizeOf<WinMmJoystick.JoyInfoEx>());

    private static WinMmJoystick.JoyInfoEx Joy(uint x = 32768, uint y = 32768, uint r = 32768, uint pov = 0xFFFF, uint buttons = 0) =>
        new() { dwXpos = x, dwYpos = y, dwRpos = r, dwPOV = pov, dwButtons = buttons };

    [Fact]
    public void JoystickAxesAreCenteredAndUpIsPositive()
    {
        var center = WinMmJoystick.Convert(Joy());
        Assert.Equal(0f, center.LeftX, 2);
        Assert.Equal(0f, center.LeftY, 2);

        var upRight = WinMmJoystick.Convert(Joy(x: 65535, y: 0, r: 0));
        Assert.Equal(1f, upRight.LeftX, 2);
        Assert.Equal(1f, upRight.LeftY, 2);                       // pushed up: small raw value
        Assert.Equal(1f, upRight.RightY, 2);
    }

    [Fact]
    public void JoystickHatAndButtons()
    {
        Assert.Equal(GamepadButtons.None, WinMmJoystick.Convert(Joy(pov: 0xFFFF)).Buttons);
        Assert.Equal(GamepadButtons.None, WinMmJoystick.Convert(Joy(pov: 0xFFFFFFFF)).Buttons);
        Assert.Equal(GamepadButtons.DPadUp, WinMmJoystick.Convert(Joy(pov: 0)).Buttons);
        Assert.Equal(GamepadButtons.DPadRight, WinMmJoystick.Convert(Joy(pov: 9000)).Buttons);
        Assert.Equal(GamepadButtons.DPadDown, WinMmJoystick.Convert(Joy(pov: 18000)).Buttons);
        Assert.Equal(GamepadButtons.DPadLeft, WinMmJoystick.Convert(Joy(pov: 27000)).Buttons);
        Assert.Equal(GamepadButtons.DPadUp | GamepadButtons.DPadRight, WinMmJoystick.Convert(Joy(pov: 4500)).Buttons);
        Assert.Equal(GamepadButtons.DPadUp | GamepadButtons.DPadLeft, WinMmJoystick.Convert(Joy(pov: 31500)).Buttons);

        var b = WinMmJoystick.Convert(Joy(buttons: 0b1000_0101)).Buttons;      // buttons 1, 3 and 8
        Assert.Equal(GamepadButtons.A | GamepadButtons.X | GamepadButtons.Start, b);
    }

    [Fact]
    public void JoystickIsFoundByScanning()
    {
        var pad = new WinMmJoystick(id => (id == 5 ? 0u : 167u, Joy(buttons: 1)));
        var s = pad.Poll(0);
        Assert.True(s.Connected);
        Assert.Equal("Joystick #6", pad.DeviceName);
        Assert.Equal(GamepadButtons.A, s.Buttons);
    }

    // ---------------------------------------------------------------- choosing a device

    private sealed class FakePad : IGamepadSource
    {
        public bool Connected;
        public int Polls;
        public string Name = "";
        public string? DeviceName => Connected ? Name : null;

        public GamepadState Poll(double nowMs)
        {
            Polls++;
            return Connected ? new GamepadState(true, 0, 0, 0, 0, 0, 0, GamepadButtons.None) : default;
        }
    }

    [Fact]
    public void PollerPrefersTheFirstSourceAndFallsBack()
    {
        var xi = new FakePad { Name = "xinput" };
        var wm = new FakePad { Name = "winmm", Connected = true };
        var poller = new GamepadPoller(xi, wm);

        Assert.True(poller.Poll(0).Connected);
        Assert.Equal("winmm", poller.DeviceName);

        // An XInput pad appears, but the joystick in use is kept until it goes away.
        xi.Connected = true;
        Assert.True(poller.Poll(10).Connected);
        Assert.Equal("winmm", poller.DeviceName);

        wm.Connected = false;
        Assert.True(poller.Poll(20).Connected);
        Assert.Equal("xinput", poller.DeviceName);

        xi.Connected = false;
        Assert.False(poller.Poll(30).Connected);
        Assert.Null(poller.DeviceName);
    }
}
