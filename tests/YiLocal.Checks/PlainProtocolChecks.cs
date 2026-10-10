using System.Text.Json;
using YiLocal.Core;

static class PlainProtocolChecks
{
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }

    public static async Task RunAsync()
    {
        Require(JsonSerializer.Deserialize<DeviceProfile>("{\"ip\":\"127.0.0.1\"}")!.Protocol == CameraProtocol.Stock,
            "Existing profiles must retain stock authentication/encryption.");
        bool refused = false;
        try { using var invalid = new CameraClient("127.0.0.1", "", protocol: "unknown"); }
        catch (ArgumentException) { refused = true; }
        Require(refused, "Unknown protocol silently fell back.");
        var source = new DeviceProfile { Ip = "127.0.0.4", Name = "Synthetic local02", Protocol = CameraProtocol.LocalPlain, Password = "obsolete-secret" };
        var copy = await PairingImport.VerifyAndSaveAsync(source, (p, _) => Task.FromResult("44".PadRight(40, '4')), _ => { });
        Require(copy.Protocol == CameraProtocol.LocalPlain && copy.Password == "" && source.Password == "obsolete-secret",
            "Keyless profile verification changed the old profile or retained an unused secret.");

        await using (var device = new MultiCameraChecks.SimulatedCamera("127.0.0.4", "", 0x44, CameraProtocol.LocalPlainFirmware, 200000, plain: true))
        using (var client = new CameraClient("127.0.0.4", "", device.Uid, CameraProtocol.LocalPlain))
        {
            await client.ConnectAsync();
            await client.StartVideoAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            var video = await client.Frames.ReadAsync(timeout.Token);
            Require(video.Data.SequenceEqual(device.Video) && video.Width == 160 && video.Milliseconds == 200000,
                "Plain video was decrypted or its metadata changed.");
            await client.StartAudioAsync();
            var audio = await client.AudioFrames.ReadAsync(timeout.Token);
            Require(audio.Data.SequenceEqual(device.Audio), "Plain microphone AAC was altered.");
            await client.TrackingAsync(true);
            Require((await client.SettingsAsync()).Tracking == 1, "Keyless camera control/readback failed.");
            await client.StartSpeakerAsync(cancellation: timeout.Token);
            client.SendTalkAudio(TalkChecks.Packet(0x44));
            await device.TalkRetried.Task.WaitAsync(timeout.Token);
            await client.StopSpeakerAsync();
        }
        foreach (bool stock in new[] { true, false })
        {
            string address = stock ? "127.0.0.5" : "127.0.0.6";
            await using var device = new MultiCameraChecks.SimulatedCamera(address, "AAAAAAAAAAAAAAA", 0x55, "Unsupported firmware", 0, plain: !stock);
            using var client = new CameraClient(address, "", device.Uid, CameraProtocol.LocalPlain);
            refused = false;
            try { await client.ConnectAsync(); }
            catch (NotSupportedException) { refused = true; }
            Require(refused && !client.Connected, "Plain mode accepted stock/mismatched firmware or left its connection open.");
        }
        string qr = QrProvisioning.ComposeLocal("Keittiö", "89JFSjo8");
        var fields = qr.Split('&');
        Require(fields[0].StartsWith("b=EU") && fields[0].Length == 22 && fields[2] == "p=ODlKRlNqbzg=",
            "Local QR marker or XOR-zero fallback differs from the camera parser.");
        Require(QrProvisioning.Png(qr).Length > 100, "Local setup QR was not generated.");
        Console.WriteLine("Keyless LAN mode: discovery, version gate, controls, video/audio, talk retry and profile/QR checks passed (simulated cameras).");
    }
}
