using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace YiLocal.Core;

/// <summary>Optional encoding on a bounded worker; live preview never consumes this reduced-rate output.</summary>
internal sealed class FfmpegRecording
{
    const long MaximumQueuedBytes = 16 * 1024 * 1024;
    readonly Channel<(byte[] Data, long Time)> queue = Channel.CreateBounded<(byte[], long)>(180);
    readonly Process process;
    readonly Task<EncodedClipResult> worker;
    readonly StringBuilder errors = new();
    long queuedBytes, outputMicroseconds, outputFrames;
    volatile Exception? failure;
    bool completed;

    public FfmpegRecording(string executable, string path, int width, int height, byte[] sps, byte[] pps, RecordingOptions options)
    {
        options.Validate();
        if (!File.Exists(executable)) throw new IOException("Choose an FFmpeg executable before using an encoding profile.");
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        void Add(params string[] values) { foreach (var value in values) info.ArgumentList.Add(value); }
        Add("-hide_banner", "-loglevel", "error", "-nostdin", "-n", "-f", "mp4", "-i", "pipe:0", "-map", "0:v:0", "-an");
        if (options.Filter is { } filter) Add("-vf", filter);
        Add("-c:v", "libx264", "-preset", "veryfast", "-crf", options.Encoding == RecordingEncoding.Balanced ? "28" : "32",
            "-pix_fmt", "yuv420p", "-bf", "0", "-fps_mode", "vfr", "-enc_time_base", "1:1000");
        // Preserve selected source PTS. An output -r would request CFR duplication.
        // Give the last selected picture a useful hold duration in the container.
        if (options.Mode == CaptureMode.Timelapse) Add("-bsf:v", "setts=duration=0.04/TB");
        else if (options.FramesPerSecond is { } fps)
            Add("-bsf:v", "setts=duration=1/(" + fps.ToString(CultureInfo.InvariantCulture) + "*TB)");
        Add("-force_key_frames", "expr:gte(t,n_forced*10)", "-movflags", "+frag_keyframe+empty_moov+default_base_moof",
            "-frag_duration", "1000000", "-progress", "pipe:1", "-f", "mp4", path);
        process = Process.Start(info) ?? throw new IOException("Could not start FFmpeg.");
        worker = Task.Run(async () =>
        {
            var stderr = DrainErrorsAsync(); var progress = ReadProgressAsync();
            try
            {
                var input = new FragmentedMp4(process.StandardInput.BaseStream, width, height, sps, pps);
                try
                {
                    await foreach (var frame in queue.Reader.ReadAllAsync())
                    {
                        Interlocked.Add(ref queuedBytes, -frame.Data.Length);
                        input.Write(frame.Data, frame.Time);
                    }
                }
                finally { input.Dispose(); }
                double capture = input.DurationMilliseconds / 1000.0;
                await process.WaitForExitAsync(); await Task.WhenAll(stderr, progress);
                if (process.ExitCode != 0) throw new IOException("FFmpeg recording failed: " + errors);
                if (outputFrames == 0) throw new IOException("FFmpeg produced no recording frames.");
                // FFmpeg progress precedes the duration bitstream filter. Read final container
                // timing so the catalogue agrees with playback, especially at 0.5 fps.
                var saved = FragmentedVideoInfo.Read(path);
                if (saved.Frames != outputFrames) throw new IOException("Encoded frame count does not match the saved MP4.");
                return new EncodedClipResult(saved.Duration, capture, saved.Frames);
            }
            catch (Exception e)
            {
                failure = e; queue.Writer.TryComplete(e); Kill();
                try { await Task.WhenAll(stderr, progress); } catch (IOException) { }
                throw new IOException("Recording encoder failed: " + (errors.Length > 0 ? errors.ToString() : e.Message), e);
            }
            finally { process.Dispose(); }
        });
    }

    async Task DrainErrorsAsync()
    {
        while (await process.StandardError.ReadLineAsync() is { } line)
        {
            errors.AppendLine(line.Length > 500 ? line[..500] : line);
            if (errors.Length > 4000) errors.Remove(0, errors.Length - 4000);
        }
    }
    async Task ReadProgressAsync()
    {
        while (await process.StandardOutput.ReadLineAsync() is { } line)
        {
            int separator = line.IndexOf('=');
            if (separator < 0 || !long.TryParse(line[(separator + 1)..], out long value)) continue;
            if (line.StartsWith("out_time_us=", StringComparison.Ordinal)) outputMicroseconds = Math.Max(outputMicroseconds, value);
            if (line.StartsWith("frame=", StringComparison.Ordinal)) outputFrames = Math.Max(outputFrames, value);
        }
    }
    public void Write(byte[] data, long time)
    {
        if (failure is { } error) throw new IOException("Recording encoder failed.", error);
        if (completed) throw new InvalidOperationException("Recording encoder has finished.");
        if (Interlocked.Add(ref queuedBytes, data.Length) <= MaximumQueuedBytes && queue.Writer.TryWrite((data, time))) return;
        Interlocked.Add(ref queuedBytes, -data.Length);
        throw new IOException("Recording encoder cannot keep up. Choose a lighter profile or lower capture rate.");
    }
    public async Task<EncodedClipResult> FinishAsync()
    {
        completed = true; queue.Writer.TryComplete();
        try { return await worker.WaitAsync(TimeSpan.FromSeconds(15)); }
        catch (TimeoutException)
        {
            Kill();
            try { await worker.WaitAsync(TimeSpan.FromSeconds(2)); } catch (Exception) { }
            throw new IOException("Recording encoder did not finish within 15 seconds; the clip remains marked interrupted.");
        }
    }
    void Kill()
    {
        try { if (!process.HasExited) process.Kill(true); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
}
