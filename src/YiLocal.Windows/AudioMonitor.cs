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
internal interface IPcmPlaybackOutput : IDisposable
{
    double Seconds { get; }
    Task WriteAsync(byte[] pcm, CancellationToken cancellation);
    Task DrainAsync(CancellationToken cancellation);
}

internal sealed record PlaybackDevice(uint Id, string Name)
{
    public override string ToString() => Name;
}

internal sealed class PcmOutput : IPcmPlaybackOutput
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct Capabilities
    {
        public ushort Manufacturer, Product; public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        public uint Formats; public ushort Channels, Reserved; public uint Support;
    }
    [DllImport("winmm.dll")] static extern uint waveOutGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] static extern uint waveOutGetDevCapsW(UIntPtr device, out Capabilities capabilities, uint size);
    [DllImport("winmm.dll")] static extern uint waveOutGetVolume(IntPtr handle, out uint volume);
    [DllImport("winmm.dll")] static extern uint waveOutSetVolume(IntPtr handle, uint volume);
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
    [StructLayout(LayoutKind.Sequential)] struct Position { public uint Type, Value, Padding; }
    [DllImport("winmm.dll")] static extern uint waveOutGetPosition(IntPtr handle, ref Position position, uint size);
    readonly IntPtr handle;
    readonly uint? previousVolume;
    readonly object clockLock = new();
    double lastPosition;
    bool disposed;
    readonly Queue<(IntPtr Header, IntPtr Data)> pending = new();
    static readonly uint HeaderSize = (uint)Marshal.SizeOf<Header>();
    static void Check(uint result) { if (result != 0) throw new IOException($"Windows audio output failed ({result})."); }
    public double Seconds
    {
        get
        {
            lock (clockLock)
            {
                if (disposed) return lastPosition;
                var position = new Position { Type = 2 };
                if (waveOutGetPosition(handle, ref position, (uint)Marshal.SizeOf<Position>()) != 0) return lastPosition;
                lastPosition = position.Type switch { 2 => position.Value / 16000.0, 1 => position.Value / 1000.0, 4 => position.Value / 32000.0, _ => lastPosition };
                return lastPosition;
            }
        }
    }
    public static IReadOnlyList<PlaybackDevice> Devices()
    {
        var result = new List<PlaybackDevice> { new(uint.MaxValue, "Windows default output") };
        for (uint i = 0; i < waveOutGetNumDevs(); i++)
            if (waveOutGetDevCapsW((UIntPtr)i, out var caps, (uint)Marshal.SizeOf<Capabilities>()) == 0) result.Add(new(i, caps.Name));
        return result;
    }
    public static PlaybackDevice ResolveDevice(string? name)
    {
        if (name is null) return Devices()[0];
        var matching = Devices().Skip(1).Where(device => device.Name == name).ToArray();
        if (matching.Length != 1) throw new IOException("The saved alarm output is unavailable or its name is ambiguous. Select a uniquely named output device; no fallback speaker was used.");
        return matching[0];
    }
    public PcmOutput(string? deviceName = null, bool fullVolume = false)
    {
        var format = new Format { Tag = 1, Channels = 1, Rate = 16000, BytesPerSecond = 32000, Align = 2, Bits = 16 };
        Check(waveOutOpen(out handle, ResolveDevice(deviceName).Id, ref format, IntPtr.Zero, IntPtr.Zero, 0));
        if (fullVolume)
        {
            try
            {
                Check(waveOutGetVolume(handle, out uint volume)); previousVolume = volume;
                // Use the open handle: this changes this output instance, never all devices or the system master.
                Check(waveOutSetVolume(handle, uint.MaxValue));
            }
            catch { waveOutClose(handle); throw; }
        }
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
    public async Task DrainAsync(CancellationToken cancellation)
    { while (pending.Count > 0) { Reclaim(); await Task.Delay(5, cancellation); } }
    public void Dispose()
    {
        lock (clockLock)
        {
            if (disposed) return;
            _ = Seconds; disposed = true; waveOutReset(handle); Reclaim(true);
            if (previousVolume is { } volume && waveOutGetVolume(handle, out uint current) == 0 && current == uint.MaxValue)
                waveOutSetVolume(handle, volume);
            waveOutClose(handle);
        }
    }
}
