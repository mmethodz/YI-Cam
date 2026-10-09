namespace YiLocal.Core;

public sealed record AudioFrame(byte[] Data, ushort Sequence, uint Seconds, uint Milliseconds, ushort Codec, byte Flags);

/// <summary>Use ADTS, not the camera's inconsistent rate/channel flags, as the codec authority.</summary>
public sealed record AacConfiguration(int FrequencyIndex, int Channels)
{
    static readonly int[] Rates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];
    public int SampleRate => FrequencyIndex is >= 0 and < 13 ? Rates[FrequencyIndex] : throw new InvalidDataException("Unsupported AAC sample rate.");
    public byte[] AudioSpecificConfig => [(byte)(0x10 | (FrequencyIndex >> 1)), (byte)((FrequencyIndex << 7) | (Channels << 3))];
    public void Validate()
    {
        if (SampleRate > 48000) throw new InvalidDataException("AAC sample rates above 48 kHz are not supported.");
        if (Channels is < 1 or > 2) throw new InvalidDataException("Only mono/stereo AAC-LC is supported.");
    }
    public static (AacConfiguration Configuration, byte[] Sample) Parse(byte[] adts)
    {
        if (adts.Length < 7 || adts[0] != 0xff || (adts[1] & 0xf6) != 0xf0)
            throw new InvalidDataException("Missing AAC ADTS header.");
        int header = (adts[1] & 1) == 0 ? 9 : 7;
        int size = ((adts[3] & 3) << 11) | (adts[4] << 3) | (adts[5] >> 5);
        if (size != adts.Length || size <= header || (adts[6] & 3) != 0 || (adts[2] >> 6) != 1)
            throw new InvalidDataException("Expected one complete AAC-LC access unit.");
        var configuration = new AacConfiguration((adts[2] >> 2) & 15, ((adts[2] & 1) << 2) | (adts[3] >> 6));
        configuration.Validate();
        return (configuration, adts[header..]);
    }
}
