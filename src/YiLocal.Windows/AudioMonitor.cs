using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using YiLocal.Core;

namespace YiLocal.Windows;

/// <summary>Bounded live AAC decoder. Speaker monitoring does not change recorded timestamps.</summary>
internal sealed class AudioMonitor : IDisposable
{
    readonly Process process;
    readonly Channel<byte[]> packets = Channel.CreateBounded<byte[]>(32);
    readonly CancellationTokenSource stop = new();
    readonly Task writer, reader, errors;
    public bool Failed { get; private set; }
    public AudioMonitor(string executable)
    {
        var info = MediaTools.StartInfo(executable, "-hide_banner", "-loglevel", "error", "-probesize", "32", "-analyzeduration", "0",
            "-f", "aac", "-i", "pipe:0", "-vn", "-ac", "1", "-ar", "16000", "-f", "s16le", "-flush_packets", "1", "pipe:1");
        info.RedirectStandardInput = info.RedirectStandardOutput = true;
        process = Process.Start(info) ?? throw new IOException("Could not start the audio decoder.");
        writer = Task.Run(async () =>
        {
            try
            {
                await foreach (var packet in packets.Reader.ReadAllAsync(stop.Token))
                { await process.StandardInput.BaseStream.WriteAsync(packet, stop.Token); await process.StandardInput.BaseStream.FlushAsync(stop.Token); }
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { Failed = true; }
        });
        reader = Task.Run(async () =>
        {
            try
            {
                using var speakers = new PcmOutput(); var buffer = new byte[2048];
                while (!stop.IsCancellationRequested)
                {
                    await process.StandardOutput.BaseStream.ReadExactlyAsync(buffer, stop.Token);
                    await speakers.WriteAsync(buffer, stop.Token);
                }
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { Failed = true; }
        });
        errors = Task.Run(async () =>
        {
            try { while (await process.StandardError.ReadLineAsync(stop.Token) is not null) { } }
            catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException) { }
        });
    }
    public bool Push(AudioFrame frame)
    {
        if (frame.Codec != 138) return false;
        try { AacConfiguration.Parse(frame.Data); } catch (InvalidDataException) { return false; }
        return !Failed && packets.Writer.TryWrite(frame.Data);
    }
    public void Dispose()
    {
        stop.Cancel(); packets.Writer.TryComplete();
        try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
        try { Task.WaitAll([writer, reader, errors], 2000); } catch (AggregateException) { }
        process.Dispose(); stop.Dispose();
    }
}

/// <summary>Windows speaker output for 16 kHz mono signed PCM; at most 640 ms queued.</summary>
internal sealed class PcmOutput : IDisposable
{
    [StructLayout(LayoutKind.Sequential, Pack = 2)] struct Format
    { public ushort Tag, Channels; public uint Rate, BytesPerSecond; public ushort Align, Bits, Extra; }
    [StructLayout(LayoutKind.Sequential)] struct Header
    { public IntPtr Data; public uint Length, Recorded; public IntPtr User; public uint Flags, Loops; public IntPtr Next, Reserved; }
    [DllImport("winmm.dll")] static extern uint waveOutOpen(out IntPtr handle, uint device, ref Format format, IntPtr callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll")] static extern uint waveOutPrepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] static extern uint waveOutWrite(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] static extern uint waveOutUnprepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] static extern uint waveOutReset(IntPtr handle);
    [DllImport("winmm.dll")] static extern uint waveOutClose(IntPtr handle);
    readonly IntPtr handle;
    readonly Queue<(IntPtr Header, IntPtr Data)> pending = new();
    static readonly uint HeaderSize = (uint)Marshal.SizeOf<Header>();
    static void Check(uint result) { if (result != 0) throw new IOException($"Windows audio output failed ({result})."); }
    public PcmOutput()
    {
        var format = new Format { Tag = 1, Channels = 1, Rate = 16000, BytesPerSecond = 32000, Align = 2, Bits = 16 };
        Check(waveOutOpen(out handle, uint.MaxValue, ref format, IntPtr.Zero, IntPtr.Zero, 0));
    }
    void Reclaim(bool all = false)
    {
        while (pending.TryPeek(out var item) && (all || (Marshal.PtrToStructure<Header>(item.Header).Flags & 1) != 0))
        {
            pending.Dequeue(); waveOutUnprepareHeader(handle, item.Header, HeaderSize);
            Marshal.FreeHGlobal(item.Data); Marshal.FreeHGlobal(item.Header);
        }
    }
    public async Task WriteAsync(byte[] pcm, CancellationToken cancellation)
    {
        Reclaim();
        while (pending.Count >= 10) { await Task.Delay(5, cancellation); Reclaim(); }
        cancellation.ThrowIfCancellationRequested();
        var data = Marshal.AllocHGlobal(pcm.Length); var header = Marshal.AllocHGlobal((int)HeaderSize);
        bool prepared = false;
        try
        {
            Marshal.Copy(pcm, 0, data, pcm.Length);
            Marshal.StructureToPtr(new Header { Data = data, Length = (uint)pcm.Length }, header, false);
            Check(waveOutPrepareHeader(handle, header, HeaderSize)); prepared = true;
            Check(waveOutWrite(handle, header, HeaderSize)); pending.Enqueue((header, data));
        }
        catch
        {
            if (prepared) waveOutUnprepareHeader(handle, header, HeaderSize);
            Marshal.FreeHGlobal(data); Marshal.FreeHGlobal(header); throw;
        }
    }
    public void Dispose() { waveOutReset(handle); Reclaim(true); waveOutClose(handle); }
}
