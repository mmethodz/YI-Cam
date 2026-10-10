using System.Text;
using YiLocal.Core;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Reject(Action action, string message)
{
    try { action(); } catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or InvalidOperationException) { return; }
    throw new Exception(message);
}
static VideoFrame Frame(ushort seq, bool key = false, uint ms = 100000) => new([0, 0, 0, 1, key ? (byte)0x65 : (byte)0x41, 1], seq, 1280, 720, 1, ms, key, 1, 78);
if (AudioChecks.Fixture(args)) return;
if (MotionChecks.Fixture(args)) return;
if (args is ["--localization"]) { LocalizationChecks.Run(); return; }

if (args.Length == 2 && args[0] == "--qr-fixture")
{
    File.WriteAllBytes(args[1], QrProvisioning.Png(QrProvisioning.Compose("Test WiFi", "password123", "TEST-TOKEN"))); return;
}

if (args.Length > 0 && args[0] == "--inspect-import")
{
    if (args.Length != 2) throw new ArgumentException("--inspect-import <encrypted-profile-to-compare>");
    var saved = DeviceProfile.Load(args[1]);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
    var imported = await VendorClientImporter.ReadProfileAsync(saved.Ip, saved.Name, saved.Uid, timeout.Token);
    Console.WriteLine($"Read a matching live pairing. Same identity: {saved.Uid == imported.Uid}; same key: {saved.Password == imported.Password}. No profile was changed.");
    return;
}

