namespace YiLocal.Core;

public sealed record VideoSnapshot(int Width, int Height, byte[] Sps, byte[] Pps, IReadOnlyList<(VideoFrame Frame, long Time)> Pictures)
{
    public byte[] ToMp4()
    {
        using var memory = new MemoryStream();
        using (var muxer = new FragmentedMp4(memory, Width, Height, Sps, Pps))
            foreach (var (frame, time) in Pictures) muxer.Write(frame.Data, time - Pictures[0].Time);
        return memory.ToArray();
    }
}

/// <summary>Retains one independently decodable GOP for a full-resolution snapshot. Never grows without bound.</summary>
public sealed class SnapshotBuffer
{
    readonly object gate = new();
    readonly List<(VideoFrame Frame, long Time)> frames = [];
    readonly Dictionary<int, byte[]> parameters = [];
    readonly int maximumFrames;
    readonly long maximumBytes;
    string? identity;
    long bytes;
    public SnapshotBuffer(int maximumFrames = 600, long maximumBytes = 16 * 1024 * 1024)
    { this.maximumFrames = maximumFrames; this.maximumBytes = maximumBytes; }
    public void Clear()
    { lock (gate) { frames.Clear(); parameters.Clear(); bytes = 0; identity = null; } }
    public void Add(VideoFrame frame, long time, int epoch)
    {
        lock (gate)
        {
            string nextIdentity = $"{epoch}:{frame.Generation}:{frame.Width}:{frame.Height}";
            if (identity != nextIdentity) { Clear(); identity = nextIdentity; }
            var nals = FragmentedMp4.Nals(frame.Data);
            bool key = nals.Any(nal => (nal[0] & 31) == 5);
            foreach (var nal in nals.Where(nal => (nal[0] & 31) is 7 or 8))
            {
                int type = nal[0] & 31;
                if (parameters.TryGetValue(type, out var old) && !old.SequenceEqual(nal)) { frames.Clear(); bytes = 0; }
                parameters[type] = nal;
            }
            if (!nals.Any(nal => (nal[0] & 31) is 1 or 5)) return;
            if (key) { frames.Clear(); bytes = 0; }
            if (!key && frames.Count == 0 || !parameters.ContainsKey(7) || !parameters.ContainsKey(8)) return;
            if (frames.Count >= maximumFrames || bytes + frame.Data.Length > maximumBytes)
            { frames.Clear(); bytes = 0; return; }
            frames.Add((frame, time)); bytes += frame.Data.Length;
        }
    }
    public VideoSnapshot Take()
    {
        lock (gate)
        {
            if (frames.Count == 0) throw new IOException("Wait for the next camera keyframe before taking a snapshot.");
            var last = frames[^1].Frame;
            return new(last.Width, last.Height, parameters[7], parameters[8], frames.ToArray());
        }
    }
}
