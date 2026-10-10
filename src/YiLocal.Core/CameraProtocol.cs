using System.Text;

namespace YiLocal.Core;

public static class CameraProtocol
{
    public const string Stock = "yi-stock";
    public const string LocalPlain = "openyi-lan-plain-v1";
    public const string LocalPlainFirmware = "6.0.24.10_202610100003";
    public static bool IsPlain(string protocol) => protocol switch
    {
        Stock => false,
        LocalPlain => true,
        _ => throw new ArgumentException(L.Get("UnknownCameraProtocol"))
    };
    // A published format marker, not a password or authentication mechanism.
    internal static byte[] PlainMarker()
    {
        var marker = new byte[32]; Encoding.ASCII.GetBytes("OpenYI-LAN-v1").CopyTo(marker, 0); return marker;
    }
    internal static void CheckPlainFirmware(string firmware)
    {
        if (firmware != LocalPlainFirmware) throw new NotSupportedException(L.Get("LocalPlainFirmwareRequired"));
    }
}
