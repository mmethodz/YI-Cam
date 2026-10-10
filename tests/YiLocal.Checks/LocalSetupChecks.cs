using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using YiLocal.Core;

static class LocalSetupChecks
{
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Reject(Action action)
    { try { action(); } catch (Exception e) when (e is ArgumentException or IOException or InvalidDataException) { return; } throw new Exception("Unsafe setup input accepted."); }
    static async Task RejectAsync(Func<Task> action)
    { try { await action(); } catch (Exception e) when (e is IOException or InvalidDataException or OperationCanceledException) { return; } throw new Exception("Unsafe setup response accepted."); }

    public static async Task RunAsync()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "YiLocal.sln"))) root = root.Parent;
        if (root is not null)
        {
            using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root.FullName, "firmware/profiles/ak3918e-local01.json")));
            var binary = manifest.RootElement.GetProperty("binaries").EnumerateArray().Single(b => b.GetProperty("path").GetString() == "bin/anyka_ipc");
            Check(manifest.RootElement.GetProperty("target_version").GetString() == LocalFirmwarePairing.Firmware &&
                binary.GetProperty("source_size").GetInt32() == LocalFirmwarePairing.BinarySize &&
                binary.GetProperty("result_sha256").GetString() == LocalFirmwarePairing.BinaryHash, "Native onboarding gates drifted from the reviewed firmware manifest.");
        }
        byte[] key = Encoding.ASCII.GetBytes("abcdefghijklmno\n"), identity = Encoding.ASCII.GetBytes("[identity]\np2pid=TNPTEST-000123-ABCDE\n");
        var profile = LocalFirmwarePairing.ParseProfile("192.168.1.30", " Test ", key, identity, null);
        Check(profile.Password == "abcdefghijklmno" && profile.Name == "Test" && profile.Uid == VendorPairingLayout.ParseIdentity("TNPTEST-000123-ABCDE"), "Local key/identity parse failed.");
        Reject(() => LocalFirmwarePairing.ParseProfile("192.168.1.30", "Test", key[..15], identity, null));
        Reject(() => LocalFirmwarePairing.ParseProfile("192.168.1.30", "Test", new byte[16], identity, null));
        Reject(() => LocalFirmwarePairing.ParseProfile("192.168.1.30", "Test", key, identity.Concat(identity).ToArray(), null));
        Reject(() => LocalFirmwarePairing.ParseProfile("192.168.1.30", "Test", key, identity, new string('0', 40)));
        foreach (string ip in new[] { "example.com", "8.8.8.8", "0.0.0.0", "224.0.0.1", "::1" }) Reject(() => CameraDiscovery.LanAddress(ip));
        byte[] ini = Encoding.UTF8.GetBytes("[softap]\nssid=Wrong\n[wireless]\nssid=Keittiö #1\npassword=  spaced=password  \n");
        Check(LocalFirmwarePairing.IniValue(ini, "wireless", "ssid") == "Keittiö #1" &&
            LocalFirmwarePairing.IniValue(ini, "wireless", "password") == "  spaced=password  ", "Wi-Fi credentials were trimmed or read from the wrong section.");
        Reject(() => LocalFirmwarePairing.IniValue(Encoding.UTF8.GetBytes("[wireless]\nssid=a\nssid=b"), "wireless", "ssid"));
        Check(!new WifiSetupNetwork("Test", "DoNotPrintThis").ToString().Contains("DoNotPrintThis"), "Wi-Fi object leaks its password.");

        var reads = new List<string>();
        await RejectAsync(() => LocalFirmwarePairing.VerifyFirmwareAsync((path, _, _) =>
        { reads.Add(path); return Task.FromResult(Encoding.ASCII.GetBytes("stock-version")); }, default));
        Check(reads.SequenceEqual(new[] { "/usr/fw_version" }), "An unsupported version caused key or binary reads.");
        reads.Clear();
        await RejectAsync(() => LocalFirmwarePairing.VerifyFirmwareAsync((path, _, _) =>
        { reads.Add(path); return Task.FromResult(path.EndsWith("fw_version") ? Encoding.ASCII.GetBytes(LocalFirmwarePairing.Firmware) : new byte[LocalFirmwarePairing.BinarySize]); }, default));
        Check(reads.SequenceEqual(new[] { "/usr/fw_version", "/usr/bin/anyka_ipc" }), "A wrong binary caused key reads.");

        var packet = new byte[24]; packet[0] = 0xf1; packet[1] = 0x41; packet[3] = 20;
        Convert.FromHexString(profile.Uid!).CopyTo(packet, 4);
        Check(CameraDiscovery.ParseReply(packet, IPAddress.Parse("192.168.1.30"))?.Uid == profile.Uid, "Discovery identity was not preserved.");
        Check(CameraDiscovery.ParseReply(packet, IPAddress.Parse("8.8.8.8")) is null && CameraDiscovery.ParseReply(packet[..23], IPAddress.Loopback) is null, "Malformed/public discovery accepted.");
        packet[3] = 19; Check(CameraDiscovery.ParseReply(packet, IPAddress.Loopback) is null, "Discovery length mismatch accepted.");
        Check(MaintenanceReader.PassivePort("227 Entering Passive Mode (203,0,113,9,123,45)") == 31533, "Passive port parse failed.");
        Reject(() => MaintenanceReader.PassivePort("227 (1,2,3,4,999,1)"));
        await CheckFtpAsync(false);
        await CheckFtpAsync(true);
        Console.WriteLine("Local onboarding: firmware gates, key/identity validation, private discovery, read-only FTP, PASV host pinning and transfer bounds passed.");
    }

    static async Task CheckFtpAsync(bool oversized)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var worker = Task.Run(async () =>
        {
            using var accepted = await listener.AcceptTcpClientAsync(timeout.Token);
            using var input = new StreamReader(accepted.GetStream(), Encoding.ASCII, false, 1024, true);
            using var output = new StreamWriter(accepted.GetStream(), Encoding.ASCII, 1024, true) { NewLine = "\r\n", AutoFlush = true };
            await output.WriteLineAsync("220-Fixture greeting\r\n220 Ready");
            foreach (var (command, reply) in new[] { ("USER root", "331 Password"), ("PASS test-password", "230 Logged in"), ("TYPE I", "200 OK") })
            { Check(await input.ReadLineAsync(timeout.Token) == command, "Unexpected FTP command."); await output.WriteLineAsync(reply); }
            Check(await input.ReadLineAsync(timeout.Token) == "PASV", "Client did not request passive transfer.");
            var dataListener = new TcpListener(IPAddress.Loopback, 0); dataListener.Start();
            try
            {
                int dataPort = ((IPEndPoint)dataListener.LocalEndpoint).Port;
                // Deliberately false host: the client must remain pinned to the control address.
                await output.WriteLineAsync($"227 Passive (203,0,113,9,{dataPort / 256},{dataPort % 256})");
                using var data = await dataListener.AcceptTcpClientAsync(timeout.Token);
                Check(await input.ReadLineAsync(timeout.Token) == "RETR /etc/jffs2/openyi.key", "Unexpected file read or mutation.");
                await output.WriteLineAsync("150 Data");
                await data.GetStream().WriteAsync(Encoding.ASCII.GetBytes(oversized ? "abcdefghijklmno\n!" : "abcdefghijklmno\n"), timeout.Token);
                data.Close(); await output.WriteLineAsync("226 Complete");
            }
            finally { dataListener.Stop(); }
        }, timeout.Token);
        try
        {
            using var client = await MaintenanceReader.ConnectAsync("127.0.0.1", "test-password", timeout.Token, port);
            if (oversized) await RejectAsync(() => client.ReadAsync("/etc/jffs2/openyi.key", 16, timeout.Token));
            else Check(Encoding.ASCII.GetString(await client.ReadAsync("/etc/jffs2/openyi.key", 16, timeout.Token)) == "abcdefghijklmno\n", "FTP changed key bytes.");
            await worker;
        }
        finally { listener.Stop(); }
    }

    public static async Task HardwareAsync(string existingPath, string outputDirectory)
    {
        var previous = DeviceProfile.Load(existingPath);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var found = await CameraDiscovery.FindAsync(timeout.Token);
        Console.WriteLine($"Native broadcast discovery found the reference camera: {found.Any(c => c.Uid == previous.Uid)}.");
        var exported = await LocalFirmwarePairing.ReadProfileAsync(previous.Ip, previous.Name, LocalFirmwarePairing.DefaultMaintenancePassword, previous.Uid, timeout.Token);
        var verified = await PairingImport.VerifyAsync(exported, timeout.Token);
        Check(verified.Password == previous.Password, "Exported key differs from the pre-test pairing.");
        Directory.CreateDirectory(outputDirectory);
        string path = Path.Combine(outputDirectory, "native-local-pairing.dpapi");
        Check(!File.Exists(path), "Choose a new private output directory.");
        verified.Save(path); var loaded = DeviceProfile.Load(path);
        Check(loaded.Password == verified.Password && loaded.Uid == verified.Uid, "Native encrypted profile round-trip failed.");
        var wifi = await LocalFirmwarePairing.ReadWifiAsync(previous.Ip, LocalFirmwarePairing.DefaultMaintenancePassword, timeout.Token);
        File.WriteAllBytes(Path.Combine(outputDirectory, "native-local-setup.png"), QrProvisioning.Png(QrProvisioning.ComposeLocal(wifi.Ssid, wifi.Password!)));
        using var camera = new CameraClient(loaded.Ip, loaded.Password, loaded.Uid);
        await camera.ConnectAsync(timeout.Token); await camera.StartVideoAsync();
        int frames = 0;
        while (frames < 10)
        { var frame = await camera.Frames.ReadAsync(timeout.Token); Check(frame.Width > 0 && frame.Height > 0 && frame.Data.Length > 0, "Empty video after native onboarding."); frames++; }
        await camera.StopVideoAsync();
        Console.WriteLine("Native direct import, verified authentication, DPAPI save/reload, camera Wi-Fi reuse, QR export and 10 live frames passed. Existing client files unchanged.");
    }
}
