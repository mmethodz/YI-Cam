using System.Text;

namespace YiLocal.Core;

/// <summary>Minimal AVC fragmented MP4 writer. The verified camera has no B frames.
/// One fragment per picture keeps completed pictures recoverable after interruption.</summary>
public sealed class FragmentedMp4 : IDisposable
{
    readonly Stream output;
    (byte[] Data, long Time, bool Key)? pending;
    uint sequence;
    int lastDuration = 67;
    public long DurationMilliseconds { get; private set; }
    public long Frames { get; private set; }

    public static List<byte[]> Nals(byte[] data)
    {
        var starts = new List<(int Start, int Data)>();
        for (int i = 0; i + 3 <= data.Length; i++)
        {
            if (data[i] != 0 || data[i + 1] != 0) continue;
            int size = data[i + 2] == 1 ? 3 : i + 4 <= data.Length && data[i + 2] == 0 && data[i + 3] == 1 ? 4 : 0;
            if (size == 0) continue;
            starts.Add((i, i + size)); i += size - 1;
        }
        var result = new List<byte[]>();
        for (int i = 0; i < starts.Count; i++)
        {
            int end = i + 1 < starts.Count ? starts[i + 1].Start : data.Length;
            while (end > starts[i].Data && data[end - 1] == 0) end--;
            if (end > starts[i].Data) result.Add(data[starts[i].Data..end]);
        }
        return result;
    }

    static byte[] B(string type, params byte[][] payload)
    {
        var bytes = Wire.Join(payload);
        return Wire.Join(Wire.U32(checked((uint)(bytes.Length + 8))), Encoding.ASCII.GetBytes(type), bytes);
    }
    static byte[] F(string type, uint flags, params byte[][] payload) => B(type, Wire.U32(flags), Wire.Join(payload));
    static byte[] U64(ulong n) => Wire.Join(Wire.U32((uint)(n >> 32)), Wire.U32((uint)n));
    static byte[] Matrix => Wire.Join(Wire.U32(0x10000), new byte[12], Wire.U32(0x10000), new byte[12], Wire.U32(0x40000000));

    public FragmentedMp4(string path, int width, int height, byte[] sps, byte[] pps)
        : this(OpenFile(path, width, height, sps, pps), width, height, sps, pps) { }

    static Stream OpenFile(string path, int width, int height, byte[] sps, byte[] pps)
    {
        ValidateConfiguration(width, height, sps, pps);
        return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536);
    }
    static void ValidateConfiguration(int width, int height, byte[] sps, byte[] pps)
    {
        if (sps.Length is < 4 or > 65535 || pps.Length is < 1 or > 65535 || width is <= 0 or > 8192 || height is <= 0 or > 8192)
            throw new InvalidDataException("Incomplete AVC configuration.");
    }

    /// <summary>Takes ownership of the stream. Used to feed timestamped fragments to an optional encoder.</summary>
    public FragmentedMp4(Stream stream, int width, int height, byte[] sps, byte[] pps)
    {
        ValidateConfiguration(width, height, sps, pps);
        output = stream;
        var avcc = B("avcC", [1, sps[1], sps[2], sps[3], 255, 225], Wire.U16(checked((ushort)sps.Length)), sps,
            [1], Wire.U16(checked((ushort)pps.Length)), pps);
        var sample = B("avc1", new byte[6], Wire.U16(1), new byte[16], Wire.U16((ushort)width), Wire.U16((ushort)height),
            Wire.U32(0x480000), Wire.U32(0x480000), new byte[4], Wire.U16(1), new byte[32], Wire.U16(24), Wire.U16(65535), avcc);
        var stbl = B("stbl", F("stsd", 0, Wire.U32(1), sample), F("stts", 0, new byte[4]),
            F("stsc", 0, new byte[4]), F("stsz", 0, new byte[8]), F("stco", 0, new byte[4]));
        var dinf = B("dinf", F("dref", 0, Wire.U32(1), F("url ", 1)));
        var mdia = B("mdia", F("mdhd", 0, new byte[8], Wire.U32(1000), new byte[4], Wire.U16(0x55c4), new byte[2]),
            F("hdlr", 0, new byte[4], Encoding.ASCII.GetBytes("vide"), new byte[12], Encoding.ASCII.GetBytes("YI Local\0")),
            B("minf", F("vmhd", 1, new byte[8]), dinf, stbl));
        var trak = B("trak", F("tkhd", 7, new byte[8], Wire.U32(1), new byte[16], new byte[8], Matrix,
            Wire.U32((uint)width << 16), Wire.U32((uint)height << 16)), mdia);
        var moov = B("moov", F("mvhd", 0, new byte[8], Wire.U32(1000), new byte[4], Wire.U32(0x10000),
            Wire.U16(0x100), new byte[10], Matrix, new byte[24], Wire.U32(2)), trak,
            B("mvex", F("trex", 0, Wire.U32(1), Wire.U32(1), new byte[12])));
        try
        {
            output.Write(B("ftyp", Encoding.ASCII.GetBytes("isom"), Wire.U32(512), Encoding.ASCII.GetBytes("isomiso6avc1mp41")));
            output.Write(moov); output.Flush();
        }
        catch { output.Dispose(); throw; }
    }

    public void Write(byte[] annexB, long milliseconds)
    {
        var nals = Nals(annexB);
        bool key = nals.Any(n => (n[0] & 31) == 5);
        if (!nals.Any(n => (n[0] & 31) is 1 or 5)) return;
        if (pending.HasValue)
        {
            long delta = milliseconds - pending.Value.Time;
            if (delta is < 1 or > 30000) throw new InvalidDataException("Non-monotonic video timestamps.");
            lastDuration = (int)delta; FlushFrame(lastDuration);
        }
        var sample = Wire.Join(nals.Select(n => Wire.Join(Wire.U32((uint)n.Length), n)).ToArray());
        pending = (sample, milliseconds, key);
    }

    void FlushFrame(int duration)
    {
        if (pending is not { } frame) return;
        byte[] Fragment(uint offset) => B("moof", F("mfhd", 0, Wire.U32(sequence + 1)),
            B("traf", F("tfhd", 0x020000, Wire.U32(1)), F("tfdt", 0x01000000, U64((ulong)frame.Time)),
                F("trun", 0x000701, Wire.U32(1), Wire.U32(offset), Wire.U32((uint)duration),
                    Wire.U32((uint)frame.Data.Length), Wire.U32(frame.Key ? 0x02000000u : 0x01010000u))));
        byte[] moof = Fragment(0);
        output.Write(Fragment((uint)moof.Length + 8));
        output.Write(B("mdat", frame.Data)); output.Flush();
        sequence++; Frames++; DurationMilliseconds = frame.Time + duration; pending = null;
    }
    public void Dispose()
    {
        try { FlushFrame(lastDuration); } finally { output.Dispose(); }
    }
}
