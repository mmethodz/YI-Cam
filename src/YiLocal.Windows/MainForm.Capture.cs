using YiLocal.Core;

namespace YiLocal.Windows;

public sealed partial class MainForm
{
    readonly ComboBox recordingProfile = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 340 };
    readonly ComboBox recordingMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 340 };
    readonly CheckBox limitRecordingRate = new() { Text = "Limit recording capture rate", AutoSize = true };
    readonly NumericUpDown recordingRate = Number(0.5m, 120, 5, 2);
    readonly CheckBox recordingAudio = new() { Text = "Include camera microphone audio (AAC)", AutoSize = true };
    readonly NumericUpDown postMotion = Number(1, 3600, 30, 0);
    readonly NumericUpDown motionThreshold = Number(0.1m, 100, 2, 1);
    readonly Label recordingEstimate = Label("Storage estimates appear after a completed recording.");
    bool savingSnapshot;

    async Task SaveSnapshotAsync()
    {
        if (savingSnapshot) return;
        var snapshot = (session ?? throw new IOException("Connect the camera first.")).Snapshot();
        string executable = ffmpeg.Text.Trim();
        if (!File.Exists(executable)) throw new IOException("Choose an FFmpeg executable in Storage for snapshots.");
        using var dialog = new SaveFileDialog
        {
            Filter = "PNG image|*.png", FileName = $"OpenYI_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (File.Exists(dialog.FileName)) throw new IOException("Choose a new filename; snapshots do not overwrite files.");
        savingSnapshot = true;
        try
        {
            await MediaTools.SaveSnapshot(executable, snapshot, dialog.FileName, exportStop.Token);
            status.Text = $"Snapshot saved at original {snapshot.Width} × {snapshot.Height} resolution: {dialog.FileName}";
        }
        finally { savingSnapshot = false; }
    }

    void BuildCapture()
    {
        var body = Column(); body.AccessibleName = "Recording capture settings"; Page("Capture options").Controls.Add(new ScrollableColumn(body));
        body.Controls.Add(Label("Recording profile"));
        recordingProfile.Items.AddRange(["Original stream · lossless (default)", "H.264 balanced · CRF 28", "H.264 smaller · CRF 32"]);
        body.Controls.Add(recordingProfile);
        body.Controls.Add(Label("Original keeps the camera's encoded video, every frame and original timing. Encoding profiles trade CPU and some detail for smaller files; the saving depends on the scene."));
        body.Controls.Add(Label("Capture mode"));
        recordingMode.Items.AddRange(["Continuous / low-rate · real elapsed time", "Timelapse · selected frames played at 25 fps", "Motion · local detection on this PC"]);
        body.Controls.Add(recordingMode); body.Controls.Add(limitRecordingRate);
        var rate = new FlowLayoutPanel { AutoSize = true };
        recordingRate.Increment = 0.5m; rate.Controls.Add(recordingRate); rate.Controls.Add(Label("frames / second (at most the source rate)", 420)); body.Controls.Add(rate);
        var presets = new FlowLayoutPanel { AutoSize = true };
        foreach (decimal fps in new[] { 5m, 1m, 0.5m })
            presets.Controls.Add(Button($"{fps:0.#} fps", () => { if (recordingProfile.SelectedIndex == 0) recordingProfile.SelectedIndex = 1; limitRecordingRate.Checked = true; recordingRate.Value = fps; }));
        body.Controls.Add(presets);
        body.Controls.Add(Label("Frame selection affects saved recordings only. Live preview and camera stream quality stay independent. Low-rate recordings preserve elapsed time; timelapse deliberately plays faster."));
        var motion = new FlowLayoutPanel { AutoSize = true };
        motion.Controls.Add(Label("Seconds after last motion", 230)); motion.Controls.Add(postMotion);
        body.Controls.Add(motion);
        var sensitivity = new FlowLayoutPanel { AutoSize = true };
        sensitivity.Controls.Add(Label("Changed image area (%)", 230)); sensitivity.Controls.Add(motionThreshold);
        motionThreshold.Increment = 0.1m; body.Controls.Add(sensitivity);
        body.Controls.Add(Label("A lower area threshold is more sensitive. Local detection checks up to 5 times/second, independently of saved fps. New motion restarts the timer. Camera movement and lighting can also trigger it. A short buffered lead-in is included when available (up to one keyframe interval, capped at 5 seconds)."));
        body.Controls.Add(recordingAudio);
        body.Controls.Add(Label("Audio is off by default. Original and encoded real-time profiles retain camera-timestamped AAC; accelerated timelapse has no audio."));
        body.Controls.Add(Row(Button("Save capture settings", () => _ = Guard(() => { SaveCaptureOptions(); status.Text = "Capture settings saved for the next recording."; return Task.CompletedTask; })),
            Button("Refresh measured storage estimate", UpdateStorageEstimate)));
        body.Controls.Add(Label("Changes save automatically when no recording is running. Recording and talking always start manually."));
        body.Controls.Add(recordingEstimate);
        recordingProfile.SelectedIndexChanged += (_, _) => UpdateCaptureControls();
        recordingMode.SelectedIndexChanged += (_, _) => UpdateCaptureControls();
        limitRecordingRate.CheckedChanged += (_, _) => UpdateCaptureControls();
    }
    void ShowCaptureOptions()
    {
        recordingProfile.SelectedIndex = (int)preferences.Recording.Encoding;
        recordingMode.SelectedIndex = (int)preferences.Recording.Mode;
        limitRecordingRate.Checked = preferences.Recording.FramesPerSecond is not null;
        recordingRate.Value = (decimal)(preferences.Recording.FramesPerSecond ?? 5);
        recordingAudio.Checked = preferences.Recording.IncludeAudio;
        postMotion.Value = (decimal)preferences.Recording.PostMotionSeconds;
        motionThreshold.Value = (decimal)preferences.Recording.MotionThresholdPercent;
        UpdateCaptureControls();
        UpdateRecordLabels();
    }
    void UpdateCaptureControls()
    {
        bool encode = recordingProfile.SelectedIndex > 0;
        limitRecordingRate.Enabled = encode;
        if (!encode) { if (recordingMode.SelectedIndex == 1) recordingMode.SelectedIndex = 0; limitRecordingRate.Checked = false; }
        else if (recordingMode.SelectedIndex == 1) limitRecordingRate.Checked = true;
        recordingRate.Enabled = encode && limitRecordingRate.Checked;
        recordingAudio.Enabled = recordingMode.SelectedIndex != 1;
        if (!recordingAudio.Enabled) recordingAudio.Checked = false;
        postMotion.Enabled = motionThreshold.Enabled = recordingMode.SelectedIndex == 2;
    }
    void SaveCaptureOptions()
    {
        if (AnyRecording) throw new InvalidOperationException("Stop or disarm all recordings before changing the capture profile.");
        var next = preferences.Copy(); next.Recording = ReadCaptureOptions(); next.Ffmpeg = ffmpeg.Text.Trim(); next.Save();
        preferences = next; captureDirty = false;
        UpdateStorageEstimate();
        UpdateRecordLabels();
    }
    RecordingOptions ReadCaptureOptions()
    {
        UpdateCaptureControls();
        var selected = new RecordingOptions((RecordingEncoding)recordingProfile.SelectedIndex,
            limitRecordingRate.Checked ? (double)recordingRate.Value : null, (CaptureMode)recordingMode.SelectedIndex, recordingAudio.Checked,
            (double)postMotion.Value, (double)motionThreshold.Value);
        selected.Validate();
        if ((selected.Encoding != RecordingEncoding.Original || selected.Mode == CaptureMode.Motion) && !File.Exists(ffmpeg.Text.Trim()))
            throw new IOException("Choose an FFmpeg executable in Storage for encoding profiles or local motion detection.");
        return selected;
    }
    void UpdateStorageEstimate()
    {
        try
        {
            using var library = new RecordingLibrary(preferences.Folder);
            var matching = library.Clips().Where(c => c.Complete && c.Exists &&
                (c.Metadata?.Profile ?? "Original stream") == preferences.Recording.Label &&
                c.Metadata?.TargetFps == preferences.Recording.FramesPerSecond &&
                (c.Metadata?.Audio is not null) == preferences.Recording.IncludeAudio &&
                (c.Metadata?.Kind == "Timelapse") == (preferences.Recording.Mode == CaptureMode.Timelapse))
                .Take(10).ToArray();
            double seconds = matching.Sum(c => c.Metadata is { CaptureDuration: > 0 } m ? m.CaptureDuration : c.Duration);
            if (seconds < 5) { recordingEstimate.Text = "Finish a recording with the saved profile to measure storage use. Changes in lighting, movement and scene detail affect the result."; return; }
            double perHour = matching.Sum(c => c.Bytes) / seconds * 3600 / 1_000_000;
            recordingEstimate.Text = $"Saved profile: {preferences.Recording.Label} · {(preferences.Recording.FramesPerSecond is { } fps ? $"{fps:0.##} fps" : "source fps")}\n" +
                $"Measured from {matching.Length} recent clips ({seconds / 60:0.0} minutes captured):\n" +
                (preferences.Recording.Mode == CaptureMode.Motion ? $"{perHour:0.0} MB per hour of recorded footage. Daily use depends on how often motion triggers.\n" :
                    $"{perHour:0.0} MB/hour · {perHour * 24 / 1000:0.00} GB/day per camera · {perHour * 96 / 1000:0.00} GB/day for four similar cameras.\n") +
                "Estimate only: movement, lighting and scene detail change file size. MB/GB use decimal units.";
        }
        catch (Exception e) { recordingEstimate.Text = "Storage estimate unavailable: " + e.Message; }
    }
}
