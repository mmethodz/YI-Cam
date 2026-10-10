using YiLocal.Core;

static class AlarmChecks
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static void Reject(Action action)
    {
        try { action(); } catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return; }
        throw new Exception("Invalid alarm configuration was accepted.");
    }
    public static void Run()
    {
        Reject(() => new MotionAlarmGate(new()).Arm(0));
        Reject(() => new AlarmOptions(ThresholdPercent: double.NaN).Validate());
        Reject(() => new AlarmOptions(DurationSeconds: 0).Validate());
        Reject(() => new AlarmOptions(EntryGraceSeconds: -1).Validate());
        Reject(() => new AlarmOptions(ExitDelaySeconds: 3601).Validate());
        Reject(() => new AlarmOptions(Output: (AlarmOutput)99).Validate());
        var gate = new MotionAlarmGate(new(true, 10, 30, 15, 60));
        Check(gate.Phase == AlarmPhase.Home, "Alarm did not start in Home.");
        gate.Observe(100, 0); gate.Observe(100, 200);
        Check(!gate.Armed, "Home triggered an alarm.");
        gate.Arm(0); gate.Observe(100, 59000); gate.Observe(100, 59200);
        Check(gate.Phase == AlarmPhase.ExitDelay, "Motion bypassed the exit delay.");
        gate.Tick(60000); gate.Observe(3, 60000); gate.Observe(3, 60200);
        Check(gate.Phase == AlarmPhase.Watching, "Recording threshold leaked into the alarm threshold.");
        gate.Observe(12, 60400);
        Check(gate.Phase == AlarmPhase.Watching, "A single frame triggered the alarm.");
        gate.Observe(12, 60600);
        Check(gate.Phase == AlarmPhase.EntryDelay && gate.RemainingSeconds(60600) == 15, "Entry grace was skipped.");
        gate.Tick(75599); Check(gate.Phase == AlarmPhase.EntryDelay, "Entry countdown ended early.");
        gate.Home(); gate.Tick(100000); Check(!gate.Armed, "Home failed to cancel entry.");
        gate = new(new(true, 10, 30, 0, 0)); gate.Arm(0);
        gate.Observe(15, 0); gate.Observe(15, 2000);
        Check(gate.Phase == AlarmPhase.Watching, "Stale separated samples triggered the alarm.");
        gate.Observe(15, 2200); Check(gate.Phase == AlarmPhase.Sounding, "Independent alarm threshold failed.");
        gate.Tick(90000); Check(gate.Phase == AlarmPhase.Sounding, "Alarm duration ran before playback started.");
        gate.PlaybackStarted(90000); gate.Observe(100, 119000); gate.Tick(120000);
        Check(gate.Phase == AlarmPhase.WaitingForQuiet, "Repeated motion extended the fixed alarm duration.");
        gate.Observe(20, 120200); gate.Observe(20, 120400);
        Check(gate.Phase == AlarmPhase.WaitingForQuiet, "Ongoing movement retriggered a stopped alarm.");
        for (int i = 0; i <= 10; i++) gate.Observe(0, 121000 + i * 200);
        Check(gate.Phase == AlarmPhase.Watching, "Alarm did not rearm after two quiet seconds.");
        gate.Observe(15, 123200); gate.Observe(15, 123400); gate.Stop();
        Check(gate.Phase == AlarmPhase.WaitingForQuiet, "Manual stop did not silence the incident.");
        gate.Observe(0, 123600); gate.Observe(0, 130000);
        Check(gate.Phase == AlarmPhase.WaitingForQuiet, "A sample gap counted as continuously quiet.");
        gate = new(new(true, 10, null, 0, 0)); gate.Arm(0); gate.Observe(20, 0); gate.Observe(20, 200);
        gate.PlaybackStarted(200); gate.Tick(10_000_000);
        Check(gate.Phase == AlarmPhase.Sounding && gate.RemainingSeconds(10_000_000) is null, "Latched alarm expired.");
        gate.Home(); Check(!gate.Armed, "Disarm retained the alarm state.");
        var recording = new MotionWindow(60); recording.Observe(new(0, 12, true));
        Check(recording.Observe(new(30000, 0, false)), "Alarm interfered with recording tail.");
        Console.WriteLine("Alarm: independent threshold, Home/Away, exit/entry grace, fixed/latched duration, stop and quiet rearm passed.");
    }
}
