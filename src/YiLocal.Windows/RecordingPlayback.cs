using System.Diagnostics;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;

namespace YiLocal.Windows;

/// <summary>Independent bounded decoder pipes, sharing the seek origin and speaker sample clock.</summary>
internal sealed class RecordingPlayback : IDisposable
{
    public const int Width = 960, Height = 540;
    readonly Process process;
    readonly Process? audioProcess;
    readonly CancellationTokenSource stop = new();
    readonly Stopwatch clock = new();
    readonly Task videoTask, audioTask, errors, audioErrors, completion;
    readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly double start, duration;
    readonly bool sound;
    readonly Func<IPcmPlaybackOutput> audioOutput;
    IPcmPlaybackOutput? speakers;
    Bitmap? latest;
    readonly object errorLock = new();
    public string? Error { get; private set; }
    public bool Finished => completion.IsCompleted;
    public double Position => Math.Min(duration, start + (sound ? speakers?.Seconds ?? 0 : clock.Elapsed.TotalSeconds));

    public RecordingPlayback(string executable, string path, double start, double duration, bool sound,
        Func<IPcmPlaybackOutput>? audioOutput = null)
    {
        this.start = start; this.duration = duration; this.sound = sound;
        this.audioOutput = audioOutput ?? (() => new PcmOutput());
        var info = MediaTools.StartInfo(executable); info.RedirectStandardOutput = true;
        void Add(params string[] args) { foreach (string arg in args) info.ArgumentList.Add(arg); }
        string remaining = Math.Max(0.04, duration - start).ToString("0.######", CultureInfo.InvariantCulture);
        Add("-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-ss", start.ToString("0.######", CultureInfo.InvariantCulture), "-noaccurate_seek", "-i", path,
            "-map", "0:v:0", "-vf", $"scale={Width}:{Height}:force_original_aspect_ratio=decrease,pad={Width}:{Height}:(ow-iw)/2:(oh-ih)/2,fps=25:start_time=0",
            "-t", remaining, "-an", "-pix_fmt", "bgr24", "-f", "rawvideo", "pipe:1");
        process = Process.Start(info) ?? throw new IOException("Could not start the recording player.");
        try
        {
            if (sound)
            {
                // A single FFmpeg process can fill its video pipe before emitting audio
                // (or vice versa). Pacing either pipe then waits on the other forever.
                // Separate demuxers retain the same timestamp origin without that cycle.
                var audioInfo = MediaTools.StartInfo(executable, "-hide_banner", "-loglevel", "error", "-nostdin",
                    "-ss", start.ToString("0.######", CultureInfo.InvariantCulture), "-noaccurate_seek", "-i", path,
                    "-map", "0:a:0", "-vn", "-af", "aresample=16000:async=1:first_pts=0,apad", "-t", remaining,
                    "-ac", "1", "-ar", "16000", "-f", "s16le", "-flush_packets", "1", "pipe:1");
                audioInfo.RedirectStandardOutput = true;
                audioProcess = Process.Start(audioInfo) ?? throw new IOException("Could not start the recording audio decoder.");
            }
        }
        catch { Kill(); process.Dispose(); stop.Dispose(); throw; }
        errors = ObserveDecoder(process, "Video");
        audioErrors = audioProcess is null ? Task.CompletedTask : ObserveDecoder(audioProcess, "Audio");
        videoTask = Task.Run(ReadVideoAsync);
        audioTask = sound ? Task.Run(ReadAudioAsync) : Task.CompletedTask;
        completion = Task.Run(async () =>
        {
            try
            {
                await Task.WhenAll(videoTask, audioTask, errors, audioErrors);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
            { if (!stop.IsCancellationRequested) Error = e.Message; stop.Cancel(); Kill(); }
        });
    }
    async Task ObserveDecoder(Process decoder, string track)
    {
        string text = "";
        try
        {
            while (await decoder.StandardError.ReadLineAsync(stop.Token) is { } line)
            { text += line + "\n"; if (text.Length > 2000) text = text[^2000..]; }
            await decoder.WaitForExitAsync(stop.Token);
            if (decoder.ExitCode != 0) throw new IOException(track + " decoder: " + (text.Length > 0 ? text : "Playback failed."));
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
        { Fail(e); }
    }
    void Fail(Exception error)
    {
        lock (errorLock) { if (!stop.IsCancellationRequested) Error ??= error.Message; stop.Cancel(); }
        ready.TrySetResult(); Kill();
    }
    async Task ReadVideoAsync()
    {
        var buffer = new byte[Width * Height * 3]; int index = 0;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                int first = await process.StandardOutput.BaseStream.ReadAsync(buffer, stop.Token);
                if (first == 0) break;
                await process.StandardOutput.BaseStream.ReadExactlyAsync(buffer.AsMemory(first), stop.Token);
                if (index == 0) { clock.Start(); ready.TrySetResult(); }
                while (index / 25.0 > Position - start + .01) await Task.Delay(5, stop.Token);
                var bitmap = new Bitmap(Width, Height, PixelFormat.Format24bppRgb);
                var bits = bitmap.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
                try { Marshal.Copy(buffer, 0, bits.Scan0, buffer.Length); } finally { bitmap.UnlockBits(bits); }
                Interlocked.Exchange(ref latest, bitmap)?.Dispose(); index++;
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
        { Fail(e); }
        finally { ready.TrySetResult(); }
    }
    async Task ReadAudioAsync()
    {
        try
        {
            using var output = audioOutput(); speakers = output;
            await ready.Task.WaitAsync(stop.Token);
            var buffer = new byte[2048];
            while (!stop.IsCancellationRequested)
            {
                int count = 0;
                while (count < buffer.Length)
                {
                    int n = await audioProcess!.StandardOutput.BaseStream.ReadAsync(buffer.AsMemory(count), stop.Token);
                    if (n == 0) break;
                    count += n;
                }
                if (count == 0) break;
                await output.WriteAsync(count == buffer.Length ? buffer : buffer[..count], stop.Token);
            }
            await output.DrainAsync(stop.Token);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
        { Fail(e); }
    }
    public Bitmap? Take() => Interlocked.Exchange(ref latest, null);
    void Kill()
    {
        foreach (var decoder in new[] { process, audioProcess })
            try { if (decoder is not null && !decoder.HasExited) decoder.Kill(true); }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
    public void Dispose()
    {
        stop.Cancel(); ready.TrySetResult(); Kill();
        try { Task.WaitAll([videoTask, audioTask, errors, audioErrors, completion], 2000); } catch (AggregateException) { }
        Interlocked.Exchange(ref latest, null)?.Dispose(); process.Dispose(); audioProcess?.Dispose(); stop.Dispose();
    }
}
