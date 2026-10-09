namespace YiLocal.Core;

/// <summary>One reconnecting LAN session. Recording survives reconnects as separate clips.</summary>
public sealed class CameraSession : IAsyncDisposable
{
    readonly DeviceProfile profile;
    readonly object recordLock = new();
    readonly SnapshotBuffer snapshots = new();
    readonly CancellationTokenSource stop = new();
    Task? run;
    SegmentRecorder? recorder;
    string? recordFolder;
    StoragePolicy policy = new();
    RecordingOptions recordingOptions = new();
    string? recordingFfmpeg;
    bool recording;
    byte quality = 1;
    public CameraClient? Client { get; private set; }
    public bool Recording { get { lock (recordLock) return recording; } }
    public event Action<string>? Status;
    public event Action<CameraSettings?>? Settings;
    public event Action<VideoFrame, long, int>? Frame;
    public event Action<bool>? RecordingChanged;
    public event Action? PairingRejected;
    public VideoSnapshot Snapshot() => snapshots.Take();
    public CameraSession(DeviceProfile profile) { this.profile = profile; }
    public void Start() { if (run is not null) throw new InvalidOperationException(); run = Task.Run(RunAsync); }
    public async Task SetQualityAsync(byte value)
    {
        if (value > 2) throw new ArgumentOutOfRangeException(nameof(value));
        await (Client ?? throw new IOException("Camera is disconnected.")).QualityAsync(value); quality = value;
    }
    public void StartRecording(string folder, StoragePolicy selectedPolicy, RecordingOptions? options = null, string? ffmpeg = null)
    {
        lock (recordLock)
        {
            if (recording) return;
            recordingOptions = options ?? new(); recordingFfmpeg = ffmpeg;
            recorder = new SegmentRecorder(folder, selectedPolicy, recordingOptions, ffmpeg, profile.Name);
            recordFolder = folder; policy = selectedPolicy;
            // Queue the initial status before incoming frames can report an opened clip.
            Status?.Invoke("Recording armed; waiting for the next keyframe."); recording = true;
        }
        RecordingChanged?.Invoke(true);
    }
    public void StopRecording()
    {
        SegmentRecorder? closing;
        lock (recordLock)
        {
            recording = false; closing = recorder; recorder = null;
        }
        try { closing?.Dispose(); }
        finally { RecordingChanged?.Invoke(false); }
    }
    void CloseSegment()
    {
        lock (recordLock) recorder?.CloseSegment();
    }
    async Task RunAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var client = new CameraClient(profile.Ip, profile.Password, profile.Uid);
                Status?.Invoke("Connecting over the LAN…");
                await client.ConnectAsync(stop.Token);
                await client.FirmwareAsync();
                if (profile.Uid is null) { profile.Uid = client.Uid; profile.Save(); }
                Client = client;
                CameraSettings? settings = null;
                try { settings = await client.SettingsAsync(); } catch (NotSupportedException) { }
                Settings?.Invoke(settings);
                await client.StartVideoAsync(quality);
                Status?.Invoke("Connected locally.");
                var order = new FrameOrder(); var clock = new FrameClock();
                await foreach (var input in client.Frames.ReadAllAsync(stop.Token))
                    foreach (var frame in order.Feed(input))
                    {
                        long time = clock.Time(frame);
                        snapshots.Add(frame, time, order.Epoch);
                        lock (recordLock)
                        {
                            if (recording)
                                try
                                {
                                    recorder ??= new SegmentRecorder(recordFolder!, policy, recordingOptions, recordingFfmpeg, profile.Name);
                                    bool opening = recorder.Current is null;
                                    recorder.Write(frame, time, order.Epoch);
                                    if (opening && recorder.Current is not null) Status?.Invoke("Recording to local disk · " + recordingOptions.Label + ".");
                                }
                                catch (Exception e)
                                {
                                    recording = false;
                                    try { recorder?.Dispose(); } catch (IOException) { }
                                    recorder = null;
                                    RecordingChanged?.Invoke(false); Status?.Invoke("Recording stopped: " + e.Message);
                                }
                        }
                        Frame?.Invoke(frame, time, order.Epoch);
                    }
                if (!stop.IsCancellationRequested) throw new IOException("Camera stream ended.");
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (CameraAuthenticationException e) when (e.Result == 1)
            {
                Status?.Invoke(e.Message);
                PairingRejected?.Invoke();
                break; // A rejected saved key needs refreshing, not an endless reconnect loop.
            }
            catch (Exception e) { Status?.Invoke("Disconnected: " + e.Message + " Retrying in 3 seconds."); }
            finally
            {
                Client = null;
                snapshots.Clear();
                Settings?.Invoke(null);
                try { CloseSegment(); }
                catch (Exception e)
                {
                    lock (recordLock) recording = false;
                    RecordingChanged?.Invoke(false); Status?.Invoke("Could not finish clip: " + e.Message);
                }
            }
            try { await Task.Delay(3000, stop.Token); } catch (OperationCanceledException) { break; }
        }
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        try { if (run is not null) await run; }
        finally { StopRecording(); stop.Dispose(); }
    }
}
