using GrblHost.Core.GCode;
using GrblHost.Core.Machine;

namespace GrblHost.Core.Tests;

/// <summary>
/// Jogging must never leave the machine moving by itself or the link stuck. The controller drops the
/// input it has not read when it gets a jog cancel (0x85) and answers none of it; a jog cancel outside
/// a jog would take lines out of a job; a lost release leaves a jog running to the end of the travel.
/// </summary>
public class JogSafetyTests
{
    /// <summary>A controller that only does what the test says: lines and bytes written are recorded, answers are fed by hand.</summary>
    private sealed class ScriptedPort : IGrblTransport
    {
        private readonly object _lock = new();
        private readonly List<string> _lines = new();
        private readonly List<byte> _raw = new();
        private int _answered;

        public string Name => "scripted";
        public bool IsOpen { get; private set; }
        public event Action<string>? LineReceived;
        public event Action<Exception>? Faulted { add { } remove { } }

        public void Open() => IsOpen = true;
        public void Close() => IsOpen = false;
        public void Dispose() => Close();
        public void WriteLine(string line) { lock (_lock) _lines.Add(line); }
        public void WriteRaw(byte value)
        {
            lock (_lock)
                _raw.Add(value);
            // Like the controller: a status request is answered (a link without any answer is dropped).
            if (value == (byte)'?')
                Task.Run(() => Feed(Status));
        }

        public void Feed(string line) => LineReceived?.Invoke(line);

        /// <summary>What the next status request is answered with.</summary>
        public volatile string Status = "<Idle|MPos:0.000,0.000,0.000|Bf:100,1023|FS:0,0>";

        public string[] Lines { get { lock (_lock) return _lines.ToArray(); } }
        public int Cancels { get { lock (_lock) return _raw.Count(b => b == 0x85); } }

        /// <summary>Answer every line written so far that has not been answered.</summary>
        public void AnswerAll()
        {
            int n;
            lock (_lock)
            {
                n = _lines.Count - _answered;
                _answered = _lines.Count;
            }
            for (int i = 0; i < n; i++)
                Feed("ok");
        }

        /// <summary>The controller never saw the lines written so far (it dropped them): they get no answer.</summary>
        public void ForgetAll()
        {
            lock (_lock)
                _answered = _lines.Count;
        }
    }

