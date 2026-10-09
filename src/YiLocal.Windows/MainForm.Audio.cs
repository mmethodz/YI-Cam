using YiLocal.Core;

namespace YiLocal.Windows;

public sealed partial class MainForm
{
    readonly CheckBox listen = new() { Text = "Listen to camera", AutoSize = true };
    readonly object audioLock = new();
    AudioMonitor? audioMonitor;
    void BuildAudio(Control parent)
    {
        parent.Controls.Add(listen);
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
}
