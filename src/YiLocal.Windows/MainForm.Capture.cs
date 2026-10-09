using YiLocal.Core;

namespace YiLocal.Windows;

public sealed partial class MainForm
{
    readonly ComboBox recordingProfile = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 340 };
    readonly ComboBox recordingMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 340 };
    readonly CheckBox limitRecordingRate = new() { Text = "Limit recording capture rate", AutoSize = true };
    readonly NumericUpDown recordingRate = Number(0.5m, 120, 5, 2);
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
        var body = Column(); body.AccessibleName = "Recording capture settings"; Page("Capture options").Controls.Add(body);
        body.Controls.Add(Label("Recording profile"));
        recordingProfile.Items.AddRange(["Original stream · lossless (default)", "H.264 balanced · CRF 28", "H.264 smaller · CRF 32"]);
        body.Controls.Add(recordingProfile);
        body.Controls.Add(Label("Original keeps the camera's encoded video, every frame and original timing. Encoding profiles trade CPU and some detail for smaller files; the saving depends on the scene."));
        body.Controls.Add(Label("Capture mode"));
        recordingMode.Items.AddRange(["Continuous / low-rate · real elapsed time", "Timelapse · selected frames played at 25 fps"]);
        body.Controls.Add(recordingMode); body.Controls.Add(limitRecordingRate);
        var rate = new FlowLayoutPanel { AutoSize = true };
        recordingRate.Increment = 0.5m; rate.Controls.Add(recordingRate); rate.Controls.Add(Label("frames / second (at most the source rate)", 420)); body.Controls.Add(rate);
        var presets = new FlowLayoutPanel { AutoSize = true };
        foreach (decimal fps in new[] { 5m, 1m, 0.5m })
            presets.Controls.Add(Button($"{fps:0.#} fps", () => { if (recordingProfile.SelectedIndex == 0) recordingProfile.SelectedIndex = 1; limitRecordingRate.Checked = true; recordingRate.Value = fps; }));
        body.Controls.Add(presets);
        body.Controls.Add(Label("Frame selection affects saved recordings only. Live preview and camera stream quality stay independent. Low-rate recordings preserve elapsed time; timelapse deliberately plays faster."));
        body.Controls.Add(Button("Save capture settings", () => _ = Guard(() => { SaveCaptureOptions(); status.Text = "Capture settings saved for the next recording."; return Task.CompletedTask; })));
        body.Controls.Add(Button("Refresh measured storage estimate", UpdateStorageEstimate));
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
        UpdateCaptureControls();
    }
    void UpdateCaptureControls()
    {
        bool encode = recordingProfile.SelectedIndex > 0;
        recordingMode.Enabled = limitRecordingRate.Enabled = encode;
        if (!encode) { recordingMode.SelectedIndex = 0; limitRecordingRate.Checked = false; }
        else if (recordingMode.SelectedIndex == 1) limitRecordingRate.Checked = true;
        recordingRate.Enabled = encode && limitRecordingRate.Checked;
    }
    void SaveCaptureOptions()
    {
        if (session?.Recording == true) throw new InvalidOperationException("Stop recording before changing the capture profile.");
        UpdateCaptureControls();
        var selected = new RecordingOptions((RecordingEncoding)recordingProfile.SelectedIndex,
            limitRecordingRate.Checked ? (double)recordingRate.Value : null, (CaptureMode)recordingMode.SelectedIndex);
        selected.Validate();
        if (selected.Encoding != RecordingEncoding.Original && !File.Exists(ffmpeg.Text.Trim()))
            throw new IOException("Choose an FFmpeg executable in Storage for encoding profiles.");
        preferences.Recording = selected; preferences.Ffmpeg = ffmpeg.Text.Trim(); preferences.Save();
        UpdateStorageEstimate();
    }
    void UpdateStorageEstimate()
    {
        try
        {
            using var library = new RecordingLibrary(preferences.Folder);
            var matching = library.Clips().Where(c => c.Complete && c.Exists &&
                (c.Metadata?.Profile ?? "Original stream") == preferences.Recording.Label &&
                c.Metadata?.TargetFps == preferences.Recording.FramesPerSecond &&
                (c.Metadata?.Kind == "Timelapse") == (preferences.Recording.Mode == CaptureMode.Timelapse))
                .Take(10).ToArray();
            double seconds = matching.Sum(c => c.Metadata is { CaptureDuration: > 0 } m ? m.CaptureDuration : c.Duration);
            if (seconds < 5) { recordingEstimate.Text = "Finish a recording with the saved profile to measure storage use. Changes in lighting, movement and scene detail affect the result."; return; }
            double perHour = matching.Sum(c => c.Bytes) / seconds * 3600 / 1_000_000;
            recordingEstimate.Text = $"Saved profile: {preferences.Recording.Label} · {(preferences.Recording.FramesPerSecond is { } fps ? $"{fps:0.##} fps" : "source fps")}\n" +
                $"Measured from {matching.Length} recent clips ({seconds / 60:0.0} minutes captured):\n" +
                $"{perHour:0.0} MB/hour · {perHour * 24 / 1000:0.00} GB/day per camera · {perHour * 96 / 1000:0.00} GB/day for four similar cameras.\n" +
                "Estimate only: movement, lighting and scene detail change file size. MB/GB use decimal units.";
        }
        catch (Exception e) { recordingEstimate.Text = "Storage estimate unavailable: " + e.Message; }
    }
}
