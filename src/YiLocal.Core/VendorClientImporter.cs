using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace YiLocal.Core;

/// <summary>Reads an existing pairing from one supported vendor client. Never writes to its process.</summary>
public static class VendorClientImporter
{
    public const string SupportedVersion = "1.0.1.1_202209261648";
    const string ExecutableHash = "f6f9c422fa046f66d032a919e85444c195084c019efc959971a1213f46f32c46";
    const uint CameraVtableRva = 0xa46758;

    public static Task<DeviceProfile> ReadProfileAsync(string ip, string name, string? expectedUid = null,
        CancellationToken cancellation = default) => Task.Run(() => ReadProfile(ip, name, expectedUid, cancellation), cancellation);

    static DeviceProfile ReadProfile(string ip, string name, string? expectedUid, CancellationToken cancellation)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            throw new PlatformNotSupportedException(L.Get("ImportFromYIIoTRequiresThe64BitWindowsApplication"));
        var processes = Process.GetProcessesByName("YIIOTHomePCClientIntl");
        if (processes.Length == 0) throw new InvalidOperationException(L.Get("OpenTheYIIoTPCAppAndThisCameraSLive"));
        var candidates = new List<DeviceProfile>(); bool supported = false, inaccessible = false;
        try
        {
            foreach (var process in processes)
            {
                cancellation.ThrowIfCancellationRequested();
                try
                {
                    var module = process.MainModule;
                    if (module is null) continue;
                    using var file = File.OpenRead(module.FileName);
                    if (!Convert.ToHexString(SHA256.HashData(file)).Equals(ExecutableHash, StringComparison.OrdinalIgnoreCase)) continue;
                    supported = true;
                    ulong moduleBase = (ulong)module.BaseAddress.ToInt64();
                    if (moduleBase + CameraVtableRva > uint.MaxValue) continue;
                    using var handle = OpenProcess(0x0400 | 0x0010, false, process.Id); // Query information + VM read only.
                    if (handle.IsInvalid) { inaccessible = true; continue; }
                    candidates.AddRange(Scan(handle, (uint)(moduleBase + CameraVtableRva), expectedUid, cancellation));
                }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
                { inaccessible = true; } // A vendor process may exit while it is being inspected.
            }
        }
        finally { foreach (var process in processes) process.Dispose(); }
        if (!supported) throw new NotSupportedException(L.Format("ThisImporterSupportsYIIoTPC0OnlyNoSavedPairing", SupportedVersion));
        if (candidates.Count == 0 && inaccessible)
            throw new IOException(L.Get("CouldNotReadTheRunningYIIoTClientOpenItsLive"));
        var selected = VendorPairingLayout.Select(candidates);
        selected.Ip = ip; selected.Name = string.IsNullOrWhiteSpace(name) ? L.Get("Camera") : name;
        return selected;
    }

    static List<DeviceProfile> Scan(SafeProcessHandle handle, uint vtable, string? expectedUid, CancellationToken cancellation)
    {
        var found = new List<DeviceProfile>(); var visited = new HashSet<ulong>();
        var marker = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(marker, vtable);
        var buffer = new byte[1024 * 1024]; ulong scanned = 0; var watch = Stopwatch.StartNew();
        byte[]? Read(ulong address, int length)
        {
            cancellation.ThrowIfCancellationRequested();
            if (address < 0x10000 || address + (ulong)length > 0x1_0000_0000) return null;
            var bytes = new byte[length];
            return ReadProcessMemory(handle, (nuint)address, bytes, (nuint)length, out var count) && count == (nuint)length ? bytes : null;
        }
        for (ulong address = 0; address < 0x1_0000_0000;)
        {
            cancellation.ThrowIfCancellationRequested();
            if (watch.Elapsed > TimeSpan.FromSeconds(15) || scanned > 512UL * 1024 * 1024)
                throw new IOException(L.Get("TheYIIoTMemoryScanReachedItsLimitCloseExtraVendor"));
            if (VirtualQueryEx(handle, (nuint)address, out var region, (nuint)Marshal.SizeOf<MemoryRegion>()) == 0) break;
            ulong end = Math.Min((ulong)region.BaseAddress + (ulong)region.RegionSize, 0x1_0000_0000);
            if (end <= address) break;
            // Camera objects live in writable private heap memory. Ignore code, images and guarded pages.
            if (region.State == 0x1000 && region.Type == 0x20000 && (region.Protect & 0x100) == 0 &&
                (region.Protect & 0xff) is 0x04 or 0x08 or 0x40 or 0x80)
            {
                for (ulong chunk = (ulong)region.BaseAddress; chunk < end;)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (watch.Elapsed > TimeSpan.FromSeconds(15) || scanned > 512UL * 1024 * 1024)
                        throw new IOException(L.Get("TheYIIoTMemoryScanReachedItsLimitCloseExtraVendor"));
                    int length = (int)Math.Min((ulong)buffer.Length, end - chunk); scanned += (ulong)length;
                    if (ReadProcessMemory(handle, (nuint)chunk, buffer, (nuint)length, out var count) && count == (nuint)length)
                    {
                        int offset = 0;
                        while (offset <= length - 4)
                        {
                            int match = buffer.AsSpan(offset, length - offset).IndexOf(marker);
                            if (match < 0) break;
                            offset += match; ulong candidate = chunk + (ulong)offset;
                            if ((candidate & 3) == 0 && visited.Add(candidate))
                            {
                                var profile = VendorPairingLayout.Read(Read, candidate, vtable, expectedUid);
                                if (profile is not null) found.Add(profile);
                            }
                            offset += 4;
                        }
                    }
                    if (length <= 4) break;
                    chunk += (ulong)length - 4; // Include vtable pointers straddling two reads.
                }
            }
            address = end;
        }
        return found;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MemoryRegion
    {
        public nuint BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State, Protect, Type;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool ReadProcessMemory(SafeProcessHandle process, nuint address, byte[] buffer, nuint size, out nuint read);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern nuint VirtualQueryEx(SafeProcessHandle process, nuint address, out MemoryRegion region, nuint length);
}

