using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using YiLocal.Core;

static class MultiCameraChecks
{
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    static void Reject(Action action)
    { try { action(); } catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return; } throw new Exception("Invalid camera registration accepted."); }
    public static async Task RunAsync()
    {
        await using var first = new SimulatedCamera("127.0.0.2", "AAAAAAAAAAAAAAA", 0x11, "Simulated A", 100000);
        await using var second = new SimulatedCamera("127.0.0.3", "BBBBBBBBBBBBBBB", 0x22, "Simulated B", 300000);
        using var a = new CameraClient("127.0.0.2", first.Key, first.Uid);
        using var b = new CameraClient("127.0.0.3", second.Key, second.Uid);
        await Task.WhenAll(a.ConnectAsync(), b.ConnectAsync());
        var queries = Enumerable.Range(0, 10).Select(async _ =>
        {
            var replies = await Task.WhenAll(a.FirmwareAsync(), b.FirmwareAsync());
            Require(replies[0] == "Simulated A" && replies[1] == "Simulated B", "Concurrent camera responses crossed sessions.");
        });
        await Task.WhenAll(queries);
        await Task.WhenAll(a.TrackingAsync(true), b.TrackingAsync(false));
        Require(first.Tracking && !second.Tracking, "A control command reached the wrong camera.");
        await Task.WhenAll(a.StartVideoAsync(), b.StartVideoAsync(), a.StartAudioAsync(), b.StartAudioAsync());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var va = await a.Frames.ReadAsync(timeout.Token); var vb = await b.Frames.ReadAsync(timeout.Token);
        var aa = await a.AudioFrames.ReadAsync(timeout.Token); var ab = await b.AudioFrames.ReadAsync(timeout.Token);
        Require(va.Milliseconds == 100000 && vb.Milliseconds == 300000 && va.Data.SequenceEqual(first.Video) && vb.Data.SequenceEqual(second.Video), "Video keys or clocks crossed sessions.");
        Require(aa.Milliseconds == 100000 && ab.Milliseconds == 300000 && aa.Data.SequenceEqual(first.Audio) && ab.Data.SequenceEqual(second.Audio), "Audio keys or clocks crossed sessions.");

        string folder = Path.Combine(Path.GetTempPath(), "openyi-registry-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            var registry = new CameraRegistry(folder);
            Reject(() => registry.ProfilePath("../device")); Reject(() => CameraRegistry.RecordingFolder(folder, ".."));
            string rootA = CameraRegistry.RecordingFolder(folder, Guid.NewGuid().ToString("N"));
            string rootB = CameraRegistry.RecordingFolder(folder, Guid.NewGuid().ToString("N"));
            using (var writerA = new SegmentRecorder(rootA, new()))
            using (var writerB = new SegmentRecorder(rootB, new()))
            using (var libraryA = new RecordingLibrary(rootA))
            using (var libraryB = new RecordingLibrary(rootB))
            {
                const string name = "YI_2026-01-01_00-00-00_160x90_abcdef.mp4";
                foreach (var library in new[] { libraryA, libraryB })
                { File.WriteAllBytes(library.ClipPath(name), [1, 2, 3]); library.Register(name, 160, 90); library.Finish(name, 1); }
                libraryA.Protect(name, true); libraryB.Delete(name);
                Require(libraryA.Clips().Single().Protected && File.Exists(libraryA.ClipPath(name)) && libraryB.Clips().Count == 0, "One camera's catalogue operation affected another camera.");
                Require(CameraRegistry.ExistingRecordingFolders(folder).Count() == 2, "Unconfigured recording catalogues disappeared from discovery.");
            }
            if (OperatingSystem.IsWindows())
            {
                var primary = new DeviceProfile { Ip = "127.0.0.2", Password = first.Key, Uid = first.Uid, Name = "Primary fixture" };
                var additional = new DeviceProfile { Ip = "127.0.0.3", Password = second.Key, Uid = second.Uid, Name = "Additional fixture" };
                Reject(() => registry.SaveVerified(primary, primary));
                var entry = registry.SaveVerified(additional, primary);
                Require(registry.Entries.Count == 1 && entry.Id.Length == 32, "Registry did not save an independent entry.");
                Reject(() => registry.SaveVerified(additional, primary));
                var loaded = new CameraRegistry(folder).LoadProfile(entry.Id);
                Require(loaded.SavePath() == registry.ProfilePath(entry.Id), "Reloaded additional profile would overwrite the primary profile.");
                Require(loaded.Uid == second.Uid && loaded.Password == second.Key, "Encrypted additional pairing changed.");
                registry.SetEnabled(entry.Id, false); Require(!new CameraRegistry(folder).Entries[0].Enabled, "Per-camera enable setting was lost.");
                Require(!File.Exists(Path.Combine(folder, "device.dpapi")), "Registry wrote a primary profile.");
                registry.Remove(entry.Id); Require(registry.Entries.Count == 0 && !File.Exists(registry.ProfilePath(entry.Id)), "Registry removal retained its key.");
            }
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(folder, true); }
        Console.WriteLine("Two simulated cameras passed concurrent authentication, command, video, audio and clock isolation; registry/path checks passed.");
    }

    sealed class SimulatedCamera : IAsyncDisposable
    {
        readonly UdpClient socket;
        readonly CancellationTokenSource stop = new();
        readonly Task worker;
        readonly byte[] uid;
        readonly ushort[] sequences = new ushort[6];
        readonly uint stamp;
        readonly string firmware;
        readonly byte marker;
        public string Key { get; }
        public string Uid => Convert.ToHexString(uid).ToLowerInvariant();
        public bool Tracking { get; private set; }
        public byte[] Video => Wire.Join([0, 0, 0, 1], Enumerable.Repeat(marker, 32).ToArray());
        public byte[] Audio => Enumerable.Repeat(marker, 37).ToArray();
        public SimulatedCamera(string address, string key, byte marker, string firmware, uint stamp)
        {
            Key = key; this.marker = marker; this.firmware = firmware; this.stamp = stamp;
            uid = Enumerable.Repeat(marker, 20).ToArray(); socket = new(new IPEndPoint(IPAddress.Parse(address), 32108));
            worker = Task.Run(RunAsync);
        }
        async Task Send(byte[] bytes, IPEndPoint peer) => await socket.SendAsync(bytes, peer);
        async Task Message(byte channel, byte kind, byte[] body, IPEndPoint peer) =>
            await Send(Wire.Packet(0xd0, Wire.Join([0xd1, channel], Wire.U16(sequences[channel]++), [2, kind, 0, 0], Wire.U32((uint)body.Length), body)), peer);
        async Task Frame(bool audio, byte generation, IPEndPoint peer)
        {
            var header = new byte[24]; Wire.U16(audio ? (ushort)138 : (ushort)78).CopyTo(header, 0); header[2] = audio ? (byte)27 : (byte)1; header[5] = generation;
            Wire.U16(160).CopyTo(header, 8); Wire.U16(90).CopyTo(header, 10); Wire.U32(123).CopyTo(header, 12); Wire.U32(stamp).CopyTo(header, 20);
            using var cipher = Aes.Create(); cipher.Key = Encoding.ASCII.GetBytes(Key + "0");
            var bytes = audio ? Audio : Video;
            if (audio) cipher.EncryptEcb(bytes.AsSpan(0, 32), PaddingMode.None).CopyTo(bytes, 0);
            else cipher.EncryptEcb(bytes.AsSpan(4, 32), PaddingMode.None).CopyTo(bytes, 4);
            await Message(audio ? (byte)1 : (byte)2, audio ? (byte)2 : (byte)1, Wire.Join(header, bytes), peer);
        }
        async Task RunAsync()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var packet = await socket.ReceiveAsync(stop.Token); var data = packet.Buffer; var peer = packet.RemoteEndPoint;
                    if (data.Length < 4) continue;
                    if (data[1] == 0x30) await Send(Wire.Packet(0x41, uid), peer);
                    else if (data[1] == 0x41) await Send(Wire.Packet(0x42), peer);
                    else if (data[1] == 0xd0 && data.Length >= 56)
                    {
                        await Send(Wire.Packet(0xd1, Wire.Join([0xd1, 0, 0, 1], data[6..8])), peer);
                        var body = data[16..]; ushort command = Wire.U16(body);
                        string nonce = Encoding.ASCII.GetString(body, 8, 15);
                        bool valid = Wire.Auth(Key, nonce).SequenceEqual(body[8..40]);
                        if (!valid) throw new Exception("Simulated camera received another camera's authentication key.");
                        if (command == 0x2345) { await Frame(false, body[40], peer); continue; }
                        if (command == 0x0300) { await Frame(true, 0, peer); continue; }
                        byte[] reply = command == 0x1300 ? Encoding.ASCII.GetBytes(firmware) : [];
                        if (command == 0x400b) Tracking = Wire.U32(body.AsSpan(40)) != 0;
                        ushort response = command == 0x400b ? (ushort)0x400c : (ushort)(command + 1);
                        await Message(0, 3, Wire.Join(Wire.U16(response), body[2..4], new byte[2], Wire.U16((ushort)reply.Length), new byte[32], reply), peer);
                    }
                }
            }
            catch (Exception e) when (stop.IsCancellationRequested && e is OperationCanceledException or ObjectDisposedException or SocketException) { }
        }
        public async ValueTask DisposeAsync() { stop.Cancel(); socket.Dispose(); await worker; stop.Dispose(); }
    }
}
