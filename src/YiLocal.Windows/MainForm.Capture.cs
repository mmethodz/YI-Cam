using YiLocal.Core;

namespace YiLocal.Windows;

public sealed partial class MainForm
{
    readonly ComboBox recordingProfile = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 340 };
    readonly ComboBox recordingMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 340 };
    readonly CheckBox limitRecordingRate = new() { Text = L.Get("LimitRecordingCaptureRate"), AutoSize = true };
    readonly NumericUpDown recordingRate = Number(0.5m, 120, 5, 2);
    readonly CheckBox recordingAudio = new() { Text = L.Get("IncludeCameraMicrophoneAudioAAC"), AutoSize = true };
    readonly NumericUpDown postMotion = Number(1, 3600, 30, 0);
    readonly NumericUpDown motionThreshold = Number(0.1m, 100, 2, 1);
    readonly Label recordingEstimate = Label(L.Get("StorageEstimatesAppearAfterACompletedRecording"));
    bool savingSnapshot;

    async Task SaveSnapshotAsync()
    {
        if (savingSnapshot) return;
        var snapshot = (session ?? throw new IOException(L.Get("ConnectTheCameraFirst"))).Snapshot();
        string executable = ffmpeg.Text.Trim();
        if (!File.Exists(executable)) throw new IOException(L.Get("ChooseAnFFmpegExecutableInStorageForSnapshots"));
        using var dialog = new SaveFileDialog
        {
            Filter = L.Get("PNGImagePng"), FileName = $"OpenYI_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (File.Exists(dialog.FileName)) throw new IOException(L.Get("ChooseANewFilenameSnapshotsDoNotOverwriteFiles"));
        savingSnapshot = true;
        try
        {
            await MediaTools.SaveSnapshot(executable, snapshot, dialog.FileName, exportStop.Token);
            status.Text = L.Format("SnapshotSavedAtOriginal01Resolution2", snapshot.Width, snapshot.Height, dialog.FileName);
        }
        finally { savingSnapshot = false; }
    }

    void BuildCapture()
    {
        var body = Column(); body.AccessibleName = L.Get("RecordingCaptureSettings"); Page(L.Get("CaptureOptions")).Controls.Add(new ScrollableColumn(body));
        body.Controls.Add(Label(L.Get("RecordingProfile")));
        recordingProfile.Items.AddRange([L.Get("OriginalStreamLosslessDefault"), L.Get("H264BalancedCRF28"), L.Get("H264SmallerCRF32")]);
        body.Controls.Add(recordingProfile);
        body.Controls.Add(Label(L.Get("OriginalKeepsTheCameraSEncodedVideoEveryFrameAndOriginal")));
        body.Controls.Add(Label(L.Get("CaptureMode")));
        recordingMode.Items.AddRange([L.Get("ContinuousLowRateRealElapsedTime"), L.Get("TimelapseSelectedFramesPlayedAt25Fps"), L.Get("MotionLocalDetectionOnThisPC")]);
        body.Controls.Add(recordingMode); body.Controls.Add(limitRecordingRate);
        var rate = new FlowLayoutPanel { AutoSize = true };
        recordingRate.Increment = 0.5m; rate.Controls.Add(recordingRate); rate.Controls.Add(Label(L.Get("FramesSecondAtMostTheSourceRate"), 420)); body.Controls.Add(rate);
        var presets = new FlowLayoutPanel { AutoSize = true };
        foreach (decimal fps in new[] { 5m, 1m, 0.5m })
            presets.Controls.Add(Button(L.Format("Capture.FrameRatePreset", fps), () => { if (recordingProfile.SelectedIndex == 0) recordingProfile.SelectedIndex = 1; limitRecordingRate.Checked = true; recordingRate.Value = fps; }));
        body.Controls.Add(presets);
        body.Controls.Add(Label(L.Get("FrameSelectionAffectsSavedRecordingsOnlyLivePreviewAndCameraStream")));
        var motion = new FlowLayoutPanel { AutoSize = true };
        motion.Controls.Add(Label(L.Get("SecondsAfterLastMotion"), 230)); motion.Controls.Add(postMotion);
        body.Controls.Add(motion);
        body.Controls.Add(Row(Button(L.Get("Duration.ThirtySecondsShort"), () => postMotion.Value = 30), Button(L.Get("Duration.OneMinuteShort"), () => postMotion.Value = 60), Button(L.Get("Duration.FiveMinutesShort"), () => postMotion.Value = 300)));
        var sensitivity = new FlowLayoutPanel { AutoSize = true };
        sensitivity.Controls.Add(Label(L.Get("ChangedImageArea"), 230)); sensitivity.Controls.Add(motionThreshold);
        motionThreshold.Increment = 0.1m; body.Controls.Add(sensitivity);
        body.Controls.Add(Label(L.Get("ALowerAreaThresholdIsMoreSensitiveLocalDetectionChecksUp")));
        body.Controls.Add(recordingAudio);
        body.Controls.Add(Label(L.Get("AudioIsOffByDefaultOriginalAndEncodedRealTimeProfiles")));
        body.Controls.Add(Row(Button(L.Get("SaveCaptureSettings"), () => _ = Guard(() => { SaveCaptureOptions(); status.Text = L.Get("CaptureSettingsSavedForTheNextRecording"); return Task.CompletedTask; })),
            Button(L.Get("RefreshMeasuredStorageEstimate"), UpdateStorageEstimate)));
        body.Controls.Add(Label(L.Get("ChangesSaveAutomaticallyWhenNoRecordingIsRunningRecordingAndTalking")));
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
        if (AnyRecording) throw new InvalidOperationException(L.Get("StopOrDisarmAllRecordingsBeforeChangingTheCaptureProfile"));
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
            throw new IOException(L.Get("ChooseAnFFmpegExecutableInStorageForEncodingProfilesOrLocal"));
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
            if (seconds < 5) { recordingEstimate.Text = L.Get("FinishARecordingWithTheSavedProfileToMeasureStorageUse"); return; }
            double perHour = matching.Sum(c => c.Bytes) / seconds * 3600 / 1_000_000;
            recordingEstimate.Text = L.Format("SavedProfile01", RecordingText.Profile(preferences.Recording.Label), (preferences.Recording.FramesPerSecond is { } fps ? L.Format("Capture.FrameRate", fps) : L.Get("SourceFps"))) +
                L.Format("MeasuredFrom0RecentClips100MinutesCaptured", matching.Length, seconds / 60) +
                (preferences.Recording.Mode == CaptureMode.Motion ? L.Format("Storage.MotionEstimate", perHour) :
                    L.Format("Storage.ContinuousEstimate", perHour, perHour * 24 / 1000, perHour * 96 / 1000)) +
                L.Get("EstimateOnlyMovementLightingAndSceneDetailChangeFileSizeMB");
        }
        catch (Exception e) { recordingEstimate.Text = L.Get("StorageEstimateUnavailable") + e.Message; }
    }
}
