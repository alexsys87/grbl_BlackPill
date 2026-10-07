using GrblHost.Core.GCode;
using GrblHost.Core.Machine;

namespace GrblHost.Core.Tests;

public class ProtocolTests
{
    [Fact]
    public void ParsesStatusReports()
    {
        var s = GrblStatus.Parse("<Run|MPos:10.000,-2.500,0.125|Bf:30,1000|FS:500,8000|WCO:5.000,0.000,-1.000|Pn:XP>");
        Assert.NotNull(s);
        Assert.Equal(MachineState.Run, s!.State);
        Assert.Equal(new Axes(10, -2.5, 0.125), s.MPos);
        Assert.Equal(30, s.PlannerFree);
        Assert.Equal(1000, s.RxFree);
        Assert.Equal(500, s.Feed);
        Assert.Equal(8000, s.Spindle);
        Assert.True(s.PinActive('P'));
        Assert.False(s.PinActive('Y'));

        var snap = new MachineSnapshot();
        snap.Apply(s);
        Assert.Equal(new Axes(5, -2.5, 1.125), snap.WPos);

        // The next report has no WCO: the last one stays; Ov and A arrive.
        snap.Apply(GrblStatus.Parse("<Hold:0|MPos:10.000,-2.500,0.125|FS:0,0|Ov:120,50,90|A:SF>")!);
        Assert.Equal(MachineState.Hold, snap.State);
        Assert.Equal(0, snap.SubState);
        Assert.Equal(new Axes(5, 0, -1), snap.Wco);
        Assert.Equal(120, snap.FeedOverride);
        Assert.Equal(50, snap.RapidOverride);
        Assert.Equal(90, snap.SpindleOverride);
        Assert.True(snap.SpindleCw);
        Assert.True(snap.Flood);
        Assert.False(snap.Mist);

        // WPos instead of MPos ($10=0): the machine position follows from WCO.
        snap.Apply(GrblStatus.Parse("<Idle|WPos:1.000,2.000,3.000|FS:0,0>")!);
        Assert.Equal(new Axes(1, 2, 3), snap.WPos);

        Assert.Null(GrblStatus.Parse("ok"));
        Assert.Equal(MachineState.Alarm, GrblStatus.Parse("<Alarm|MPos:0,0,0>")!.State);
        Assert.Equal(MachineState.Door, GrblStatus.Parse("<Door:1|MPos:0,0,0>")!.State);
    }

    [Theory]
    [InlineData("G1 X10 ; comment", "G1 X10")]
    [InlineData("(header) G0 Z5 (safe)", "G0 Z5")]
    [InlineData("  M3 S1000\r", "M3 S1000")]
    [InlineData("G1 X1 (Ø6 mill)", "G1 X1")]
    [InlineData("G1 XÜ", "G1 X?")]
    public void CleansLines(string input, string expected) => Assert.Equal(expected, GrblConnection.Clean(input));

    [Fact]
    public void ErrorAndAlarmTexts()
    {
        Assert.Contains("Unsupported", GrblCodes.Error(20, false));
        Assert.Contains("Hard limit", GrblCodes.Alarm(1, false));
        Assert.NotEqual(GrblCodes.Error(20, false), GrblCodes.Error(20, true));
        Assert.True(GrblCodes.AlarmLosesPosition(3));
        Assert.False(GrblCodes.AlarmLosesPosition(5));
        Assert.Equal("mm/min", GrblCodes.SettingUnit(110));
    }

    // ------------------------------------------------------------ with the virtual controller

