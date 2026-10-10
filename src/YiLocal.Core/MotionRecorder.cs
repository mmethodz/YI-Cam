namespace YiLocal.Core;

public sealed record MotionRecordingState(bool Capturing, double ChangedPercent, double RemainingSeconds);

/// <summary>Local motion gating around the existing recorder. Call under the session's recording lock.</summary>
public sealed class MotionRecorder : IDisposable
{
    readonly SegmentRecorder recorder;
    readonly RecordingOptions options;
    readonly string ffmpeg;
    readonly MotionWindow window;
    // One decodable GOP, capped independently in time, bytes and frame count.
    readonly SnapshotBuffer preRoll = new(180, 8 * 1024 * 1024, 5000);
    readonly Queue<(AudioFrame Frame, long Time)> audio = new();
    MotionVideoDecoder? decoder;
    string? identity;
    long audioBytes, lastVideo = long.MinValue, lastAudio = long.MinValue, lastReport;
    int orderEpoch;
    public string? Current => recorder.Current;
    public long Frames => recorder.Frames;
    public event Action<MotionRecordingState>? State;
    // Every analyzed sample, before UI throttling. Null invalidates the analysis baseline.
    public event Action<MotionMeasurement?>? Measurement;

    public MotionRecorder(string folder, StoragePolicy policy, RecordingOptions options, string? ffmpeg, string cameraName = "")
    {
        options.Validate();
        if (options.Mode != CaptureMode.Motion) throw new ArgumentException("Select motion capture mode.");
        if (!File.Exists(ffmpeg)) throw new IOException("Choose an FFmpeg executable for local motion detection.");
        this.options = options; this.ffmpeg = ffmpeg;
        window = new(options.PostMotionSeconds);
        recorder = new(folder, policy, options, ffmpeg, cameraName);
    }

    public void Write(VideoFrame frame, long time, int epoch = 0)
    {
        string nextIdentity = $"{epoch}:{frame.Generation}:{frame.Width}:{frame.Height}";
        if (identity != nextIdentity)
        {
            ConnectionEnded(); identity = nextIdentity; orderEpoch = epoch;
        }
        preRoll.Add(frame, time, epoch);
        if (decoder is null)
        {
            if (!frame.Keyframe) return;
            decoder = new(ffmpeg, options.MotionThresholdPercent);
        }
        decoder.Push(frame, time);
        while (decoder.Measurements.TryRead(out var sample))
        {
            if (time - sample.Milliseconds > 5000) throw new IOException("Local motion detection is more than five seconds behind. Recording has been disarmed.");
            Measurement?.Invoke(sample);
            bool wasActive = window.Active;
            bool active = window.Observe(sample);
            if (active && !wasActive) BeginClip();
            if (!active && wasActive) recorder.ConnectionEnded();
            if (active != wasActive || Environment.TickCount64 - lastReport >= 1000)
            {
                lastReport = Environment.TickCount64;
                State?.Invoke(new(active, sample.ChangedPercent, window.RemainingSeconds(sample.Milliseconds)));
            }
        }
        if (window.Active && time > lastVideo)
        { recorder.Write(frame, time, epoch); lastVideo = time; }
        recorder.CheckCompleted();
    }

    void BeginClip()
    {
        recorder.ConnectionEnded(); lastVideo = lastAudio = long.MinValue;
        VideoSnapshot snapshot;
        try { snapshot = preRoll.Take(); }
        catch (IOException) { return; } // The existing recorder will wait for the next complete keyframe.
        long start = snapshot.Pictures[0].Time;
        var retainedAudio = audio.Where(a => a.Time >= start).ToArray();
        if (retainedAudio.Length > 0) recorder.PrimeAudio(retainedAudio[0].Frame);
        int a = 0;
        foreach (var (picture, time) in snapshot.Pictures)
        {
            while (a < retainedAudio.Length && retainedAudio[a].Time < time)
            {
                var packet = retainedAudio[a++]; recorder.WriteAudio(packet.Frame, packet.Time); lastAudio = packet.Time;
            }
            // Parameter sets can have arrived separately from the buffered IDR.
            var frame = time == start ? picture with { Data = Wire.Join([0, 0, 0, 1], snapshot.Sps, [0, 0, 0, 1], snapshot.Pps, picture.Data) } : picture;
            recorder.Write(frame, time, orderEpoch); lastVideo = time;
        }
        while (a < retainedAudio.Length)
        {
            var packet = retainedAudio[a++]; recorder.WriteAudio(packet.Frame, packet.Time); lastAudio = packet.Time;
        }
    }

    public void WriteAudio(AudioFrame frame, long time)
    {
        if (!options.IncludeAudio) return;
        if (frame.Data.Length > 1024 * 1024) throw new IOException("Audio packet exceeds the motion buffer limit.");
        audio.Enqueue((frame, time)); audioBytes += frame.Data.Length;
        while (audio.Count > 128 || audioBytes > 1024 * 1024 || audio.Count > 0 && time - audio.Peek().Time > 8000)
            audioBytes -= audio.Dequeue().Frame.Data.Length;
        if (window.Active && time > lastAudio)
        { recorder.WriteAudio(frame, time); lastAudio = time; }
    }

    public void ConnectionEnded()
    {
        Measurement?.Invoke(null);
        decoder?.Dispose(); decoder = null; identity = null;
        window.Reset(); preRoll.Clear(); audio.Clear(); audioBytes = 0;
        lastVideo = lastAudio = long.MinValue;
        recorder.ConnectionEnded();
        State?.Invoke(new(false, 0, 0));
    }
    public void Dispose()
    {
        try { ConnectionEnded(); }
        finally { recorder.Dispose(); }
    }
}
