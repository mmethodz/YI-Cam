using System.Diagnostics;
using System.Security.Cryptography;
using YiLocal.Core;

namespace YiLocal.Windows;

internal sealed record AlarmClip(IReadOnlyList<byte[]> Packets)
{
    public double Seconds => Packets.Count * .064;
}

/// <summary>Imports once, then loops bounded, validated AAC in memory. No microphone or PC speaker is opened.</summary>
internal static class AlarmAudio
{
    internal const int MaximumBytes = 1024 * 1024;
    public static string CacheDirectory => Path.Combine(DeviceProfile.SettingsDirectory, "alarm-sounds");
    public static AlarmClip Load(string path) => Parse(ReadBounded(path));
    static byte[] ReadBounded(string path)
    {
        using var file = File.OpenRead(path);
        if (file.Length > MaximumBytes) throw new IOException(L.Get("AlarmSampleExceedsTheOneMiBAACLimit"));
        var bytes = new byte[(int)file.Length]; file.ReadExactly(bytes); return bytes;
    }
    internal static AlarmClip Parse(byte[] bytes)
    {
        if (bytes.Length > MaximumBytes) throw new IOException(L.Get("AlarmSampleIsTooLarge"));
        var packets = new List<byte[]>();
        for (int at = 0; at < bytes.Length;)
        {
            if (bytes.Length - at < 7) throw new InvalidDataException(L.Get("TruncatedAlarmAACHeader"));
            int length = ((bytes[at + 3] & 3) << 11) | (bytes[at + 4] << 3) | (bytes[at + 5] >> 5);
            if (length <= 7 || length > TalkAudio.MaximumPacketBytes || length > bytes.Length - at)
                throw new InvalidDataException(L.Get("InvalidAlarmAACPacketLength"));
            var packet = bytes[at..(at + length)]; TalkAudio.Validate(packet);
            packets.Add(packet); at += length;
            if (packets.Count > 950) throw new IOException(L.Get("AlarmSamplesAreLimitedToSixtySeconds"));
        }
        if (packets.Count < 8) throw new IOException(L.Get("ChooseAnAlarmSampleAtLeastHalfASecondLong"));
        return new(packets);
    }
    public static async Task<(AlarmClip Clip, string Path)> ImportAsync(string executable, string source, string cache, CancellationToken cancellation)
    {
        if (!File.Exists(source)) throw new IOException(L.Get("AlarmSoundFileWasNotFound"));
        if (new FileInfo(source).Length > 100L * 1024 * 1024) throw new IOException(L.Get("ChooseAnAudioFileNoLargerThan100MiB"));
        var info = MediaTools.StartInfo(executable, "-hide_banner", "-loglevel", "error", "-nostdin",
            "-protocol_whitelist", "file,pipe", "-i", Path.GetFullPath(source), "-map", "0:a:0", "-vn", "-t", "60",
            "-af", "loudnorm=I=-9:TP=-1:LRA=7", "-ar", "16000", "-ac", "1",
            "-c:a", "aac", "-profile:a", "aac_low", "-b:a", "32k", "-map_metadata", "-1", "-f", "adts", "pipe:1");
        info.RedirectStandardOutput = true;
        using var process = new Process { StartInfo = info };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(60));
        process.Start();
        using var registration = timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        string lastError = "";
        var errors = Task.Run(async () =>
        {
            try { while (await process.StandardError.ReadLineAsync(timeout.Token) is { } line) lastError = line.Length > 600 ? line[..600] : line; }
            catch (Exception e) when (e is OperationCanceledException or IOException) { }
        });
        try
        {
            using var output = new MemoryStream(); var buffer = new byte[8192];
            int count;
            while ((count = await process.StandardOutput.BaseStream.ReadAsync(buffer, timeout.Token)) > 0)
            {
                if (output.Length + count > MaximumBytes) throw new IOException(L.Get("ConvertedAlarmExceededItsSizeLimit"));
                output.Write(buffer, 0, count);
            }
            await process.WaitForExitAsync(timeout.Token); await errors;
            if (process.ExitCode != 0) throw new IOException(L.Get("AlarmAudioConversionFailed") + lastError);
            byte[] bytes = output.ToArray(); var clip = Parse(bytes);
            Directory.CreateDirectory(cache);
            string path = Path.Combine(cache, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() + ".aac");
            // The imported source can be moved/deleted afterwards; keep our own complete, immutable copy.
            if (!File.Exists(path))
            {
                string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { await File.WriteAllBytesAsync(temporary, bytes, timeout.Token); File.Move(temporary, path, true); }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            return (clip, path);
        }
        finally
        {
            timeout.Cancel();
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
            await errors;
        }
    }
    public static async Task<AlarmClip> DefaultAsync(string executable, CancellationToken cancellation)
    {
        string temporary = Path.Combine(Path.GetTempPath(), "openyi-alarm-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            await using (var resource = typeof(AlarmAudio).Assembly.GetManifestResourceStream("OpenYI.SirenNoise.wav")
                ?? throw new IOException(L.Get("BundledPublicDomainSirenIsMissing")))
            await using (var file = File.Create(temporary)) await resource.CopyToAsync(file, cancellation);
            return (await ImportAsync(executable, temporary, CacheDirectory, cancellation)).Clip;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static async Task<byte[]> DecodeAsync(string executable, AlarmClip clip, CancellationToken cancellation)
    {
        var info = MediaTools.StartInfo(executable, "-hide_banner", "-loglevel", "error", "-f", "aac", "-i", "pipe:0",
            "-ac", "1", "-ar", "16000", "-f", "s16le", "pipe:1");
        info.RedirectStandardInput = info.RedirectStandardOutput = true;
        using var process = new Process { StartInfo = info };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(15000);
        process.Start();
        using var registration = timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        var write = Task.Run(async () =>
        {
            foreach (var packet in clip.Packets) await process.StandardInput.BaseStream.WriteAsync(packet, timeout.Token);
            process.StandardInput.Close();
        });
        try
        {
            using var output = new MemoryStream(); var buffer = new byte[8192]; int count;
            while ((count = await process.StandardOutput.BaseStream.ReadAsync(buffer, timeout.Token)) > 0)
            {
                if (output.Length + count > 2 * MaximumBytes) throw new IOException(L.Get("DecodedAlarmExceededItsLimit"));
                output.Write(buffer, 0, count);
            }
            await write; await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0) throw new IOException(L.Get("CannotPrepareComputerAlarm") + await errors);
            if (output.Length < 16000 || output.Length % 2 != 0) throw new IOException(L.Get("AlarmDecoderReturnedInvalidPCM"));
            return output.ToArray();
        }
        finally
        {
            timeout.Cancel();
            try { await write; await errors; } catch (Exception e) when (e is IOException or OperationCanceledException) { }
        }
    }
}

