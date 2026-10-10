using System.Security.Cryptography;
using System.Text;
using YiLocal.Core;

static class TalkChecks
{
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    public static byte[] Packet(byte marker, int length = 37)
    {
        var data = Enumerable.Repeat(marker, length).ToArray();
        data[0] = 0xff; data[1] = 0xf1; data[2] = 0x60;
        data[3] = (byte)(0x40 | (length >> 11)); data[4] = (byte)(length >> 3);
        data[5] = (byte)((length << 5) | 31); data[6] = 0xfc;
        return data;
    }
    public static void Run()
    {
        var clear = Packet(0x5a); var original = clear.ToArray();
        var message = TalkAudio.Message(clear, "AAAAAAAAAAAAAAA", 7);
        Require(clear.SequenceEqual(original), "Talk encoding mutated the caller's AAC packet.");
        Require(message[..4].SequenceEqual(new byte[] { 2, 2, 0, 0 }) && Wire.U32(message.AsSpan(4)) == 61,
            "Talk message kind or size is invalid.");
        Require(Wire.U16(message.AsSpan(8)) == 138 && message[10] == 2 && Wire.U32(message.AsSpan(20)) == 140 &&
            Wire.U16(message.AsSpan(14)) == 0 && Wire.U32(message.AsSpan(28)) == 0, "Talk frame metadata changed.");
        using var cipher = Aes.Create(); cipher.Key = Encoding.ASCII.GetBytes("AAAAAAAAAAAAAAA0");
        var decrypted = cipher.DecryptEcb(message.AsSpan(32, 32), PaddingMode.None);
        Require(decrypted.SequenceEqual(clear[..32]) && message[64..].SequenceEqual(clear[32..]), "Talk AES blocks or clear tail changed.");
        Require(Wire.U32(TalkAudio.Message(clear, "AAAAAAAAAAAAAAA", 0).AsSpan(20)) != 0, "Talk timestamp zero can be rejected by firmware.");
        var wrongRate = Packet(1); wrongRate[2] = 0x50;
        var stereo = Packet(1); stereo[3] = 0x80;
        var mainProfile = Packet(1); mainProfile[2] = 0x20;
        foreach (var invalid in new[] { Array.Empty<byte>(), Packet(1)[..20], Packet(1, 1025), wrongRate, stereo, mainProfile })
        {
            try { TalkAudio.Validate(invalid); } catch (InvalidDataException) { continue; }
            throw new Exception("Invalid talk audio was accepted.");
        }
        Console.WriteLine("Talk AAC envelope, encryption/tail, immutable input and format rejection passed.");
    }
}
