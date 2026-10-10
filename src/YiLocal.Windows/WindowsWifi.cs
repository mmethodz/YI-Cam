using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using YiLocal.Core;

namespace YiLocal.Windows;

/// <summary>Read connected and saved networks through Native Wi-Fi, without shell output or profile files.</summary>
internal static class WindowsWifi
{
    [DllImport("wlanapi.dll")] static extern uint WlanOpenHandle(uint version, IntPtr reserved, out uint negotiated, out IntPtr handle);
    [DllImport("wlanapi.dll")] static extern uint WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] static extern uint WlanQueryInterface(IntPtr handle, ref Guid id, uint code, IntPtr reserved, out uint size, out IntPtr data, out uint type);
    [DllImport("wlanapi.dll")] static extern uint WlanGetProfileList(IntPtr handle, ref Guid id, IntPtr reserved, out IntPtr list);
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
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ProfileInfo
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Name;
        public uint Flags;
    }

    public static IReadOnlyList<WindowsWifiNetwork> ReadAvailable()
    {
        uint error = WlanOpenHandle(2, IntPtr.Zero, out _, out var handle);
        if (error != 0) throw new Win32Exception((int)error, L.Get("Setup.PcWifiUnavailable"));
        try
        {
            error = WlanEnumInterfaces(handle, IntPtr.Zero, out var list);
            if (error != 0) throw new Win32Exception((int)error, L.Get("Setup.PcWifiUnavailable"));
            try
            {
                var result = new List<WindowsWifiNetwork>();
                int count = Marshal.ReadInt32(list);
                if (count is < 0 or > 64) throw new IOException(L.Get("Setup.PcWifiUnavailable"));
                bool denied = false;
                for (int index = 0; index < count; index++)
                {
                    var info = Marshal.PtrToStructure<InterfaceInfo>(list + 8 + index * Marshal.SizeOf<InterfaceInfo>());
                    // The interface need not be connected: an Ethernet PC can still have saved Wi-Fi profiles.
                    if (info.State == 1) // wlan_interface_state_connected
                    {
                        error = WlanQueryInterface(handle, ref info.Id, 7, IntPtr.Zero, out uint size, out var data, out _);
                        denied |= error == 5;
                        if (error == 0)
                            try
                            {
                                // WLAN_CONNECTION_ATTRIBUTES: state/mode, WCHAR profile[256], DOT11_SSID.
                                if (size >= 556 && Marshal.ReadInt32(data) == 1)
                                {
                                    int length = Marshal.ReadInt32(data, 520);
                                    if (length is >= 1 and <= 32)
                                    {
                                        byte[] bytes = new byte[length]; Marshal.Copy(data + 524, bytes, 0, length);
                                        string profile = (Marshal.PtrToStringUni(data + 8, 256) ?? "").TrimEnd('\0');
                                        var document = ReadProfile(handle, ref info.Id, profile, ref denied);
                                        try { result.Add(new(new(DecodeSsid(bytes), Password(document)), true)); }
                                        catch (DecoderFallbackException) { /* QR setup requires a UTF-8 SSID. */ }
                                    }
                                }
                            }
                            finally { WlanFreeMemory(data); }
                    }
                    error = WlanGetProfileList(handle, ref info.Id, IntPtr.Zero, out var profiles);
                    denied |= error == 5;
                    if (error != 0) continue;
                    try
                    {
                        int profileCount = Marshal.ReadInt32(profiles);
                        if (profileCount is < 0 or > 4096) throw new IOException(L.Get("Setup.PcWifiUnavailable"));
                        for (int p = 0; p < profileCount; p++)
                        {
                            var profile = Marshal.PtrToStructure<ProfileInfo>(profiles + 8 + p * Marshal.SizeOf<ProfileInfo>());
                            var document = ReadProfile(handle, ref info.Id, profile.Name, ref denied);
                            if (document is null) continue;
                            foreach (var network in ProfileNetworks(document)) result.Add(new(network, false));
                        }
                    }
                    finally { WlanFreeMemory(profiles); }
                }
                if (result.Count == 0 && denied) throw new Win32Exception(5, L.Get("Setup.WifiPermission"));
                return result.OrderByDescending(n => n.Connected).ThenByDescending(n => n.Network.Password is not null)
                    .DistinctBy(n => n.Network.Ssid, StringComparer.Ordinal).ToArray();
            }
            finally { WlanFreeMemory(list); }
        }
        finally { WlanCloseHandle(handle, IntPtr.Zero); }
    }

    static XDocument? ReadProfile(IntPtr handle, ref Guid id, string profile, ref bool denied)
    {
        uint flags = 4; // WLAN_PROFILE_GET_PLAINTEXT_KEY; Windows controls access.
        uint error = WlanGetProfile(handle, ref id, profile, IntPtr.Zero, out var xml, ref flags, out _);
        denied |= error == 5;
        if (error != 0) return null;
        try
        {
            try { return Parse(Marshal.PtrToStringUni(xml) ?? ""); }
            catch (XmlException) { return null; }
        }
        finally { WlanFreeMemory(xml); }
    }

    static XDocument Parse(string xml)
    {
        using var input = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 131072 });
        return XDocument.Load(input);
    }
    static readonly XNamespace ns = "http://www.microsoft.com/networking/WLAN/profile/v1";
    internal static string? ProfilePassword(string xml) => Password(Parse(xml));
    static string? Password(XDocument? document)
    {
        var security = document?.Root?.Element(ns + "MSM")?.Element(ns + "security");
        var encryption = security?.Element(ns + "authEncryption")?.Element(ns + "encryption")?.Value;
        if (encryption == "none") return "";
        var key = security?.Element(ns + "sharedKey");
        // Never mistake encrypted key material or an enterprise profile for an open network.
        return key?.Element(ns + "protected")?.Value == "false" ? key.Element(ns + "keyMaterial")?.Value : null;
    }

    internal static IReadOnlyList<WifiSetupNetwork> ProfileNetworks(string xml) => ProfileNetworks(Parse(xml));
    static IReadOnlyList<WifiSetupNetwork> ProfileNetworks(XDocument document)
    {
        var result = new List<WifiSetupNetwork>();
        var ssids = document.Root?.Element(ns + "SSIDConfig")?.Elements(ns + "SSID") ?? [];
        foreach (var ssid in ssids)
        {
            // Profile names can be renamed. The SSID hex bytes are authoritative, not the profile/display name.
            string? name;
            try
            {
                name = ssid.Element(ns + "hex") is { } hex ? DecodeSsid(Convert.FromHexString(hex.Value)) : ssid.Element(ns + "name")?.Value;
            }
            catch (Exception error) when (error is FormatException or DecoderFallbackException or ArgumentException) { continue; }
            if (name is null || Encoding.UTF8.GetByteCount(name) is < 1 or > 32 || name.Any(char.IsControl)) continue;
            result.Add(new(name, Password(document)));
        }
        return result;
    }
    static string DecodeSsid(byte[] bytes) => new UTF8Encoding(false, true).GetString(bytes);

    internal static int DefaultNetwork(IReadOnlyList<WindowsWifiNetwork> networks, string previousSsid)
    {
        for (int i = 0; i < networks.Count; i++) if (networks[i].Network.Ssid == previousSsid) return i;
        var connected = networks.Select((n, i) => (n, i)).Where(item => item.n.Connected).ToArray();
        if (connected.Length == 1) return connected[0].i;
        return networks.Count == 1 ? 0 : -1; // Never silently choose an unrelated saved network.
    }
}

internal sealed class WindowsWifiNetwork(WifiSetupNetwork network, bool connected)
{
    public WifiSetupNetwork Network { get; } = network;
    public bool Connected { get; } = connected;
    public override string ToString() => Network.Ssid; // Never include the password in UI text or logs.
}