/// <summary>Same verified half-duplex speaker bootstrap as talk-back, with a repeating prepared sample.</summary>
internal sealed class CameraAlarm : IAsyncDisposable
{
    readonly CancellationTokenSource stop = new();
    int disposing, packetsSent;
    public int PacketsSent => Volatile.Read(ref packetsSent);
    public string? Error { get; private set; }
    public Task Completion { get; }
    public CameraAlarm(DeviceProfile profile, AlarmClip clip, double? durationSeconds)
    {
        Completion = Task.Run(async () =>
        {
            try
            {
                using var client = new CameraClient(profile.Ip, profile.Password, profile.Uid, profile.Protocol);
                using var setup = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); setup.CancelAfter(10000);
                await client.ConnectAsync(setup.Token);
                await client.FirmwareAsync().WaitAsync(setup.Token);
                try
                {
                    await client.StartSpeakerAsync(profile.StreamQuality, setup.Token);
                    await SendLoopAsync(clip, durationSeconds, packet => { client.SendTalkAudio(packet); Interlocked.Increment(ref packetsSent); }, stop.Token);
                }
                finally { if (client.Connected) await client.StopSpeakerAsync(); }
            }
            catch (Exception e) { if (!stop.IsCancellationRequested) Error = e is OperationCanceledException ? L.Get("CameraSpeakerInitializationTimedOut") : e.Message; }
        });
    }
    internal static async Task SendLoopAsync(AlarmClip clip, double? durationSeconds, Action<byte[]> send, CancellationToken cancellation)
    {
        if (clip.Packets.Count == 0) throw new ArgumentException(L.Get("EmptyAlarmSample"));
        var clock = Stopwatch.StartNew(); long sent = 0;
        while (!cancellation.IsCancellationRequested)
        {
            double target = sent * .064;
            if (durationSeconds is { } duration && target >= duration)
            {
                double tail = duration - clock.Elapsed.TotalSeconds;
                if (tail > 0) await Task.Delay(TimeSpan.FromSeconds(tail), cancellation);
                return;
            }
            double delay = target - clock.Elapsed.TotalSeconds;
            if (delay < -.8) throw new IOException(L.Get("AlarmAudioFellBehindRealTime"));
            if (delay > 0) await Task.Delay(TimeSpan.FromSeconds(delay), cancellation);
            cancellation.ThrowIfCancellationRequested();
            send(clip.Packets[(int)(sent % clip.Packets.Count)]); sent++;
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposing, 1) == 0)
        { stop.Cancel(); try { await Completion; } finally { stop.Dispose(); } }
        else await Completion;
    }
}

