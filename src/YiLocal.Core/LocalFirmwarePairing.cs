using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace YiLocal.Core;

/// <summary>Owner-initiated, read-only onboarding for the exact reviewed local01 firmware.</summary>
public static class LocalFirmwarePairing
{
    public const string Firmware = "6.0.24.10_202610100002";
    public const string DefaultMaintenancePassword = "yunyi666";
    internal const int BinarySize = 663784;
    internal const string BinaryHash = "8d4d0e5b8e2481e0e3bcb45a4b687378c07afe8f7b466311404d5b78d3fa6803";

    public static async Task<DeviceProfile> ReadProfileAsync(string ip, string name, string maintenancePassword,
        string? expectedUid = null, CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100)
            throw new ArgumentException(L.Get("UseACameraNameOf1100Characters"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var reader = await MaintenanceReader.ConnectAsync(ip, maintenancePassword, timeout.Token);
        await VerifyFirmwareAsync(reader.ReadAsync, timeout.Token);
        return ParseProfile(ip, name, await reader.ReadAsync("/etc/jffs2/openyi.key", 16, timeout.Token),
            await reader.ReadAsync("/etc/jffs2/yi.conf", 16384, timeout.Token), expectedUid);
    }

    public static async Task<WifiSetupNetwork> ReadWifiAsync(string ip, string maintenancePassword, CancellationToken cancellation = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var reader = await MaintenanceReader.ConnectAsync(ip, maintenancePassword, timeout.Token);
        await VerifyFirmwareAsync(reader.ReadAsync, timeout.Token);
        var ini = await reader.ReadAsync("/etc/jffs2/anyka_cfg.ini", 131072, timeout.Token);
        return new WifiSetupNetwork(IniValue(ini, "wireless", "ssid"), IniValue(ini, "wireless", "password"));
    }

    internal static async Task VerifyFirmwareAsync(Func<string, int, CancellationToken, Task<byte[]>> read, CancellationToken token)
    {
        if (Encoding.ASCII.GetString(await read("/usr/fw_version", 128, token)).Trim() != Firmware)
            throw new InvalidDataException(L.Get("Setup.LocalFirmwareRequired"));
        byte[] binary = await read("/usr/bin/anyka_ipc", BinarySize, token);
        if (binary.Length != BinarySize || !Convert.ToHexString(SHA256.HashData(binary)).Equals(BinaryHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(L.Get("Setup.FirmwareMismatch"));
    }

    internal static DeviceProfile ParseProfile(string ip, string name, byte[] key, byte[] identity, string? expectedUid)
    {
        CameraDiscovery.LanAddress(ip);
        if (key.Length != 16 || key[15] != 10 || key.Take(15).Any(b => b is < 33 or > 126))
            throw new InvalidDataException(L.Get("Setup.InvalidOwnerKey"));
        var ids = Regex.Matches(Encoding.ASCII.GetString(identity), @"^\s*p2pid\s*=\s*(\S+)\s*$", RegexOptions.Multiline);
        string? uid = ids.Count == 1 ? VendorPairingLayout.ParseIdentity(ids[0].Groups[1].Value) : null;
        if (uid is null) throw new InvalidDataException(L.Get("Setup.InvalidIdentity"));
        if (expectedUid is not null && !uid.Equals(expectedUid, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(L.Get("ADifferentCameraAnsweredTheSavedPairingWasNotChanged"));
        return new DeviceProfile { Ip = ip, Name = name.Trim(), Uid = uid, Password = Encoding.ASCII.GetString(key, 0, 15) };
    }

    internal static string IniValue(byte[] bytes, string section, string key)
    {
        string current = ""; var matches = new List<string>();
        foreach (string raw in new UTF8Encoding(false, true).GetString(bytes).Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            string trimmed = line.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']')) current = trimmed[1..^1];
            else if (current == section && !trimmed.StartsWith(';') && !trimmed.StartsWith('#') && line.IndexOf('=') is var at && at >= 0 && line[..at].Trim() == key)
                matches.Add(line[(at + 1)..]); // Wi-Fi spaces are significant; do not trim credentials.
        }
        if (matches.Count != 1) throw new InvalidDataException(L.Get("Setup.WifiUnavailable"));
        return matches[0];
    }
}

/// <summary>Transient setup data; never persisted as app preferences or printed by ToString.</summary>
public sealed class WifiSetupNetwork(string ssid, string? password)
{
    public string Ssid { get; } = ssid;
    public string? Password { get; } = password; // null = unavailable; empty = explicitly open network
    public override string ToString() => nameof(WifiSetupNetwork);
}

/// <summary>Minimal passive FTP reader. Fixed files, bounded replies/transfers, no writes or server-directed hosts.</summary>
internal sealed class MaintenanceReader : IDisposable
{
    readonly TcpClient control = new();
    readonly IPAddress address;
    StreamReader reader = null!;
    StreamWriter writer = null!;
    MaintenanceReader(IPAddress address) => this.address = address;
    internal static async Task<MaintenanceReader> ConnectAsync(string ip, string password, CancellationToken token, int port = 21)
    {
        if (password.Length is < 1 or > 128 || password.Any(c => c is < ' ' or > '~'))
            throw new ArgumentException(L.Get("Setup.MaintenancePasswordInvalid"));
        var connection = new MaintenanceReader(CameraDiscovery.LanAddress(ip));
        try
        {
            await connection.control.ConnectAsync(connection.address, port, token);
            var stream = connection.control.GetStream();
            connection.reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
            connection.writer = new StreamWriter(stream, Encoding.ASCII, 1024, true) { NewLine = "\r\n", AutoFlush = true };
            connection.Expect(await connection.ResponseAsync(token), 220);
            var reply = await connection.CommandAsync("USER root", token);
            if (reply.Code == 331) reply = await connection.CommandAsync("PASS " + password, token);
            if (reply.Code != 230) throw new IOException(L.Get("Setup.MaintenanceLoginFailed"));
            connection.Expect(await connection.CommandAsync("TYPE I", token), 200);
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }
    public async Task<byte[]> ReadAsync(string path, int maximum, CancellationToken token)
    {
        if (path is not ("/usr/fw_version" or "/usr/bin/anyka_ipc" or "/etc/jffs2/openyi.key" or "/etc/jffs2/yi.conf" or "/etc/jffs2/anyka_cfg.ini") || maximum < 1 || maximum > LocalFirmwarePairing.BinarySize)
            throw new ArgumentException(nameof(path));
        var passive = await CommandAsync("PASV", token); Expect(passive, 227);
        int port = PassivePort(passive.Text);
        using var data = new TcpClient();
        await data.ConnectAsync(address, port, token); // Never follow the address advertised by PASV.
        var transfer = await CommandAsync("RETR " + path, token);
        if (transfer.Code is not (125 or 150)) throw new IOException(L.Get("Setup.MaintenanceReadFailed"));
        using var result = new MemoryStream();
        byte[] buffer = new byte[8192];
        while (true)
        {
            int count = await data.GetStream().ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maximum + 1 - (int)result.Length)), token);
            if (count == 0) break;
            result.Write(buffer, 0, count);
            if (result.Length > maximum) throw new InvalidDataException(L.Get("Setup.MaintenanceReadFailed"));
        }
        data.Close(); Expect(await ResponseAsync(token), 226);
        return result.ToArray();
    }
    internal static int PassivePort(string reply)
    {
        var match = Regex.Match(reply, @"\((\d{1,3}),(\d{1,3}),(\d{1,3}),(\d{1,3}),(\d{1,3}),(\d{1,3})\)", RegexOptions.CultureInvariant);
        if (!match.Success) throw new IOException(L.Get("Setup.MaintenanceReadFailed"));
        var bytes = match.Groups.Cast<Group>().Skip(1).Select(g => int.Parse(g.Value, CultureInfo.InvariantCulture)).ToArray();
        if (bytes.Any(b => b > 255) || bytes[4] * 256 + bytes[5] == 0) throw new IOException(L.Get("Setup.MaintenanceReadFailed"));
        return bytes[4] * 256 + bytes[5];
    }
    async Task<(int Code, string Text)> CommandAsync(string command, CancellationToken token)
    {
        await writer.WriteLineAsync(command.AsMemory(), token);
        return await ResponseAsync(token);
    }
    async Task<(int Code, string Text)> ResponseAsync(CancellationToken token)
    {
        async Task<string> Line()
        {
            var text = new StringBuilder(); var character = new char[1];
            while (text.Length < 1024)
            {
                if (await reader.ReadAsync(character, token) == 0) break;
                if (character[0] == '\n') return text.ToString().TrimEnd('\r');
                text.Append(character[0]);
            }
            throw new IOException(L.Get("Setup.MaintenanceReadFailed"));
        }
        string first = await Line();
        if (first.Length < 4 || !int.TryParse(first[..3], NumberStyles.None, CultureInfo.InvariantCulture, out int code))
            throw new IOException(L.Get("Setup.MaintenanceReadFailed"));
        if (first[3] == ' ') return (code, first);
        if (first[3] == '-')
            for (int i = 0; i < 32; i++) { string line = await Line(); if (line.StartsWith(first[..3] + " ", StringComparison.Ordinal)) return (code, line); }
        throw new IOException(L.Get("Setup.MaintenanceReadFailed"));
    }
    void Expect((int Code, string Text) response, int code)
    { if (response.Code != code) throw new IOException(L.Get("Setup.MaintenanceReadFailed")); }
    public void Dispose() { writer?.Dispose(); reader?.Dispose(); control.Dispose(); }
}
