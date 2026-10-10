using YiLocal.Core;

namespace YiLocal.Windows;

public sealed partial class MainForm
{
    readonly CheckBox alarmEnabled = new() { Text = L.Get("EnableOptionalMotionAlarm"), AutoSize = true };
    readonly NumericUpDown alarmThreshold = Number(.1m, 100, 10, 1);
    readonly NumericUpDown alarmDuration = Number(1, 3600, 30, 0);
    readonly NumericUpDown alarmEntry = Number(0, 300, 15, 0);
    readonly NumericUpDown alarmExitMinutes = Number(0, 60, 1, 1);
    readonly ComboBox alarmDurationPreset = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
    readonly ComboBox alarmOutput = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    readonly ComboBox alarmDevice = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 370, DropDownWidth = 480 };
    readonly Label alarmSoundLabel = Label(L.Get("SirenNoiseKevanGCPublicDomain"));
    readonly Label alarmMotionLevel = Label(L.Get("LiveChangedAreaWaitingForMotionRecordingToBeArmed"));
    readonly Label alarmBadge = new() { Text = L.Get("HOMEAlarmDisarmed"), AutoSize = false, Width = 225, Height = 32, TextAlign = ContentAlignment.MiddleCenter };
    readonly Button alarmMode = new() { Text = L.Get("AwayArmAlarm"), AutoSize = true, Margin = new Padding(3) };
    readonly Button silenceAlarm = new() { Text = L.Get("StopAlarm"), AutoSize = true, Enabled = false, Margin = new Padding(3) };
    readonly FlowLayoutPanel alarmSettings = Column();
    readonly NotifyIcon alarmTray = new();
    readonly Icon homeIcon = AlarmIcon(Color.SeaGreen), awayIcon = AlarmIcon(Color.Firebrick), delayIcon = AlarmIcon(Color.DarkOrange);
    MotionAlarmGate? alarmGate;
    AlarmClip? alarmClip;
    byte[]? alarmPcm;
    CameraAlarm? cameraAlarm;
    ComputerAlarm? computerAlarm;
    Task alarmStopping = Task.CompletedTask;
    CancellationTokenSource? alarmPreparation;
    string? selectedAlarmFile, selectedAlarmName;
    bool alarmDirty, showingAlarm, alarmTest;
    long lastAlarmMeasurement;
    bool AlarmArmed => alarmGate?.Armed == true;
    bool AlarmPlaying => cameraAlarm is not null || computerAlarm is not null || !alarmStopping.IsCompleted;
    static Label AlarmNote(string text) => new() { Text = text, AutoSize = true, MaximumSize = new Size(740, 0), Margin = new Padding(3, 5, 3, 4) };

    static Icon AlarmIcon(Color color)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(color); graphics.FillEllipse(brush, 1, 1, 30, 30);
            using var font = new Font("Segoe UI", 20, FontStyle.Bold, GraphicsUnit.Pixel);
            graphics.DrawString("!", font, Brushes.White, 11, 2);
        }
        IntPtr handle = bitmap.GetHicon();
        try { using var icon = Icon.FromHandle(handle); return (Icon)icon.Clone(); }
        finally { DestroyIcon(handle); }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);

    Control BuildAlarmStatus()
    {
        var bar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = Padding.Empty };
        bar.ColumnStyles.Add(new(SizeType.Percent, 100));
        for (int i = 0; i < 3; i++) bar.ColumnStyles.Add(new(SizeType.AutoSize));
        bar.Controls.Add(status, 0, 0); bar.Controls.Add(alarmBadge, 1, 0); bar.Controls.Add(alarmMode, 2, 0); bar.Controls.Add(silenceAlarm, 3, 0);
        alarmMode.Click += (_, _) => _ = Guard(async () =>
        {
            if (AlarmArmed || alarmPreparation is not null || AlarmPlaying) await HomeAlarmAsync(); else await ArmAlarmAsync();
        });
        silenceAlarm.Click += (_, _) => _ = StopAlarmIncidentAsync();
        var menu = new ContextMenuStrip();
        menu.Items.Add(L.Get("OpenOpenYI"), null, (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); });
        menu.Items.Add(L.Get("HomeDisarmAlarm"), null, (_, _) => _ = HomeAlarmAsync());
        menu.Items.Add(L.Get("StopAlarm"), null, (_, _) => _ = StopAlarmIncidentAsync());
        alarmTray.ContextMenuStrip = menu; alarmTray.Icon = homeIcon; alarmTray.Text = L.Get("OpenYIHOMEAlarmDisarmed"); alarmTray.Visible = true;
        alarmTray.DoubleClick += (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); };
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape && (AlarmPlaying || alarmGate?.Phase == AlarmPhase.EntryDelay)) { e.Handled = true; _ = HomeAlarmAsync(); } };
        Disposed += (_, _) => { alarmTray.Visible = false; alarmTray.Dispose(); menu.Dispose(); homeIcon.Dispose(); awayIcon.Dispose(); delayIcon.Dispose(); };
        return bar;
    }
    void BuildAlarm()
    {
        var body = alarmSettings; Page(L.Get("MotionAlarm")).Controls.Add(new ScrollableColumn(body));
        body.Controls.Add(alarmEnabled);
        body.Controls.Add(AlarmNote(L.Get("PrimaryCameraArmMotionRecordingInLiveCameraThenChooseAway")));
        body.Controls.Add(Row(Label(L.Get("AlarmChangedImageArea"), 260), alarmThreshold));
        alarmThreshold.Increment = .1m; body.Controls.Add(alarmMotionLevel);
        alarmDurationPreset.Items.AddRange([L.Get("Duration.ThirtySeconds"), L.Get("Duration.OneMinute"), L.Get("Duration.FiveMinutes"), L.Get("Custom"), L.Get("UntilStopped")]);
        body.Controls.Add(Row(Label(L.Get("RepeatSoundFor"), 170), alarmDurationPreset, alarmDuration, Label(L.Get("SecondsCustom"), 150)));
        body.Controls.Add(Row(Label(L.Get("EntryGraceSeconds"), 230), alarmEntry));
        body.Controls.Add(Row(Label(L.Get("ArmAfterMinutes"), 230), alarmExitMinutes));
        alarmExitMinutes.Increment = .5m;
        body.Controls.Add(AlarmNote(L.Get("HomeCancelsTheEntryCountdownStopSilencesThisIncidentAnotherTrigger")));
        alarmOutput.Items.AddRange([L.Get("CameraSpeakerDefault"), L.Get("ComputerOutput"), L.Get("CameraComputer")]);
        body.Controls.Add(Row(Label(L.Get("PlayAlarmThrough"), 180), alarmOutput));
        body.Controls.Add(Row(Label(L.Get("ComputerSoundDevice"), 200), alarmDevice));
        body.Controls.Add(alarmSoundLabel);
        body.Controls.Add(Row(Button(L.Get("ChooseSound"), () => _ = Guard(ImportAlarmSoundAsync)), Button(L.Get("UseDefaultSiren"), () =>
        { selectedAlarmFile = selectedAlarmName = null; alarmSoundLabel.Text = L.Get("SirenNoiseKevanGCPublicDomain"); MarkAlarmDirty(); })));
        body.Controls.Add(AlarmNote(L.Get("WAVMP3AACFLACOGGAndOtherFFmpegAudioFormatsImported")));
        body.Controls.Add(AlarmNote(L.Get("MaximumApplicationPlaybackLevelWindowsMasterMuteAndAmplifierVolumeStill")));
        body.Controls.Add(Row(Button(L.Get("SaveAlarmSettings"), () => _ = Guard(() => { SaveAlarmOptions(); return Task.CompletedTask; })),
            Button(L.Get("TestSound3SLoud"), () => _ = Guard(TestAlarmAsync))));
        body.Controls.Add(AlarmNote(L.Get("EveryLaunchStartsInHomeSettingsSaveAutomaticallyHomeAndStop")));
        alarmDurationPreset.SelectedIndexChanged += (_, _) =>
        {
            if (alarmDurationPreset.SelectedIndex is >= 0 and <= 2) alarmDuration.Value = new[] { 30, 60, 300 }[alarmDurationPreset.SelectedIndex];
            UpdateAlarmControls(); MarkAlarmDirty();
        };
        alarmEnabled.CheckedChanged += (_, _) => { UpdateAlarmControls(); MarkAlarmDirty(); };
        alarmOutput.SelectedIndexChanged += (_, _) => { UpdateAlarmControls(); MarkAlarmDirty(); };
        alarmDevice.SelectionChangeCommitted += (_, _) => MarkAlarmDirty();
        alarmDevice.DropDown += (_, _) => ShowAlarmDevices();
        foreach (var number in new[] { alarmThreshold, alarmDuration, alarmEntry, alarmExitMinutes }) number.ValueChanged += (_, _) => MarkAlarmDirty();
    }
    void ShowAlarmOptions()
    {
        showingAlarm = true;
        try
        {
            var saved = preferences.Alarm;
            alarmEnabled.Checked = saved.Enabled; alarmThreshold.Value = (decimal)saved.ThresholdPercent;
            alarmDuration.Value = (decimal)(saved.DurationSeconds ?? 30);
            alarmDurationPreset.SelectedIndex = saved.DurationSeconds switch { 30 => 0, 60 => 1, 300 => 2, null => 4, _ => 3 };
            alarmEntry.Value = (decimal)saved.EntryGraceSeconds; alarmExitMinutes.Value = (decimal)saved.ExitDelaySeconds / 60;
            alarmOutput.SelectedIndex = (int)saved.Output;
            selectedAlarmFile = saved.SoundFile; selectedAlarmName = saved.SoundName;
            alarmSoundLabel.Text = saved.SoundFile is null ? L.Get("SirenNoiseKevanGCPublicDomain") : L.Get("ImportedSound") + (saved.SoundName ?? Path.GetFileName(saved.SoundFile));
            ShowAlarmDevices(saved.ComputerDevice, fromSettings: true); UpdateAlarmControls();
        }
        finally { showingAlarm = false; alarmDirty = false; }
    }
    void ShowAlarmDevices(string? saved = null, bool fromSettings = false)
    {
        if (!fromSettings) saved = alarmDevice.SelectedItem is PlaybackDevice { Id: not uint.MaxValue } selected ? selected.Name : null;
        alarmDevice.Items.Clear(); alarmDevice.Items.AddRange(PcmOutput.Devices().Cast<object>().ToArray()); alarmDevice.SelectedIndex = 0;
        if (saved is not null)
        {
            var device = alarmDevice.Items.Cast<PlaybackDevice>().FirstOrDefault(d => d.Name == saved) ?? new(uint.MaxValue - 1, saved);
            if (!alarmDevice.Items.Contains(device)) alarmDevice.Items.Add(device);
            alarmDevice.SelectedItem = device;
        }
    }
    void UpdateAlarmControls()
    {
        alarmDuration.Enabled = alarmDurationPreset.SelectedIndex == 3;
        alarmDevice.Enabled = alarmOutput.SelectedIndex > 0;
    }
    void MarkAlarmDirty()
    {
        if (showingAlarm) return;
        alarmDirty = true; preferencesTimer.Start();
    }
    AlarmOptions ReadAlarmOptions()
    {
        var selected = new AlarmOptions(alarmEnabled.Checked, (double)alarmThreshold.Value,
            alarmDurationPreset.SelectedIndex == 4 ? null : (double)alarmDuration.Value,
            (double)alarmEntry.Value, (double)alarmExitMinutes.Value * 60, selectedAlarmFile, selectedAlarmName,
            (AlarmOutput)alarmOutput.SelectedIndex, alarmDevice.SelectedItem is PlaybackDevice { Id: not uint.MaxValue } d ? d.Name : null);
        selected.Validate(); return selected;
    }
    void SaveAlarmOptions()
    {
        if (AlarmArmed || AlarmPlaying || alarmPreparation is not null) throw new InvalidOperationException(L.Get("ChooseHomeAndStopTheAlarmBeforeChangingItsSettings"));
        var next = preferences.Copy(); next.Alarm = ReadAlarmOptions(); next.Save(); preferences = next; alarmDirty = false;
        status.Text = L.Get("AlarmSettingsSavedChooseAwayToArmStartupAlwaysRemainsHome");
    }
    async Task ImportAlarmSoundAsync()
    {
        using var dialog = new OpenFileDialog { Filter = L.Get("AudioFilesWavMp3AacM4aFlacOggOpusWmaAll") };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await PrepareAlarmAsync(async token =>
        {
            var converted = await AlarmAudio.ImportAsync(ffmpeg.Text.Trim(), dialog.FileName, AlarmAudio.CacheDirectory, token);
            token.ThrowIfCancellationRequested(); selectedAlarmFile = converted.Path; selectedAlarmName = Path.GetFileName(dialog.FileName);
            alarmSoundLabel.Text = L.Format("ImportedSound0100SLoop", selectedAlarmName, converted.Clip.Seconds);
            MarkAlarmDirty();
        });
        if (alarmDirty) SaveAlarmOptions();
    }
    async Task PrepareAlarmAsync(Func<CancellationToken, Task> action)
    {
        if (alarmPreparation is not null) return;
        using var preparing = new CancellationTokenSource(); alarmPreparation = preparing;
        UpdateAlarmState();
        try { await action(preparing.Token); }
        catch (OperationCanceledException) when (preparing.IsCancellationRequested) { }
        finally { alarmPreparation = null; if (!IsDisposed && !closing) UpdateAlarmState(); }
    }
    bool MotionReady => session?.Recording == true && session.Client is { Connected: true } &&
        preferences.Recording.Mode == CaptureMode.Motion && lastAlarmMeasurement != 0 && Environment.TickCount64 - lastAlarmMeasurement <= 5000;
    async Task LoadAlarmSoundAsync(CancellationToken token)
    {
        var selected = preferences.Alarm;
        if (selected.Output != AlarmOutput.Camera) PcmOutput.ResolveDevice(selected.ComputerDevice);
        alarmClip = selected.SoundFile is { } file ? AlarmAudio.Load(file) : await AlarmAudio.DefaultAsync(ffmpeg.Text.Trim(), token);
        alarmPcm = selected.Output == AlarmOutput.Camera ? null : await AlarmAudio.DecodeAsync(ffmpeg.Text.Trim(), alarmClip, token);
    }
    async Task ArmAlarmAsync()
    {
        SaveAlarmOptions();
        if (!preferences.Alarm.Enabled) throw new IOException(L.Get("EnableTheOptionalAlarmInMotionAlarmFirst"));
        if (!MotionReady) throw new IOException(L.Get("ArmLocalMotionRecordingForThePrimaryCameraAndWaitFor"));
        await StopTalkAsync(); await alarmStopping;
        await PrepareAlarmAsync(async token =>
        {
            await LoadAlarmSoundAsync(token); token.ThrowIfCancellationRequested();
            if (!MotionReady || closing) throw new IOException(L.Get("MotionAnalysisStoppedWhilePreparingTheAlarm"));
            alarmGate = new(preferences.Alarm); alarmGate.Arm(Environment.TickCount64);
        });
    }
    async Task TestAlarmAsync()
    {
        SaveAlarmOptions(); await StopTalkAsync(); await alarmStopping;
        if (preferences.Alarm.Output != AlarmOutput.Computer && session?.Client is not { Connected: true }) throw new IOException(L.Get("ConnectTheCameraToTestItsSpeaker"));
        await PrepareAlarmAsync(async token =>
        {
            await LoadAlarmSoundAsync(token); token.ThrowIfCancellationRequested();
            alarmTest = true; StartAlarmPlayback(3);
        });
    }
    void StartAlarmPlayback(double? duration)
    {
        if (AlarmPlaying || alarmClip is null) return;
        ClearAudio(); listen.Enabled = false;
        if (preferences.Alarm.Output != AlarmOutput.Computer)
            cameraAlarm = new(profile ?? throw new IOException(L.Get("NoSavedCamera")), alarmClip, duration);
        if (preferences.Alarm.Output != AlarmOutput.Camera)
            computerAlarm = new(alarmPcm ?? throw new IOException(L.Get("ComputerAlarmWasNotPrepared")), preferences.Alarm.ComputerDevice, duration);
    }
    Task StopAlarmPlaybackAsync()
    {
        if (cameraAlarm is null && computerAlarm is null) return alarmStopping;
        var camera = cameraAlarm; var computer = computerAlarm; cameraAlarm = null; computerAlarm = null;
        return alarmStopping = FinishAlarmPlaybackAsync(camera, computer);
    }
    async Task FinishAlarmPlaybackAsync(CameraAlarm? camera, ComputerAlarm? computer)
    {
        await Task.WhenAll(camera?.DisposeAsync().AsTask() ?? Task.CompletedTask, computer?.DisposeAsync().AsTask() ?? Task.CompletedTask);
        if (!IsDisposed) listen.Enabled = talkback is null && talkStopping.IsCompleted;
    }
    Task HomeAlarmAsync()
    {
        alarmPreparation?.Cancel(); alarmGate?.Home(); alarmTest = false; alarmClip = null; alarmPcm = null;
        var stopped = StopAlarmPlaybackAsync(); RenderAlarmState(); return stopped;
    }
    Task StopAlarmIncidentAsync()
    {
        if (alarmPreparation is not null || alarmGate?.Phase == AlarmPhase.ExitDelay) return HomeAlarmAsync();
        alarmGate?.Stop(); alarmTest = false;
        var stopped = StopAlarmPlaybackAsync(); RenderAlarmState(); return stopped;
    }
    void OnAlarmMeasurement(CameraSession active, MotionMeasurement? sample)
    {
        long received = Environment.TickCount64;
        Ui(() =>
        {
            if (session != active) return;
            if (sample is null || Environment.TickCount64 - received > 1000)
            {
                lastAlarmMeasurement = 0;
                if (AlarmArmed) { _ = HomeAlarmAsync(); status.Text = L.Get("AlarmReturnedToHomeMotionAnalysisRestartedOrFellBehind"); }
                return;
            }
            lastAlarmMeasurement = received;
            alarmMotionLevel.Text = L.Format("LiveChangedArea000Recording100Alarm", sample.ChangedPercent, preferences.Recording.MotionThresholdPercent, preferences.Alarm.ThresholdPercent);
            alarmGate?.Observe(sample.ChangedPercent, received);
        });
    }
    void UpdateAlarmState()
    {
        long now = Environment.TickCount64;
        if (AlarmArmed && !MotionReady)
        { _ = HomeAlarmAsync(); status.Text = L.Get("AlarmReturnedToHomeRecordingStoppedOrFreshMotionAnalysisIs"); }
        if ((cameraAlarm?.Error ?? computerAlarm?.Error) is { } error)
        { _ = HomeAlarmAsync(); status.Text = L.Get("AlarmReturnedToHome") + error; }
        else if ((cameraAlarm is not null || computerAlarm is not null) &&
            (cameraAlarm?.Completion.IsCompleted ?? true) && (computerAlarm?.Completion.IsCompleted ?? true)) _ = StopAlarmIncidentAsync();
        if (cameraAlarm?.PacketsSent > 0 || computerAlarm?.Started == true) alarmGate?.PlaybackStarted(now);
        alarmGate?.Tick(now);
        if (alarmGate?.Phase == AlarmPhase.Sounding && !AlarmPlaying && alarmPreparation is null)
            StartAlarmPlayback(preferences.Alarm.DurationSeconds);
        else if (alarmGate?.Phase != AlarmPhase.Sounding && !alarmTest && AlarmPlaying) _ = StopAlarmPlaybackAsync();
        RenderAlarmState();
    }
    void RenderAlarmState()
    {
        long now = Environment.TickCount64;
        var phase = alarmGate?.Phase ?? AlarmPhase.Home;
        string text = alarmPreparation is not null ? L.Get("HOMEPreparingSound") : alarmTest ? L.Get("TESTLOUDAlarm") : phase switch
        {
            AlarmPhase.ExitDelay => L.Format("EXITArmingIn00S", alarmGate!.RemainingSeconds(now)),
            AlarmPhase.Watching => L.Get("AWAYALARMARMED"),
            AlarmPhase.EntryDelay => L.Format("ENTRYAlarmIn00S", alarmGate!.RemainingSeconds(now)),
            AlarmPhase.Sounding => alarmGate!.RemainingSeconds(now) is { } remaining ? L.Format("ALARM00SRemaining", remaining) : L.Get("ALARMUntilStopped"),
            AlarmPhase.WaitingForQuiet => L.Get("AWAYWaitingForQuiet"),
            _ => L.Get("HOMEAlarmDisarmed")
        };
        Color color = phase is AlarmPhase.ExitDelay or AlarmPhase.EntryDelay ? Color.DarkOrange : AlarmArmed || alarmTest ? Color.Firebrick : Color.DarkGreen;
        alarmBadge.Text = text; alarmBadge.ForeColor = Color.White; alarmBadge.BackColor = color;
        alarmMode.Text = AlarmArmed || AlarmPlaying || alarmPreparation is not null ? L.Get("HomeDisarmAlarm") : L.Get("AwayArmAlarm");
        silenceAlarm.Enabled = AlarmPlaying || phase == AlarmPhase.EntryDelay || phase == AlarmPhase.ExitDelay || alarmPreparation is not null;
        alarmSettings.Enabled = !closing && !AlarmArmed && !AlarmPlaying && alarmPreparation is null;
        string trayText = "OpenYI · " + text;
        alarmTray.Text = trayText.Length <= 63 ? trayText : trayText[..62] + "…";
        var icon = color == Color.DarkOrange ? delayIcon : color == Color.Firebrick ? awayIcon : homeIcon;
        if (!ReferenceEquals(alarmTray.Icon, icon)) alarmTray.Icon = icon;
    }
}