if (args.Length > 0 && args[0] is "--mux-fixture" or "--encode-fixture" or "--measure-recording")
{
    if (args.Length != 3 && args.Length != 10 && args.Length != 11) throw new ArgumentException("--mux-fixture <160x90-annex-b-with-AUD> <output-folder> OR --encode-fixture <annex-b-with-AUD> <output-folder> <ffmpeg> <Original|Balanced|Small> <fps|source> <Continuous|Timelapse> <width> <height> <frame-ms> [timestamp-array.json]");
    var options = args.Length == 3 ? new RecordingOptions() : new(Enum.Parse<RecordingEncoding>(args[4]),
        args[5] == "source" ? null : double.Parse(args[5], System.Globalization.CultureInfo.InvariantCulture), Enum.Parse<CaptureMode>(args[6]));
    int width = args.Length == 3 ? 160 : int.Parse(args[7]), height = args.Length == 3 ? 90 : int.Parse(args[8]);
    int step = args.Length == 3 ? 67 : int.Parse(args[9]);
    var times = args.Length == 11 ? System.Text.Json.JsonSerializer.Deserialize<long[]>(File.ReadAllText(args[10])) : null;
    var snapshots = new SnapshotBuffer();
    var units = FragmentedMp4.Nals(File.ReadAllBytes(args[1])); var access = new List<byte[]>(); ushort nr = 0;
    using var recorder = new SegmentRecorder(args[2], new(SegmentMinutes: args[0] == "--measure-recording" ? 10 : 0.1), options, args.Length == 3 ? null : args[3], "Fixture camera");
    void Flush()
    {
        if (!access.Any(n => (n[0] & 31) is 1 or 5)) { access.Clear(); return; }
        var data = Wire.Join(access.Select(n => Wire.Join([0, 0, 0, 1], n)).ToArray());
        long time = times?[nr] ?? nr * step;
        var frame = new VideoFrame(data, nr, width, height, 0, (uint)time, access.Any(n => (n[0] & 31) == 5), 1, 78);
        recorder.Write(frame, time); snapshots.Add(frame, time, 0);
        if (options.Encoding != RecordingEncoding.Original) Thread.Sleep(20); // Pace the accelerated source instead of overflowing a deliberately bounded live queue.
        nr++; access.Clear();
    }
    foreach (var nal in units) { if ((nal[0] & 31) == 9) Flush(); access.Add(nal); }
    Flush(); Check(recorder.Frames >= 100, "Too few fixture frames.");
    var snapshot = snapshots.Take();
    File.WriteAllBytes(Path.Combine(args[2], "snapshot-input.mp4"), snapshot.ToMp4());
    File.WriteAllText(Path.Combine(args[2], "snapshot-index.txt"), (snapshot.Pictures.Count - 1).ToString());
    Console.WriteLine($"Wrote {recorder.Frames} source frames with {step} ms camera timestamps ({options.Label})."); return;
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
MotionChecks.Run();
AlarmChecks.Run();
TalkChecks.Run();
await MultiCameraChecks.RunAsync();
Check(CameraAlert.Parse(new byte[4]).Count == 0, "Empty alert history rejected.");
Check(CameraAlert.Parse(Wire.Join(Wire.U32(1), Wire.U32(1), Wire.U32(100), Wire.U32(6))).Single() == new CameraAlert(1, 100, 6), "Alert history fields differ.");
Reject(() => CameraAlert.Parse(Wire.U32(1)), "Truncated alert history accepted.");
Reject(() => CameraAlert.Parse(Wire.U32(uint.MaxValue)), "Unbounded alert count accepted.");
Reject(() => new RecordingOptions(FramesPerSecond: 1).Validate(), "Original stream silently discarded source frames.");
Reject(() => new RecordingOptions(RecordingEncoding.Balanced, double.NaN).Validate(), "Non-finite capture rate accepted.");
Reject(() => new RecordingOptions(RecordingEncoding.Balanced, Mode: CaptureMode.Timelapse).Validate(), "Timelapse without a capture rate accepted.");
Check(QrProvisioning.Compose("Test", "89", "TEST") == "b=TEST&s=VGVzdA==&p=ODk=", "QR zero-XOR fallback or Base64 differs from observed format.");
Check(QrProvisioning.Compose("Test", "", null, true, "TESTID") == "t=1&s=VGVzdA==&p=&d=TESTID", "Wi-Fi-change QR format differs.");
Reject(() => QrProvisioning.Compose("Test", "", null, true), "Wi-Fi-change QR omitted the target device ID.");
Reject(() => QrProvisioning.Compose("Test", "password", null), "Fresh QR invented a binding token.");
Reject(() => QrProvisioning.Compose("Test", "password", "x&s=bad"), "QR field injection accepted.");
Reject(() => QrProvisioning.Compose(new string('ä', 17), "password", "TEST"), "Overlong UTF-8 SSID accepted.");
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
Check(timeTest.AudioTime(new([], 0, 1, 45, 138, 27)) == 56, "Audio wrap mapping changed the common clock.");
Check(timeTest.Time(Frame(3, ms: 122)) == 133, "Audio perturbed video timing.");
var adts = new byte[] { 0xff, 0xf9, 0x60, 0x40, 0x01, 0x1f, 0xfc, 0 };
var aac = AacConfiguration.Parse(adts);
Check(aac.Configuration.SampleRate == 16000 && aac.Configuration.Channels == 1 && aac.Configuration.AudioSpecificConfig.SequenceEqual(new byte[] { 0x14, 8 }), "AAC configuration differs from ADTS.");
Reject(() => AacConfiguration.Parse(adts[..7]), "Truncated AAC accepted.");
Reject(() => new RecordingOptions(RecordingEncoding.Balanced, 1, CaptureMode.Timelapse, true).Validate(), "Accelerated timelapse accepted real-time audio.");
var nals = FragmentedMp4.Nals([0, 0, 0, 1, 0x67, 2, 0, 0, 1, 0x68, 3]);
Check(nals.Count == 2 && nals[0].SequenceEqual(new byte[] { 0x67, 2 }), "Annex-B parsing failed.");
var snapshotCache = new SnapshotBuffer(maximumFrames: 2);
var snapshotKey = Frame(1, true) with { Data = [0, 0, 0, 1, 0x67, 0x42, 0, 0x1e, 0, 0, 0, 1, 0x68, 1, 0, 0, 0, 1, 0x65, 1] };
snapshotCache.Add(Frame(0), 0, 0); Reject(() => snapshotCache.Take(), "Snapshot accepted a P frame without an IDR.");
snapshotCache.Add(snapshotKey, 1, 0); snapshotCache.Add(Frame(2), 68, 0);
Check(snapshotCache.Take().Pictures.Count == 2, "Snapshot lost a valid GOP.");
snapshotCache.Add(Frame(3), 135, 0); Reject(() => snapshotCache.Take(), "Snapshot cache exceeded its bound.");
snapshotCache.Add(snapshotKey, 202, 1); Check(snapshotCache.Take().Pictures.Count == 1, "Snapshot did not recover at a new keyframe.");
snapshotCache.Clear(); Reject(() => snapshotCache.Take(), "Snapshot survived disconnection.");
string folder = Path.Combine(Path.GetTempPath(), "yi-local-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
try
{
    using var library = new RecordingLibrary(folder);
    const string name = "YI_2026-01-01_00-00-00_1280x720_abcdef.mp4";
    File.WriteAllBytes(library.ClipPath(name), [1, 2, 3]); library.Register(name, 1280, 720, new(Audio: "AAC-LC 16000 Hz, 1 channel(s)")); library.Finish(name, 1);
    Check(library.Clips().Single().Metadata?.Audio == "AAC-LC 16000 Hz, 1 channel(s)", "Catalogue lost audio metadata.");
    library.Protect(name, true); Reject(() => library.Delete(name), "Protected clip deleted.");
    Reject(() => library.ClipPath("../outside.mp4"), "Unsafe name accepted.");
    Reject(() => library.Enforce(new(), freeBytes: () => 1), "Full disk accepted.");
    Check(File.Exists(library.ClipPath(name)), "Default policy erased a clip.");
    library.Protect(name, false);
    using (var lease = library.Hold(name))
    {
        Check(library.Clips().Single().InUse && !library.Clips().Single().Protected, "Playback reservation altered user protection or was lost.");
        Reject(() => library.Delete(name), "Reserved clip deleted.");
        Reject(() => library.Enforce(new(Recycle: true), freeBytes: () => 1), "Reserved clip was recycled.");
        lease.Renew();
    }
    Check(!library.Clips().Single().InUse, "Closed playback kept a reservation.");
    Check(library.Enforce(new(Recycle: true, KeepDays: 1), freeBytes: () => 100L << 30) == 0, "New clip expired.");
    Reject(() => library.Enforce(new(Recycle: true), freeBytes: () => 1), "Full disk incorrectly recovered.");
    Check(!File.Exists(library.ClipPath(name)), "Unprotected clip not recycled.");
    var filtered = new Clip(name, 100, 4, 1280, 720, 1, true, false, true, new("Hall", "Small", "Timelapse", .5, 120));
    Check(new ClipFilter(From: 200, Until: 300, Camera: "Hall", Kind: "Timelapse", Protected: false).Matches(filtered), "Date filter used accelerated playback instead of capture duration.");
    Check(!new ClipFilter(From: 221).Matches(filtered) && !new ClipFilter(Kind: "Motion").Matches(filtered), "Filter included unrelated capture times/type.");
    using var recorder = new SegmentRecorder(folder, new());
    Reject(() => { using var other = new SegmentRecorder(folder, new()); }, "Second writer accepted.");
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    Directory.Delete(folder, true);
}
Console.WriteLine("All native pairing, protocol, timing, storage, and writer-exclusion checks passed.");
