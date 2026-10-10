using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using YiLocal.Core;

namespace YiLocal.Windows;

internal sealed class Preferences
{
    public string Folder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "YI Local");
    public string? Ffmpeg { get; set; }
    public StoragePolicy Storage { get; set; } = new();
    public RecordingOptions Recording { get; set; } = new();
    public bool ExperimentalMultipleCameras { get; set; }
    public static string FilePath => Path.Combine(DeviceProfile.SettingsDirectory, "settings.json");
    public void Save()
    {
        Directory.CreateDirectory(DeviceProfile.SettingsDirectory);
        File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(FilePath + ".tmp", FilePath, true);
    }
}

public sealed partial class MainForm : Form
{
    Preferences preferences = new();
    DeviceProfile? profile;
    CameraSession? session;
    readonly object previewLock = new();
    VideoPreview? preview;
    string? previewIdentity;
    long lastMetrics;
    long firstTime;
    int frameCount;
    bool closing, closed, exporting, importing;
    readonly CancellationTokenSource exportStop = new();
    readonly System.Windows.Forms.Timer timer = new() { Interval = 40 };
    readonly TabControl tabs = new() { Dock = DockStyle.Fill };
    readonly PictureBox picture = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(26, 29, 34), SizeMode = PictureBoxSizeMode.Zoom };
    readonly Label status = new() { Text = "Ready. Connect a saved camera to begin.", Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly Label metrics = new() { Text = "Original stream · local connection", AutoSize = true };
    readonly Label recordState = new() { Text = "Recording is off", AutoSize = true, ForeColor = Color.DarkSlateGray };
    readonly Button connect = new() { Text = "Connect camera", AutoSize = true };
    readonly Button record = new() { Text = "Start recording", AutoSize = true, Enabled = false };
    readonly ComboBox quality = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 218 };
    readonly ComboBox night = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 218, Enabled = false };
    readonly CheckBox tracking = new() { Text = "Motion tracking", AutoSize = true, Enabled = false };
    readonly FlowLayoutPanel cameraControls = new() { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Dock = DockStyle.Top, Enabled = false };
    readonly RecordingListView clips = new() { View = View.Details, FullRowSelect = true, MultiSelect = false, Dock = DockStyle.Fill, HideSelection = false };
    readonly TextBox folder = new() { Width = 610 };
    readonly TextBox ffmpeg = new() { Width = 610 };
    readonly NumericUpDown quota = Number(0.1m, 100000, 20);
    readonly NumericUpDown free = Number(0.1m, 100000, 2);
    readonly NumericUpDown segment = Number(0.1m, 120, 10);
    readonly NumericUpDown days = Number(0, 36500, 0, 0);
    readonly CheckBox recycle = new() { Text = "Recycle oldest unprotected recordings when limits are reached", AutoSize = true };
    readonly TextBox ip = new() { Width = 280 };
    readonly TextBox cameraName = new() { Width = 280 };
    readonly TextBox key = new() { Width = 280, UseSystemPasswordChar = true };
    readonly Label pairing = new() { AutoSize = true, MaximumSize = new Size(760, 0) };
    readonly FlowLayoutPanel pairingControls = Column();
    [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);

    public MainForm()
    {
        Text = "OpenYI — Camera & Recordings (Windows)";
        Font = new Font("Segoe UI", 10); ClientSize = new Size(1180, 740); MinimumSize = new Size(1000, 650);
        StartPosition = FormStartPosition.CenterScreen;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Padding = new Padding(10) };
        layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.Absolute, 40));
        layout.Controls.Add(tabs, 0, 0); layout.Controls.Add(status, 0, 1); Controls.Add(layout);
        BuildLive(); BuildRecordings(); BuildStorage(); BuildCamera(); BuildProvisioning(); BuildCapture(); BuildCameras();
        try
        {
            if (File.Exists(Preferences.FilePath)) preferences = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(Preferences.FilePath)) ?? new();
            preferences.Storage.Validate();
            preferences.Recording.Validate();
            if (File.Exists(DeviceProfile.DefaultPath)) profile = DeviceProfile.Load();
            else
                for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
                    if (File.Exists(Path.Combine(dir.FullName, "YiLocal.sln")) && File.Exists(Path.Combine(dir.FullName, ".local", "device.dpapi")))
                    { profile = DeviceProfile.Load(Path.Combine(dir.FullName, ".local", "device.dpapi")); profile.Save(DeviceProfile.DefaultPath); break; }
        }
        catch (Exception e) { preferences = new(); status.Text = "Saved settings could not be loaded: " + e.Message; }
        folder.Text = preferences.Folder; ffmpeg.Text = preferences.Ffmpeg ?? MediaTools.FindFfmpeg() ?? "";
        quota.Value = (decimal)preferences.Storage.QuotaGiB; free.Value = (decimal)preferences.Storage.MinimumFreeGiB;
        segment.Value = (decimal)preferences.Storage.SegmentMinutes; days.Value = preferences.Storage.KeepDays; recycle.Checked = preferences.Storage.Recycle;
        ShowProfile();
        ShowCaptureOptions();
        LoadCameras();
        timer.Tick += (_, _) =>
        {
            UpdatePlayback();
            UpdateCameraGrid();
            Bitmap? next; lock (previewLock) next = preview?.Take();
            if (next is not null) { var prior = picture.Image; picture.Image = next; SetPrimaryGridImage(next); prior?.Dispose(); }
        };
        timer.Start();
        FormClosing += OnClosing;
    }
    static NumericUpDown Number(decimal min, decimal max, decimal value, int decimals = 1) =>
        new() { Minimum = min, Maximum = max, Value = value, DecimalPlaces = decimals, Width = 140 };
    static Label Label(string text, int width = 740) => new() { Text = text, AutoSize = true, MaximumSize = new Size(width, 0), Margin = new Padding(3, 12, 3, 4) };
    static FlowLayoutPanel Column() => new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(12) };
    TabPage Page(string title) { var page = new TabPage(title) { Padding = new Padding(8), UseVisualStyleBackColor = true }; tabs.TabPages.Add(page); return page; }
    static Button Button(string text, Action action) { var b = new Button { Text = text, AutoSize = true, Margin = new Padding(3, 8, 3, 3) }; b.Click += (_, _) => action(); return b; }
    void Ui(Action action) { if (!IsDisposed && IsHandleCreated) try { BeginInvoke(action); } catch (InvalidOperationException) { } }
    async Task Guard(Func<Task> action)
    {
        try { await action(); } catch (Exception e) { status.Text = e.Message; MessageBox.Show(this, e.Message, "OpenYI", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
    void BuildLive()
    {
        var page = Page("Live camera");
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        grid.ColumnStyles.Add(new(SizeType.Percent, 100)); grid.ColumnStyles.Add(new(SizeType.Absolute, 270));
        grid.RowStyles.Add(new(SizeType.Percent, 100)); grid.RowStyles.Add(new(SizeType.Absolute, 36));
        grid.Controls.Add(picture, 0, 0); grid.Controls.Add(metrics, 0, 1);
        var side = Column(); grid.Controls.Add(side, 1, 0); grid.SetRowSpan(side, 2); page.Controls.Add(grid);
        side.Controls.Add(connect); side.Controls.Add(record); side.Controls.Add(recordState); side.Controls.Add(cameraControls);
        side.Controls.Add(Button("Save snapshot…", () => _ = Guard(SaveSnapshotAsync)));
        BuildAudio(side);
        connect.Click += async (_, _) => await Guard(ToggleConnection);
        record.Click += async (_, _) => await Guard(ToggleRecordingAsync);
        cameraControls.Controls.Add(Label("Stream quality", 218)); quality.Items.AddRange(["HD — original stream", "SD — smaller stream", "Automatic quality"]); quality.SelectedIndex = 0;
        cameraControls.Controls.Add(quality);
        quality.SelectionChangeCommitted += async (_, _) => await Guard(async () => { if (session is not null) await session.SetQualityAsync(new byte[] { 1, 2, 0 }[quality.SelectedIndex]); });
        cameraControls.Controls.Add(Label("Night vision", 218)); night.Items.AddRange(["Infrared (in darkness)", "Colour — visible lights", "Automatic lighting"]); cameraControls.Controls.Add(night);
        night.SelectionChangeCommitted += async (_, _) => await Guard(async () =>
        {
            if (session?.Client is not { } client) return;
            await client.NightVisionAsync((uint)night.SelectedIndex); var read = await client.SettingsAsync(); night.SelectedIndex = read.NightVision;
            status.Text = "Night-vision mode confirmed by the camera.";
        });
        cameraControls.Controls.Add(tracking);
        tracking.Click += async (_, _) => await Guard(async () =>
        {
            if (session?.Client is not { } client) return;
            await client.TrackingAsync(tracking.Checked); tracking.Checked = (await client.SettingsAsync()).Tracking != 0;
            status.Text = "Motion-tracking setting confirmed by the camera.";
        });
        cameraControls.Controls.Add(Label("Move camera · short steps", 218));
        var directions = new TableLayoutPanel { ColumnCount = 3, RowCount = 3, AutoSize = true };
        void Move(string label, uint value, int x, int y)
        {
            var b = Button(label, () => _ = Guard(async () => { if (session?.Client is { } c) await c.MoveAsync(value); }));
            b.MinimumSize = new Size(65, 36); directions.Controls.Add(b, x, y);
        }
        Move("Up", 1, 1, 0); Move("Left", 3, 0, 1); Move("Right", 4, 2, 1); Move("Down", 2, 1, 2);
        directions.Controls.Add(Button("Stop", () => _ = Guard(async () => { if (session?.Client is { } c) await c.StopMovingAsync(); })), 1, 1);
        cameraControls.Controls.Add(directions);
        side.Controls.Add(Label("Recordings keep the original resolution. 4K export is a software upscale.", 224));
    }
    async Task ToggleRecordingAsync()
    {
        if (!record.Enabled || session is not { } active) return;
        record.Enabled = false;
        try
        {
            if (active.Recording) { await Task.Run(active.StopRecording); status.Text = "Recording saved."; RefreshClips(); }
            else
            {
                if (!extraCameras.Any(camera => camera.Recording)) SaveCaptureOptions();
                active.StartRecording(preferences.Folder, preferences.Storage, preferences.Recording, preferences.Ffmpeg);
            }
        }
        finally { record.Enabled = session is not null; }
    }
    async Task ToggleConnection()
    {
        connect.Enabled = false;
        try
        {
            if (session is not null)
            {
                await DisconnectAsync(); status.Text = "Disconnected. Recordings are saved."; return;
            }
            if (profile is null) { tabs.SelectedIndex = 3; throw new InvalidOperationException("Import a paired device profile or enter its device key first."); }
            EnsurePrimaryDistinct(profile);
            preferences.Ffmpeg = ffmpeg.Text.Trim();
            var active = new CameraSession(profile); session = active;
            session.Status += text => Ui(() => { if (session == active) { status.Text = text; UpdatePrimaryGridState(text); } });
            session.PairingRejected += () => Ui(() => _ = Guard(async () =>
            {
                if (session != active) return;
                await DisconnectAsync(); tabs.SelectedIndex = 3;
                pairing.Text = "The camera rejected the saved device key. Open this camera's live view in YI IoT, then click Import from running YI IoT below. No new camera pairing is needed.";
                status.Text = "Device key rejected. Refresh it in Camera setup; automatic retries have stopped.";
            }));
            session.Settings += settings =>
            {
                if (session != active) return;
                ClearPreview(); ClearAudio(); firstTime = 0; frameCount = 0;
                Ui(() =>
                {
                    if (session != active) return;
                    cameraControls.Enabled = settings is not null; night.Enabled = tracking.Enabled = settings is not null;
                    if (settings is not null) { night.SelectedIndex = settings.NightVision is <= 2 ? settings.NightVision : -1; tracking.Checked = settings.Tracking != 0; }
                });
            };
            session.RecordingChanged += recordingActive => Ui(() =>
            {
                if (session != active) return;
                UpdateRecordLabels();
                if (!recordingActive) UpdatePrimaryGridState("Recording is off.");
                UpdateKeepAwake();
            });
            session.MotionChanged += state => Ui(() =>
            {
                if (session != active || !active.Recording) return;
                recordState.Text = state.Capturing ? $"● Motion · {state.RemainingSeconds:0} s remaining" : $"Watching · {state.ChangedPercent:0.0}% changed";
                recordState.ForeColor = state.Capturing ? Color.Firebrick : Color.DarkGreen;
            });
            session.Audio += OnAudio; session.Frame += OnFrame; session.Start(); connect.Text = "Disconnect"; record.Enabled = true;
        }
        finally { connect.Enabled = !importing; }
    }
    void UpdateRecordLabels()
    {
        bool armed = session?.Recording == true, motion = preferences.Recording.Mode == CaptureMode.Motion;
        record.Text = motion ? armed ? "Disarm motion recording" : "Arm motion recording" : armed ? "Stop recording" : "Start recording";
        recordState.Text = armed ? motion ? "Watching for motion" : "● Recording to local disk" : "Recording is off";
        recordState.ForeColor = armed ? motion ? Color.DarkGreen : Color.Firebrick : Color.DarkSlateGray;
    }
    async Task DisconnectAsync()
    {
        var previous = session; session = null;
        try { if (previous is not null) await previous.DisposeAsync(); }
        finally
        {
            ClearPreview(); ClearAudio(); listen.Checked = false; cameraControls.Enabled = record.Enabled = false;
            connect.Text = "Connect camera"; UpdateRecordLabels();
            UpdateKeepAwake(); UpdatePrimaryGridState();
        }
    }
    void ClearPreview()
    {
        lock (previewLock) { preview?.Dispose(); preview = null; previewIdentity = null; }
        Ui(() => { var old = picture.Image; picture.Image = null; SetPrimaryGridImage(null); old?.Dispose(); });
    }
    void OnFrame(VideoFrame frame, long time, int epoch)
    {
        lock (previewLock)
        {
            string identity = $"{frame.Generation}:{frame.Width}:{frame.Height}:{epoch}";
            if (previewIdentity != identity || preview?.Failed == true) { preview?.Dispose(); preview = null; previewIdentity = identity; }
            if (preview is null && frame.Keyframe && File.Exists(preferences.Ffmpeg))
                try { preview = new(preferences.Ffmpeg!); } catch (Exception e) { Ui(() => status.Text = "Preview unavailable: " + e.Message); }
            if (preview is not null && !preview.Push(frame.Data)) { preview.Dispose(); preview = null; }
        }
        if (frameCount++ == 0) firstTime = time;
        if (Environment.TickCount64 - lastMetrics > 1000)
        {
            lastMetrics = Environment.TickCount64;
            double fps = time > firstTime ? (frameCount - 1) * 1000.0 / (time - firstTime) : 0;
            Ui(() => metrics.Text = $"{frame.Width} × {frame.Height}  ·  {fps:0.0} source fps  ·  Local LAN" + (string.IsNullOrEmpty(preferences.Ffmpeg) ? "  ·  Select FFmpeg for preview" : ""));
        }
    }
    void BuildStorage()
    {
        var body = Column(); Page("Storage").Controls.Add(body);
        body.Controls.Add(Label("Recording folder")); body.Controls.Add(folder);
        body.Controls.Add(Button("Choose folder…", () => { using var dialog = new FolderBrowserDialog { InitialDirectory = folder.Text }; if (dialog.ShowDialog(this) == DialogResult.OK) folder.Text = dialog.SelectedPath; }));
        void Field(string text, Control control) { var row = new FlowLayoutPanel { AutoSize = true }; row.Controls.Add(new Label { Text = text, Width = 245, AutoSize = false, Height = 28, TextAlign = ContentAlignment.MiddleLeft }); row.Controls.Add(control); body.Controls.Add(row); }
        Field("Recording budget (GiB)", quota); Field("Keep disk space free (GiB)", free); Field("Clip length (minutes)", segment); Field("Maximum age (days; 0 = unlimited)", days);
        body.Controls.Add(recycle);
        body.Controls.Add(Label("Recycling is off by default. Only completed, unprotected recordings registered in this folder can be recycled. Changes apply to the next recording session."));
        body.Controls.Add(Label("FFmpeg executable · needed for preview, snapshots, local motion detection, encoding profiles and 4K export")); body.Controls.Add(ffmpeg);
        body.Controls.Add(Button("Choose FFmpeg…", () => { using var dialog = new OpenFileDialog { Filter = "FFmpeg executable|ffmpeg*.exe|Executable|*.exe" }; if (dialog.ShowDialog(this) == DialogResult.OK) ffmpeg.Text = dialog.FileName; }));
        body.Controls.Add(Button("Save storage settings", () => _ = Guard(() =>
        {
            if (AnyRecording || exporting || gridBusy) throw new InvalidOperationException("Stop all recordings and let camera changes/exports finish before changing storage.");
            var policy = new StoragePolicy((double)quota.Value, (double)free.Value, (double)segment.Value, recycle.Checked, (int)days.Value); policy.Validate();
            preferences.Folder = Path.GetFullPath(folder.Text.Trim()); preferences.Ffmpeg = ffmpeg.Text.Trim(); preferences.Storage = policy;
            StopPlayback(); Directory.CreateDirectory(preferences.Folder); preferences.Save(); ClearPreview(); status.Text = "Storage settings saved."; return Task.CompletedTask;
        })));
    }
    void ShowProfile()
    {
        ip.Text = profile?.Ip ?? ""; cameraName.Text = profile?.Name ?? "Camera"; key.Clear();
        pairing.Text = profile is null ? "No saved camera. Enter its LAN address, open its live view in YI IoT, and import the existing pairing below."
            : $"Saved camera: {profile.Name}. Its device key is encrypted for this Windows account. No cloud account login is used.";
    }
    void BuildCamera()
    {
        var body = pairingControls; Page("Camera setup").Controls.Add(body); body.Controls.Add(pairing);
        body.Controls.Add(Label("Camera name")); body.Controls.Add(cameraName); body.Controls.Add(Label("Camera IPv4 address on your LAN")); body.Controls.Add(ip);
        body.Controls.Add(Button("Import from running YI IoT", () => _ = Guard(() => UpdatePairingAsync(token =>
            VendorClientImporter.ReadProfileAsync(ip.Text.Trim(), cameraName.Text.Trim(), profile?.Uid, token)))));
        body.Controls.Add(Label("Open this camera's live view in YI IoT first. Import checks the key against the camera before replacing your saved profile and connecting. An active recording is finished before import. You do not need to pair the camera again."));
        body.Controls.Add(Label("Device pairing key · leave blank to keep the saved key")); body.Controls.Add(key);
        body.Controls.Add(Button("Verify and save camera", () => _ = Guard(() => UpdatePairingAsync(_ =>
        {
            string password = key.Text.Length > 0 ? key.Text : profile?.Password ?? "";
            return Task.FromResult(new DeviceProfile { Ip = ip.Text.Trim(), Name = cameraName.Text.Trim(), Password = password,
                Uid = key.Text.Length == 0 ? profile?.Uid : null });
        }))));
        body.Controls.Add(Button("Import encrypted pairing profile…", () => _ = Guard(async () =>
        {
            using var dialog = new OpenFileDialog { Filter = "Encrypted Windows profile|*.dpapi" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            await UpdatePairingAsync(_ => Task.FromResult(DeviceProfile.Load(dialog.FileName)));
        })));
        body.Controls.Add(Label($"Direct import supports YI IoT PC {VendorClientImporter.SupportedVersion}. It reads the existing pairing without changing the vendor app. After a successful import you can close YI IoT. QR generation is available in the experimental Setup QR tab."));
        body.Controls.Add(Label("Compatibility: initially tested on the Anyka-family YI IoT camera reporting hardware 253. AAC microphone audio is optional. Native 4K capture is not verified."));
    }
    async Task UpdatePairingAsync(Func<CancellationToken, Task<DeviceProfile>> read)
    {
        if (importing) return;
        importing = true; pairingControls.Enabled = connect.Enabled = record.Enabled = false;
        bool resumeRecording = session?.Recording == true;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try
        {
            try
            {
                await DisconnectAsync();
                status.Text = "Reading the pairing; the saved profile is kept until verification succeeds…";
                var candidate = await read(timeout.Token);
                status.Text = "Checking the imported key against the LAN camera…";
                var verified = await PairingImport.VerifyAsync(candidate, timeout.Token);
                EnsurePrimaryDistinct(verified);
                verified.Save(DeviceProfile.DefaultPath); profile = verified;
            }
            catch (Exception e)
            {
                throw new InvalidOperationException((e is OperationCanceledException ? "Pairing import timed out." : e.Message) +
                    " The saved camera profile was not replaced.", e);
            }
            ShowProfile(); tabs.SelectedIndex = 0;
            status.Text = "Pairing verified and saved. Connecting locally…";
            await ToggleConnection();
            if (resumeRecording) session?.StartRecording(preferences.Folder, preferences.Storage, preferences.Recording, preferences.Ffmpeg);
        }
        finally { importing = false; pairingControls.Enabled = connect.Enabled = true; }
    }
    async void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (closed) return;
        e.Cancel = true;
        if (closing) return;
        if (importing) { status.Text = "Finishing pairing verification. Please wait before closing."; return; }
        if (gridBusy) { status.Text = "Finishing a camera operation. Please wait before closing."; return; }
        if (exporting) { status.Text = "An export is running. Wait for it to finish before closing."; return; }
        if (savingSnapshot) { status.Text = "Finishing the snapshot. Please wait before closing."; return; }
        closing = true; Enabled = false; status.Text = "Saving recording and disconnecting…";
        try { await Task.WhenAll(StopExtraCameras(), session?.DisposeAsync().AsTask() ?? Task.CompletedTask); }
        catch (Exception error) { MessageBox.Show(this, "The recording could not be finalized: " + error.Message, "OpenYI", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        finally
        {
            timer.Stop(); timer.Dispose(); StopPlayback(); thumbnailStop?.Cancel(); thumbnailStop?.Dispose(); thumbnails.Dispose(); ClearPreview(); ClearAudio(); SetThreadExecutionState(0x80000000); exportStop.Dispose(); closed = true; Close();
        }
    }
}
