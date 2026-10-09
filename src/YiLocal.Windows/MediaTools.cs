using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using YiLocal.Core;

namespace YiLocal.Windows;

internal static class MediaTools
{
    public static string? FindFfmpeg()
    {
        string direct = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(direct)) return direct;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "YiLocal.sln"))) continue;
            string path = Path.Combine(dir.FullName, "runtimes", "win-x64", "native", "ffmpeg.exe");
            if (File.Exists(path)) return path;
        }
        return null;
    }
    public static ProcessStartInfo StartInfo(string executable, params string[] args)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (string arg in args) info.ArgumentList.Add(arg);
        return info;
    }
    public static async Task Export4K(string executable, string input, string output, CancellationToken cancellation)
    {
        // A unique temporary output prevents overwriting a file created while the export runs.
        string temporary = output + "." + Guid.NewGuid().ToString("N") + ".partial.mp4";
        using var process = new Process { StartInfo = StartInfo(executable, "-hide_banner", "-loglevel", "error", "-nostdin", "-n", "-i", input,
            "-map", "0:v:0", "-vf", "scale=3840:2160:flags=lanczos", "-c:v", "libx264", "-preset", "veryfast", "-crf", "20",
            "-fps_mode", "passthrough", "-enc_time_base", "1:1000", "-an", "-movflags", "+faststart", temporary) };
        try
        {
            process.Start(); var error = process.StandardError.ReadToEndAsync(cancellation);
            using var registration = cancellation.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
            await process.WaitForExitAsync(cancellation);
            if (process.ExitCode != 0) throw new IOException("4K export failed: " + await error);
            File.Move(temporary, output, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static async Task SaveSnapshot(string executable, VideoSnapshot snapshot, string output, CancellationToken cancellation)
    {
        string temporary = output + "." + Guid.NewGuid().ToString("N") + ".partial.png";
        var info = StartInfo(executable, "-hide_banner", "-loglevel", "error", "-nostdin", "-n", "-f", "mp4", "-i", "pipe:0",
            "-map", "0:v:0", "-vf", $"select=eq(n\\,{snapshot.Pictures.Count - 1})", "-frames:v", "1", "-fps_mode", "passthrough", temporary);
        info.RedirectStandardInput = true;
        using var process = new Process { StartInfo = info };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            process.Start(); var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            using var registration = timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
            await Task.Run(async () =>
            {
                await process.StandardInput.BaseStream.WriteAsync(snapshot.ToMp4(), timeout.Token);
                process.StandardInput.Close();
            }, timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0 || !File.Exists(temporary)) throw new IOException("Snapshot failed: " + await errors);
            File.Move(temporary, output, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

internal sealed class VideoPreview : IDisposable
{
    public const int Width = 960, Height = 540;
    readonly Process process;
    readonly Channel<byte[]> queue = Channel.CreateBounded<byte[]>(90);
    readonly CancellationTokenSource stop = new();
    readonly Task writer, reader, errors;
    Bitmap? latest;
    public bool Failed { get; private set; }
    public VideoPreview(string executable)
    {
        var info = MediaTools.StartInfo(executable, "-hide_banner", "-loglevel", "error", "-flags", "low_delay", "-probesize", "32768", "-analyzeduration", "0",
            "-f", "h264", "-i", "pipe:0", "-vf", $"scale={Width}:{Height}", "-an", "-pix_fmt", "bgr24", "-f", "rawvideo", "pipe:1");
        info.RedirectStandardInput = info.RedirectStandardOutput = true;
        process = Process.Start(info) ?? throw new IOException("Unable to start FFmpeg.");
        // Drain stderr continuously, without keeping an unbounded log.
        errors = Task.Run(async () => { try { while (await process.StandardError.ReadLineAsync(stop.Token) is not null) { } } catch (OperationCanceledException) { } });
        writer = Task.Run(async () =>
        {
            try
            {
                await foreach (byte[] packet in queue.Reader.ReadAllAsync(stop.Token))
                { await process.StandardInput.BaseStream.WriteAsync(packet, stop.Token); await process.StandardInput.BaseStream.FlushAsync(stop.Token); }
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { Failed = true; }
        });
        reader = Task.Run(async () =>
        {
            var buffer = new byte[Width * Height * 3];
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    await process.StandardOutput.BaseStream.ReadExactlyAsync(buffer, stop.Token);
                    var bitmap = new Bitmap(Width, Height, PixelFormat.Format24bppRgb);
                    var data = bitmap.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
                    try { Marshal.Copy(buffer, 0, data.Scan0, buffer.Length); } finally { bitmap.UnlockBits(data); }
                    Interlocked.Exchange(ref latest, bitmap)?.Dispose();
                }
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { Failed = true; }
        });
    }
    public bool Push(byte[] bytes) => !Failed && queue.Writer.TryWrite(bytes);
    public Bitmap? Take() => Interlocked.Exchange(ref latest, null);
    public void Dispose()
    {
        stop.Cancel(); queue.Writer.TryComplete();
        try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
        try { Task.WaitAll([writer, reader, errors], 1500); } catch (AggregateException) { }
        Interlocked.Exchange(ref latest, null)?.Dispose(); process.Dispose(); stop.Dispose();
    }
}
