using System.Runtime.InteropServices;

namespace YiLocal.Windows;

internal interface IPcmCaptureInput : IDisposable
{
    Task<byte[]> ReadAsync(CancellationToken cancellation);
}

internal sealed record MicrophoneDevice(uint Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>16 kHz mono PCM input. Eight 64 ms buffers bound capture latency and memory.</summary>
internal sealed class PcmInput : IPcmCaptureInput
{
    [StructLayout(LayoutKind.Sequential, Pack = 2)] struct Format
    { public ushort Tag, Channels; public uint Rate, BytesPerSecond; public ushort Align, Bits, Extra; }
    [StructLayout(LayoutKind.Sequential)] struct Header
    { public IntPtr Data; public uint Length, Recorded; public IntPtr User; public uint Flags, Loops; public IntPtr Next, Reserved; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct Capabilities
    {
        public ushort Manufacturer, Product; public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        public uint Formats; public ushort Channels, Reserved;
    }
    [DllImport("winmm.dll")] static extern uint waveInGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] static extern uint waveInGetDevCapsW(UIntPtr device, out Capabilities capabilities, uint size);
    [DllImport("winmm.dll")] static extern uint waveInOpen(out IntPtr handle, uint device, ref Format format, IntPtr callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll")] static extern uint waveInPrepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] static extern uint waveInAddBuffer(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] static extern uint waveInStart(IntPtr handle);
    [DllImport("winmm.dll")] static extern uint waveInReset(IntPtr handle);
    [DllImport("winmm.dll")] static extern uint waveInUnprepareHeader(IntPtr handle, IntPtr header, uint size);
    [DllImport("winmm.dll")] static extern uint waveInClose(IntPtr handle);
    readonly List<(IntPtr Header, IntPtr Data)> buffers = [];
    static readonly uint HeaderSize = (uint)Marshal.SizeOf<Header>();
    IntPtr handle;
    int index;
    public static IReadOnlyList<MicrophoneDevice> Devices()
    {
        var result = new List<MicrophoneDevice> { new(uint.MaxValue, "Windows default microphone") };
        for (uint i = 0; i < waveInGetNumDevs(); i++)
            if (waveInGetDevCapsW((UIntPtr)i, out var caps, (uint)Marshal.SizeOf<Capabilities>()) == 0)
                result.Add(new(i, caps.Name));
        return result;
    }
    static void Check(uint error)
    {
        if (error != 0) throw new IOException($"Cannot use the PC microphone (Windows audio error {error}). Check the selected input and Windows microphone access for desktop apps.");
    }
    public PcmInput(string? name)
    {
        var device = name is null ? Devices()[0] : Devices().FirstOrDefault(d => d.Name == name)
            ?? throw new IOException("The saved microphone is unavailable. Select an available microphone before talking.");
        var format = new Format { Tag = 1, Channels = 1, Rate = 16000, BytesPerSecond = 32000, Align = 2, Bits = 16 };
        Check(waveInOpen(out handle, device.Id, ref format, IntPtr.Zero, IntPtr.Zero, 0));
        try
        {
            for (int i = 0; i < 8; i++)
            {
                var data = Marshal.AllocHGlobal(2048); var header = Marshal.AllocHGlobal((int)HeaderSize);
                Marshal.StructureToPtr(new Header { Data = data, Length = 2048 }, header, false);
                uint error = waveInPrepareHeader(handle, header, HeaderSize);
                if (error != 0) { Marshal.FreeHGlobal(data); Marshal.FreeHGlobal(header); Check(error); }
                buffers.Add((header, data)); Check(waveInAddBuffer(handle, header, HeaderSize));
            }
            Check(waveInStart(handle));
        }
        catch { Dispose(); throw; }
    }
    public async Task<byte[]> ReadAsync(CancellationToken cancellation)
    {
        long deadline = Environment.TickCount64 + 3000;
        var item = buffers[index]; Header header;
        while (((header = Marshal.PtrToStructure<Header>(item.Header)).Flags & 1) == 0)
        {
            if (Environment.TickCount64 >= deadline) throw new IOException("The microphone stopped supplying audio.");
            await Task.Delay(5, cancellation);
        }
        cancellation.ThrowIfCancellationRequested();
        if (buffers.All(buffer => (Marshal.PtrToStructure<Header>(buffer.Header).Flags & 1) != 0))
            throw new IOException("Microphone processing fell behind. Talking stopped to avoid delayed speech.");
        if (header.Recorded is 0 or > 2048 || header.Recorded % 2 != 0) throw new IOException("Invalid microphone PCM block.");
        var pcm = new byte[header.Recorded]; Marshal.Copy(item.Data, pcm, 0, pcm.Length);
        Check(waveInAddBuffer(handle, item.Header, HeaderSize)); index = (index + 1) % buffers.Count;
        return pcm;
    }
    public void Dispose()
    {
        if (handle == IntPtr.Zero) return;
        waveInReset(handle);
        foreach (var item in buffers)
            if (waveInUnprepareHeader(handle, item.Header, HeaderSize) == 0)
            { Marshal.FreeHGlobal(item.Data); Marshal.FreeHGlobal(item.Header); }
        buffers.Clear(); waveInClose(handle); handle = IntPtr.Zero;
    }
}
