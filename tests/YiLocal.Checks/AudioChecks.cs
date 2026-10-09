using YiLocal.Core;

static class AudioChecks
{
    public static bool Fixture(string[] args)
    {
        if (args.Length == 0 || args[0] != "--av-fixture") return false;
        if (args.Length != 6) throw new ArgumentException("--av-fixture <video.h264> <audio.aac> <output> <ffmpeg> <Original|Balanced>");
        var source = File.ReadAllBytes(args[2]); var audio = new List<byte[]>();
        for (int at = 0; at < source.Length;)
        {
            if (source.Length - at < 7) throw new InvalidDataException();
            int size = ((source[at + 3] & 3) << 11) | (source[at + 4] << 3) | (source[at + 5] >> 5);
            if (size < 7 || size > source.Length - at) throw new InvalidDataException();
            audio.Add(source[at..(at + size)]); at += size;
        }
        var pictures = new List<byte[]>(); var nals = new List<byte[]>();
        void Flush()
        {
            if (nals.Any(n => (n[0] & 31) is 1 or 5)) pictures.Add(Wire.Join(nals.Select(n => Wire.Join([0, 0, 0, 1], n)).ToArray()));
            nals.Clear();
        }
        foreach (var nal in FragmentedMp4.Nals(File.ReadAllBytes(args[1]))) { if ((nal[0] & 31) == 9) Flush(); nals.Add(nal); }
        Flush();
        var encoding = Enum.Parse<RecordingEncoding>(args[5]);
        using var recorder = new SegmentRecorder(args[3], new(SegmentMinutes: 0.1), new(encoding, encoding == RecordingEncoding.Original ? null : 1, IncludeAudio: true), args[4], "AV fixture");
        recorder.WriteAudio(new(audio[0], 65535, 0, 0, 138, 27), -64); // Known configuration before the first IDR.
        int a = 0;
        for (int v = 0; v < pictures.Count; v++)
        {
            long time = v * 67;
            while (a < audio.Count && 250 + a * 64 + a / 50 * 10 <= time)
            {
                long stamp = 250 + a * 64 + a / 50 * 10;
                recorder.WriteAudio(new(audio[a], (ushort)a, 0, (uint)stamp, 138, 27), stamp); a++;
            }
            bool key = FragmentedMp4.Nals(pictures[v]).Any(n => (n[0] & 31) == 5);
            recorder.Write(new(pictures[v], (ushort)v, 160, 90, 0, (uint)time, key, 1, 78), time);
            if (encoding != RecordingEncoding.Original) Thread.Sleep(15);
        }
        Console.WriteLine($"Muxed {pictures.Count} video pictures and {a} delayed AAC packets with camera timestamp gaps.");
        return true;
    }
}
