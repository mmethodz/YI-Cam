using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;

namespace YiLocal.Core;

/// <summary>Bounded grayscale decode independent of preview and recording rate. No frames are sent outside the PC.</summary>
internal sealed class MotionVideoDecoder : IDisposable
{
    readonly Process process;
    readonly CancellationTokenSource stop = new();
    readonly Channel<(VideoFrame Frame, long Time)> input = Channel.CreateBounded<(VideoFrame, long)>(90);
    readonly ConcurrentQueue<long> timestamps = new();
    readonly Channel<MotionMeasurement> output = Channel.CreateBounded<MotionMeasurement>(90);
    readonly Task writer, reader, errors;
    readonly PixelMotionDetector detector;
    long queuedBytes, lastOutput = Environment.TickCount64;
    Exception? failure;
    public ChannelReader<MotionMeasurement> Measurements => output.Reader;
    public MotionVideoDecoder(string executable, double threshold)
    {
        detector = new(threshold);
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-hide_banner", "-loglevel", "error", "-flags", "low_delay", "-probesize", "32", "-analyzeduration", "0",
            "-f", "h264", "-i", "pipe:0", "-an", "-vf", "scale=160:90:flags=area", "-fps_mode", "passthrough", "-pix_fmt", "gray", "-f", "rawvideo", "pipe:1" }) info.ArgumentList.Add(arg);
        process = Process.Start(info) ?? throw new IOException(L.Get("CouldNotStartLocalMotionDetection"));
        writer = Task.Run(async () =>
        {
            try
            {
                await foreach (var item in input.Reader.ReadAllAsync(stop.Token))
                {
                    Interlocked.Add(ref queuedBytes, -item.Frame.Data.Length);
                    if (timestamps.Count >= 180) throw new IOException(L.Get("MotionDecoderIsFallingBehind"));
                    timestamps.Enqueue(item.Time);
                    await process.StandardInput.BaseStream.WriteAsync(item.Frame.Data, stop.Token);
                    await process.StandardInput.BaseStream.FlushAsync(stop.Token);
                }
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { Fail(e); }
        });
        reader = Task.Run(async () =>
        {
            var gray = new byte[PixelMotionDetector.Width * PixelMotionDetector.Height];
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    await process.StandardOutput.BaseStream.ReadExactlyAsync(gray, stop.Token);
                    if (!timestamps.TryDequeue(out long time)) throw new IOException(L.Get("MotionDecoderFrameTimingLostAlignment"));
                    Interlocked.Exchange(ref lastOutput, Environment.TickCount64);
                    if (detector.Analyze(gray, time) is { } sample && !output.Writer.TryWrite(sample))
                        throw new IOException(L.Get("MotionAnalysisResultsAreNotBeingConsumed"));
                }
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { Fail(e); }
        });
        errors = Task.Run(async () =>
        {
            string text = "";
            try
            {
                while (await process.StandardError.ReadLineAsync(stop.Token) is { } line)
                { text += line + "\n"; if (text.Length > 1500) text = text[^1500..]; }
                await process.WaitForExitAsync(stop.Token);
                if (!stop.IsCancellationRequested) Fail(new IOException(L.Get("MotionDecoderExited") + text));
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { Fail(e); }
        });
    }
    void Fail(Exception error)
    { if (!stop.IsCancellationRequested) Interlocked.CompareExchange(ref failure, error, null); }
    public void Push(VideoFrame frame, long time)
    {
        if (failure is { } error) throw new IOException(L.Get("LocalMotionDetectionStopped") + error.Message, error);
        if (Environment.TickCount64 - Interlocked.Read(ref lastOutput) > 10000)
            throw new IOException(L.Get("NoDecodedMotionFramesFor10SecondsRecordingHasBeenDisarmed"));
        if (!FragmentedMp4.Nals(frame.Data).Any(nal => (nal[0] & 31) is 1 or 5)) return;
        if (Interlocked.Add(ref queuedBytes, frame.Data.Length) <= 16 * 1024 * 1024 && input.Writer.TryWrite((frame, time))) return;
        Interlocked.Add(ref queuedBytes, -frame.Data.Length);
        throw new IOException(L.Get("LocalMotionDetectorCannotKeepUpRecordingHasBeenDisarmed"));
    }
    public void Dispose()
    {
        stop.Cancel(); input.Writer.TryComplete();
        try { if (!process.HasExited) process.Kill(true); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        try { Task.WaitAll([writer, reader, errors], 2000); } catch (AggregateException) { }
        process.Dispose(); stop.Dispose();
    }
}
