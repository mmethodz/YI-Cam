using System.Text.Json;
using YiLocal.Core;

static class MotionChecks
{
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static void Run()
    {
        byte[] gray = Enumerable.Repeat((byte)60, PixelMotionDetector.Width * PixelMotionDetector.Height).ToArray();
        var detector = new PixelMotionDetector(2);
        Check(detector.Analyze(gray, 0) is { Active: false }, "Motion started without a baseline.");
        var brighter = Enumerable.Repeat((byte)100, gray.Length).ToArray();
        Check(detector.Analyze(brighter, 200) is { Active: false, ChangedPercent: 0 }, "Global exposure change triggered motion.");
        var noise = brighter.Select((p, i) => (byte)(p + i % 9 - 4)).ToArray();
        Check(detector.Analyze(noise, 400) is { Active: false, ChangedPercent: 0 }, "Small pixel noise triggered motion.");
        var small = (byte[])brighter.Clone(); Array.Fill(small, (byte)180, 0, 100);
        Check(detector.Analyze(small, 600) is { Active: false }, "Sub-threshold area triggered motion.");
        var first = (byte[])brighter.Clone(); Array.Fill(first, (byte)180, 1000, 600);
        var second = (byte[])brighter.Clone(); Array.Fill(second, (byte)180, 2000, 600);
        Check(detector.Analyze(first, 800) is { Active: false }, "A single changed frame triggered motion.");
        Check(detector.Analyze(second, 850) is null, "Detector exceeded its five-Hz analysis limit.");
        Check(detector.Analyze(second, 1000) is { Active: true, ChangedPercent: > 2 }, "Consecutive area changes failed to trigger.");
        Check(detector.Analyze(second, 1200) is { Active: false }, "Stationary object kept triggering motion.");
        Check(detector.Analyze(first, 0) is { Active: false, ChangedPercent: 0 }, "Clock reset retained an old baseline.");
        var insensitive = new PixelMotionDetector(20); insensitive.Analyze(brighter, 0);
        insensitive.Analyze(first, 200);
        Check(insensitive.Analyze(second, 400) is { Active: false }, "Area threshold was ignored.");
        var gate = new MotionWindow(3);
        Check(!gate.Observe(new(0, 0, false)), "Quiet input opened a recording.");
        Check(gate.Observe(new(1000, 4, true)) && gate.Observe(new(3900, 0, false)), "Post-motion tail was not retained.");
        Check(gate.Observe(new(3900, 4, true)) && gate.Observe(new(6899, 0, false)), "Repeated motion did not extend the timer.");
        Check(!gate.Observe(new(6900, 0, false)), "Post-motion interval did not expire.");
        gate.Observe(new(7000, 4, true));
        Check(!gate.Observe(new(0, 0, false)), "Clock reset retained active recording.");
        gate.Observe(new(1000, 4, true)); gate.Reset();
        Check(!gate.Active && gate.RemainingSeconds(1000) == 0, "Disconnect retained a motion timer.");
        new RecordingOptions(Mode: CaptureMode.Motion).Validate();
        new RecordingOptions(RecordingEncoding.Balanced, 0.5, CaptureMode.Motion, true).Validate();
        Console.WriteLine("Motion: noise/exposure rejection, threshold, five-Hz analysis, retrigger, timer and reset checks passed.");
    }

    public static bool Fixture(string[] args)
    {
        if (args.Length == 0 || args[0] != "--motion-fixture") return false;
        if (args.Length != 6) throw new ArgumentException("--motion-fixture <video.h264> <audio.aac> <output> <ffmpeg> <Original|Balanced>");
        var pictures = new List<byte[]>(); var unit = new List<byte[]>();
        void Flush()
        {
            if (unit.Any(n => (n[0] & 31) is 1 or 5)) pictures.Add(Wire.Join(unit.Select(n => Wire.Join([0, 0, 0, 1], n)).ToArray()));
            unit.Clear();
        }
        foreach (var nal in FragmentedMp4.Nals(File.ReadAllBytes(args[1]))) { if ((nal[0] & 31) == 9) Flush(); unit.Add(nal); }
        Flush();
        var bytes = File.ReadAllBytes(args[2]); var audio = new List<byte[]>();
        for (int at = 0; at < bytes.Length;)
        {
            int length = ((bytes[at + 3] & 3) << 11) | bytes[at + 4] << 3 | bytes[at + 5] >> 5;
            audio.Add(bytes[at..(at + length)]); at += length;
        }
        var encoding = Enum.Parse<RecordingEncoding>(args[5]);
        var events = new List<object>(); long now = 0; bool wasActive = false; int triggers = 0, tails = 0, samples = 0, resets = 0;
        var alarm = new MotionAlarmGate(new(true, 100, 1, 0, 0));
        using (var recorder = new MotionRecorder(args[3], new(SegmentMinutes: 10),
            new(encoding, encoding == RecordingEncoding.Original ? null : 0.5, CaptureMode.Motion, true, 2, 2), args[4], "Synthetic motion camera"))
        {
            recorder.Measurement += sample =>
            {
                if (sample is null) { resets++; alarm.Home(); return; }
                samples++;
                if (!alarm.Armed) alarm.Arm(sample.Milliseconds);
                alarm.Observe(sample.ChangedPercent, sample.Milliseconds);
                Check(alarm.Phase == AlarmPhase.Watching, "Second threshold borrowed the recording trigger.");
            };
            recorder.State += state =>
            {
                if (state.Capturing != wasActive)
                {
                    if (state.Capturing) triggers++; else tails++;
                    events.Add(new { milliseconds = now, active = state.Capturing });
                }
                wasActive = state.Capturing;
            };
            int a = 0;
            for (int v = 0; v < pictures.Count; v++)
            {
                now = (long)Math.Round(v * 1000.0 / 15);
                if (v == 225) recorder.ConnectionEnded(); // Reconnect during the quiet gap must not retain audio/baselines.
                while (a < audio.Count && 250 + a * 64 <= now)
                {
                    long stamp = 250 + a * 64;
                    recorder.WriteAudio(new(audio[a], (ushort)a, 0, (uint)stamp, 138, 27), stamp); a++;
                }
                bool key = FragmentedMp4.Nals(pictures[v]).Any(n => (n[0] & 31) == 5);
                recorder.Write(new(pictures[v], (ushort)v, 160, 90, 0, (uint)now, key, v < 225 ? (byte)1 : (byte)2, 78), now);
                if (now < 4900) Check(recorder.Frames == 0, "Quiet period saved video before a trigger.");
                Thread.Sleep(25);
            }
            Check(triggers == 2 && tails == 2 && recorder.Current is null, $"Expected two motion bursts and expired tails; got {triggers}/{tails}.");
            Check(samples > 90 && resets >= 2, "Alarm did not receive unthrottled samples and reset notifications.");
        }
        File.WriteAllText(Path.Combine(args[3], "events.json"), JsonSerializer.Serialize(events));
        Console.WriteLine($"{encoding}: two motion clips, quiet periods omitted and both post-motion timers expired.");
        return true;
    }
}
