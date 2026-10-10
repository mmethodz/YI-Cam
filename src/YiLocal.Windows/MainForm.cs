using System.Diagnostics;
using System.Runtime.InteropServices;
using YiLocal.Core;

namespace YiLocal.Windows;

public sealed partial class MainForm : Form
{
    Preferences preferences = new();
    DeviceProfile? profile;
    CameraSession? session;
    GimbalControls gimbal = null!;
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
    readonly Label status = new() { Text = L.Get("ReadyConnectASavedCameraToBegin"), Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly Label metrics = new() { Text = L.Get("OriginalStreamLocalConnection"), AutoSize = true };
    readonly Label recordState = new() { Text = L.Get("RecordingIsOff"), AutoSize = true, ForeColor = Color.DarkSlateGray };
    readonly Button connect = new() { Text = L.Get("ConnectCamera"), AutoSize = true };
    readonly Button record = new() { Text = L.Get("StartRecording"), AutoSize = true, Enabled = false };
    readonly ComboBox quality = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 174 };
    readonly ComboBox night = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 174, Enabled = false };
    readonly CheckBox tracking = new() { Text = L.Get("MotionTracking"), AutoSize = true, Enabled = false };
    readonly FlowLayoutPanel cameraControls = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty, Enabled = false };
    readonly RecordingListView clips = new() { View = View.Details, FullRowSelect = true, MultiSelect = false, Dock = DockStyle.Fill, HideSelection = false };
    readonly TextBox folder = new() { Width = 610 };
    readonly TextBox ffmpeg = new() { Width = 610 };
    readonly NumericUpDown quota = Number(0.1m, 100000, 20);
    readonly NumericUpDown free = Number(0.1m, 100000, 2);
    readonly NumericUpDown segment = Number(0.1m, 120, 10);
    readonly NumericUpDown days = Number(0, 36500, 0, 0);
    readonly CheckBox recycle = new() { Text = L.Get("RecycleOldestUnprotectedRecordingsWhenLimitsAreReached"), AutoSize = true };
    readonly TextBox ip = new() { Width = 280 };
    readonly TextBox cameraName = new() { Width = 280 };
    readonly TextBox key = new() { Width = 280, UseSystemPasswordChar = true };
    readonly CheckBox localPlain = new() { AutoSize = true, Text = L.Get("LocalPlainMode") };
    readonly Label pairing = new() { AutoSize = true, MaximumSize = new Size(760, 0) };
    readonly FlowLayoutPanel pairingControls = Column();
    [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);

    public MainForm()
    {
        Text = L.Get("OpenYICameraRecordingsWindows");
        Font = new Font("Segoe UI", 10); ClientSize = new Size(1180, 740); MinimumSize = new Size(1000, 650);
        StartPosition = FormStartPosition.CenterScreen;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Padding = new Padding(10) };
        layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.Absolute, 40));
        layout.Controls.Add(tabs, 0, 0); layout.Controls.Add(BuildAlarmStatus(), 0, 1); Controls.Add(layout);
        BuildLive(); BuildRecordings(); BuildStorage(); BuildCamera(); BuildProvisioning(); BuildCapture(); BuildAlarm(); BuildCameras();
        preferences = Preferences.Load(out string? settingsNotice);
        BuildLanguage();
        settingsMessage.Text = settingsNotice ?? L.Get("SettingsSaveAutomaticallyChangesApplyToTheNextRecordingSession");
        if (settingsNotice is not null) status.Text = settingsNotice;
        try
        {
            if (File.Exists(DeviceProfile.DefaultPath)) profile = DeviceProfile.Load();
            else
                for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
                    if (File.Exists(Path.Combine(dir.FullName, "YiLocal.sln")) && File.Exists(Path.Combine(dir.FullName, ".local", "device.dpapi")))
                    { profile = DeviceProfile.Load(Path.Combine(dir.FullName, ".local", "device.dpapi")); profile.Save(DeviceProfile.DefaultPath); break; }
        }
        catch (Exception e) { status.Text = L.Get("SavedCameraPairingCouldNotBeLoaded") + e.Message; }
        folder.Text = preferences.Folder; ffmpeg.Text = preferences.Ffmpeg ?? MediaTools.FindFfmpeg() ?? "";
        quota.Value = (decimal)preferences.Storage.QuotaGiB; free.Value = (decimal)preferences.Storage.MinimumFreeGiB;
        segment.Value = (decimal)preferences.Storage.SegmentMinutes; days.Value = preferences.Storage.KeepDays; recycle.Checked = preferences.Storage.Recycle;
        ShowProfile();
        ShowCaptureOptions();
        ShowAlarmOptions();
        LoadCameras();
        ShowMicrophones();
        TrackPreferenceEdits();
        if (profile is null) tabs.SelectedIndex = 4;
        timer.Tick += (_, _) =>
        {
            UpdatePlayback();
            UpdateTalkState();
            UpdateAlarmState();
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
    static FlowLayoutPanel Column() => new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
    static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty };
        row.Controls.AddRange(controls); return row;
    }
    TabPage Page(string title) { var page = new TabPage(title) { Padding = new Padding(8), UseVisualStyleBackColor = true }; tabs.TabPages.Add(page); return page; }
    static Button Button(string text, Action action) { var b = new Button { Text = text, AutoSize = true, Margin = new Padding(3, 8, 3, 3) }; b.Click += (_, _) => action(); return b; }
    void Ui(Action action) { if (!IsDisposed && IsHandleCreated) try { BeginInvoke(action); } catch (InvalidOperationException) { } }
    async Task Guard(Func<Task> action)
    {
        try { await action(); } catch (Exception e) { status.Text = e.Message; MessageBox.Show(this, e.Message, "OpenYI", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
    void BuildLive()
    {
        var page = Page(L.Get("LiveCamera"));
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        grid.ColumnStyles.Add(new(SizeType.Percent, 100)); grid.ColumnStyles.Add(new(SizeType.Absolute, 300));
        grid.RowStyles.Add(new(SizeType.Percent, 100)); grid.RowStyles.Add(new(SizeType.Absolute, 36));
        grid.Controls.Add(picture, 0, 0); grid.Controls.Add(metrics, 0, 1);
        var side = Column(); var scroll = new ScrollableColumn(side); grid.Controls.Add(scroll, 1, 0); grid.SetRowSpan(scroll, 2); page.Controls.Add(grid);
        side.Controls.Add(Row(connect, record)); side.Controls.Add(recordState); side.Controls.Add(cameraControls);
        var snapshot = Button(L.Get("Snapshot"), () => _ = Guard(SaveSnapshotAsync)); snapshot.Margin = new Padding(3);
        side.Controls.Add(Row(snapshot, listen)); BuildAudio(side);
        connect.Click += async (_, _) => await Guard(ToggleConnection);
        record.Click += async (_, _) => await Guard(ToggleRecordingAsync);
        quality.Items.AddRange([L.Get("HDOriginalStream"), L.Get("SDSmallerStream"), L.Get("AutomaticQuality")]); quality.SelectedIndex = 0;
        cameraControls.Controls.Add(Row(new Label { Text = L.Get("Quality"), Width = 66, Height = 26, TextAlign = ContentAlignment.MiddleLeft }, quality));
        quality.SelectionChangeCommitted += async (_, _) => await Guard(async () =>
        {
            if (session is null || profile is null) return;
            await session.SetQualityAsync(new byte[] { 1, 2, 0 }[quality.SelectedIndex]);
            profile.StreamQuality = session.Quality; profile.Save();
        });
        night.Items.AddRange([L.Get("InfraredInDarkness"), L.Get("ColourVisibleLights"), L.Get("AutomaticLighting")]);
        cameraControls.Controls.Add(Row(new Label { Text = L.Get("Night"), Width = 66, Height = 26, TextAlign = ContentAlignment.MiddleLeft }, night));
        night.SelectionChangeCommitted += async (_, _) => await Guard(async () =>
        {
            if (session?.Client is not { } client) return;
            await client.NightVisionAsync((uint)night.SelectedIndex); var read = await client.SettingsAsync(); night.SelectedIndex = read.NightVision;
            status.Text = L.Get("NightVisionModeConfirmedByTheCamera");
        });
        cameraControls.Controls.Add(tracking);
        tracking.Click += async (_, _) => await Guard(async () =>
        {
            if (session?.Client is not { } client) return;
            await client.TrackingAsync(tracking.Checked); tracking.Checked = (await client.SettingsAsync()).Tracking != 0;
            status.Text = L.Get("MotionTrackingSettingConfirmedByTheCamera");
        });
        var directions = new TableLayoutPanel { ColumnCount = 3, RowCount = 3, AutoSize = true, Margin = new Padding(3, 4, 3, 4) };
        void Move(string label, uint value, int x, int y)
        {
            var b = Button(label, () => _ = Guard(async () => { if (session is { } active) await active.MoveAsync(value); }));
            b.MinimumSize = new Size(68, 28); b.Margin = new Padding(2); directions.Controls.Add(b, x, y);
        }
        Move(L.Get("Up"), 1, 1, 0); Move(L.Get("Left"), 3, 0, 1); Move(L.Get("Right"), 4, 2, 1); Move(L.Get("Down"), 2, 1, 2);
        var stopMoving = Button(L.Get("Stop"), () => _ = Guard(async () => { if (session?.Client is { } c) await c.StopMovingAsync(); }));
        stopMoving.Margin = new Padding(2); directions.Controls.Add(stopMoving, 1, 1);
        cameraControls.Controls.Add(directions);
        gimbal = new(() => session, () => profile, Guard); cameraControls.Controls.Add(gimbal);
    }
    async Task ToggleRecordingAsync()
    {
        if (!record.Enabled || session is not { } active) return;
        record.Enabled = false;
        try
        {
            if (active.Recording) { await HomeAlarmAsync(); await Task.Run(active.StopRecording); status.Text = L.Get("RecordingSaved"); RefreshClips(); }
            else
            {
                if (!extraCameras.Any(camera => camera.Recording)) { SavePendingPreferences(); SaveCaptureOptions(); }
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
                await DisconnectAsync(); status.Text = L.Get("DisconnectedRecordingsAreSaved"); return;
            }
            if (profile is null) { tabs.SelectedIndex = 4; status.Text = L.Get("Setup.NoSavedCamera"); return; }
            EnsurePrimaryDistinct(profile);
            preferences.Ffmpeg = ffmpeg.Text.Trim();
            var active = new CameraSession(profile); session = active;
            session.Status += text => Ui(() => { if (session == active) { status.Text = text; UpdatePrimaryGridState(text); } });
            session.PairingRejected += () => Ui(() => _ = Guard(async () =>
            {
                if (session != active) return;
                await DisconnectAsync(); tabs.SelectedIndex = 3;
                pairing.Text = L.Get("TheCameraRejectedTheSavedDeviceKeyOpenThisCameraS");
                status.Text = L.Get("DeviceKeyRejectedRefreshItInCameraSetupAutomaticRetriesHave");
            }));
            session.Settings += settings =>
            {
                if (session != active) return;
                ClearPreview(); ClearAudio(); firstTime = 0; frameCount = 0;
                Ui(() =>
                {
                    if (session != active) return;
                    if (settings is null) { lastAlarmMeasurement = 0; _ = HomeAlarmAsync(); }
                    cameraControls.Enabled = settings is not null; night.Enabled = tracking.Enabled = settings is not null;
                    if (settings is not null) { night.SelectedIndex = settings.NightVision is <= 2 ? settings.NightVision : -1; tracking.Checked = settings.Tracking != 0; }
                    gimbal.Refresh(settings);
                });
            };
            session.RecordingChanged += recordingActive => Ui(() =>
            {
                if (session != active) return;
                UpdateRecordLabels();
                if (!recordingActive) { lastAlarmMeasurement = 0; _ = HomeAlarmAsync(); UpdatePrimaryGridState(L.Get("Recording.OffStatus")); }
                UpdateKeepAwake();
            });
            session.MotionChanged += state => Ui(() =>
            {
                if (session != active || !active.Recording) return;
                recordState.Text = state.Capturing ? L.Format("Motion00SRemaining", state.RemainingSeconds) : L.Format("Watching000Changed", state.ChangedPercent);
                recordState.ForeColor = state.Capturing ? Color.Firebrick : Color.DarkGreen;
            });
            session.MotionMeasured += sample => OnAlarmMeasurement(active, sample);
            session.Audio += OnAudio; session.Frame += OnFrame; session.Start(); connect.Text = L.Get("Disconnect"); record.Enabled = true;
        }
        finally { connect.Enabled = !importing; }
    }
    void UpdateRecordLabels()
    {
        bool armed = session?.Recording == true, motion = preferences.Recording.Mode == CaptureMode.Motion;
        record.Text = motion ? armed ? L.Get("DisarmMotion") : L.Get("ArmMotion") : armed ? L.Get("StopRecording") : L.Get("StartRecording");
        recordState.Text = armed ? motion ? L.Get("WatchingForMotion") : L.Get("RecordingToLocalDisk") : L.Get("RecordingIsOff");
        recordState.ForeColor = armed ? motion ? Color.DarkGreen : Color.Firebrick : Color.DarkSlateGray;
    }
    async Task DisconnectAsync()
    {
        await HomeAlarmAsync(); lastAlarmMeasurement = 0;
        await StopTalkAsync();
        var previous = session; session = null;
        try { if (previous is not null) await previous.DisposeAsync(); }
        finally
        {
            ClearPreview(); ClearAudio(); listen.Checked = false; cameraControls.Enabled = record.Enabled = false;
            connect.Text = L.Get("ConnectCamera"); UpdateRecordLabels();
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
                try { preview = new(preferences.Ffmpeg!); } catch (Exception e) { Ui(() => status.Text = L.Get("PreviewUnavailable") + e.Message); }
            if (preview is not null && !preview.Push(frame.Data)) { preview.Dispose(); preview = null; }
        }
        if (frameCount++ == 0) firstTime = time;
        if (Environment.TickCount64 - lastMetrics > 1000)
        {
            lastMetrics = Environment.TickCount64;
            double fps = time > firstTime ? (frameCount - 1) * 1000.0 / (time - firstTime) : 0;
            Ui(() => metrics.Text = L.Format("Live.StreamMetrics", frame.Width, frame.Height, fps) + (string.IsNullOrEmpty(preferences.Ffmpeg) ? L.Get("SelectFFmpegForPreview") : ""));
        }
    }
    void BuildStorage()
    {
        var body = Column(); Page(L.Get("Storage")).Controls.Add(new ScrollableColumn(body));
        body.Controls.Add(Label(L.Get("RecordingFolder"))); body.Controls.Add(folder);
        body.Controls.Add(Button(L.Get("ChooseFolder"), () => { using var dialog = new FolderBrowserDialog { InitialDirectory = folder.Text }; if (dialog.ShowDialog(this) == DialogResult.OK) folder.Text = dialog.SelectedPath; }));
        void Field(string text, Control control) { var row = new FlowLayoutPanel { AutoSize = true }; row.Controls.Add(new Label { Text = text, Width = 245, AutoSize = false, Height = 28, TextAlign = ContentAlignment.MiddleLeft }); row.Controls.Add(control); body.Controls.Add(row); }
        Field(L.Get("RecordingBudgetGiB"), quota); Field(L.Get("KeepDiskSpaceFreeGiB"), free); Field(L.Get("ClipLengthMinutes"), segment); Field(L.Get("MaximumAgeDays0Unlimited"), days);
        body.Controls.Add(recycle);
        body.Controls.Add(Label(L.Get("RecyclingIsOffByDefaultOnlyCompletedUnprotectedRecordingsRegisteredIn")));
        body.Controls.Add(Label(L.Get("FFmpegExecutableNeededForPreviewSnapshotsLocalMotionDetectionEncodingProfiles"))); body.Controls.Add(ffmpeg);
        body.Controls.Add(Button(L.Get("ChooseFFmpeg"), () => { using var dialog = new OpenFileDialog { Filter = L.Get("FFmpegExecutableFfmpegExeExecutableExe") }; if (dialog.ShowDialog(this) == DialogResult.OK) ffmpeg.Text = dialog.FileName; }));
        body.Controls.Add(Button(L.Get("SaveStorageSettings"), () => _ = Guard(() => { SaveStorageOptions(); return Task.CompletedTask; })));
        body.Controls.Add(settingsMessage);
        body.Controls.Add(Label(L.Get("SettingsLocation") + Preferences.FilePath));
    }
    void ShowProfile()
    {
        ip.Text = profile?.Ip ?? ""; cameraName.Text = profile?.Name ?? L.Get("Camera"); key.Clear();
        localPlain.Checked = profile?.Protocol == CameraProtocol.LocalPlain;
        key.Enabled = !localPlain.Checked;
        quality.SelectedIndex = profile?.StreamQuality switch { 0 => 2, 2 => 1, _ => 0 };
        pairing.Text = profile is null ? L.Get("Setup.NoSavedCamera")
            : L.Format(localPlain.Checked ? "SavedKeylessCamera" : "SavedCamera0ItsDeviceKeyIsEncryptedForThisWindows", profile.Name);
    }
    void BuildCamera()
    {
        var body = pairingControls; Page(L.Get("CameraSetup")).Controls.Add(new ScrollableColumn(body)); body.Controls.Add(pairing);
        body.Controls.Add(Button(L.Get("Setup.OpenOnboarding"), () => tabs.SelectedIndex = 4));
        body.Controls.Add(Label(L.Get("CameraName"))); body.Controls.Add(cameraName); body.Controls.Add(Label(L.Get("CameraIPv4AddressOnYourLAN"))); body.Controls.Add(ip);
        body.Controls.Add(localPlain);
        localPlain.CheckedChanged += (_, _) => key.Enabled = !localPlain.Checked;
        body.Controls.Add(Button(L.Get("ImportFromRunningYIIoT"), () => _ = Guard(() => UpdatePairingAsync(token =>
            VendorClientImporter.ReadProfileAsync(ip.Text.Trim(), cameraName.Text.Trim(), profile?.Uid, token)))));
        body.Controls.Add(Label(L.Get("OpenThisCameraSLiveViewInYIIoTFirstImport")));
        body.Controls.Add(Label(L.Get("DevicePairingKeyLeaveBlankToKeepTheSavedKey"))); body.Controls.Add(key);
        body.Controls.Add(Button(L.Get("VerifyAndSaveCamera"), () => _ = Guard(() => UpdatePairingAsync(_ =>
        {
            string password = localPlain.Checked ? "" : key.Text.Length > 0 ? key.Text : profile?.Password ?? "";
            return Task.FromResult(new DeviceProfile { Ip = ip.Text.Trim(), Name = cameraName.Text.Trim(), Password = password,
                Protocol = localPlain.Checked ? CameraProtocol.LocalPlain : CameraProtocol.Stock,
                Uid = key.Text.Length == 0 ? profile?.Uid : null });
        }))));
        body.Controls.Add(Button(L.Get("ImportEncryptedPairingProfile"), () => _ = Guard(async () =>
        {
            using var dialog = new OpenFileDialog { Filter = L.Get("EncryptedWindowsProfileDpapi") };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            await UpdatePairingAsync(_ => Task.FromResult(DeviceProfile.Load(dialog.FileName)));
        })));
        body.Controls.Add(Label(L.Format("DirectImportSupportsYIIoTPC0ItReadsTheExisting", VendorClientImporter.SupportedVersion)));
        body.Controls.Add(Label(L.Get("CompatibilityInitiallyTestedOnTheAnykaFamilyYIIoTCameraReporting")));
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
                status.Text = L.Get("ReadingThePairingTheSavedProfileIsKeptUntilVerificationSucceeds");
                var candidate = await read(timeout.Token);
                status.Text = L.Get("CheckingTheImportedKeyAgainstTheLANCamera");
                var verified = await PairingImport.VerifyAsync(candidate, timeout.Token);
                EnsurePrimaryDistinct(verified);
                verified.KeepControlsFrom(profile);
                verified.Save(DeviceProfile.DefaultPath); profile = verified;
            }
            catch (Exception e)
            {
                throw new InvalidOperationException((e is OperationCanceledException ? L.Get("PairingImportTimedOut") : e.Message) +
                    L.Get("TheSavedCameraProfileWasNotReplaced"), e);
            }
            ShowProfile(); tabs.SelectedIndex = 0;
            status.Text = L.Get("PairingVerifiedAndSavedConnectingLocally");
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
        if (importing) { status.Text = L.Get("FinishingPairingVerificationPleaseWaitBeforeClosing"); return; }
        if (localSetup?.Busy == true) { status.Text = L.Get("Setup.WaitOrCancel"); return; }
        if (gridBusy) { status.Text = L.Get("FinishingACameraOperationPleaseWaitBeforeClosing"); return; }
        if (exporting) { status.Text = L.Get("AnExportIsRunningWaitForItToFinishBeforeClosing"); return; }
        if (savingSnapshot) { status.Text = L.Get("FinishingTheSnapshotPleaseWaitBeforeClosing"); return; }
        closing = true; preferencesTimer.Stop(); Enabled = false; status.Text = L.Get("SavingRecordingAndDisconnecting");
        try { await HomeAlarmAsync(); await StopTalkAsync(); await Task.WhenAll(StopExtraCameras(), session?.DisposeAsync().AsTask() ?? Task.CompletedTask); SavePendingPreferences(); }
        catch (Exception error) { MessageBox.Show(this, L.Get("ShutdownNeedsAttention") + error.Message, "OpenYI", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        finally
        {
            timer.Stop(); timer.Dispose(); StopPlayback(); thumbnailStop?.Cancel(); thumbnailStop?.Dispose(); thumbnails.Dispose(); ClearPreview(); ClearAudio(); SetThreadExecutionState(0x80000000); exportStop.Dispose(); closed = true; Close();
        }
    }
}
