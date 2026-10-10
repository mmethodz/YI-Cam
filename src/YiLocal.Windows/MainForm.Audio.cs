using YiLocal.Core;

namespace YiLocal.Windows;

public sealed partial class MainForm
{
    readonly CheckBox listen = new() { Text = "Listen to camera", AutoSize = true };
    readonly object audioLock = new();
    AudioMonitor? audioMonitor;
    readonly Button talk = new() { Text = "Talk to camera", AutoSize = true, Enabled = false };
    readonly ComboBox microphone = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 244, DropDownWidth = 420 };
    readonly Label talkState = new() { Text = "Microphone off", AutoSize = true, MaximumSize = new Size(130, 0), Margin = new Padding(3, 7, 3, 3) };
    readonly Button testSpeaker = new() { Text = "Test speaker", AutoSize = true, Enabled = false };
    readonly ProgressBar inputLevel = new() { Width = 65, Height = 14, Maximum = 60, Margin = new Padding(3, 8, 3, 3), AccessibleName = "Input level, minus 60 to zero dB" };
    readonly Label inputLevelText = new() { Text = "Input off", Width = 76, Height = 23, Margin = new Padding(3, 7, 3, 3) };
    Talkback? talkback;
    Task talkStopping = Task.CompletedTask;
    void BuildAudio(Control parent)
    {
        parent.Controls.Add(new Label { Text = "PC microphone for talk-back", AutoSize = true, Margin = new Padding(3, 8, 3, 2) });
        parent.Controls.Add(microphone);
        parent.Controls.Add(Row(talk, talkState));
        parent.Controls.Add(Row(testSpeaker, inputLevel, inputLevelText));
        parent.Controls.Add(new Label { Text = "Click to start / stop talking. Listening pauses; leaving this view stops the mic.", AutoSize = true, MaximumSize = new Size(250, 0) });
        microphone.DropDown += (_, _) => ShowMicrophones();
        microphone.SelectionChangeCommitted += (_, _) => _ = Guard(() =>
        {
            var next = preferences.Copy(); var device = (MicrophoneDevice)microphone.SelectedItem!;
            next.Microphone = device.Id == uint.MaxValue ? null : device.Name;
            next.Save(); preferences = next; return Task.CompletedTask;
        });
        talk.Click += (_, _) => _ = Guard(ToggleTalkAsync);
        testSpeaker.Click += (_, _) => _ = Guard(() => StartTalkAsync(true));
        Deactivate += (_, _) => _ = StopTalkAsync();
        tabs.SelectedIndexChanged += (_, _) => { if (tabs.SelectedIndex != 0) _ = StopTalkAsync(); };
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape && talkback is not null) { e.Handled = true; _ = StopTalkAsync(); } };
        listen.Click += async (_, _) => await Guard(async () =>
        {
            if (session is null) { listen.Checked = false; throw new IOException("Connect the camera first."); }
            if (listen.Checked && !File.Exists(preferences.Ffmpeg)) { listen.Checked = false; throw new IOException("Choose FFmpeg in Storage to listen."); }
            if (!listen.Checked) ClearAudio();
            await session.SetMonitoringAsync(listen.Checked);
        });
    }
    void OnAudio(AudioFrame frame, long time)
    {
        lock (audioLock)
        {
            if (talkback is not null || !talkStopping.IsCompleted) return;
            try
            {
                if (audioMonitor?.Failed == true) { audioMonitor.Dispose(); audioMonitor = null; }
                if (audioMonitor is null && File.Exists(preferences.Ffmpeg)) audioMonitor = new(preferences.Ffmpeg!);
                if (audioMonitor is not null && !audioMonitor.Push(frame))
                    throw new IOException("Unsupported audio or speaker monitoring fell behind.");
            }
            catch (Exception e)
            {
                audioMonitor?.Dispose(); audioMonitor = null;
                Ui(() => { status.Text = e.Message; listen.Checked = false; if (session is { } active) _ = active.SetMonitoringAsync(false); });
            }
        }
    }
    void ClearAudio() { lock (audioLock) { audioMonitor?.Dispose(); audioMonitor = null; } }
    void ShowMicrophones()
    {
        if (talkback is not null) return;
        microphone.Items.Clear();
        microphone.Items.AddRange(PcmInput.Devices().Cast<object>().ToArray());
        microphone.SelectedIndex = 0;
        if (preferences.Microphone is { } saved)
        {
            var found = microphone.Items.Cast<MicrophoneDevice>().FirstOrDefault(device => device.Name == saved);
            if (found is null) { found = new(uint.MaxValue - 1, saved); microphone.Items.Add(found); }
            microphone.SelectedItem = found;
        }
    }
    async Task ToggleTalkAsync()
    {
        if (talkback is not null) { await StopTalkAsync(); return; }
        await StartTalkAsync(false);
    }
    async Task StartTalkAsync(bool testing)
    {
        if (talkback is not null) return;
        await talkStopping;
        if (session?.Client is not { Connected: true } || profile is null) throw new IOException("Connect the camera before talking.");
        if (!File.Exists(ffmpeg.Text.Trim())) throw new IOException("Choose FFmpeg in Storage for two-way audio.");
        ClearAudio();
        talkback = new Talkback(profile, ffmpeg.Text.Trim(), preferences.Microphone, testing);
        talk.Text = testing ? "Stop test" : "Stop talking"; talk.BackColor = Color.MistyRose;
        listen.Enabled = microphone.Enabled = testSpeaker.Enabled = false;
        talkState.Text = testing ? "Starting test…" : "Starting microphone…";
    }
    Task StopTalkAsync()
    {
        if (talkback is not { } previous) return talkStopping;
        talkback = null;
        return talkStopping = FinishTalkAsync(previous);
    }
    async Task FinishTalkAsync(Talkback previous)
    {
        talk.Enabled = false;
        talkState.Text = "Stopping…";
        await previous.DisposeAsync();
        if (IsDisposed) return;
        talk.Text = "Talk to camera"; talk.BackColor = SystemColors.Control;
        talkState.Text = "Microphone off"; listen.Enabled = microphone.Enabled = true;
        talk.Enabled = !closing && session?.Client is { Connected: true };
        testSpeaker.Enabled = talk.Enabled; inputLevel.Value = 0;
        inputLevelText.Text = previous.IsSpeakerTest ? "Input off" : previous.MaximumInputPeak > 0 ? $"Last: {20 * Math.Log10(previous.MaximumInputPeak):0} dB" : "Last: silent";
        if (previous.Error is { } error) status.Text = "Talking stopped: " + error;
    }
    void UpdateTalkState()
    {
        if (talkback is { } active)
        {
            if (active.Completion.IsCompleted || session?.Client is not { Connected: true }) { _ = StopTalkAsync(); return; }
            talkState.Text = active.PacketsSent > 0 ? active.IsSpeakerTest ? "● Speaker test" : "● Mic live" : active.IsSpeakerTest ? "Starting test…" : "Starting microphone…";
            talkState.ForeColor = Color.Firebrick;
            double db = active.InputPeak > 0 ? 20 * Math.Log10(active.InputPeak) : -100;
            inputLevel.Value = Math.Clamp((int)db + 60, 0, 60);
            inputLevelText.Text = active.IsSpeakerTest ? "Test tone" : active.InputPeak > 0 ? $"{db:0} dB" : "Silent";
        }
        else if (talkStopping.IsCompleted)
        { talk.Enabled = testSpeaker.Enabled = !closing && session?.Client is { Connected: true }; talkState.ForeColor = Color.DarkSlateGray; }
    }
}
