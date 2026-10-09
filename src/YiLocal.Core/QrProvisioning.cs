using System.Text;
using QRCoder;

namespace YiLocal.Core;

/// <summary>Observed QR composition only. This does not implement or bypass the binding service.</summary>
public static class QrProvisioning
{
    // Public, fixed obfuscation mask embedded in the vendor format; not a camera/account secret.
    const string Mask = "89JFSjo8HUbhou5776NJOMp9i90ghg7Y78G78t68899y79HY7g7y87y9ED45Ew30O0jkkl";
    static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    public static string Compose(string ssid, string password, string? bindingToken, bool changeWifi = false, string? deviceId = null)
    {
        if (Utf8.GetByteCount(ssid) is < 1 or > 32 || ssid.Contains('\0'))
            throw new ArgumentException("The Wi-Fi name must contain 1–32 UTF-8 bytes without a NUL character.");
        if (password.Length > 64 || password.Contains('\0')) throw new ArgumentException("The Wi-Fi password must contain at most 64 characters without NUL.");
        if (!changeWifi && (string.IsNullOrWhiteSpace(bindingToken) || bindingToken.Length > 256 ||
            bindingToken.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '_' and not '-' and not '~')))
            throw new ArgumentException("Fresh setup needs a valid existing binding token. Account-free token generation has not been established.");
        if (changeWifi && (string.IsNullOrWhiteSpace(deviceId) || deviceId.Length > 64 || deviceId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')))
            throw new ArgumentException("Wi-Fi-change QR needs the camera's device ID shown in the vendor app (not the wire UID).");
        var masked = password.ToCharArray();
        for (int i = 0; i < masked.Length; i++) { char x = (char)(password[i] ^ Mask[i % Mask.Length]); masked[i] = x == '\0' ? password[i] : x; }
        return (changeWifi ? "t=1" : "b=" + bindingToken) + "&s=" + Convert.ToBase64String(Utf8.GetBytes(ssid)) +
            "&p=" + Convert.ToBase64String(Utf8.GetBytes(new string(masked))) + (changeWifi ? "&d=" + deviceId : "");
    }
    public static byte[] Png(string payload)
    {
        using var data = QRCodeGenerator.GenerateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        using var code = new PngByteQRCode(data);
        return code.GetGraphic(12); // Includes a quiet zone, suitable for a phone screen or printout.
    }
}
