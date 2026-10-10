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
    [JsonPropertyName("protocol")] public string Protocol { get; set; } = CameraProtocol.Stock;
    [JsonPropertyName("reverse_pan_controls")] public bool ReversePanControls { get; set; }
    [JsonPropertyName("reverse_tilt_controls")] public bool ReverseTiltControls { get; set; }
    [JsonPropertyName("stream_quality")] public byte StreamQuality { get; set; } = 1;
    [JsonIgnore] public string? StoragePath { get; private set; }
    public static string SettingsDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YI Local");
    public static string DefaultPath => Path.Combine(SettingsDirectory, "device.dpapi");
    public static DeviceProfile Load(string? path = null)
    {
        path = Path.GetFullPath(path ?? DefaultPath);
        var profile = JsonSerializer.Deserialize<DeviceProfile>(Crypt(File.ReadAllBytes(path), false)) ?? throw new InvalidDataException(L.Get("EmptyDeviceProfile"));
        if (profile.StreamQuality > 2) profile.StreamQuality = 1;
        profile.StoragePath = path; return profile;
    }
    public void Save(string? path = null)
    {
        path = SavePath(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        File.WriteAllBytes(temporary, Crypt(JsonSerializer.SerializeToUtf8Bytes(this), true));
        File.Move(temporary, path, true);
        StoragePath = path;
    }
    internal string SavePath(string? path = null) => Path.GetFullPath(path ?? StoragePath ?? DefaultPath);
    public uint MapDirection(uint direction) => direction switch
    {
        1 => ReverseTiltControls ? 2u : 1u,
        2 => ReverseTiltControls ? 1u : 2u,
        3 => ReversePanControls ? 4u : 3u,
        4 => ReversePanControls ? 3u : 4u,
        _ => throw new ArgumentOutOfRangeException(nameof(direction))
    };
    public void KeepControlsFrom(DeviceProfile? previous)
    {
        if (Uid is null || previous?.Uid is null || !Uid.Equals(previous.Uid, StringComparison.OrdinalIgnoreCase)) return;
        ReversePanControls = previous.ReversePanControls; ReverseTiltControls = previous.ReverseTiltControls;
        StreamQuality = previous.StreamQuality;
    }
    [StructLayout(LayoutKind.Sequential)] struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true)] static extern bool CryptProtectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
    static byte[] Crypt(byte[] data, bool protect)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException(L.Get("DPAPIProfilesRequireWindowsTheLANProtocolItselfIsPortable"));
        var input = new Blob { Size = data.Length, Data = Marshal.AllocHGlobal(data.Length) };
        try
        {
            Marshal.Copy(data, 0, input.Data, data.Length); Blob output;
            bool ok = protect ? CryptProtectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error(), L.Get("UnableToOpenSaveThisWindowsAccountSEncryptedCameraProfile"));
            try { var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, result.Length); return result; }
            finally { LocalFree(output.Data); }
        }
        finally { Marshal.FreeHGlobal(input.Data); }
    }
}