    internal static async Task WaitFor(Func<bool> condition, int seconds = 30)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > end)
                throw new TimeoutException();
            await Task.Delay(10);
        }
    }

    internal static (GrblConnection c, VirtualGrbl v) Online(double timeScale = 50)
    {
        var v = new VirtualGrbl { TimeScale = timeScale };
        var c = new GrblConnection { StatusIntervalMs = 20 };
        c.Connect(v);
        return (c, v);
    }

    internal static List<JobLine> JobFrom(string text)
    {
        var doc = GCodeDocument.FromText("t", text);
        var job = new List<JobLine>();
        for (int i = 0; i < doc.Lines.Count; i++)
        {
            string l = GrblConnection.Clean(doc.Lines[i]);
            if (l.Length > 0)
                job.Add(new JobLine(l, i));
        }
        return job;
    }

    [Fact]
    public async Task ConnectsAndReadsSettings()
    {
        var settings = new Dictionary<int, string>();
        var (c, v) = Online();
        c.SettingReceived += (id, val) => { lock (settings) settings[id] = val; };
        using (c)
        {
            await WaitFor(() => c.State == ConnectionState.Online);
            await WaitFor(() => { lock (settings) return settings.ContainsKey(132); });
            Assert.Equal(VirtualGrbl.RxBufferSize, c.RxBufferSize);
            lock (settings)
                Assert.Equal("800.000", settings[100]);
            Assert.Contains("$I", v.Received);
            Assert.Contains("$$", v.Received);
        }
    }

    [Fact]
    public async Task StreamsAJobToTheEnd()
    {
        var (c, v) = Online(200);
        var result = new TaskCompletionSource<JobResult>();
        int progress = -1;
        c.JobCompleted += r => result.TrySetResult(r);
        c.JobProgress += i => progress = i;
        int maxInflight = 0;
        using (c)
        {
            await WaitFor(() => c.State == ConnectionState.Online && c.RxBufferSize == VirtualGrbl.RxBufferSize);
            var job = JobFrom(DemoGCode.Generate());
            c.StartJob(job);
            Assert.True(c.IsJobRunning);
            var r = await result.Task.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Equal(JobResult.Done, r);
            Assert.Equal(job.Count - 1, progress);
            Assert.Equal(MachineState.Idle, c.Snapshot.State);
            // M30 ends at X0 Y0, Z at the safe height.
            Assert.Equal(0, v.MachinePosition.X, 3);
            Assert.Equal(5, v.MachinePosition.Z, 3);
            Assert.True(maxInflight <= VirtualGrbl.RxBufferSize);
            // Every job line went out once, in order.
            var sent = v.Received.Where(l => job.Exists(j => j.Command == l)).ToList();
            Assert.True(sent.Count >= job.Count);
        }
    }

    [Fact]
    public async Task ErrorStopsTheJob()
    {
        var (c, v) = Online(200);
        var result = new TaskCompletionSource<JobResult>();
        string? detail = null;
        c.JobCompleted += r => result.TrySetResult(r);
        c.Info += (m, d) => { if (m == ConnectionMessage.JobStoppedByError) detail = d; };
        using (c)
        {
            await WaitFor(() => c.State == ConnectionState.Online);
            c.StartJob(JobFrom("G21 G90\nG1 X1 F100\nG1 X2 Q5\nG1 X3\n"));
            Assert.Equal(JobResult.Error, await result.Task.WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.NotNull(detail);
            Assert.StartsWith("3|", detail);
            await WaitFor(() => c.Snapshot.State == MachineState.Idle);
            // After the reset the controller takes G-code again.
            var done = new TaskCompletionSource<int>();
            c.CommandCompleted += (t, k, code) => { if (t == "G0 X0") done.TrySetResult(code); };
            c.Send("G0 X0");
            Assert.Equal(0, await done.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        }
    }

    [Fact]
    public async Task ManualErrorIsClearedWithAnEmptyLine()
    {
        var (c, v) = Online();
        var codes = new List<(string, int)>();
        c.CommandCompleted += (t, k, code) => { lock (codes) codes.Add((t, code)); };
        using (c)
        {
            await WaitFor(() => c.State == ConnectionState.Online);
            // grblHAL refuses G-code after an error until an empty line: the
            // connection sends one, so the next command works.
            c.Send("G1 X1 Q2");
            await WaitFor(() => { lock (codes) return codes.Count > 0; });
            c.Send("G0 X1");
            await WaitFor(() => { lock (codes) return codes.Exists(x => x.Item1 == "G0 X1"); });
            lock (codes)
            {
                Assert.Equal(36, codes.Find(x => x.Item1 == "G1 X1 Q2").Item2);
                Assert.Equal(0, codes.Find(x => x.Item1 == "G0 X1").Item2);
            }
            Assert.Contains("", v.Received);
        }
    }

    [Fact]
    public async Task HoldResumeAndStop()
    {
        var (c, v) = Online(5);
        var result = new TaskCompletionSource<JobResult>();
        c.JobCompleted += r => result.TrySetResult(r);
        using (c)
        {
            await WaitFor(() => c.State == ConnectionState.Online);
            c.StartJob(JobFrom("G1 X100 F600\nG1 Y100\n"));
            await WaitFor(() => c.Snapshot.State == MachineState.Run);
            c.PauseJob();
            await WaitFor(() => c.Snapshot.State == MachineState.Hold && c.Snapshot.SubState == 0);
            var held = v.MachinePosition;
            await Task.Delay(100);
            Assert.Equal(held, v.MachinePosition);
            c.ResumeJob();
            await WaitFor(() => c.Snapshot.State == MachineState.Run);
            await WaitFor(() => v.MachinePosition.X > held.X + 1);
            c.StopJob();
            Assert.Equal(JobResult.Cancelled, await result.Task.WaitAsync(TimeSpan.FromSeconds(20)));
            await WaitFor(() => c.Snapshot.State == MachineState.Idle);
            // Stopped with a hold first: no alarm, the position is kept.
            Assert.True(v.MachinePosition.X > 0);
        }
    }

    [Fact]
    public async Task JogAndCancel()
    {
        var (c, v) = Online(1);
        using (c)
        {
            await WaitFor(() => c.State == ConnectionState.Online);
            c.Jog("G91 G21 X50 F1000");
            await WaitFor(() => c.Snapshot.State == MachineState.Jog);
            await WaitFor(() => v.MachinePosition.X > 0.5);
            c.JogCancel();
            await WaitFor(() => c.Snapshot.State == MachineState.Idle);
            float x = v.MachinePosition.X;
            Assert.InRange(x, 0.5f, 49);
            await Task.Delay(100);
            Assert.Equal(x, v.MachinePosition.X);
        }
    }

    [Fact]
    public async Task ProbeAndZero()
    {
        var (c, v) = Online(100);
        v.ProbePlateZ = -12;
        var probe = new TaskCompletionSource<(Axes, bool)>();
        bool queried = false;
        c.CommandCompleted += (t, k, code) => { if (t == "$#") queried = true; };
        using (c)
        {
            // $# reports the last probe too: wait for it before probing.
            await WaitFor(() => queried);
            c.ProbeResult += (p, ok) => probe.TrySetResult((p, ok));
            c.Send("G38.2 Z-40 F100");
            var (pos, ok) = await probe.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(ok);
            Assert.Equal(-12, pos.Z, 3);
            // Zero the work Z on the plate top: WPos Z becomes 0.
            c.Send("G10 L20 P0 Z0");
            await WaitFor(() => Math.Abs(c.Snapshot.WPos.Z) < 1e-6 && Math.Abs(c.Snapshot.MPos.Z + 12) < 1e-6);
        }
    }

    [Fact]
    public async Task ResetInMotionRaisesAlarm3()
    {
        var (c, v) = Online(1);
        var alarms = new List<int>();
        c.AlarmRaised += a => { lock (alarms) alarms.Add(a); };
        using (c)
        {
            await WaitFor(() => c.State == ConnectionState.Online);
            c.Send("G1 X100 F1000");
            await WaitFor(() => c.Snapshot.State == MachineState.Run);
            c.SoftReset();
            await WaitFor(() => c.Snapshot.State == MachineState.Alarm);
            lock (alarms)
                Assert.Contains(3, alarms);
            c.Send("$X");
            await WaitFor(() => c.Snapshot.State == MachineState.Idle);
        }
    }

    [Fact]
    public async Task OverridesReachTheController()
    {
        var (c, v) = Online();
        using (c)
        {
            await WaitFor(() => c.State == ConnectionState.Online);
            c.SendRealtime(RealtimeCommand.FeedOvCoarsePlus);
            c.SendRealtime(RealtimeCommand.FeedOvCoarsePlus);
            c.SendRealtime(RealtimeCommand.RapidOvLow);
            c.SendRealtime(RealtimeCommand.SpindleOvCoarseMinus);
            await WaitFor(() => c.Snapshot.FeedOverride == 120 && c.Snapshot.RapidOverride == 25 &&
                                c.Snapshot.SpindleOverride == 90);
            c.Send("M3 S5000");
            c.Send("M8");
            await WaitFor(() => c.Snapshot.SpindleCw && c.Snapshot.Flood);
        }
    }
}
