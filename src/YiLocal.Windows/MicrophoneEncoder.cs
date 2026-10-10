using System.Diagnostics;
using System.Runtime.CompilerServices;
using YiLocal.Core;

namespace YiLocal.Windows;

/// <summary>Streaming native FFmpeg AAC encoder. Audio stays in memory and never enters the recording catalogue.</summary>
internal sealed class MicrophoneEncoder : IAsyncDisposable
{
    readonly IPcmCaptureInput microphone;
    readonly Process process;
    readonly CancellationTokenSource stop = new();
    readonly Task writer, errors;
    Exception? writeError;
    string lastError = "";
    public MicrophoneEncoder(string executable, IPcmCaptureInput microphone, Action<double>? inputLevel = null)
    {
        this.microphone = microphone;
        var info = MediaTools.StartInfo(executable, "-hide_banner", "-loglevel", "error", "-probesize", "32", "-analyzeduration", "0",
            "-f", "s16le", "-ar", "16000", "-ac", "1", "-blocksize", "2048", "-i", "pipe:0",
            "-c:a", "aac", "-profile:a", "aac_low", "-b:a", "32k", "-f", "adts", "-flush_packets", "1", "pipe:1");
        info.RedirectStandardInput = info.RedirectStandardOutput = true;
        try { process = Process.Start(info) ?? throw new IOException(L.Get("CouldNotStartTheMicrophoneEncoder")); }
        catch { microphone.Dispose(); stop.Dispose(); throw; }
        writer = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var pcm = await microphone.ReadAsync(stop.Token);
                    inputLevel?.Invoke(Peak(pcm));
                    await process.StandardInput.BaseStream.WriteAsync(pcm, stop.Token);
                    await process.StandardInput.BaseStream.FlushAsync(stop.Token);
                }
            }
            catch (Exception e) { if (!stop.IsCancellationRequested) writeError = e; }
            finally { try { process.StandardInput.Close(); } catch (IOException) { } }
        });
        errors = Task.Run(async () =>
        {
            try { while (await process.StandardError.ReadLineAsync(stop.Token) is { } line) lastError = line.Length > 600 ? line[..600] : line; }
            catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException) { }
        });
    }
    internal static double Peak(byte[] pcm)
    {
        int maximum = 0;
        for (int offset = 0; offset + 1 < pcm.Length; offset += 2)
            maximum = Math.Max(maximum, Math.Abs((int)System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(offset))));
        return maximum / 32768.0;
    }
    public async IAsyncEnumerable<byte[]> Packets([EnumeratorCancellation] CancellationToken cancellation)
    {
        while (true)
        {
            if (writeError is { } error) throw new IOException(L.Get("MicrophoneCaptureStopped") + error.Message, error);
            var header = new byte[7];
            try { await process.StandardOutput.BaseStream.ReadExactlyAsync(header, cancellation).AsTask().WaitAsync(TimeSpan.FromSeconds(3), cancellation); }
            catch (EndOfStreamException e) { throw new IOException(L.Get("MicrophoneEncoderStopped") + (writeError?.Message ?? lastError), e); }
            int length = ((header[3] & 3) << 11) | (header[4] << 3) | (header[5] >> 5);
            if (header[0] != 0xff || (header[1] & 0xf6) != 0xf0 || length is <= 7 or > TalkAudio.MaximumPacketBytes)
                throw new InvalidDataException(L.Get("MicrophoneEncoderReturnedAnInvalidAACFrame"));
            var packet = new byte[length]; header.CopyTo(packet, 0);
            await process.StandardOutput.BaseStream.ReadExactlyAsync(packet.AsMemory(7), cancellation).AsTask().WaitAsync(TimeSpan.FromSeconds(3), cancellation);
            TalkAudio.Validate(packet); yield return packet;
        }
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
        try { await Task.WhenAll(writer, errors); }
        finally { microphone.Dispose(); process.Dispose(); stop.Dispose(); }
    }
}
