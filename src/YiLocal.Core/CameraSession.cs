namespace YiLocal.Core;

/// <summary>One reconnecting LAN session. Recording survives reconnects as separate clips.</summary>
public sealed class CameraSession : IAsyncDisposable
{
    readonly DeviceProfile profile;
    readonly object recordLock = new();
    readonly SnapshotBuffer snapshots = new();
    readonly CancellationTokenSource stop = new();
    readonly SemaphoreSlim audioControl = new(1);
    bool monitoring, audioStarted;
    Task? run;
    SegmentRecorder? recorder;
    MotionRecorder? motionRecorder;
    string? recordFolder;
    StoragePolicy policy = new();
    RecordingOptions recordingOptions = new();
    string? recordingFfmpeg;
    bool recording;
    byte quality = 1;
    public CameraClient? Client { get; private set; }
    public bool Recording { get { lock (recordLock) return recording; } }
    public byte Quality => quality;
    public event Action<string>? Status;
    public event Action<CameraSettings?>? Settings;
    public event Action<VideoFrame, long, int>? Frame;
    public event Action<AudioFrame, long>? Audio;
    public event Action<bool>? RecordingChanged;
    public event Action<MotionRecordingState>? MotionChanged;
    public event Action? PairingRejected;
    public VideoSnapshot Snapshot() => snapshots.Take();
    public CameraSession(DeviceProfile profile) { this.profile = profile; }
    public async Task SetMonitoringAsync(bool enabled)
    {
        monitoring = enabled; await UpdateAudioAsync();
    }
    async Task UpdateAudioAsync()
    {
        await audioControl.WaitAsync();
        try
        {
            if (Client is not { Connected: true } client) return;
            bool wanted; lock (recordLock) wanted = monitoring || recording && recordingOptions.IncludeAudio;
            if (wanted == audioStarted) return;
            if (wanted) await client.StartAudioAsync(); else await client.StopAudioAsync();
            audioStarted = wanted;
        }
        catch (Exception e) { Status?.Invoke("Audio control: " + e.Message); }
        finally { audioControl.Release(); }
    }
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
            if (recordingOptions.Mode == CaptureMode.Motion)
            {
                motionRecorder = new(folder, selectedPolicy, recordingOptions, ffmpeg, profile.Name);
                motionRecorder.State += state =>
                {
                    MotionChanged?.Invoke(state);
                    if (recording) Status?.Invoke(state.Capturing
                        ? $"Motion recording · {state.RemainingSeconds:0} s after last motion · {state.ChangedPercent:0.0}% changed"
                        : $"Watching for motion · {state.ChangedPercent:0.0}% changed / {recordingOptions.MotionThresholdPercent:0.0}% threshold");
                };
            }
            else recorder = new SegmentRecorder(folder, selectedPolicy, recordingOptions, ffmpeg, profile.Name);
            recordFolder = folder; policy = selectedPolicy;
            // Queue the initial status before incoming frames can report an opened clip.
            Status?.Invoke(recordingOptions.Mode == CaptureMode.Motion ? "Motion recording armed; starting local detection." :
                recordingOptions.IncludeAudio ? "Recording armed; waiting for AAC audio and the next keyframe." : "Recording armed; waiting for the next keyframe."); recording = true;
        }
        RecordingChanged?.Invoke(true);
        _ = UpdateAudioAsync();
    }
    public void StopRecording()
    {
        SegmentRecorder? closing;
        MotionRecorder? closingMotion;
        lock (recordLock)
        {
            recording = false; closing = recorder; recorder = null; closingMotion = motionRecorder; motionRecorder = null;
        }
        try { closing?.Dispose(); closingMotion?.Dispose(); }
        finally { RecordingChanged?.Invoke(false); _ = UpdateAudioAsync(); }
    }
    void CloseSegment()
    {
        lock (recordLock) { recorder?.ConnectionEnded(); motionRecorder?.ConnectionEnded(); }
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
                audioStarted = false;
                CameraSettings? settings = null;
                try { settings = await client.SettingsAsync(); } catch (NotSupportedException) { }
                Settings?.Invoke(settings);
                await client.StartVideoAsync(quality);
                Status?.Invoke("Connected locally.");
                var order = new FrameOrder(); var clock = new FrameClock();
                using var audioStop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                var audioTask = ReadAudioAsync(client, clock, audioStop.Token);
                await UpdateAudioAsync();
                try
                {
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
                                    if (motionRecorder is not null) motionRecorder.Write(frame, time, order.Epoch);
                                    else
                                    {
                                        recorder ??= new SegmentRecorder(recordFolder!, policy, recordingOptions, recordingFfmpeg, profile.Name);
                                        bool opening = recorder.Current is null;
                                        recorder.Write(frame, time, order.Epoch);
                                        if (opening && recorder.Current is not null) Status?.Invoke("Recording to local disk · " + recordingOptions.Label + ".");
                                    }
                                }
                                catch (Exception e)
                                {
                                    recording = false;
                                    try { recorder?.Dispose(); } catch (IOException) { }
                                    try { motionRecorder?.Dispose(); } catch (IOException) { }
                                    recorder = null; motionRecorder = null;
                                    RecordingChanged?.Invoke(false); Status?.Invoke("Recording stopped: " + e.Message);
                                    _ = UpdateAudioAsync();
                                }
                        }
                        Frame?.Invoke(frame, time, order.Epoch);
                    }
                }
                finally
                {
                    audioStop.Cancel();
                    try { await audioTask; } catch (OperationCanceledException) { }
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
    async Task ReadAudioAsync(CameraClient client, FrameClock clock, CancellationToken cancellation)
    {
        try
        {
            await foreach (var frame in client.AudioFrames.ReadAllAsync(cancellation))
            {
                if (clock.AudioTime(frame) is not { } time) continue;
                lock (recordLock)
                {
                    if (recording)
                        try
                        {
                            if (motionRecorder is not null) motionRecorder.WriteAudio(frame, time);
                            else recorder?.WriteAudio(frame, time);
                        }
                        catch (Exception e)
                        {
                            recording = false;
                            try { recorder?.Dispose(); } catch (IOException) { }
                            try { motionRecorder?.Dispose(); } catch (IOException) { }
                            recorder = null; motionRecorder = null;
                            RecordingChanged?.Invoke(false); Status?.Invoke("Recording stopped: " + e.Message);
                            _ = UpdateAudioAsync();
                        }
                }
                if (monitoring) Audio?.Invoke(frame, time);
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException)
        { if (!cancellation.IsCancellationRequested && client.Connected) Status?.Invoke("Audio stream: " + e.Message); }
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        try { if (run is not null) await run; }
        finally { StopRecording(); stop.Dispose(); }
    }
}