internal sealed class ComputerAlarm : IAsyncDisposable
{
    readonly CancellationTokenSource stop = new();
    int disposing, started;
    public bool Started => Volatile.Read(ref started) != 0;
    public string? Error { get; private set; }
    public Task Completion { get; }
    public ComputerAlarm(byte[] pcm, string? device, double? durationSeconds, Func<IPcmPlaybackOutput>? outputFactory = null)
    {
        Completion = Task.Run(async () =>
        {
            try
            {
                using var output = outputFactory?.Invoke() ?? new PcmOutput(device, fullVolume: true);
                await PlayAsync(pcm, durationSeconds, output, () => Volatile.Write(ref started, 1), stop.Token);
            }
            catch (Exception e) { if (!stop.IsCancellationRequested) Error = e.Message; }
        });
    }
    internal static async Task PlayAsync(byte[] pcm, double? durationSeconds, IPcmPlaybackOutput output, Action started, CancellationToken cancellation)
    {
        if (pcm.Length == 0 || pcm.Length % 2 != 0) throw new ArgumentException(L.Get("InvalidPCMSample"));
        long remaining = durationSeconds is { } duration ? (long)(duration * 16000) * 2 : long.MaxValue;
        int at = 0;
        while (remaining > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            int count = (int)Math.Min(Math.Min(2048, pcm.Length - at), remaining);
            // A disconnected/stalled output cannot retain a sounding state forever.
            using (var writeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                writeTimeout.CancelAfter(3000);
                try { await output.WriteAsync(pcm[at..(at + count)], writeTimeout.Token); }
                catch (OperationCanceledException e) when (!cancellation.IsCancellationRequested)
                { throw new IOException(L.Get("ComputerAlarmOutputStoppedAcceptingAudio"), e); }
            }
            started(); remaining -= count; at = (at + count) % pcm.Length;
        }
        using var drainTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); drainTimeout.CancelAfter(3000);
        try { await output.DrainAsync(drainTimeout.Token); }
        catch (OperationCanceledException e) when (!cancellation.IsCancellationRequested)
        { throw new IOException(L.Get("ComputerAlarmOutputStoppedResponding"), e); }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposing, 1) == 0)
        { stop.Cancel(); try { await Completion; } finally { stop.Dispose(); } }
        else await Completion;
    }
}