    private static async Task<(GrblConnection c, ScriptedPort p)> OnlineAsync()
    {
        var p = new ScriptedPort();
        var c = new GrblConnection { AutoReconnect = false };
        c.Connect(p);
        await ProtocolTests.WaitFor(() => c.State == ConnectionState.Online);
        // The start-up queries ($I, $$ …): answer them.
        await ProtocolTests.WaitFor(() => p.Lines.Length >= 1);
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(20);
            p.AnswerAll();
        }
        return (c, p);
    }

    private const string JogLine = "G91 G21 X300 F1000";

    // ---------------------------------------------------------------- the dropped lines

    [Fact]
    public async Task ALineTheControllerDroppedDoesNotStealTheNextAnswer()
    {
        var (c, p) = await OnlineAsync();
        using (c)
        {
            c.Jog(JogLine);
            await ProtocolTests.WaitFor(() => p.Lines.Any(l => l.StartsWith("$J=")));
            c.JogCancel();
            Assert.Equal(1, p.Cancels);
            p.ForgetAll();                               // the controller dropped the unread jog line, no answer comes

            var done = new List<string>();
            c.CommandCompleted += (t, k, code) => { lock (done) done.Add(t); };
            await Task.Delay(150);                       // nothing is sent right after a cancel
            c.Send("G4 P0");
            await ProtocolTests.WaitFor(() => p.Lines.Contains("G4 P0"));
            p.AnswerAll();
            await ProtocolTests.WaitFor(() => { lock (done) return done.Contains("G4 P0"); });
            lock (done)
                Assert.DoesNotContain(done, t => t.StartsWith("$J="));
        }
    }

    [Fact]
    public async Task ManyCancelledJogsDoNotBlockTheLink()
    {
        var (c, p) = await OnlineAsync();
        using (c)
        {
            // Without the fix every dropped line stayed "in flight" for ever; 1023 characters of them stopped everything.
            for (int i = 0; i < 80; i++)
            {
                c.Jog(JogLine);
                await ProtocolTests.WaitFor(() => p.Lines.Count(l => l.StartsWith("$J=")) > i);
                c.JogCancel();
                p.ForgetAll();
                await Task.Delay(70);
            }
            c.Send("G4 P0");
            await ProtocolTests.WaitFor(() => p.Lines.Contains("G4 P0"));
        }
    }

    [Fact]
    public async Task AJogLineGoesAloneAndNothingFollowsUntilItIsAnswered()
    {
        var (c, p) = await OnlineAsync();
        using (c)
        {
            c.Jog(JogLine);
            c.Send("G4 P0");
            c.Jog("G91 G21 X10 F1000");
            await ProtocolTests.WaitFor(() => p.Lines.Any(l => l.StartsWith("$J=")));
            await Task.Delay(150);
            Assert.Equal(1, p.Lines.Count(l => l.StartsWith("$J=")));
            Assert.DoesNotContain("G4 P0", p.Lines);

            p.AnswerAll();                               // the first jog is answered: the next line may go
            await ProtocolTests.WaitFor(() => p.Lines.Contains("G4 P0"));
            await Task.Delay(100);
            Assert.Equal(1, p.Lines.Count(l => l.StartsWith("$J=")));      // the second jog waits for G4

            p.AnswerAll();
            await ProtocolTests.WaitFor(() => p.Lines.Count(l => l.StartsWith("$J=")) == 2);
        }
    }

    [Fact]
    public async Task AJogLineThatWaitedIsNotSentAfterItsCancel()
    {
        var (c, p) = await OnlineAsync();
        using (c)
        {
            c.Send("G4 P1");                             // not answered yet
            c.Jog(JogLine);                              // waits behind it
            await ProtocolTests.WaitFor(() => p.Lines.Contains("G4 P1"));
            c.JogCancel();                               // the button was let go
            Assert.Equal(1, p.Cancels);
            p.AnswerAll();
            await Task.Delay(300);
            Assert.DoesNotContain(p.Lines, l => l.StartsWith("$J="));
        }
    }

    // ---------------------------------------------------------------- a cancel outside a jog

    [Fact]
    public async Task NoJogCancelWhenNothingJogs()
    {
        var (c, p) = await OnlineAsync();
        using (c)
        {
            c.JogCancel();
            await Task.Delay(50);
            Assert.Equal(0, p.Cancels);
        }
    }

    [Fact]
    public async Task JogCancelIsSentWhenTheMachineReportsJog()
    {
        var (c, p) = await OnlineAsync();
        using (c)
        {
            p.Status = "<Jog|MPos:5.000,0.000,0.000|Bf:99,1023|FS:1000,0>";
            await ProtocolTests.WaitFor(() => c.Snapshot.State == MachineState.Jog);
            c.JogCancel();
            Assert.Equal(1, p.Cancels);
        }
    }

    [Fact]
    public async Task JogCancelNeverTouchesAJob()
    {
        var (c, p) = await OnlineAsync();
        using (c)
        {
            var job = Enumerable.Range(1, 50).Select(i => new JobLine($"G1 X{i} F100", i - 1)).ToList();
            c.StartJob(job);
            await ProtocolTests.WaitFor(() => p.Lines.Any(l => l.StartsWith("G1 X")));
            p.Status = "<Run|MPos:1.000,0.000,0.000|Bf:90,900|FS:100,0>";
            await ProtocolTests.WaitFor(() => c.Snapshot.State == MachineState.Run);
            c.JogCancel();                               // Esc, the stop button, B on the gamepad
            c.JogCancel();
            await Task.Delay(50);
            Assert.Equal(0, p.Cancels);                  // 0x85 would drop the unread job lines
        }
    }

    // ---------------------------------------------------------------- planner and watchdog

    [Fact]
    public async Task PlannerSizeComesFromTheOptions()
    {
        var (c, p) = await OnlineAsync();
        using (c)
        {
            Assert.Equal(0, c.PlannerBlocks);
            p.Feed("[OPT:V,100,1023,3,0]");
            await ProtocolTests.WaitFor(() => c.PlannerBlocks == 100);
            Assert.True(c.Snapshot.PlannerKnown);
            Assert.Equal(100, c.Snapshot.PlannerFree);
        }
    }

    [Fact]
    public void PlannerIsUnknownUntilTheStatusSaysSo()
    {
        var snap = new MachineSnapshot();
        Assert.False(snap.PlannerKnown);
        snap.Apply(GrblStatus.Parse("<Idle|MPos:0.000,0.000,0.000|FS:0,0>")!);
        Assert.False(snap.PlannerKnown);
        snap.Apply(GrblStatus.Parse("<Idle|MPos:0.000,0.000,0.000|Bf:35,127|FS:0,0>")!);
        Assert.True(snap.PlannerKnown);
        Assert.Equal(35, snap.PlannerFree);
    }

    [Fact]
    public void WatchdogStopsAJogNobodyHolds()
    {
        var w = new JogWatchdog { GraceMs = 1000, RepeatMs = 1000 };
        Assert.False(w.Update(0, machineJogging: true, jogHeld: false, continuousMode: true));      // the first report only starts the clock
        Assert.False(w.Update(900, true, false, true));
        Assert.True(w.Update(1000, true, false, true));
        Assert.False(w.Update(1500, true, false, true));                                             // not again at once
        Assert.True(w.Update(2000, true, false, true));
    }

    [Fact]
    public void WatchdogLeavesAHeldJogAlone()
    {
        var w = new JogWatchdog { GraceMs = 1000, RepeatMs = 1000 };
        for (double t = 0; t < 20000; t += 200)
            Assert.False(w.Update(t, machineJogging: true, jogHeld: true, continuousMode: true));
        // Let go: the machine needs a moment to stop, then the grace period applies from there.
        Assert.False(w.Update(20000, true, false, true));
        Assert.False(w.Update(20500, true, false, true));
        Assert.True(w.Update(21000, true, false, true));
    }

    [Fact]
    public void WatchdogIgnoresStepJogsAndAMachineThatStopped()
    {
        var w = new JogWatchdog { GraceMs = 1000, RepeatMs = 1000 };
        for (double t = 0; t < 10000; t += 200)
            Assert.False(w.Update(t, machineJogging: true, jogHeld: false, continuousMode: false));  // steps end by themselves
        Assert.False(w.Update(10000, true, false, true));
        Assert.False(w.Update(10500, false, false, true));                                            // it stopped: the clock is reset
        Assert.False(w.Update(10600, true, false, true));
        Assert.False(w.Update(11500, true, false, true));
        Assert.True(w.Update(11600, true, false, true));
    }
}
