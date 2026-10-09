using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YiLocal.Core;

public sealed class DeviceProfile
{
    [JsonPropertyName("ip")] public string Ip { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "Camera";
    [JsonPropertyName("uid")] public string? Uid { get; set; }
    [JsonPropertyName("password")] public string Password { get; set; } = "";
    public static string SettingsDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YI Local");
    public static string DefaultPath => Path.Combine(SettingsDirectory, "device.dpapi");
    public static DeviceProfile Load(string? path = null) =>
        JsonSerializer.Deserialize<DeviceProfile>(Crypt(File.ReadAllBytes(path ?? DefaultPath), false)) ?? throw new InvalidDataException("Empty device profile.");
    public void Save(string? path = null)
    {
        path ??= DefaultPath; Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + ".tmp";
        File.WriteAllBytes(temporary, Crypt(JsonSerializer.SerializeToUtf8Bytes(this), true));
        File.Move(temporary, path, true);
    }
    [StructLayout(LayoutKind.Sequential)] struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true)] static extern bool CryptProtectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
    static byte[] Crypt(byte[] data, bool protect)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI profiles require Windows. The LAN protocol itself is portable.");
        var input = new Blob { Size = data.Length, Data = Marshal.AllocHGlobal(data.Length) };
        try
        {
            Marshal.Copy(data, 0, input.Data, data.Length); Blob output;
            bool ok = protect ? CryptProtectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to open/save this Windows account's encrypted camera profile.");
            try { var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, result.Length); return result; }
            finally { LocalFree(output.Data); }
        }
        finally { Marshal.FreeHGlobal(input.Data); }
    }
}
