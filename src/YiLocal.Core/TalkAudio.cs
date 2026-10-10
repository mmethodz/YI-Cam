using System.Security.Cryptography;
using System.Text;

namespace YiLocal.Core;

/// <summary>Observed mobile TNP AAC talk-back envelope; unrelated to recorded camera timestamps.</summary>
public static class TalkAudio
{
    public const int SampleRate = 16000;
    public const int SamplesPerPacket = 1024;
    public const int MaximumPacketBytes = 1024;
    public static void Validate(byte[] adts)
    {
        var (configuration, _) = AacConfiguration.Parse(adts);
        if (configuration.SampleRate != SampleRate || configuration.Channels != 1 || adts.Length > MaximumPacketBytes)
            throw new InvalidDataException("Talk-back requires AAC-LC 16 kHz mono, at most 1024 bytes per ADTS packet.");
    }
    internal static byte[] Message(byte[] adts, string key, uint frameNumber)
    {
        Validate(adts);
        var data = (byte[])adts.Clone(); int encrypted = data.Length / 16 * 16;
        using var cipher = Aes.Create(); cipher.Key = Encoding.UTF8.GetBytes(key + "0");
        if (encrypted > 0) cipher.EncryptEcb(data.AsSpan(0, encrypted), PaddingMode.None).CopyTo(data, 0);
        var header = new byte[24]; Wire.U16(138).CopyTo(header, 0); header[2] = 2;
        // The legacy mobile sender uses a nonzero counter (20 per AAC packet), with seq/ms zero.
        // The related firmware ignores this value except for rejecting zero. Pace packets at 64 ms.
        uint stamp = unchecked(frameNumber * 20); Wire.U32(stamp == 0 ? 20 : stamp).CopyTo(header, 12);
        return Wire.Join([2, 2, 0, 0], Wire.U32((uint)(24 + data.Length)), header, data);
    }
}
