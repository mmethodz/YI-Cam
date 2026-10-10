using System.Text;
using System.Security.Cryptography;
using QRCoder;

namespace YiLocal.Core;

/// <summary>Stock QR composition and explicit setup for the patched local firmware.</summary>
public static class QrProvisioning
{
    // Public, fixed obfuscation mask embedded in the vendor format; not a camera/account secret.
    const string Mask = "89JFSjo8HUbhou5776NJOMp9i90ghg7Y78G78t68899y79HY7g7y87y9ED45Ew30O0jkkl";
    static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    public static string ComposeLocal(string ssid, string password, string region = "EU")
    {
        if (region is not ("EU" or "US" or "CN")) throw new ArgumentException(L.Get("LocalQrRegionRequired"));
        if (ssid.Any(char.IsControl) || password.Length != 0 &&
            (password.Length is < 8 or > 63 || password.Any(c => c is < ' ' or > '~')))
            throw new ArgumentException(L.Get("LocalQrNetworkLimits"));
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        string marker = region + new string(Enumerable.Range(0, 18).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]).ToArray());
        return Compose(ssid, password, marker);
    }
    public static string Compose(string ssid, string password, string? bindingToken, bool changeWifi = false, string? deviceId = null)
    {
        if (Utf8.GetByteCount(ssid) is < 1 or > 32 || ssid.Contains('\0'))
            throw new ArgumentException(L.Get("TheWiFiNameMustContain132UTF8Bytes"));
        if (password.Length > 64 || password.Contains('\0')) throw new ArgumentException(L.Get("TheWiFiPasswordMustContainAtMost64CharactersWithout"));
        if (!changeWifi && (string.IsNullOrWhiteSpace(bindingToken) || bindingToken.Length > 256 ||
            bindingToken.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '_' and not '-' and not '~')))
            throw new ArgumentException(L.Get("FreshSetupNeedsAValidExistingBindingTokenAccountFreeToken"));
        if (changeWifi && (string.IsNullOrWhiteSpace(deviceId) || deviceId.Length > 64 || deviceId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')))
            throw new ArgumentException(L.Get("WiFiChangeQRNeedsTheCameraSDeviceIDShown"));
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
