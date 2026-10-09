using System.Text;
using YiLocal.Core;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Reject(Action action, string message)
{
    try { action(); } catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or InvalidOperationException) { return; }
    throw new Exception(message);
}
static VideoFrame Frame(ushort seq, bool key = false, uint ms = 100000) => new([0, 0, 0, 1, key ? (byte)0x65 : (byte)0x41, 1], seq, 1280, 720, 1, ms, key, 1, 78);

if (args.Length > 0 && args[0] == "--inspect-import")
{
    if (args.Length != 2) throw new ArgumentException("--inspect-import <encrypted-profile-to-compare>");
    var saved = DeviceProfile.Load(args[1]);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
    var imported = await VendorClientImporter.ReadProfileAsync(saved.Ip, saved.Name, saved.Uid, timeout.Token);
    Console.WriteLine($"Read a matching live pairing. Same identity: {saved.Uid == imported.Uid}; same key: {saved.Password == imported.Password}. No profile was changed.");
    return;
}

if (args.Length > 0 && args[0] == "--mux-fixture")
{
    if (args.Length != 3) throw new ArgumentException("--mux-fixture <160x90-annex-b-with-AUD> <output-folder>");
    var units = FragmentedMp4.Nals(File.ReadAllBytes(args[1])); var access = new List<byte[]>(); ushort nr = 0;
    using var recorder = new SegmentRecorder(args[2], new(SegmentMinutes: 0.1));
    void Flush()
    {
        if (!access.Any(n => (n[0] & 31) is 1 or 5)) { access.Clear(); return; }
        var data = Wire.Join(access.Select(n => Wire.Join([0, 0, 0, 1], n)).ToArray());
        recorder.Write(new(data, nr, 160, 90, 0, (uint)(nr * 67), access.Any(n => (n[0] & 31) == 5), 1, 78), nr * 67);
        nr++; access.Clear();
    }
    foreach (var nal in units) { if ((nal[0] & 31) == 9) Flush(); access.Add(nal); }
    Flush(); Check(recorder.Frames >= 100, "Too few fixture frames.");
    Console.WriteLine($"Wrote {recorder.Frames} fixture frames with 67 ms camera timestamps."); return;
}

if (args.Length > 0 && args[0] == "--live")
{
    if (args.Length != 3) throw new ArgumentException("--live <encrypted-profile> <output-folder>");
    var profile = DeviceProfile.Load(args[1]);
    using var camera = new CameraClient(profile.Ip, profile.Password, profile.Uid);
    await camera.ConnectAsync();
    Console.WriteLine("Native LAN connection established; firmware " + await camera.FirmwareAsync());
    var before = await camera.SettingsAsync();
    Console.WriteLine($"Hardware {before.Hardware}; night mode {before.NightVision}; tracking {before.Tracking}");
    await camera.NightVisionAsync(before.NightVision);
    await camera.TrackingAsync(before.Tracking != 0);
    Check(await camera.SettingsAsync() == before, "Control readback changed unexpectedly.");
    await camera.StartVideoAsync();
    var clock = new FrameClock(); var order = new FrameOrder();
    using (var recorder = new SegmentRecorder(args[2], new(SegmentMinutes: 0.1)))
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try
        {
            await foreach (var incoming in camera.Frames.ReadAllAsync(timeout.Token))
                foreach (var frame in order.Feed(incoming))
                {
                    long time = clock.Time(frame); recorder.Write(frame, time, order.Epoch);
                    if (recorder.Frames % 60 == 0) Console.WriteLine($"{recorder.Frames} frames, {frame.Width}x{frame.Height}, {time} ms");
                    if (time > 21000) goto done;
                }
        }
        catch (OperationCanceledException) { }
        done: Check(recorder.Frames > 150, "Too few frames received.");
    }
    using var library = new RecordingLibrary(args[2]);
    Console.WriteLine($"Native recording finished: {library.Clips().Count} clips.");
    return;
}

await PairingChecks.RunAsync();
var channel = new ReliableChannel();
var message = Wire.Join([2, 3, 0, 0], Wire.U32(5), [1, 2, 3, 4, 5]);
Check(channel.Feed(1, message[6..]).Count == 0, "Out-of-order fragment escaped.");
Check(channel.Feed(0, message[..6]).Single().Body.SequenceEqual(new byte[] { 1, 2, 3, 4, 5 }), "Reassembly failed.");
Check(channel.Feed(0, message).Count == 0, "Duplicate escaped.");
channel = new ReliableChannel { Expected = 65535 };
Check(channel.Feed(65535, message).Count == 1 && channel.Feed(0, message).Count == 1, "Packet wrap failed.");
Reject(() => new ReliableChannel().Feed(0, Wire.Join([2, 3, 0, 0], Wire.U32(9000000))), "Oversize accepted.");
Check(Encoding.ASCII.GetString(Wire.Auth("123456789012345", "123456789012345")) == "123456789012345,01ALv/UXbTmKuLm\0", "Authentication golden fixture mismatch.");
var orderTest = new FrameOrder();
Check(orderTest.Feed(Frame(2)).Count == 0, "P frame escaped before I.");
Check(orderTest.Feed(Frame(1, true)).Select(f => f.Sequence).SequenceEqual(new ushort[] { 1, 2 }), "I/P merge failed.");
Check(orderTest.Feed(Frame(4), 100).Count == 0, "Gap not held.");
Check(orderTest.Feed(Frame(5, true), 2201).Single().Sequence == 5 && orderTest.Epoch == 1, "Gap recovery failed.");
var timeTest = new FrameClock();
Check(timeTest.Time(Frame(1, true, uint.MaxValue - 10)) == 0 && timeTest.Time(Frame(2, ms: 55)) == 66, "Timestamp wrap failed.");
var nals = FragmentedMp4.Nals([0, 0, 0, 1, 0x67, 2, 0, 0, 1, 0x68, 3]);
Check(nals.Count == 2 && nals[0].SequenceEqual(new byte[] { 0x67, 2 }), "Annex-B parsing failed.");
string folder = Path.Combine(Path.GetTempPath(), "yi-local-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
try
{
    using var library = new RecordingLibrary(folder);
    const string name = "YI_2026-01-01_00-00-00_1280x720_abcdef.mp4";
    File.WriteAllBytes(library.ClipPath(name), [1, 2, 3]); library.Register(name, 1280, 720); library.Finish(name, 1);
    library.Protect(name, true); Reject(() => library.Delete(name), "Protected clip deleted.");
    Reject(() => library.ClipPath("../outside.mp4"), "Unsafe name accepted.");
    Reject(() => library.Enforce(new(), freeBytes: () => 1), "Full disk accepted.");
    Check(File.Exists(library.ClipPath(name)), "Default policy erased a clip.");
    library.Protect(name, false);
    Check(library.Enforce(new(Recycle: true, KeepDays: 1), freeBytes: () => 100L << 30) == 0, "New clip expired.");
    Reject(() => library.Enforce(new(Recycle: true), freeBytes: () => 1), "Full disk incorrectly recovered.");
    Check(!File.Exists(library.ClipPath(name)), "Unprotected clip not recycled.");
    using var recorder = new SegmentRecorder(folder, new());
    Reject(() => { using var other = new SegmentRecorder(folder, new()); }, "Second writer accepted.");
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    Directory.Delete(folder, true);
}
Console.WriteLine("All native pairing, protocol, timing, storage, and writer-exclusion checks passed.");
