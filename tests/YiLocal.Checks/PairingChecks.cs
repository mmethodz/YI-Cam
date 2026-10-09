using System.Buffers.Binary;
using System.Text;
using YiLocal.Core;

internal static class PairingChecks
{
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static async Task RejectAsync<T>(Func<Task> action, string message) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new Exception(message);
    }

    internal static async Task RunAsync()
    {
        const uint address = 0x10008, textAddress = 0x20000, vtable = 0x12345678;
        const string identity = "TNPTEST-000123-ABCDE";
        string uid = Convert.ToHexString(Wire.Join(Encoding.ASCII.GetBytes("TNPTEST\0"), Wire.U32(123), Encoding.ASCII.GetBytes("ABCDE\0\0\0"))).ToLowerInvariant();
        var data = new byte[0x150];
        BinaryPrimitives.WriteUInt32LittleEndian(data, vtable);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), textAddress);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4 + 16), (uint)identity.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4 + 20), 31);
        Encoding.ASCII.GetBytes("123456789012345\0").CopyTo(data, 0xb0);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0xb0 + 16), 15);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0xb0 + 20), 15);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(0x14c), 3);
        var text = Encoding.ASCII.GetBytes(identity + "\0");
        byte[]? Read(ulong a, int length) => a == address ? data.ToArray() : a == textAddress ? text[..length] : null;
        var pairing = VendorPairingLayout.Read(Read, address, vtable, uid);
        Check(pairing?.Uid == uid && pairing.Password == "123456789012345", "Valid vendor pairing was not read.");
        Check(VendorPairingLayout.Read(Read, address, vtable, uid.ToUpperInvariant()) is not null, "Uppercase saved UID rejected.");
        Check(VendorPairingLayout.Read(Read, address, vtable, new string('0', 40)) is null, "Another camera's key was accepted.");
        Check(VendorPairingLayout.Read(Read, address, vtable + 4, null) is null, "Wrong object type accepted.");
        Check(VendorPairingLayout.Read((_, _) => null, address, vtable, null) is null, "Freed memory accepted.");
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(0x14c), -1);
        Check(VendorPairingLayout.Read(Read, address, vtable, null) is null, "Inactive camera object accepted.");
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(0x14c), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0xb0 + 20), uint.MaxValue);
        Check(VendorPairingLayout.Read(Read, address, vtable, null) is null, "Unbounded string capacity accepted.");
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0xb0 + 20), 15);
        data[0xb0 + 15] = 1;
        Check(VendorPairingLayout.Read(Read, address, vtable, null) is null, "Unterminated device key accepted.");
        data[0xb0 + 15] = 0;
        int objectReads = 0;
        Check(VendorPairingLayout.Read((a, n) =>
        {
            var bytes = Read(a, n);
            if (a == address && ++objectReads == 2) bytes![0x14c]++;
            return bytes;
        }, address, vtable, null) is null, "Object changed during import but was accepted.");
        Check(VendorPairingLayout.ParseIdentity("TNPTEST-123-ABCDE") is null &&
            VendorPairingLayout.ParseIdentity("TNPTEST-4294967296-ABCDE") is null, "Malformed identity accepted.");
        Check(ReferenceEquals(VendorPairingLayout.Select([pairing!, pairing!]), pairing), "Duplicate copies of the same pairing rejected.");
        await RejectAsync<InvalidOperationException>(() => Task.FromResult(VendorPairingLayout.Select([])), "Empty pairing selection accepted.");
        await RejectAsync<InvalidOperationException>(() => Task.FromResult(VendorPairingLayout.Select([pairing!, new() { Uid = uid, Password = "ABCDEFGHIJKLMNO" }])),
            "Ambiguous keys accepted.");

        var old = new DeviceProfile { Ip = "127.0.0.1", Uid = uid, Password = "old-key-12345678" };
        DeviceProfile saved = old; int saves = 0;
        void Save(DeviceProfile p) { saved = p; saves++; }
        await RejectAsync<CameraAuthenticationException>(() => PairingImport.VerifyAndSaveAsync(pairing!, (_, _) =>
            Task.FromException<string>(new CameraAuthenticationException(1)), Save), "Rejected camera key saved.");
        await RejectAsync<TimeoutException>(() => PairingImport.VerifyAndSaveAsync(pairing!, (_, _) =>
            Task.FromException<string>(new TimeoutException()), Save), "Offline camera key saved.");
        await RejectAsync<InvalidDataException>(() => PairingImport.VerifyAndSaveAsync(pairing!, (_, _) => Task.FromResult(new string('0', 40)), Save),
            "Pairing for a different camera overwrote the old one.");
        using var cancellation = new CancellationTokenSource();
        await RejectAsync<OperationCanceledException>(() => PairingImport.VerifyAndSaveAsync(pairing!, (_, _) =>
        { cancellation.Cancel(); return Task.FromResult(uid); }, Save, cancellation.Token), "Canceled import saved a profile.");
        Check(saves == 0 && ReferenceEquals(old, saved), "Failed import changed the stored profile.");
        var candidate = new DeviceProfile { Ip = "127.0.0.1", Password = "123456789012345" };
        var verified = await PairingImport.VerifyAndSaveAsync(candidate, (_, _) => Task.FromResult(uid), Save);
        Check(saves == 1 && verified.Uid == uid && candidate.Uid is null && !ReferenceEquals(candidate, saved),
            "Successful import failed to pin identity or mutated the original profile.");
        Check(!verified.ToString()!.Contains(verified.Password), "Profile string representation leaks a key.");
    }
}
