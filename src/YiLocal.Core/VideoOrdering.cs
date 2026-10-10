namespace YiLocal.Core;

public sealed class FrameOrder
{
    readonly Dictionary<ushort, VideoFrame> pending = [];
    ushort? expected;
    long? waiting;
    public int Epoch { get; private set; }
    public IReadOnlyList<VideoFrame> Feed(VideoFrame frame, long? now = null)
    {
        long clock = now ?? Environment.TickCount64;
        if (expected.HasValue && (ushort)(frame.Sequence - expected.Value) >= 32768) return [];
        pending.TryAdd(frame.Sequence, frame);
        if (!expected.HasValue && frame.Keyframe)
        {
            expected = frame.Sequence;
            DiscardBefore(expected.Value);
        }
        if (pending.Count > 300) { pending.Clear(); expected = null; Epoch++; return []; }
        var result = new List<VideoFrame>();
        Drain(result);
        if (pending.Count > 0)
        {
            waiting ??= clock;
            if (clock - waiting > 2000)
            {
                var keys = pending.Values.Where(f => f.Keyframe).ToList();
                if (keys.Count > 0)
                {
                    ushort first = expected ?? keys[0].Sequence;
                    expected = keys.MinBy(f => (ushort)(f.Sequence - first))!.Sequence;
                    DiscardBefore(expected.Value); Epoch++; Drain(result);
                    waiting = pending.Count > 0 ? clock : null;
                }
            }
        }
        return result;
    }
    void Drain(List<VideoFrame> result)
    {
        while (expected.HasValue && pending.Remove(expected.Value, out var frame))
        { result.Add(frame); expected = (ushort)(expected.Value + 1); waiting = null; }
    }
    void DiscardBefore(ushort first)
    {
        foreach (ushort sequence in pending.Keys.Where(s => (ushort)(s - first) >= 32768).ToArray()) pending.Remove(sequence);
    }
}

public sealed class FrameClock
{
    readonly object gate = new();
    ulong? previous;
    bool? uptime;
    long elapsed;
    public long Time(VideoFrame frame)
    {
        lock (gate)
        {
            uptime ??= frame.Milliseconds > 999;
            ulong value = uptime.Value ? frame.Milliseconds : (ulong)frame.Seconds * 1000 + frame.Milliseconds;
            if (previous.HasValue)
            {
                long delta = uptime.Value ? (uint)(value - previous.Value) : (long)value - (long)previous.Value;
                if (delta is < 0 or > 30000) throw new InvalidDataException(L.Get("CameraTimestampJumpedReconnectingIsRequired"));
                elapsed += Math.Max(1, delta);
            }
            previous = value;
            return elapsed;
        }
    }
    /// <summary>Map audio onto the video clock without advancing or perturbing that clock.</summary>
    public long? AudioTime(AudioFrame frame)
    {
        lock (gate)
        {
            if (previous is null) return null;
            long delta = uptime == true ? unchecked((int)(frame.Milliseconds - (uint)previous.Value))
                : checked((long)frame.Seconds * 1000 + frame.Milliseconds - (long)previous.Value);
            if (Math.Abs(delta) > 30000) throw new InvalidDataException(L.Get("AudioAndVideoClocksDiverged"));
            return elapsed + delta;
        }
    }
}
