using System.Globalization;

namespace YiLocal.Core;

public enum RecordingEncoding { Original, Balanced, Small }
public enum CaptureMode { Continuous, Timelapse }

public sealed record RecordingOptions(RecordingEncoding Encoding = RecordingEncoding.Original,
    double? FramesPerSecond = null, CaptureMode Mode = CaptureMode.Continuous)
{
    public string Label => Encoding switch
    {
        RecordingEncoding.Original => "Original stream",
        RecordingEncoding.Balanced => "H.264 balanced (CRF 28)",
        _ => "H.264 smaller (CRF 32)"
    };
    public void Validate()
    {
        if (!Enum.IsDefined(Encoding) || !Enum.IsDefined(Mode) ||
            FramesPerSecond is { } fps && (!double.IsFinite(fps) || fps is < 0.5 or > 120))
            throw new ArgumentException("Recording rate must be between 0.5 and 120 fps, or the source rate.");
        if (Encoding == RecordingEncoding.Original && (FramesPerSecond is not null || Mode != CaptureMode.Continuous))
            throw new ArgumentException("Original-stream recording preserves every source frame and its timing. Choose an H.264 profile for frame selection or timelapse.");
        if (Mode == CaptureMode.Timelapse && FramesPerSecond is null)
            throw new ArgumentException("Choose a capture rate for timelapse (played at 25 fps).");
    }
    internal string? Filter
    {
        get
        {
            var filters = new List<string>();
            if (FramesPerSecond is { } fps)
            {
                string rate = fps.ToString("0.########", CultureInfo.InvariantCulture);
                // One source picture per time bucket. Never duplicate pictures to raise the rate.
                filters.Add($"select='isnan(prev_selected_t)+gt(floor(t*{rate}),floor(prev_selected_t*{rate}))'");
            }
            if (Mode == CaptureMode.Timelapse) filters.Add("setpts=N/(25*TB)");
            return filters.Count == 0 ? null : string.Join(',', filters);
        }
    }
}

public sealed record ClipMetadata(string Camera = "", string Profile = "Original stream", string Kind = "Continuous",
    double? TargetFps = null, double CaptureDuration = 0, long Frames = 0);

internal sealed record EncodedClipResult(double Duration, double CaptureDuration, long Frames);
