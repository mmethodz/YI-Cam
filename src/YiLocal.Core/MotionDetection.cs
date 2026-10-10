namespace YiLocal.Core;

public sealed record MotionMeasurement(long Milliseconds, double ChangedPercent, bool Active);

/// <summary>Small grayscale frame differences; global brightness changes and small pixel noise are discounted.</summary>
public sealed class PixelMotionDetector
{
    public const int Width = 160, Height = 90;
    readonly double thresholdPercent;
    byte[]? previous;
    long lastTime = long.MinValue;
    int consecutive;
    public PixelMotionDetector(double thresholdPercent)
    {
        if (!double.IsFinite(thresholdPercent) || thresholdPercent is < 0.1 or > 100) throw new ArgumentOutOfRangeException(nameof(thresholdPercent));
        this.thresholdPercent = thresholdPercent;
    }
    public MotionMeasurement? Analyze(ReadOnlySpan<byte> pixels, long time)
    {
        if (pixels.Length != Width * Height) throw new ArgumentException(L.Get("ExpectedA16090GrayscaleFrame"));
        if (lastTime != long.MinValue && time > lastTime && time - lastTime < 200) return null;
        if (time <= lastTime) { previous = null; consecutive = 0; }
        lastTime = time;
        if (previous is null) { previous = pixels.ToArray(); return new(time, 0, false); }
        // Median signed difference removes a whole-scene exposure step. A local
        // moving object still changes a fraction of the image after this correction.
        Span<int> histogram = stackalloc int[511];
        histogram.Clear();
        for (int i = 0; i < pixels.Length; i++) histogram[pixels[i] - previous[i] + 255]++;
        int sum = 0, median = 0;
        for (int i = 0; i < histogram.Length; i++)
        { sum += histogram[i]; if (sum >= pixels.Length / 2) { median = i - 255; break; } }
        int changed = 0;
        for (int i = 0; i < pixels.Length; i++)
            if (Math.Abs(pixels[i] - previous[i] - median) >= 20) changed++;
        pixels.CopyTo(previous);
        double percent = changed * 100.0 / pixels.Length;
        consecutive = percent >= thresholdPercent ? Math.Min(2, consecutive + 1) : 0;
        return new(time, percent, consecutive >= 2);
    }
}

/// <summary>Uses analyzed camera time, so repeated motion extends the end even when capture fps is reduced.</summary>
public sealed class MotionWindow
{
    readonly double postMotionSeconds;
    long? lastMotion;
    long previous = long.MinValue;
    public MotionWindow(double postMotionSeconds)
    {
        if (!double.IsFinite(postMotionSeconds) || postMotionSeconds is < 1 or > 3600) throw new ArgumentOutOfRangeException(nameof(postMotionSeconds));
        this.postMotionSeconds = postMotionSeconds;
    }
    public bool Active { get; private set; }
    public double RemainingSeconds(long time) => lastMotion is { } last ? Math.Max(0, postMotionSeconds - (time - last) / 1000.0) : 0;
    public bool Observe(MotionMeasurement sample)
    {
        if (sample.Milliseconds < previous) Reset();
        previous = sample.Milliseconds;
        if (sample.Active) lastMotion = sample.Milliseconds;
        Active = lastMotion is { } last && sample.Milliseconds - last < postMotionSeconds * 1000;
        return Active;
    }
    public void Reset() { Active = false; lastMotion = null; previous = long.MinValue; }
}
