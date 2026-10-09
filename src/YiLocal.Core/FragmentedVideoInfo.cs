using System.Buffers.Binary;
using System.Text;

namespace YiLocal.Core;

/// <summary>Reads sample timing from video-only fragmented MP4 output, without decoding or reading media payloads.</summary>
internal sealed record FragmentedVideoInfo(double Duration, long Frames)
{
    static uint U32(ReadOnlySpan<byte> value) => BinaryPrimitives.ReadUInt32BigEndian(value);
    static ulong U64(ReadOnlySpan<byte> value) => BinaryPrimitives.ReadUInt64BigEndian(value);
    static IEnumerable<(string Type, byte[] Body)> Children(byte[] data)
    {
        for (int offset = 0; offset < data.Length;)
        {
            if (data.Length - offset < 8) throw new InvalidDataException("Truncated MP4 box.");
            int size = checked((int)U32(data.AsSpan(offset)));
            if (size < 8 || size > data.Length - offset) throw new InvalidDataException("Invalid nested MP4 box size.");
            yield return (Encoding.ASCII.GetString(data, offset + 4, 4), data[(offset + 8)..(offset + size)]);
            offset += size;
        }
    }
    public static FragmentedVideoInfo Read(string path)
    {
        using var stream = File.OpenRead(path);
        uint scale = 0, videoTrack = 0, trackDefaultDuration = 0;
        double duration = 0; long frames = 0;
        var header = new byte[16];
        while (stream.Position < stream.Length)
        {
            long start = stream.Position; stream.ReadExactly(header.AsSpan(0, 8));
            long size = U32(header); int headerSize = 8;
            string type = Encoding.ASCII.GetString(header, 4, 4);
            if (size == 1) { stream.ReadExactly(header.AsSpan(8, 8)); size = checked((long)U64(header.AsSpan(8))); headerSize = 16; }
            if (size == 0) size = stream.Length - start;
            if (size < headerSize || size > stream.Length - start) throw new InvalidDataException("Invalid MP4 box size.");
            if (type is not ("moov" or "moof")) { stream.Position = start + size; continue; }
            if (size > 4 * 1024 * 1024) throw new InvalidDataException("MP4 metadata exceeded its bound.");
            var data = new byte[checked((int)size - headerSize)]; stream.ReadExactly(data);
            if (type == "moov")
            {
                foreach (var (_, track) in Children(data).Where(box => box.Type == "trak"))
                {
                    var boxes = Children(track).ToArray();
                    var mdia = Children(boxes.Single(box => box.Type == "mdia").Body).ToArray();
                    var handler = mdia.Single(box => box.Type == "hdlr").Body;
                    if (Encoding.ASCII.GetString(handler, 8, 4) != "vide") continue;
                    if (videoTrack != 0) throw new InvalidDataException("Expected one video track.");
                    var tkhd = boxes.Single(box => box.Type == "tkhd").Body;
                    var mdhd = mdia.Single(box => box.Type == "mdhd").Body;
                    videoTrack = U32(tkhd.AsSpan(tkhd[0] == 1 ? 20 : 12));
                    scale = U32(mdhd.AsSpan(mdhd[0] == 1 ? 20 : 12));
                }
                foreach (var (_, mvex) in Children(data).Where(box => box.Type == "mvex"))
                    foreach (var (_, trex) in Children(mvex).Where(box => box.Type == "trex"))
                        if (U32(trex.AsSpan(4)) == videoTrack) trackDefaultDuration = U32(trex.AsSpan(12));
            }
            else
            {
                foreach (var (_, traf) in Children(data).Where(box => box.Type == "traf"))
                {
                    if (scale == 0) throw new InvalidDataException("Missing video timescale.");
                    var boxes = Children(traf).ToArray(); var tfhd = boxes.Single(box => box.Type == "tfhd").Body;
                    if (U32(tfhd.AsSpan(4)) != videoTrack) continue;
                    uint flags = U32(tfhd) & 0xffffff; int position = 8;
                    if ((flags & 1) != 0) position += 8;
                    if ((flags & 2) != 0) position += 4;
                    uint defaultDuration = (flags & 8) != 0 ? U32(tfhd.AsSpan(position)) : trackDefaultDuration;
                    var tfdt = boxes.Single(box => box.Type == "tfdt").Body;
                    long decode = checked((long)(tfdt[0] == 1 ? U64(tfdt.AsSpan(4)) : U32(tfdt.AsSpan(4))));
                    foreach (var (_, trun) in boxes.Where(box => box.Type == "trun"))
                    {
                        flags = U32(trun) & 0xffffff; uint count = U32(trun.AsSpan(4)); position = 8;
                        if (count > 100000) throw new InvalidDataException("Too many samples in an MP4 fragment.");
                        if ((flags & 1) != 0) position += 4;
                        if ((flags & 4) != 0) position += 4;
                        for (int index = 0; index < count; index++)
                        {
                            uint sampleDuration = defaultDuration;
                            if ((flags & 0x100) != 0) { sampleDuration = U32(trun.AsSpan(position)); position += 4; }
                            if ((flags & 0x200) != 0) position += 4;
                            if ((flags & 0x400) != 0) position += 4;
                            long composition = 0;
                            if ((flags & 0x800) != 0)
                            {
                                uint raw = U32(trun.AsSpan(position)); position += 4;
                                composition = trun[0] == 1 ? unchecked((int)raw) : raw;
                            }
                            if (position > trun.Length || sampleDuration == 0) throw new InvalidDataException("Invalid sample timing.");
                            duration = Math.Max(duration, (decode + composition + sampleDuration) / (double)scale);
                            decode += sampleDuration; frames++;
                        }
                    }
                }
            }
        }
        if (frames == 0 || duration <= 0) throw new InvalidDataException("No timed video samples in the recording.");
        return new(duration, frames);
    }
}
