using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using YiLocal.Core;

namespace YiLocal.Windows;

/// <summary>Read the current connection through Native Wi-Fi, without localized netsh output or profile files.</summary>
internal static class WindowsWifi
{
    [DllImport("wlanapi.dll")] static extern uint WlanOpenHandle(uint version, IntPtr reserved, out uint negotiated, out IntPtr handle);
    [DllImport("wlanapi.dll")] static extern uint WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] static extern uint WlanQueryInterface(IntPtr handle, ref Guid id, uint code, IntPtr reserved, out uint size, out IntPtr data, out uint type);
    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)] static extern uint WlanGetProfile(IntPtr handle, ref Guid id, string profile, IntPtr reserved, out IntPtr xml, ref uint flags, out uint access);
    [DllImport("wlanapi.dll")] static extern void WlanFreeMemory(IntPtr data);
    [DllImport("wlanapi.dll")] static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct InterfaceInfo
    {
        public Guid Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Description;
        public uint State;
    }

    public static IReadOnlyList<WifiSetupNetwork> ReadCurrent()
    {
        uint error = WlanOpenHandle(2, IntPtr.Zero, out _, out var handle);
        if (error != 0) throw new Win32Exception((int)error, L.Get("Setup.PcWifiUnavailable"));
        try
        {
            error = WlanEnumInterfaces(handle, IntPtr.Zero, out var list);
            if (error != 0) throw new Win32Exception((int)error, L.Get("Setup.PcWifiUnavailable"));
            try
            {
                var result = new List<WifiSetupNetwork>();
                int count = Marshal.ReadInt32(list);
                if (count is < 0 or > 64) throw new IOException(L.Get("Setup.PcWifiUnavailable"));
                bool denied = false;
                for (int index = 0; index < count; index++)
                {
                    var info = Marshal.PtrToStructure<InterfaceInfo>(list + 8 + index * Marshal.SizeOf<InterfaceInfo>());
                    if (info.State != 1) continue; // wlan_interface_state_connected
                    error = WlanQueryInterface(handle, ref info.Id, 7, IntPtr.Zero, out uint size, out var data, out _);
                    if (error != 0) { denied |= error == 5; continue; }
                    try
                    {
                        // WLAN_CONNECTION_ATTRIBUTES: state/mode, WCHAR profile[256], DOT11_SSID.
                        if (size < 556 || Marshal.ReadInt32(data) != 1) continue;
                        int length = Marshal.ReadInt32(data, 520);
                        if (length is < 1 or > 32) continue;
                        byte[] ssid = new byte[length]; Marshal.Copy(data + 524, ssid, 0, length);
                        string network;
                        try { network = new UTF8Encoding(false, true).GetString(ssid); }
                        catch (DecoderFallbackException) { continue; }
                        string profile = (Marshal.PtrToStringUni(data + 8, 256) ?? "").TrimEnd('\0');
                        string? password = null;
                        uint flags = 4; // WLAN_PROFILE_GET_PLAINTEXT_KEY; Windows controls access.
                        error = WlanGetProfile(handle, ref info.Id, profile, IntPtr.Zero, out var xml, ref flags, out _);
                        if (error == 0)
                            try
                            {
                                try { password = ProfilePassword(Marshal.PtrToStringUni(xml) ?? ""); }
                                catch (XmlException) { /* Keep the detected SSID; ask for a password instead. */ }
                            }
                            finally { WlanFreeMemory(xml); }
                        result.Add(new(network, password));
                    }
                    finally { WlanFreeMemory(data); }
                }
                if (result.Count == 0 && denied) throw new Win32Exception(5, L.Get("Setup.WifiPermission"));
                return result;
            }
            finally { WlanFreeMemory(list); }
        }
        finally { WlanCloseHandle(handle, IntPtr.Zero); }
    }

    internal static string? ProfilePassword(string xml)
    {
        using var input = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 131072 });
        var document = XDocument.Load(input);
        XNamespace ns = "http://www.microsoft.com/networking/WLAN/profile/v1";
        var security = document.Root?.Element(ns + "MSM")?.Element(ns + "security");
        var encryption = security?.Element(ns + "authEncryption")?.Element(ns + "encryption")?.Value;
        if (encryption == "none") return "";
        var key = security?.Element(ns + "sharedKey");
        // Never mistake encrypted key material or an enterprise profile for an open network.
        return key?.Element(ns + "protected")?.Value == "false" ? key.Element(ns + "keyMaterial")?.Value : null;
    }
}
