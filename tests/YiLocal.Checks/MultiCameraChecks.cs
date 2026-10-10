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
        await Task.WhenAll(a.RotateAsync(true), b.RotateAsync(false));
        Require((await a.SettingsAsync()).Rotation == 1 && (await b.SettingsAsync()).Rotation == 0,
            "Rotation payload/readback or camera isolation failed.");
        Require(await a.GimbalRestoreDelayAsync() == 20, "Gimbal restore default/readback failed.");
        await a.GimbalRestoreAsync(false);
        Require(await a.GimbalRestoreDelayAsync() == 0 && await b.GimbalRestoreDelayAsync() == 20,
            "Gimbal restore affected another camera or ignored off.");
        await a.GimbalRestoreAsync(true);
        Require(await a.GimbalRestoreDelayAsync() == 20, "Gimbal on did not use the mobile app's 20 value.");
        first.MalformedGimbalReply = true;
        try { await a.GimbalRestoreDelayAsync(); throw new Exception("Malformed gimbal response was accepted."); }
        catch (InvalidDataException) { }
        first.MalformedGimbalReply = false;
        var controls = new DeviceProfile { Uid = first.Uid, StreamQuality = 2 };
        Require(Enumerable.Range(1, 4).All(d => controls.MapDirection((uint)d) == d), "Default arrows changed.");
        controls.ReversePanControls = true;
        Require(controls.MapDirection(3) == 4 && controls.MapDirection(4) == 3 && controls.MapDirection(1) == 1, "Pan reversal affected tilt.");
        controls.ReverseTiltControls = true;
        Require(controls.MapDirection(1) == 2 && controls.MapDirection(2) == 1, "Tilt reversal failed.");
        var refreshed = new DeviceProfile { Uid = first.Uid }; refreshed.KeepControlsFrom(controls);
        Require(refreshed.ReversePanControls && refreshed.ReverseTiltControls && refreshed.StreamQuality == 2, "Pairing refresh lost local control settings.");
        var other = new DeviceProfile { Uid = second.Uid }; other.KeepControlsFrom(controls);
        Require(!other.ReversePanControls && !other.ReverseTiltControls && other.StreamQuality == 1, "Control settings leaked to another camera.");
        await Task.WhenAll(a.StartVideoAsync(), b.StartVideoAsync(), a.StartAudioAsync(), b.StartAudioAsync());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var va = await a.Frames.ReadAsync(timeout.Token); var vb = await b.Frames.ReadAsync(timeout.Token);
        var aa = await a.AudioFrames.ReadAsync(timeout.Token); var ab = await b.AudioFrames.ReadAsync(timeout.Token);
        Require(va.Milliseconds == 100000 && vb.Milliseconds == 300000 && va.Data.SequenceEqual(first.Video) && vb.Data.SequenceEqual(second.Video), "Video keys or clocks crossed sessions.");
        Require(aa.Milliseconds == 100000 && ab.Milliseconds == 300000 && aa.Data.SequenceEqual(first.Audio) && ab.Data.SequenceEqual(second.Audio), "Audio keys or clocks crossed sessions.");

        await Task.WhenAll(a.StartSpeakerAsync(), b.StartSpeakerAsync());
        a.SendTalkAudio(TalkChecks.Packet(0x11)); b.SendTalkAudio(TalkChecks.Packet(0x22));
        await Task.WhenAll(first.TalkRetried.Task, second.TalkRetried.Task).WaitAsync(TimeSpan.FromSeconds(3));
        first.HoldNextCommandAck = true;
        await Task.WhenAll(a.FirmwareAsync(), b.FirmwareAsync());
        await first.CommandRetried.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Task.WhenAll(a.StopSpeakerAsync(), b.StopSpeakerAsync());
        try { a.SendTalkAudio(TalkChecks.Packet(0x11)); throw new Exception("Talk continued after stop."); }
        catch (IOException) { }
        Console.WriteLine("Talk stream initialization, independent keys, wrong-channel ACK isolation and identical retransmission passed.");

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
                var additional = new DeviceProfile { Ip = "127.0.0.3", Password = second.Key, Uid = second.Uid, Name = "Additional fixture", ReversePanControls = true, StreamQuality = 2 };
                Reject(() => registry.SaveVerified(primary, primary));
                var entry = registry.SaveVerified(additional, primary);
                Require(registry.Entries.Count == 1 && entry.Id.Length == 32, "Registry did not save an independent entry.");
                Reject(() => registry.SaveVerified(additional, primary));
                var loaded = new CameraRegistry(folder).LoadProfile(entry.Id);
                Require(loaded.SavePath() == registry.ProfilePath(entry.Id), "Reloaded additional profile would overwrite the primary profile.");
                Require(loaded.Uid == second.Uid && loaded.Password == second.Key, "Encrypted additional pairing changed.");
                Require(loaded.ReversePanControls && !loaded.ReverseTiltControls && loaded.StreamQuality == 2, "Encrypted profile lost independent control preferences.");
                await using (var savedSession = new CameraSession(loaded))
                    Require(savedSession.Quality == 2, "A new session ignored its saved stream quality.");
                registry.SetEnabled(entry.Id, false); Require(!new CameraRegistry(folder).Entries[0].Enabled, "Per-camera enable setting was lost.");
                Require(!File.Exists(Path.Combine(folder, "device.dpapi")), "Registry wrote a primary profile.");
                registry.Remove(entry.Id); Require(registry.Entries.Count == 0 && !File.Exists(registry.ProfilePath(entry.Id)), "Registry removal retained its key.");
            }
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(folder, true); }
        Console.WriteLine("Two simulated cameras passed concurrent authentication, command, video, audio and clock isolation; registry/path checks passed.");
    }

    internal sealed class SimulatedCamera : IAsyncDisposable
    {
        readonly UdpClient socket;
        readonly CancellationTokenSource stop = new();
        readonly Task worker;
        readonly byte[] uid;
        readonly ushort[] sequences = new ushort[6];
        readonly uint stamp;
        readonly string firmware;
        readonly bool plain;
        readonly byte marker;
        public string Key { get; }
        public string Uid => Convert.ToHexString(uid).ToLowerInvariant();
        public bool Tracking { get; private set; }
        public bool Rotation { get; private set; }
        public uint GimbalRestore { get; private set; } = 20;
        public bool MalformedGimbalReply { get; set; }
        public bool HoldNextCommandAck { get; set; }
        public TaskCompletionSource TalkRetried { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CommandRetried { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        byte[]? heldTalk, heldCommand;
        bool initialized, videoStopped, speaker;
        public byte[] Video => Wire.Join([0, 0, 0, 1], Enumerable.Repeat(marker, 32).ToArray());
        public byte[] Audio => Enumerable.Repeat(marker, 37).ToArray();
        public SimulatedCamera(string address, string key, byte marker, string firmware, uint stamp, bool plain = false)
        {
            Key = key; this.marker = marker; this.firmware = firmware; this.stamp = stamp; this.plain = plain;
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
            var bytes = audio ? Audio : Video;
            if (!plain)
            {
                using var cipher = Aes.Create(); cipher.Key = Encoding.ASCII.GetBytes(Key + "0");
                if (audio) cipher.EncryptEcb(bytes.AsSpan(0, 32), PaddingMode.None).CopyTo(bytes, 0);
                else cipher.EncryptEcb(bytes.AsSpan(4, 32), PaddingMode.None).CopyTo(bytes, 4);
            }
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
                    else if (data[1] == 0xd0 && data.Length >= 40 && data[5] == 1)
                    {
                        Require(speaker, "Talk packet was sent before speaker initialization.");
                        Require(data[8] == 2 && data[9] == 2 && Wire.U16(data.AsSpan(16)) == 138 && data[18] == 2,
                            "Invalid talk channel/envelope.");
                        var audio = data[40..]; int encrypted = audio.Length / 16 * 16;
                        if (!plain)
                        {
                            using var cipher = Aes.Create(); cipher.Key = Encoding.ASCII.GetBytes(Key + "0");
                            cipher.DecryptEcb(audio.AsSpan(0, encrypted), PaddingMode.None).CopyTo(audio, 0);
                        }
                        Require(audio.SequenceEqual(TalkChecks.Packet(marker)), "Talk audio used the wrong camera key or damaged the partial block.");
                        if (heldTalk is null)
                        {
                            heldTalk = data;
                            // A command ACK with the same sequence must not acknowledge talk audio.
                            await Send(Wire.Packet(0xd1, Wire.Join([0xd1, 0, 0, 1], data[6..8])), peer);
                        }
                        else
                        {
                            Require(heldTalk.SequenceEqual(data), "A retransmission changed the original talk datagram.");
                            await Send(Wire.Packet(0xd1, Wire.Join([0xd1, 1, 0, 1], data[6..8])), peer);
                            TalkRetried.TrySetResult();
                        }
                    }
                    else if (data[1] == 0xd0 && data.Length >= 56)
                    {
                        if (HoldNextCommandAck)
                        {
                            HoldNextCommandAck = false; heldCommand = data;
                            // Likewise a talk ACK must not clear an outstanding command.
                            await Send(Wire.Packet(0xd1, Wire.Join([0xd1, 1, 0, 1], data[6..8])), peer);
                            continue;
                        }
                        if (heldCommand is not null)
                        { Require(heldCommand.SequenceEqual(data), "Command retransmission changed."); heldCommand = null; CommandRetried.TrySetResult(); }
                        await Send(Wire.Packet(0xd1, Wire.Join([0xd1, 0, 0, 1], data[6..8])), peer);
                        var body = data[16..]; ushort command = Wire.U16(body);
                        string nonce = Encoding.ASCII.GetString(body, 8, 15);
                        bool valid = plain
                            ? Encoding.ASCII.GetBytes("OpenYI-LAN-v1".PadRight(32, '\0')).SequenceEqual(body[8..40])
                            : Wire.Auth(Key, nonce).SequenceEqual(body[8..40]);
                        if (!valid)
                        {
                            await Message(0, 3, Wire.Join(Wire.U16((ushort)(command + 1)), body[2..4], new byte[4], Wire.U32(1), new byte[28]), peer);
                            continue;
                        }
                        if (command == 0x2345) { initialized = true; videoStopped = false; await Frame(false, body[40], peer); continue; }
                        if (command == 0x02ff) { Require(body.Length == 48 && body[40..].All(b => b == 0), "Wrong video stop payload."); videoStopped = true; continue; }
                        if (command == 0x0350)
                        {
                            Require(initialized && videoStopped && body.Length == 44 && body[40..].All(b => b == 0), "Talk omitted video initialization or used a different speaker mode.");
                            speaker = true; continue;
                        }
                        if (command == 0x0351)
                        { Require(body.Length == 48 && body[40..].All(b => b == 0), "Wrong speaker stop payload."); speaker = false; continue; }
                        if (command == 0x0300) { await Frame(true, 0, peer); continue; }
                        byte[] reply = command == 0x1300 ? Encoding.ASCII.GetBytes(firmware) : [];
                        if (command == 0x400b) Tracking = Wire.U32(body.AsSpan(40)) != 0;
                        if (command is 0x131f or 0x1394 or 0x1396)
                            Require(body.Length == 44, "Orientation command payload must be four bytes.");
                        if (command == 0x131f) Rotation = Wire.U32(body.AsSpan(40)) != 0;
                        if (command == 0x1394) GimbalRestore = Wire.U32(body.AsSpan(40));
                        if (command == 0x1396) reply = MalformedGimbalReply ? [0] : Wire.U32(GimbalRestore);
                        if (command is 0x0330 or 0x131f or 0x1394)
                        { reply = new byte[344]; reply[8] = 253; reply[54] = Rotation ? (byte)1 : (byte)0; reply[68] = Tracking ? (byte)1 : (byte)0; }
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