// These offsets are used only after verifying the complete vendor executable hash.
internal static class VendorPairingLayout
{
    internal static DeviceProfile? Read(Func<ulong, int, byte[]?> read, ulong address, uint vtable, string? expectedUid)
    {
        const int size = 0x150;
        var data = read(address, size);
        if (data is null || data.Length != size || BinaryPrimitives.ReadUInt32LittleEndian(data) != vtable ||
            BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0x14c)) < 0) return null;
        string? identity = ReadString(read, data.AsSpan(4, 24), 32);
        string? uid = ParseIdentity(identity);
        if (uid is null || expectedUid is not null && !uid.Equals(expectedUid, StringComparison.OrdinalIgnoreCase)) return null;
        string? key = ReadString(read, data.AsSpan(0xb0, 24), 15);
        if (key?.Length != 15) return null;
        // Do not accept an object being destroyed or updated during the read.
        var again = read(address, size);
        if (again is null || !data.AsSpan().SequenceEqual(again) ||
            identity != ReadString(read, again.AsSpan(4, 24), 32) || key != ReadString(read, again.AsSpan(0xb0, 24), 15)) return null;
        return new DeviceProfile { Uid = uid, Password = key };
    }

    static string? ReadString(Func<ulong, int, byte[]?> read, ReadOnlySpan<byte> value, int maxLength)
    {
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(value[16..]);
        uint capacity = BinaryPrimitives.ReadUInt32LittleEndian(value[20..]);
        if (length == 0 || length > maxLength || capacity < length || capacity > 4096) return null;
        byte[]? bytes = capacity < 16 ? value[..16].ToArray() : read(BinaryPrimitives.ReadUInt32LittleEndian(value), (int)length + 1);
        if (bytes is null || bytes.Length <= length || bytes[(int)length] != 0 || bytes.Take((int)length).Any(b => b is < 33 or > 126)) return null;
        return Encoding.ASCII.GetString(bytes, 0, (int)length);
    }

    internal static string? ParseIdentity(string? identity)
    {
        var parts = identity?.Split('-');
        if (parts is not { Length: 3 } || parts[0].Length is < 1 or > 8 || parts[2].Length is < 1 or > 8 ||
            !parts[0].StartsWith("TNP", StringComparison.Ordinal) ||
            parts[0].Concat(parts[2]).Any(c => !char.IsAsciiLetterOrDigit(c)) ||
            !uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var serial) ||
            parts[1] != serial.ToString("D6", CultureInfo.InvariantCulture)) return null;
        var uid = new byte[20];
        Encoding.ASCII.GetBytes(parts[0]).CopyTo(uid, 0);
        BinaryPrimitives.WriteUInt32BigEndian(uid.AsSpan(8), serial);
        Encoding.ASCII.GetBytes(parts[2]).CopyTo(uid, 12);
        return Convert.ToHexString(uid).ToLowerInvariant();
    }

    internal static DeviceProfile Select(IEnumerable<DeviceProfile> candidates)
    {
        var distinct = candidates.DistinctBy(p => (p.Uid, p.Password)).Take(2).ToArray();
        if (distinct.Length == 0)
            throw new InvalidOperationException(L.Get("NoMatchingLiveCameraWasFoundInYIIoTOpenThe"));
        if (distinct.Length != 1)
            throw new InvalidOperationException(L.Get("MoreThanOneActivePairingWasFoundKeepOnlyThisCamera"));
        return distinct[0];
    }
}
