using YiLocal.Core;

namespace YiLocal.Windows;

public sealed partial class MainForm
{
    readonly Label settingsMessage = Label("Settings save automatically.");
    readonly System.Windows.Forms.Timer preferencesTimer = new() { Interval = 700 };
    bool storageDirty, captureDirty;

    void TrackPreferenceEdits()
    {
        void Changed(bool storage)
        {
            if (storage) storageDirty = true; else captureDirty = true;
            preferencesTimer.Start();
            settingsMessage.Text = AnyRecording ? "Changes will be saved after all recordings stop." : "Saving settings…";
        }
        foreach (var box in new[] { folder, ffmpeg }) box.TextChanged += (_, _) => Changed(true);
        foreach (var number in new[] { quota, free, segment, days }) number.ValueChanged += (_, _) => Changed(true);
        recycle.CheckedChanged += (_, _) => Changed(true);
        recordingProfile.SelectedIndexChanged += (_, _) => Changed(false);
        recordingMode.SelectedIndexChanged += (_, _) => Changed(false);
        limitRecordingRate.CheckedChanged += (_, _) => Changed(false);
        recordingAudio.CheckedChanged += (_, _) => Changed(false);
        foreach (var number in new[] { recordingRate, postMotion, motionThreshold }) number.ValueChanged += (_, _) => Changed(false);
        preferencesTimer.Tick += (_, _) =>
        {
            if (alarmDirty && !AlarmArmed && !AlarmPlaying && alarmPreparation is null)
                try { SaveAlarmOptions(); } catch (Exception e) { status.Text = "Alarm settings not saved: " + e.Message; }
            if (AnyRecording || importing || exporting || gridBusy || folder.ContainsFocus || ffmpeg.ContainsFocus) return;
            preferencesTimer.Stop();
            try { SavePendingPreferences(); }
            catch (Exception e) { status.Text = settingsMessage.Text = "Settings not saved: " + e.Message; }
        };
        Disposed += (_, _) => preferencesTimer.Dispose();
    }
    void SavePendingPreferences()
    {
        if (alarmDirty) SaveAlarmOptions();
        if (!storageDirty && !captureDirty) return;
        if (AnyRecording || exporting || gridBusy) throw new InvalidOperationException("Stop all recordings and let camera changes/exports finish before changing settings.");
        // Validate both edited sections before persisting either, and update memory only after the write succeeds.
        var next = preferences.Copy();
        if (storageDirty) ReadStorageOptions(next);
        if (captureDirty) next.Recording = ReadCaptureOptions();
        next.Ffmpeg = ffmpeg.Text.Trim(); next.Save();
        bool previewChanged = next.Ffmpeg != preferences.Ffmpeg;
        if (next.Folder != preferences.Folder) StopPlayback();
        preferences = next; storageDirty = captureDirty = false;
        if (previewChanged) ClearPreview();
        settingsMessage.Text = "Settings saved automatically. Changes apply to the next recording session.";
        UpdateRecordLabels();
    }
    void ReadStorageOptions(Preferences next)
    {
        if (string.IsNullOrWhiteSpace(folder.Text)) throw new ArgumentException("Choose a recording folder.");
        next.Folder = Path.GetFullPath(folder.Text.Trim()); next.Ffmpeg = ffmpeg.Text.Trim();
        next.Storage = new((double)quota.Value, (double)free.Value, (double)segment.Value, recycle.Checked, (int)days.Value);
        next.Storage.Validate();
        // Do not create a directory while the user is still editing its name.
        Directory.CreateDirectory(next.Folder);
    }
    void SaveStorageOptions()
    {
        storageDirty = true; SavePendingPreferences(); status.Text = "Storage settings saved.";
    }
}
