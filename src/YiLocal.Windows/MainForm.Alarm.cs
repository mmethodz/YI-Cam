using YiLocal.Core;

namespace YiLocal.Windows;

public sealed partial class MainForm
{
    readonly CheckBox alarmEnabled = new() { Text = "Enable optional motion alarm", AutoSize = true };
    readonly NumericUpDown alarmThreshold = Number(.1m, 100, 10, 1);
    readonly NumericUpDown alarmDuration = Number(1, 3600, 30, 0);
    readonly NumericUpDown alarmEntry = Number(0, 300, 15, 0);
    readonly NumericUpDown alarmExitMinutes = Number(0, 60, 1, 1);
    readonly ComboBox alarmDurationPreset = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
    readonly ComboBox alarmOutput = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    readonly ComboBox alarmDevice = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 370, DropDownWidth = 480 };
    readonly Label alarmSoundLabel = Label("Siren Noise — KevanGC (public domain)");
    readonly Label alarmMotionLevel = Label("Live changed area: waiting for motion recording to be armed.");
    readonly Label alarmBadge = new() { Text = "HOME · alarm disarmed", AutoSize = false, Width = 225, Height = 32, TextAlign = ContentAlignment.MiddleCenter };
    readonly Button alarmMode = new() { Text = "Away / arm alarm", AutoSize = true, Margin = new Padding(3) };
    readonly Button silenceAlarm = new() { Text = "Stop alarm", AutoSize = true, Enabled = false, Margin = new Padding(3) };
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
        menu.Items.Add("Open OpenYI", null, (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); });
        menu.Items.Add("Home / disarm alarm", null, (_, _) => _ = HomeAlarmAsync());
        menu.Items.Add("Stop alarm", null, (_, _) => _ = StopAlarmIncidentAsync());
        alarmTray.ContextMenuStrip = menu; alarmTray.Icon = homeIcon; alarmTray.Text = "OpenYI · HOME · alarm disarmed"; alarmTray.Visible = true;
        alarmTray.DoubleClick += (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); };
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape && (AlarmPlaying || alarmGate?.Phase == AlarmPhase.EntryDelay)) { e.Handled = true; _ = HomeAlarmAsync(); } };
        Disposed += (_, _) => { alarmTray.Visible = false; alarmTray.Dispose(); menu.Dispose(); homeIcon.Dispose(); awayIcon.Dispose(); delayIcon.Dispose(); };
        return bar;
    }
    void BuildAlarm()
    {
        var body = alarmSettings; Page("Motion alarm").Controls.Add(new ScrollableColumn(body));
        body.Controls.Add(alarmEnabled);
        body.Controls.Add(AlarmNote("Primary camera: arm motion recording in Live camera, then choose Away below. Home silences and disarms the alarm while recording continues."));
        body.Controls.Add(Row(Label("Alarm changed image area (%)", 260), alarmThreshold));
        alarmThreshold.Increment = .1m; body.Controls.Add(alarmMotionLevel);
        alarmDurationPreset.Items.AddRange(["30 seconds", "1 minute", "5 minutes", "Custom", "Until stopped"]);
        body.Controls.Add(Row(Label("Repeat sound for", 170), alarmDurationPreset, alarmDuration, Label("seconds (custom)", 150)));
        body.Controls.Add(Row(Label("Entry grace (seconds)", 230), alarmEntry));
        body.Controls.Add(Row(Label("Arm after (minutes)", 230), alarmExitMinutes));
        alarmExitMinutes.Increment = .5m;
        body.Controls.Add(AlarmNote("Home cancels the entry countdown. Stop silences this incident; another trigger needs two quiet seconds. New motion extends recording, not the siren. Stream loss returns to Home."));
        alarmOutput.Items.AddRange(["Camera speaker (default)", "Computer output", "Camera + computer"]);
        body.Controls.Add(Row(Label("Play alarm through", 180), alarmOutput));
        body.Controls.Add(Row(Label("Computer sound device", 200), alarmDevice));
        body.Controls.Add(alarmSoundLabel);
        body.Controls.Add(Row(Button("Choose sound…", () => _ = Guard(ImportAlarmSoundAsync)), Button("Use default siren", () =>
        { selectedAlarmFile = selectedAlarmName = null; alarmSoundLabel.Text = "Siren Noise — KevanGC (public domain)"; MarkAlarmDirty(); })));
        body.Controls.Add(AlarmNote("WAV, MP3, AAC, FLAC, OGG and other FFmpeg audio formats. Imported sounds are copied locally; the first 60 seconds are looped."));
        body.Controls.Add(AlarmNote("Maximum application playback level; Windows master/mute and amplifier volume still apply. Camera hardware gain is unverified. Camera playback can mute its microphone in recordings."));
        body.Controls.Add(Row(Button("Save alarm settings", () => _ = Guard(() => { SaveAlarmOptions(); return Task.CompletedTask; })),
            Button("Test sound (3 s, loud)", () => _ = Guard(TestAlarmAsync))));
        body.Controls.Add(AlarmNote("Every launch starts in Home. Settings save automatically. Home and Stop stay available below and in the tray. Talking or Escape during an alarm returns to Home."));
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
            alarmSoundLabel.Text = saved.SoundFile is null ? "Siren Noise — KevanGC (public domain)" : "Imported sound: " + (saved.SoundName ?? Path.GetFileName(saved.SoundFile));
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
        if (AlarmArmed || AlarmPlaying || alarmPreparation is not null) throw new InvalidOperationException("Choose Home and stop the alarm before changing its settings.");
        var next = preferences.Copy(); next.Alarm = ReadAlarmOptions(); next.Save(); preferences = next; alarmDirty = false;
        status.Text = "Alarm settings saved. Choose Away to arm; startup always remains Home.";
    }
    async Task ImportAlarmSoundAsync()
    {
        using var dialog = new OpenFileDialog { Filter = "Audio files|*.wav;*.mp3;*.aac;*.m4a;*.flac;*.ogg;*.opus;*.wma|All files|*.*" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await PrepareAlarmAsync(async token =>
        {
            var converted = await AlarmAudio.ImportAsync(ffmpeg.Text.Trim(), dialog.FileName, AlarmAudio.CacheDirectory, token);
            token.ThrowIfCancellationRequested(); selectedAlarmFile = converted.Path; selectedAlarmName = Path.GetFileName(dialog.FileName);
            alarmSoundLabel.Text = $"Imported sound: {selectedAlarmName} · {converted.Clip.Seconds:0.0} s loop";
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
        if (!preferences.Alarm.Enabled) throw new IOException("Enable the optional alarm in Motion alarm first.");
        if (!MotionReady) throw new IOException("Arm local motion recording for the primary camera and wait for live motion measurements before choosing Away.");
        await StopTalkAsync(); await alarmStopping;
        await PrepareAlarmAsync(async token =>
        {
            await LoadAlarmSoundAsync(token); token.ThrowIfCancellationRequested();
            if (!MotionReady || closing) throw new IOException("Motion analysis stopped while preparing the alarm.");
            alarmGate = new(preferences.Alarm); alarmGate.Arm(Environment.TickCount64);
        });
    }
    async Task TestAlarmAsync()
    {
        SaveAlarmOptions(); await StopTalkAsync(); await alarmStopping;
        if (preferences.Alarm.Output != AlarmOutput.Computer && session?.Client is not { Connected: true }) throw new IOException("Connect the camera to test its speaker.");
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
            cameraAlarm = new(profile ?? throw new IOException("No saved camera."), alarmClip, duration);
        if (preferences.Alarm.Output != AlarmOutput.Camera)
            computerAlarm = new(alarmPcm ?? throw new IOException("Computer alarm was not prepared."), preferences.Alarm.ComputerDevice, duration);
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
                if (AlarmArmed) { _ = HomeAlarmAsync(); status.Text = "Alarm returned to Home: motion analysis restarted or fell behind."; }
                return;
            }
            lastAlarmMeasurement = received;
            alarmMotionLevel.Text = $"Live changed area: {sample.ChangedPercent:0.0}% · recording {preferences.Recording.MotionThresholdPercent:0.0}% / alarm {preferences.Alarm.ThresholdPercent:0.0}%";
            alarmGate?.Observe(sample.ChangedPercent, received);
        });
    }
    void UpdateAlarmState()
    {
        long now = Environment.TickCount64;
        if (AlarmArmed && !MotionReady)
        { _ = HomeAlarmAsync(); status.Text = "Alarm returned to Home: recording stopped or fresh motion analysis is unavailable."; }
        if ((cameraAlarm?.Error ?? computerAlarm?.Error) is { } error)
        { _ = HomeAlarmAsync(); status.Text = "Alarm returned to Home: " + error; }
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
        string text = alarmPreparation is not null ? "HOME · preparing sound…" : alarmTest ? "TEST · LOUD alarm" : phase switch
        {
            AlarmPhase.ExitDelay => $"EXIT · arming in {alarmGate!.RemainingSeconds(now):0} s",
            AlarmPhase.Watching => "AWAY · ALARM ARMED",
            AlarmPhase.EntryDelay => $"ENTRY · alarm in {alarmGate!.RemainingSeconds(now):0} s",
            AlarmPhase.Sounding => alarmGate!.RemainingSeconds(now) is { } remaining ? $"ALARM · {remaining:0} s remaining" : "ALARM · until stopped",
            AlarmPhase.WaitingForQuiet => "AWAY · waiting for quiet",
            _ => "HOME · alarm disarmed"
        };
        Color color = phase is AlarmPhase.ExitDelay or AlarmPhase.EntryDelay ? Color.DarkOrange : AlarmArmed || alarmTest ? Color.Firebrick : Color.DarkGreen;
        alarmBadge.Text = text; alarmBadge.ForeColor = Color.White; alarmBadge.BackColor = color;
        alarmMode.Text = AlarmArmed || AlarmPlaying || alarmPreparation is not null ? "Home / disarm alarm" : "Away / arm alarm";
        silenceAlarm.Enabled = AlarmPlaying || phase == AlarmPhase.EntryDelay || phase == AlarmPhase.ExitDelay || alarmPreparation is not null;
        alarmSettings.Enabled = !closing && !AlarmArmed && !AlarmPlaying && alarmPreparation is null;
        alarmTray.Text = "OpenYI · " + text;
        var icon = color == Color.DarkOrange ? delayIcon : color == Color.Firebrick ? awayIcon : homeIcon;
        if (!ReferenceEquals(alarmTray.Icon, icon)) alarmTray.Icon = icon;
    }
}
